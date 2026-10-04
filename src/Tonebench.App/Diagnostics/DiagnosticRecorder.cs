using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;

namespace Tonebench.App.Diagnostics;

/// <summary>
/// Everything about the device's state that is not derivable from the record itself, read through
/// <see cref="DiagnosticRecorder.ContextProvider"/> at the moment the record is analyzed. A pull
/// rather than a push because the two most useful fields change without anyone calling a setter:
/// the input range, which autoranging steps inside the device, and the dropped-chunk count.
/// Immutable, so the DSP thread only ever sees one whole consistent snapshot.
/// </summary>
public sealed record DiagnosticContext(
    double InputRangeDbv,
    double OutputRangeDbv,
    bool AutoRangeEnabled,
    GeneratorConfig? Generator,
    long DroppedRxChunks,
    long DroppedBlocks);

/// <summary>
/// Writes the evidence needed to identify a one-record spectrum glitch: a continuous per-record
/// metrics log, and — when a record's broadband floor jumps clear of its own recent baseline — the
/// exact time-domain samples that FFT analyzed, as a float32 WAV, with the generator and device
/// state that produced them alongside it.
///
/// <para><b>Why the raw record and not just the metrics.</b> A raised broadband floor for exactly
/// one record has several possible causes that the spectrum cannot tell apart but the waveform can
/// at a glance: a splice (a step discontinuity where two non-adjacent pieces of the stream were
/// concatenated) looks like a step; a single bad sample looks like an impulse and produces a *flat*
/// white spectrum; a genuine burst from the DUT looks like a burst and is not a bug at all. All
/// three lift the floor of one record. Only the samples say which happened.</para>
///
/// <para><b>Cost, and why it is safe on the DSP thread.</b> <see cref="Observe"/> is called from
/// <c>SpectrumPipeline.ProcessChannel</c>, i.e. on the pipeline's own worker. It does a fixed amount
/// of arithmetic (a median over a 32-entry ring) plus one bounded-channel write of an already-built
/// row; on a trigger it also copies the record once, out of <see cref="ArrayPool{T}"/>. All file I/O
/// happens on <see cref="RunWriterAsync"/>'s task. The queue is drop-oldest and bounded, so a slow
/// disk degrades the log rather than the acquisition — the same rule the rest of the pipeline
/// follows.</para>
///
/// <para>Off by default. It writes potentially large files, so it is armed deliberately through
/// <c>POST /api/diagnostics</c> and reports what it has written through <c>GET /api/diagnostics</c>.</para>
/// </summary>
public sealed class DiagnosticRecorder
{
    /// <summary>
    /// Records whose broadband floor feeds the baseline. ~1.6 s of history at the 20 Hz display
    /// rate — long enough that a single bad record cannot move the median that judges it, short
    /// enough to follow a real level change instead of triggering on one.
    /// </summary>
    private const int BaselineWindow = 32;

    /// <summary>Baseline needs this many records before the trigger arms, so the first records after a flush cannot fire on an empty history.</summary>
    private const int MinBaselineSamples = 12;

    private const int QueueCapacity = 256;

    private readonly ILogger<DiagnosticRecorder> _logger;
    private readonly Channel<IDiagnosticWrite> _queue;
    private readonly Task _writer;

    // Per channel. DSP-thread-only, so no synchronisation: ProcessChannel runs channels in sequence
    // on one worker thread.
    private readonly double[][] _baselineRing = [new double[BaselineWindow], new double[BaselineWindow]];
    private readonly int[] _baselineCount = new int[2];
    private readonly int[] _baselineIndex = new int[2];
    private readonly double[] _scratch = new double[BaselineWindow];
    private readonly long[] _holdoffUntilSeq = new long[2];

    private static readonly DiagnosticContext UnknownContext = new(double.NaN, double.NaN, false, null, 0, 0);

    private volatile bool _manualCapture;
    private long _capturesWritten;
    private long _recordsObserved;
    private long _queueDrops;
    private bool _loggedWriteFailure;

    // Writer-task-only, so no synchronisation.
    private StreamWriter? _metricsWriter;
    private string? _metricsPath;

    public DiagnosticRecorder(ILogger<DiagnosticRecorder> logger)
    {
        _logger = logger;
        Directory = TonebenchAppData.DiagnosticsDirectory;

        _queue = Channel.CreateBounded<IDiagnosticWrite>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            itemDropped: dropped =>
            {
                dropped.Dispose();
                Interlocked.Increment(ref _queueDrops);
            });

        _writer = Task.Run(RunWriterAsync);
    }

    /// <summary>Master switch. Off by default — see the type remarks.</summary>
    public bool Enabled { get; set; }

    /// <summary>Append a row per analyzed record to the metrics CSV. Cheap; on whenever <see cref="Enabled"/> is.</summary>
    public bool LogMetrics { get; set; } = true;

    /// <summary>Write the raw record when the trigger fires.</summary>
    public bool CaptureRawOnTrigger { get; set; } = true;

    /// <summary>
    /// How far above its own baseline a record's noise floor must jump to be captured. 6 dB is well
    /// clear of the couple of dB the floor breathes by on a healthy run, and far below the tens of
    /// dB a splice produces.
    /// </summary>
    public double TriggerThresholdDb { get; set; } = 6.0;

    /// <summary>Where the files go.</summary>
    public string Directory { get; }

    public long CapturesWritten => Interlocked.Read(ref _capturesWritten);

    public long RecordsObserved => Interlocked.Read(ref _recordsObserved);

    public long QueueDrops => Interlocked.Read(ref _queueDrops);

    /// <summary>
    /// Reads the device state to stamp on a record. Set once by <c>DeviceSessionService</c> at
    /// construction; a delegate rather than a pushed snapshot because the two values most worth
    /// having are the two that change without anyone calling a setter — the input range, which
    /// autoranging steps inside the device, and the dropped-chunk count. Invoked on the DSP thread,
    /// so it must only read fields.
    /// </summary>
    public Func<DiagnosticContext>? ContextProvider { get; set; }

    private DiagnosticContext CurrentContext()
    {
        try
        {
            return ContextProvider?.Invoke() ?? UnknownContext;
        }
        catch
        {
            // A diagnostic must never be able to take down the pipeline it is observing.
            return UnknownContext;
        }
    }

    /// <summary>Captures the next record analyzed, whatever its level. For grabbing a reference "good" record to diff against.</summary>
    public void RequestCapture() => _manualCapture = true;

    /// <summary>
    /// Called once per analyzed record, per channel, from the pipeline's DSP worker — after the
    /// flush-generation check, so a record that gets discarded is never logged as if it had been
    /// displayed.
    /// </summary>
    public void Observe(
        int channelIndex,
        uint seq,
        ReadOnlySpan<double> record,
        double sampleRateHz,
        SpectrumMeters? meters,
        long samplesWritten)
    {
        if (!Enabled || channelIndex < 0 || channelIndex > 1)
            return;

        Interlocked.Increment(ref _recordsObserved);

        var context = CurrentContext();
        double floorDbv = meters?.NoiseFloorDbv ?? double.NaN;
        double baseline = Baseline(channelIndex);
        double delta = double.IsNaN(floorDbv) || double.IsNaN(baseline) ? 0.0 : floorDbv - baseline;

        var dropouts = ScanForDropouts(record);

        bool armed = _baselineCount[channelIndex] >= MinBaselineSamples && seq >= (ulong)_holdoffUntilSeq[channelIndex];

        // Either signal arms a capture. The floor jump is the symptom; a held-constant span is the
        // cause, and needs no baseline at all to be conclusive — so it triggers even before the
        // baseline has filled.
        bool triggered = (armed && delta >= TriggerThresholdDb)
            || (dropouts.Runs > 0 && seq >= (ulong)_holdoffUntilSeq[channelIndex]);

        bool manual = _manualCapture;
        if (manual)
            _manualCapture = false;

        if (LogMetrics)
            Enqueue(new MetricsRow(BuildMetricsLine(
                channelIndex, seq, meters, baseline, delta, samplesWritten, sampleRateHz,
                record.Length, context, triggered || manual, dropouts)));

        if ((triggered || manual) && CaptureRawOnTrigger)
        {
            // Copied here, on this thread, because `record` is a span over a pooled buffer the
            // pipeline returns the moment ProcessChannel comes back.
            double[] copy = ArrayPool<double>.Shared.Rent(record.Length);
            record.CopyTo(copy);

            Enqueue(new RawCapture(
                copy, record.Length, channelIndex, seq, sampleRateHz, meters, baseline, delta,
                samplesWritten, context, manual && !triggered, DateTimeOffset.Now, dropouts));

            // One capture per record length, not one per overlapping tick. At 20 Hz with a 341 ms
            // record consecutive windows share ~93% of their samples, so a single glitch would
            // otherwise fire on every tick it slides through and write the same event seven times.
            long recordTicks = (long)Math.Ceiling(record.Length / sampleRateHz * 20.0);
            _holdoffUntilSeq[channelIndex] = seq + Math.Max(recordTicks, 1);
        }

        // Last, so the record being judged is never part of the baseline judging it.
        PushBaseline(channelIndex, floorDbv);
    }

    /// <summary>
    /// Consecutive samples the signal must hold a constant value before it counts as a dropout.
    /// 16 at 192 kHz is 83 us — a 1 kHz sine is never flat that long even across its own peak
    /// (consecutive samples there still differ by far more than the tolerance below), so this
    /// cannot fire on real signal.
    /// </summary>
    private const int MinHeldRun = 16;

    /// <summary>
    /// Scans the record for spans where the waveform holds a constant value — a gap in the
    /// generator output, which is what a starved DAC actually emits.
    ///
    /// <para>This is the ground truth the noise-floor trigger only infers. A dropout of 0.28 ms in
    /// a 341 ms record lifts the broadband floor by ~72 dB, so the floor trigger does catch it —
    /// but the floor also moves for real reasons (a range step, a change of stimulus, the DUT
    /// genuinely getting noisier), whereas a dead-flat span in a record that contains a tone is
    /// unambiguous. Measured directly on captures from this app: three glitch records contained
    /// held spans of 0.276, 3.026 and 0.917 ms, and the clean reference record contained none at
    /// all.</para>
    ///
    /// <para>The tolerance scales with the record's own RMS rather than being absolute, so it means
    /// the same thing at any signal level; the floor keeps it sane for a silent record. One pass of
    /// compares over the record, on the DSP thread — tens of microseconds at 65536 points.</para>
    /// </summary>
    private static (int Runs, int LongestSamples, int TotalSamples) ScanForDropouts(ReadOnlySpan<double> record)
    {
        if (record.Length < MinHeldRun)
            return (0, 0, 0);

        double sumSquares = 0;
        for (int i = 0; i < record.Length; i++)
            sumSquares += record[i] * record[i];

        double rms = Math.Sqrt(sumSquares / record.Length);
        double tolerance = Math.Max(rms * 1e-4, 1e-6);

        int runs = 0, longest = 0, total = 0, current = 1;
        for (int i = 1; i < record.Length; i++)
        {
            if (Math.Abs(record[i] - record[i - 1]) <= tolerance)
            {
                current++;
                continue;
            }

            if (current >= MinHeldRun)
            {
                runs++;
                total += current;
                if (current > longest)
                    longest = current;
            }

            current = 1;
        }

        if (current >= MinHeldRun)
        {
            runs++;
            total += current;
            if (current > longest)
                longest = current;
        }

        return (runs, longest, total);
    }

    /// <summary>Median of the recent floors. Median, not mean, so one bad record cannot raise the bar that would have caught the next one.</summary>
    private double Baseline(int channelIndex)
    {
        int count = _baselineCount[channelIndex];
        if (count == 0)
            return double.NaN;

        var ring = _baselineRing[channelIndex];
        var scratch = _scratch.AsSpan(0, count);
        ring.AsSpan(0, count).CopyTo(scratch);
        scratch.Sort();
        return scratch[count / 2];
    }

    private void PushBaseline(int channelIndex, double floorDbv)
    {
        if (double.IsNaN(floorDbv) || double.IsInfinity(floorDbv))
            return;

        var ring = _baselineRing[channelIndex];
        ring[_baselineIndex[channelIndex]] = floorDbv;
        _baselineIndex[channelIndex] = (_baselineIndex[channelIndex] + 1) % BaselineWindow;
        if (_baselineCount[channelIndex] < BaselineWindow)
            _baselineCount[channelIndex]++;
    }

    /// <summary>Resets the baselines. Called on a flush: the records either side of one are not comparable, and treating them as such fires the trigger on every stimulus change.</summary>
    public void ResetBaseline()
    {
        Array.Clear(_baselineCount);
        Array.Clear(_baselineIndex);
        Array.Clear(_holdoffUntilSeq);
    }

    private void Enqueue(IDiagnosticWrite write)
    {
        if (!_queue.Writer.TryWrite(write))
            write.Dispose();
    }

    private static string Fmt(double value) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? string.Empty
            : value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// One generator channel as a line of text -- "tones 1000Hz@-20dBV", "chirp 2..96000Hz@-18dBV",
    /// "off". Public because <see cref="StreamRecorder"/>'s sidecar and the session's recording
    /// events quote a stimulus the same way this type's own sidecar does, and two formatters would
    /// drift apart the first time a generator kind was added.
    /// </summary>
    public static string Describe(ChannelGeneratorConfig? channel)
    {
        if (channel is null || !channel.Enabled || channel.Kind == GeneratorKind.Off)
            return "off";

        return channel.Kind switch
        {
            GeneratorKind.Tones => "tones " + string.Join(
                "+",
                channel.Tones.Select(t =>
                    $"{t.FrequencyHz.ToString("0.####", CultureInfo.InvariantCulture)}Hz@" +
                    $"{t.AmplitudeDbv.ToString("0.##", CultureInfo.InvariantCulture)}dBV")),
            GeneratorKind.Multitone when channel.Multitone is { } mt =>
                $"multitone {mt.TonesPerOctave}/oct @{Fmt(mt.TotalRmsDbv)}dBV rolloff {Fmt(mt.RolloffDbPerOctave)}dB/oct",
            GeneratorKind.WhiteNoise when channel.Noise is { } noise => $"noise @{Fmt(noise.LevelDbv)}dBV",
            GeneratorKind.PinkNoise when channel.Noise is { } noise => $"pink noise @{Fmt(noise.LevelDbv)}dBV",
            GeneratorKind.EchoChirp when channel.Chirp is { } chirp =>
                $"chirp {Fmt(chirp.StartFrequencyHz)}..{Fmt(chirp.EndFrequencyHz)}Hz @{Fmt(chirp.AmplitudeDbv)}dBV",
            _ => channel.Kind.ToString(),
        };
    }

    private string BuildMetricsLine(
        int channelIndex, uint seq, SpectrumMeters? meters, double baseline, double delta,
        long samplesWritten, double sampleRateHz, int fftSize, DiagnosticContext context, bool captured,
        (int Runs, int LongestSamples, int TotalSamples) dropouts)
    {
        var sb = new StringBuilder(320);
        sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(seq).Append(',');
        sb.Append(channelIndex == 0 ? 'L' : 'R').Append(',');
        sb.Append(Fmt(meters?.FundamentalFreqHz ?? double.NaN)).Append(',');
        sb.Append(Fmt(meters?.FundamentalDbv ?? double.NaN)).Append(',');
        sb.Append(Fmt(meters?.ThdPercent ?? double.NaN)).Append(',');
        sb.Append(Fmt(meters?.ThdNPercent ?? double.NaN)).Append(',');
        sb.Append(Fmt(meters?.SnrDb ?? double.NaN)).Append(',');
        sb.Append(Fmt(meters?.NoiseFloorDbv ?? double.NaN)).Append(',');
        sb.Append(Fmt(baseline)).Append(',');
        sb.Append(Fmt(delta)).Append(',');
        sb.Append(Fmt(meters?.PeakDbv ?? double.NaN)).Append(',');
        sb.Append(Fmt(meters?.TotalRmsDbv ?? double.NaN)).Append(',');
        sb.Append(meters?.AveragesTaken ?? 0).Append(',');
        sb.Append(Fmt(context.InputRangeDbv)).Append(',');
        sb.Append(Fmt(context.OutputRangeDbv)).Append(',');
        sb.Append(context.AutoRangeEnabled ? 1 : 0).Append(',');
        sb.Append(context.DroppedRxChunks).Append(',');
        sb.Append(context.DroppedBlocks).Append(',');
        sb.Append(samplesWritten).Append(',');
        sb.Append(fftSize).Append(',');
        sb.Append(Fmt(sampleRateHz)).Append(',');
        sb.Append(captured ? 1 : 0).Append(',');
        sb.Append(dropouts.Runs).Append(',');
        sb.Append(Fmt(dropouts.LongestSamples / sampleRateHz * 1000.0)).Append(',');
        sb.Append(Fmt(dropouts.TotalSamples / sampleRateHz * 1000.0)).Append(',');
        sb.Append('"').Append(Describe(context.Generator?.Left)).Append("\",");
        sb.Append('"').Append(Describe(context.Generator?.Right)).Append('"');
        return sb.ToString();
    }

    private const string MetricsHeader =
        "utc,seq,ch,fundHz,fundDbv,thdPct,thdNPct,snrDb,noiseFloorDbv,baselineDbv,deltaDb,peakDbv," +
        "totalRmsDbv,averages,inputRangeDbv,outputRangeDbv,autoRange,droppedRxChunks,droppedBlocks,samplesWritten," +
        "fftSize,sampleRateHz,captured,dropoutRuns,dropoutLongestMs,dropoutTotalMs,generatorL,generatorR";

    private async Task RunWriterAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (item)
                {
                    case MetricsRow row:
                        await AppendMetricsAsync(row.Line).ConfigureAwait(false);
                        break;
                    case RawCapture capture:
                        await WriteCaptureAsync(capture).ConfigureAwait(false);
                        Interlocked.Increment(ref _capturesWritten);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Once. A failing disk must not turn into one log line per record.
                if (!_loggedWriteFailure)
                {
                    _loggedWriteFailure = true;
                    _logger.LogError(ex, "Diagnostic write failed; further failures will not be logged");
                }
            }
            finally
            {
                item.Dispose();
            }
        }
    }

    /// <summary>
    /// Appends one row, keeping the file open across rows.
    ///
    /// <para>This opened and closed a <see cref="FileStream"/> per row, which at the 20 Hz display
    /// rate and two channels is 40 open/close pairs a second — a measurable amount of I/O and GC
    /// churn to add to a machine whose actual problem turned out to be that the DAC was being
    /// starved by exactly that kind of pressure. A diagnostic that perturbs the fault it is
    /// measuring is worse than no diagnostic. Held open, flushed per row so a crash still leaves
    /// the rows on disk, and reopened when the date rolls over.</para>
    /// </summary>
    private async Task AppendMetricsAsync(string line)
    {
        string path = Path.Combine(Directory, $"metrics-{DateTime.Now:yyyyMMdd}.csv");
        if (_metricsWriter is null || !string.Equals(_metricsPath, path, StringComparison.OrdinalIgnoreCase))
        {
            if (_metricsWriter is not null)
                await _metricsWriter.DisposeAsync().ConfigureAwait(false);

            System.IO.Directory.CreateDirectory(Directory);
            bool fresh = !File.Exists(path);
            _metricsWriter = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
            _metricsPath = path;

            if (fresh)
                await _metricsWriter.WriteLineAsync(MetricsHeader).ConfigureAwait(false);
        }

        await _metricsWriter.WriteLineAsync(line).ConfigureAwait(false);
        await _metricsWriter.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteCaptureAsync(RawCapture capture)
    {
        System.IO.Directory.CreateDirectory(Directory);

        string stem = string.Create(
            CultureInfo.InvariantCulture,
            $"capture-{capture.CapturedAt:yyyyMMdd-HHmmss-fff}-seq{capture.Seq}-{(capture.ChannelIndex == 0 ? "L" : "R")}");

        await WriteWavAsync(
            Path.Combine(Directory, stem + ".wav"),
            capture.Samples.AsMemory(0, capture.Length),
            capture.SampleRateHz).ConfigureAwait(false);

        await WriteSidecarAsync(Path.Combine(Directory, stem + ".json"), capture).ConfigureAwait(false);
    }

    /// <summary>
    /// The record as a mono 32-bit-float WAV, in volts — no normalisation, no scaling. Float, so a
    /// sample is the calibrated volt value the FFT actually saw and a viewer's amplitude readout is
    /// directly comparable to the app's own dBV. Any audio editor or <c>scipy.io.wavfile</c> opens
    /// it, which is the point: the waveform is the evidence.
    /// </summary>
    private static async Task WriteWavAsync(string path, ReadOnlyMemory<double> samples, double sampleRateHz)
    {
        int frameCount = samples.Length;
        int dataBytes = frameCount * sizeof(float);
        int sampleRate = (int)Math.Round(sampleRateHz);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());

        writer.Write("fmt "u8.ToArray());
        writer.Write(16);                       // PCM-style fmt chunk size
        writer.Write((short)3);                 // WAVE_FORMAT_IEEE_FLOAT
        writer.Write((short)1);                 // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(float)); // byte rate
        writer.Write((short)sizeof(float));     // block align
        writer.Write((short)32);                // bits per sample

        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);

        var span = samples.Span;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(dataBytes);
        try
        {
            for (int i = 0; i < frameCount; i++)
                BitConverter.TryWriteBytes(buffer.AsSpan(i * sizeof(float)), (float)span[i]);

            writer.Flush();
            await stream.WriteAsync(buffer.AsMemory(0, dataBytes)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Everything needed to interpret the WAV next to it. <see cref="Utf8JsonWriter"/> rather than
    /// reflection-based serialization so this stays correct under <c>PublishTrimmed</c>.
    /// </summary>
    private static async Task WriteSidecarAsync(string path, RawCapture capture)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteString("capturedAt", capture.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
        json.WriteString(
            "reason",
            capture.Manual ? "manual" : capture.Dropouts.Runs > 0 ? "dac-dropout" : "noise-floor-jump");
        json.WriteNumber("seq", capture.Seq);
        json.WriteString("channel", capture.ChannelIndex == 0 ? "L" : "R");
        json.WriteNumber("sampleRateHz", capture.SampleRateHz);
        json.WriteNumber("fftSize", capture.Length);
        json.WriteNumber("recordDurationMs", capture.Length / capture.SampleRateHz * 1000.0);
        json.WriteNumber("samplesWrittenAtSnapshot", capture.SamplesWritten);

        json.WriteStartObject("trigger");
        json.WriteNumber("noiseFloorDbv", capture.Meters?.NoiseFloorDbv ?? double.NaN);
        json.WriteNumber("baselineDbv", capture.Baseline);
        json.WriteNumber("deltaDb", capture.Delta);
        json.WriteEndObject();

        // The dropout numbers first: when these are non-zero the generator output actually stopped,
        // and everything in "meters" below is a consequence of that rather than a measurement.
        json.WriteStartObject("dropouts");
        json.WriteNumber("runs", capture.Dropouts.Runs);
        json.WriteNumber("longestMs", capture.Dropouts.LongestSamples / capture.SampleRateHz * 1000.0);
        json.WriteNumber("totalMs", capture.Dropouts.TotalSamples / capture.SampleRateHz * 1000.0);
        json.WriteEndObject();

        json.WriteStartObject("meters");
        if (capture.Meters is { } m)
        {
            json.WriteNumber("fundamentalFreqHz", m.FundamentalFreqHz);
            json.WriteNumber("fundamentalDbv", m.FundamentalDbv);
            json.WriteNumber("thdPercent", m.ThdPercent);
            json.WriteNumber("thdNPercent", m.ThdNPercent);
            json.WriteNumber("snrDb", m.SnrDb);
            json.WriteNumber("noiseFloorDbv", m.NoiseFloorDbv);
            json.WriteNumber("peakDbv", m.PeakDbv);
            json.WriteNumber("totalRmsDbv", m.TotalRmsDbv);
            json.WriteNumber("averagesTaken", m.AveragesTaken);
        }

        json.WriteEndObject();

        json.WriteStartObject("device");
        json.WriteNumber("inputRangeDbv", capture.Context.InputRangeDbv);
        json.WriteNumber("outputRangeDbv", capture.Context.OutputRangeDbv);
        json.WriteBoolean("autoRangeEnabled", capture.Context.AutoRangeEnabled);
        json.WriteNumber("droppedRxChunks", capture.Context.DroppedRxChunks);
        json.WriteNumber("droppedBlocks", capture.Context.DroppedBlocks);
        json.WriteEndObject();

        json.WriteStartObject("generator");
        json.WriteString("left", Describe(capture.Context.Generator?.Left));
        json.WriteString("right", Describe(capture.Context.Generator?.Right));
        json.WriteBoolean("snapToBin", capture.Context.Generator?.SnapToBin ?? false);
        json.WriteEndObject();

        json.WriteEndObject();
        await json.FlushAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);

        if (_metricsWriter is not null)
        {
            await _metricsWriter.DisposeAsync().ConfigureAwait(false);
            _metricsWriter = null;
        }
    }

    private interface IDiagnosticWrite : IDisposable;

    private sealed record MetricsRow(string Line) : IDiagnosticWrite
    {
        public void Dispose() { }
    }

    private sealed record RawCapture(
        double[] Samples,
        int Length,
        int ChannelIndex,
        uint Seq,
        double SampleRateHz,
        SpectrumMeters? Meters,
        double Baseline,
        double Delta,
        long SamplesWritten,
        DiagnosticContext Context,
        bool Manual,
        DateTimeOffset CapturedAt,
        (int Runs, int LongestSamples, int TotalSamples) Dropouts) : IDiagnosticWrite
    {
        public void Dispose() => ArrayPool<double>.Shared.Return(Samples);
    }
}
