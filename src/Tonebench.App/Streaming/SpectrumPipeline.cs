using System.Buffers;
using System.Numerics;
using Tonebench.App.Diagnostics;
using Tonebench.App.Services;
using Tonebench.Device;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;

namespace Tonebench.App.Streaming;

/// <summary>
/// PcmBlock -&gt; window -&gt; FFT -&gt; PSD/amplitude -&gt; harmonics -&gt; log decimation -&gt; frame -&gt; broadcast.
///
/// Two independent rates are deliberately decoupled:
/// <list type="bullet">
/// <item><see cref="Enqueue"/> runs on the acquisition drain loop and only appends samples into
/// per-channel ring buffers — an O(block size) memcpy-like loop, always faster than real-time,
/// so it can never fall behind or stall acquisition.</item>
/// <item>A separate worker, paced by a <see cref="PeriodicTimer"/> at <see cref="SpectrumPipelineOptions.TargetFrameRateHz"/>,
/// snapshots the latest FFT-sized window and does the actual DSP + broadcast work. If that work
/// takes longer than one tick period, the timer does not queue the missed tick — the next
/// <c>WaitForNextTickAsync</c> simply returns at its own scheduled time. This is what keeps the
/// display rate roughly constant "regardless of FFT size": a huge FFT just means the same-ish
/// window gets re-analyzed across a couple of ticks until enough new samples slide in.</item>
/// </list>
/// </summary>
/// <remarks>
/// <paramref name="diagnostics"/> is optional so the many tests that construct a pipeline directly
/// keep doing so unchanged; DI always supplies the singleton, and every call below is null-guarded.
/// </remarks>
public sealed class SpectrumPipeline(
    SpectrumBroadcastHub hub, ILogger<SpectrumPipeline> logger, DiagnosticRecorder? diagnostics = null,
    UserWeightingSetting? userWeighting = null, ExternalGainSetting? externalGain = null)
{
    private const int MaxChannels = 2;

    // The per-bin power gain of the weighting in effect, built once per (spec, user tables, FFT
    // size, bin size) and reused every tick -- evaluating six rational functions per bin at 20
    // ticks a second on a 524288-point spectrum would be the most expensive thing in the tick.
    // Touched only on the worker thread.
    private (WeightingSpec Spec, int UserVersion, int SpecLen, double BinSizeHz)? _weightingKey;
    private double[]? _weightingPowerGain;

    private readonly IFftBackend _fft = new FftFlatBackend();
    private readonly SpectrumAverager[] _ampAveragers = [new(), new()];
    private readonly SpectrumAverager[] _psdAveragers = [new(), new()];
    private readonly object _ringBufferLock = new();

    // Held across a whole *set* of channels -- every append in one Enqueue, every snapshot in one
    // ProcessTick, every Reset in one Flush. Each SampleRingBuffer already has its own lock, which
    // keeps that one buffer internally consistent; this one is what keeps the buffers consistent
    // *with each other*. Enqueue appends a block to L's buffer and then to R's, so without it a
    // snapshot landing between those two appends captures an L window one whole block (512 frames,
    // ~10.7 ms at 48 kHz) ahead of the R window. For a periodic signal that aliases to an arbitrary
    // phase offset, which is why the oscilloscope's two traces could still sit at a fixed, plausible-
    // looking phase difference even once both channels were snapshotted back-to-back: back-to-back
    // shrank the race window to microseconds but did not close it.
    private readonly object _sampleSetLock = new();

    // Retained across StopAsync (so a stopped acquisition can still be saved) and cleared only by
    // Flush() (so restarting acquisition drops the stale trace). Holds ampPower -- peak-volts
    // squared, the same pre-dBV quantity ProcessChannel already computes -- rather than the
    // converted dBV trace: a Math.Log10 pass over every bin only needs to happen when someone
    // actually calls LatestSnapshots() (a user-initiated save), not on every one of this tick's
    // ~20/sec iterations regardless of whether a save ever happens. See LatestSnapshots.
    private readonly RetainedSpectrum[] _retained = [new(), new()];
    private readonly object _retainedLock = new();

    // Set once a channel's SpectrumPipelineOptions.SingleShot capture completes; ProcessTick then
    // skips that channel entirely, which is what "holds" the last broadcast frame on screen. Only
    // Flush() clears it, so toggling SingleShot off and back on without a Flush would otherwise
    // resume immediately held from a previous capture rather than starting a fresh window.
    private readonly bool[] _singleShotHeld = new bool[MaxChannels];

    // SampleRingBuffer.SamplesWritten as it stood when each channel last committed an average, or 0
    // for "no average yet in this epoch". Drives SpectrumPipelineOptions.AverageHopFraction; see
    // HasFreshSignal. Reset by Flush and by a ring-buffer resize, both of which restart
    // the counter it is compared against.
    private readonly long[] _lastAverageWritten = new long[MaxChannels];

    /// <summary>
    /// Bumped by every <see cref="Flush"/>. A tick reads it before it starts and again before it
    /// commits anything, and throws its work away if the two differ — see <see cref="ProcessChannel"/>.
    /// </summary>
    private long _flushGeneration;

    private SampleRingBuffer?[] _ringBuffers = new SampleRingBuffer?[MaxChannels];
    private int _ringBufferFftSize;
    private double _sampleRateHz;
    private int _channelCount;
    private uint _seq;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    private volatile SpectrumPipelineOptions _options = new();

    public SpectrumPipelineOptions Options
    {
        get => _options;
        set
        {
            var previous = _options;
            _options = value ?? throw new ArgumentNullException(nameof(value));

            // Resize now, on the caller's thread, rather than leaving it to the next Enqueue on the
            // acquisition thread. A caller that changes FftSize and then flushes -- the sweep does
            // exactly that, per point -- otherwise races the two: Flush captures _ringBuffers and
            // books the stimulus-latency discard on it while EnsureRingBuffers concurrently swaps in
            // replacements that read DiscardRemaining before the discard was set. The discard is
            // then lost and the first record after the change refills with pre-change audio, which
            // reads as a fundamental several dB low on the first point at each new FFT size.
            //
            // Guarded on the pipeline having started: before Start() there are no buffers to keep in
            // step, and Start() does its own EnsureRingBuffers.
            if (_ringBufferFftSize != 0)
                EnsureRingBuffers(_options.FftSize);

            // The FFT size sets both the per-tick CPU cost and the record length, so it is the first
            // thing anyone reading a log wants to know when attributing a dropped chunk or a
            // contaminated point — and nothing was recording it. Only on an actual change: the sweep
            // reassigns Options per point, and most of those reassignments change nothing.
            if (previous.FftSize != _options.FftSize || previous.AverageCount != _options.AverageCount)
            {
                logger.LogInformation(
                    "Spectrum analysis now {FftSize} points, {Window} window, {Averaging} x{AverageCount} at hop {Hop:0.##}, "
                        + "weighting {Weighting} (was {PreviousFftSize} points x{PreviousAverageCount})",
                    _options.FftSize, _options.Window, _options.Averaging, _options.AverageCount,
                    _options.AverageHopFraction, DescribeWeighting(_options.Weighting), previous.FftSize, previous.AverageCount);
            }
            else if (previous.Weighting != _options.Weighting)
            {
                logger.LogInformation(
                    "Spectrum weighting now {Weighting} (was {PreviousWeighting})",
                    DescribeWeighting(_options.Weighting), DescribeWeighting(previous.Weighting));
            }
        }
    }

    public void Start(double sampleRateHz, int channelCount)
    {
        _sampleRateHz = sampleRateHz;
        _channelCount = Math.Clamp(channelCount, 1, MaxChannels);
        _seq = 0;
        EnsureRingBuffers(_options.FftSize);

        // Per acquisition, like the device's own two drop counters and for the same reason: the
        // number is only ever asked whether the measurement on screen can be trusted, which a
        // running total cannot answer. Every runner starts its own acquisition, so each measurement
        // gets a clean count for free.
        hub.ResetDroppedFrames();

        logger.LogInformation(
            "Spectrum pipeline starting at {SampleRateHz:0} Hz, {ChannelCount} ch, {FftSize} points "
                + "({BinHz:0.###} Hz/bin, {RecordMs:0.#} ms record), {Window} window, {Averaging} x{AverageCount} "
                + "at hop {Hop:0.##}, single-shot {SingleShot}, {FrameRateHz} Hz display",
            sampleRateHz, _channelCount, _options.FftSize,
            sampleRateHz / _options.FftSize, _options.FftSize * 1000.0 / sampleRateHz,
            _options.Window, _options.Averaging, _options.AverageCount, _options.AverageHopFraction,
            _options.SingleShot, _options.TargetFrameRateHz);

        // Unconditionally, because EnsureRingBuffers only allocates fresh buffers when the FFT size
        // changed. Restarting at a new sample rate keeps the same size, so without this the first
        // FFTs after the restart analyze samples captured at the *old* rate — see Flush.
        Flush();

        var cts = new CancellationTokenSource();
        _cts = cts;

        // Capture the token by value into a local, not the field, before handing it to Task.Run: the
        // lambda doesn't actually run until the thread pool gets to it, and if StopAsync races in first
        // and nulls _cts (see below), reading the field lazily inside the lambda throws a
        // NullReferenceException instead of observing an already-cancelled token like it should.
        var runCt = cts.Token;
        _worker = Task.Run(() => RunAsync(runCt));
    }

    /// <summary>
    /// What the most recent <see cref="Flush"/> was asked to discard. Test observability only: the
    /// session's stimulus path composes that number from two sources (device latency and the
    /// configured settle time), and this is how a test checks the composition without streaming.
    /// </summary>
    internal int LastFlushDiscardFrames { get; private set; }

    /// <summary>
    /// Discard frames still owed on the first processed channel. Test observability only, for the
    /// one rule that cannot be read off <see cref="LastFlushDiscardFrames"/>: that
    /// <see cref="FlushAfterRangeStep"/> leaves a stimulus discard booked by an *earlier* flush
    /// standing rather than zeroing it.
    /// </summary>
    internal int PendingDiscardFrames => _ringBuffers[0]?.DiscardRemaining ?? 0;

    /// <summary>
    /// Drops every buffered sample and all averaging state, so the next broadcast frame is built
    /// only from samples captured after the change that prompted this call actually reached the wire.
    ///
    /// Call this whenever the signal being analyzed changes discontinuously: a new sample rate, a
    /// generator change, an input-range change. An FFT record that straddles such a change carries
    /// a step, and a step smears the tone across a broad skirt — the "noise floor jumps up and then
    /// settles" artifact, which lasts exactly one buffer-fill. Flushing trades that for a brief
    /// pause in updates (the UI keeps showing the last good frame) while a clean record fills.
    ///
    /// <para><paramref name="discardFrames"/> must be
    /// <see cref="Tonebench.Device.IAnalyzerDevice.StimulusLatencyFrames"/> for any caller flushing
    /// because of a stimulus change, and 0 only when nothing changed on the wire (an acquisition
    /// restart, where there is no stale audio to outrun). Passing 0 for a stimulus change is the
    /// bug this parameter exists for: the flush lands before the change does, so the buffer refills
    /// with pre-change audio and the step ends up inside the first record anyway — measured at up to
    /// +41 dB of broadband floor on a 1 dB amplitude change. Worse, the contaminated record then
    /// becomes average #1, so with 8 averages it stays on screen for eight frames rather than one.
    /// See <see cref="SampleRingBuffer.Reset"/> for the measurements.</para>
    ///
    /// <para>This overwrites the booked discard, which is right for every caller that knows what the
    /// change was and when it happened. The device's own in-band autorange report is neither — use
    /// <see cref="FlushAfterRangeStep"/> there.</para>
    /// </summary>
    public void Flush(int discardFrames = 0)
    {
        LastFlushDiscardFrames = discardFrames;
        FlushCore(discardFrames, keepPendingDiscard: false);
    }

    /// <summary>
    /// The flush for a discontinuity the *device* reports in band — an autorange step, announced on
    /// the first block captured after it (<c>PcmBlock.StartsAfterDiscontinuity</c>). Empties the
    /// window and resets the averagers like any flush, but never <i>reduces</i> a discard already
    /// booked.
    ///
    /// <para>That distinction is the whole method. The news is late by construction: the step's
    /// settle frames have to be consumed and a whole block assembled before the flag can ride out,
    /// so it reaches <c>DeviceSessionService.PumpAsync</c> tens of milliseconds after the register
    /// write returned — measured 47 ms on a QA403 at 192 kHz. A sweep runner has moved on by then:
    /// <c>ThdSweepRunner</c> programs the next point's range and then immediately configures its
    /// tone, and that configure books <c>StimulusLatencyFrames + CaptureDelayFrames</c>. A bare
    /// <see cref="Flush"/> arriving afterwards ran <c>SampleRingBuffer.Reset(0)</c>, which
    /// <i>overwrites</i> the counter, so the discard was deleted and nothing re-booked it: the
    /// point's averages began at the tone step instead of a capture delay later.</para>
    ///
    /// <para>Measured on an EL84 amplifier, 2026-09-29, a 122-point THD-vs-frequency sweep at 0 dBV
    /// (a 6 dBV input range leaves 1 dB of margin, so the hardware step-up backstop trips on
    /// ordinary transients). Both points that followed a range change were wrecked and no others
    /// were: 404.3 Hz read THD 0.0374 % and SNR 34.1 dB against 0.00007 % and 109 dB either side of
    /// it, 1160.2 Hz read 0.0617 % and 31.4 dB, and both showed the fundamental a few tenths of a
    /// dB low — the amplifier's settling transient sitting at the front of the first averaged
    /// record, which the configured 300 ms capture delay existed to exclude.</para>
    /// </summary>
    public void FlushAfterRangeStep() => FlushCore(0, keepPendingDiscard: true);

    private void FlushCore(int discardFrames, bool keepPendingDiscard)
    {
        // Before anything else, so a tick already running is guaranteed to see the new value by the
        // time it reaches its commit check.
        Interlocked.Increment(ref _flushGeneration);

        var ringBuffers = _ringBuffers;

        // Reset the buffers as one set, for the same reason Enqueue appends as one and ProcessTick
        // snapshots as one (see _sampleSetLock): a snapshot that caught this loop half-done would
        // pair a freshly-emptied channel against one still holding the previous epoch's audio.
        lock (_sampleSetLock)
        {
            for (int ch = 0; ch < MaxChannels; ch++)
                ringBuffers[ch]?.Reset(discardFrames, keepPendingDiscard);
        }

        for (int ch = 0; ch < MaxChannels; ch++)
        {
            _ampAveragers[ch].Reset();
            _psdAveragers[ch].Reset();
            Volatile.Write(ref _singleShotHeld[ch], false);
            // Reset() restarts SamplesWritten at 0, so a value carried over from the previous epoch
            // would sit above it and stall the hop gate permanently.
            Volatile.Write(ref _lastAverageWritten[ch], 0);
        }

        lock (_retainedLock)
        {
            for (int ch = 0; ch < MaxChannels; ch++)
                _retained[ch] = new RetainedSpectrum();
        }

        // The records either side of a flush are not comparable -- a new stimulus, range or rate is
        // exactly the "the floor moved" the trigger looks for. Without this every generator change
        // would write a capture.
        diagnostics?.ResetBaseline();
    }

    /// <summary>
    /// True once every channel this pipeline is processing has finished its
    /// <see cref="SpectrumPipelineOptions.SingleShot"/> capture and held — the server-side
    /// equivalent of a client watching for <see cref="StreamFlags.Held"/> on the meters frame.
    ///
    /// This is what <c>ThdSweepRunner</c> awaits between sweep points: it needs "the averages for
    /// this frequency are complete" without being a WebSocket client of its own. Always false while
    /// <c>SingleShot</c> is off (a free-running pipeline never completes), and cleared by
    /// <see cref="Flush"/> — so the arming sequence is "set SingleShot, then Flush", and this stays
    /// false until the capture that flush armed is done.
    ///
    /// A channel that is not processed cannot hold and is skipped, matching
    /// <see cref="ProcessTick"/>; if neither channel is processed there is nothing to wait for and
    /// this is false rather than vacuously true, so a misconfigured sweep times out loudly instead
    /// of silently recording empty points.
    /// </summary>
    public bool SingleShotComplete
    {
        get
        {
            var options = _options;
            if (!options.SingleShot)
                return false;

            bool any = false;
            for (int ch = 0; ch < _channelCount; ch++)
            {
                bool enabled = ch == 0 ? options.ProcessLeft : options.ProcessRight;
                if (!enabled)
                    continue;

                any = true;
                if (!Volatile.Read(ref _singleShotHeld[ch]))
                    return false;
            }

            return any;
        }
    }

    /// <summary>
    /// The last full-resolution spectrum retained per channel, for a "save current capture"
    /// action -- deliberately unrelated to the ~1600-column decimated display frames this
    /// pipeline broadcasts. Empty before the first tick or right after <see cref="Flush"/>; a
    /// channel with <see cref="SpectrumPipelineOptions.ProcessLeft"/>/<c>ProcessRight</c> off is
    /// simply absent, same as it is from the live broadcast.
    ///
    /// Gated only on the trace itself (<c>AmpPower</c>), not on <c>Meters</c> -- a tick that found
    /// no fundamental in band still produced a perfectly good spectrum (see
    /// <see cref="SpectrumSnapshot.Meters"/>), and requiring Meters here used to make "Save" fail
    /// with "No spectrum has been captured yet." even while a trace was clearly on screen.
    ///
    /// Deliberately not called on every tick's hot path: it converts the retained peak-volts^2
    /// trace to dBV here, on the caller's thread, rather than in <see cref="ProcessChannel"/> --
    /// see <see cref="_retained"/>.
    /// </summary>
    public SpectrumSnapshot[] LatestSnapshots()
    {
        lock (_retainedLock)
        {
            List<SpectrumSnapshot>? result = null;

            for (int ch = 0; ch < MaxChannels; ch++)
            {
                var retained = _retained[ch];
                if (retained.AmpPower is null)
                    continue;

                var dbv = new double[retained.AmpPower.Length];
                for (int i = 0; i < dbv.Length; i++)
                    dbv[i] = 10.0 * Math.Log10(Math.Max(retained.AmpPower[i] / 2.0, double.Epsilon));

                (result ??= []).Add(new SpectrumSnapshot(
                    ch == 0 ? StreamChannel.Left : StreamChannel.Right, retained.Seq, retained.CapturedUtc,
                    retained.SampleRateHz, retained.BinSizeHz, retained.FftSize,
                    retained.Window, retained.Sums, dbv, retained.Meters, retained.PreviewFft, retained.PreviewScope,
                    retained.Complex, retained.Noise, retained.Weighting));
            }

            return result?.ToArray() ?? [];
        }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var worker = _worker;
        _cts = null;
        _worker = null;

        if (cts is null)
            return;

        cts.Cancel();
        try
        {
            if (worker is not null)
                await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        finally
        {
            cts.Dispose();
        }

        // The transport's own drop counter, which until now reached the status bar and nothing else
        // -- so a log file sent in could not say that a tick had been torn apart on the way to the
        // browser. That is the other half of the oscilloscope's "flickering, and it says no
        // fundamental to remove": one tick's frames are a set the client pairs by seq, and an
        // eviction loses a member of that set while the measurement itself stays sound. Max and
        // total, because one wedged tab says nothing about the others, and at the end of the
        // acquisition the numbers describe, since they are reset by Start.
        var (droppedMax, droppedTotal) = hub.DroppedFrames();
        if (droppedTotal > 0)
        {
            logger.LogWarning(
                "Display frames evicted this acquisition: {Max} from the worst client, {Total} over all clients. "
                    + "The measurement is unaffected -- this is the WebSocket queue, not the sample stream -- but a "
                    + "torn tick can leave the oscilloscope or a metrics table showing a neighbouring tick's data",
                droppedMax, droppedTotal);
        }
    }

    /// <summary>
    /// Feeds one acquisition block's samples into the per-channel ring buffers. Takes ownership
    /// of <paramref name="block"/> — disposes it before returning, per <c>PcmBlock</c>'s contract.
    /// </summary>
    public void Enqueue(PcmBlock block)
    {
        using (block)
        {
            EnsureRingBuffers(_options.FftSize);

            var ringBuffers = _ringBuffers;
            int channelCount = Math.Min(block.ChannelCount, MaxChannels);

            // One lock around the whole set, so a snapshot never sees this block in some channels'
            // buffers and not others -- see _sampleSetLock. Taken after EnsureRingBuffers, which
            // needs _ringBufferLock and must not nest inside this one.
            lock (_sampleSetLock)
            {
                for (int ch = 0; ch < channelCount; ch++)
                {
                    // The external input gain is undone here and nowhere else: everything past
                    // this line reads the DUT's volts. What input ranging needs -- the analyzer's
                    // own volts -- is put back on SpectrumMeters.AnalyzerPeakDbv, see ComputeMeters.
                    double scale = externalGain?.LinearScale(ch) ?? 1.0;
                    ringBuffers[ch]?.AppendInterleaved(block.Samples, ch, block.ChannelCount, scale);
                }
            }
        }
    }

    private void EnsureRingBuffers(int fftSize)
    {
        if (_ringBufferFftSize == fftSize)
            return;

        lock (_ringBufferLock)
        {
            if (_ringBufferFftSize == fftSize)
                return;

            var previous = _ringBuffers;
            var next = new SampleRingBuffer?[MaxChannels];
            for (int ch = 0; ch < MaxChannels; ch++)
            {
                next[ch] = new SampleRingBuffer(fftSize);

                // A fresh buffer starts with no discard booked, so a caller that changed FFT size
                // and stimulus together would lose the stimulus-latency discard the flush had just
                // set and refill this buffer with pre-change audio -- the very step the discard
                // exists to keep out of the first record. Carrying it across makes the resize safe
                // whichever order the two land in, which matters because the resize happens here on
                // the acquisition thread while the flush happens on the caller's.
                int pendingDiscard = previous[ch]?.DiscardRemaining ?? 0;
                if (pendingDiscard > 0)
                    next[ch]!.Reset(pendingDiscard);

                // SamplesWritten restarts at 0 on the replacement buffer -- see Flush.
                Volatile.Write(ref _lastAverageWritten[ch], 0);
            }

            _ringBuffers = next;
            _ringBufferFftSize = fftSize;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Re-read the period every iteration so a live TargetFrameRateHz change takes effect
            // without restarting the pipeline.
            while (!ct.IsCancellationRequested)
            {
                var period = TimeSpan.FromSeconds(1.0 / Math.Max(1, _options.TargetFrameRateHz));
                using var timer = new PeriodicTimer(period);
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    break;

                try
                {
                    ProcessTick();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Spectrum pipeline tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
    }

    private void ProcessTick()
    {
        var options = _options;
        var ringBuffers = _ringBuffers;
        int n = options.FftSize;

        // Snapshot every eligible channel's ring-buffer tail in one pass, before any channel's FFT
        // runs -- ProcessChannel used to call SnapshotLatest itself, right before analyzing that
        // channel, and analysis (window + FFT + averaging) is easily slow enough that by the time
        // channel 1's snapshot happened, its buffer had already advanced past where channel 0's
        // snapshot ended -- by an amount that varies tick to tick with however long channel 0 took.
        // Two channels analyzed off windows taken at different instants no longer describe the same
        // moment: a stimulus that is genuinely phase-locked across L and R showed a relative phase in
        // the oscilloscope that drifted from frame to frame, because the chart indexes both channels'
        // buffers by the same offset from L's trigger, and that offset only means the same instant in
        // both when the two snapshots were actually taken together.
        //
        // Under _sampleSetLock, so this pass is atomic against Enqueue's own per-channel append loop
        // as well -- adjacent snapshot calls are not enough on their own, see that field.
        // The flush generation every channel of this tick belongs to, read before any channel's
        // window is copied out. ProcessChannel used to read it at its own start, after the
        // snapshots below -- so a flush landing between channel 0's check and channel 1's start
        // discarded the left record and committed the right one, both copied from the same
        // pre-flush audio: a held right channel one record older than the left it was paired with,
        // seen as an L/R seq mismatch on a frequency-response probe capture.
        long generation = Interlocked.Read(ref _flushGeneration);

        var pending = new (double[] TimeBuf, long WrittenAtSnapshot)?[_channelCount];
        lock (_sampleSetLock)
        {
            for (int ch = 0; ch < _channelCount; ch++)
            {
                bool enabled = ch == 0 ? options.ProcessLeft : options.ProcessRight;
                if (!enabled)
                    continue;

                if (options.SingleShot && Volatile.Read(ref _singleShotHeld[ch]))
                    continue;

                var rb = ringBuffers[ch];
                if (rb is null || rb.Capacity != n || !rb.IsFull)
                    continue;

                // Checked here rather than inside ProcessChannel so a tick that has nothing new to
                // add skips the FFT entirely instead of computing one and throwing it away.
                if (!HasFreshSignal(ch, rb, options))
                    continue;

                double[] timeBuf = ArrayPool<double>.Shared.Rent(n);
                long writtenAtSnapshot = rb.SamplesWritten;
                rb.SnapshotLatest(timeBuf.AsSpan(0, n));
                pending[ch] = (timeBuf, writtenAtSnapshot);
            }
        }

        // One sequence number for the whole tick, shared by every channel analyzed from the snapshot
        // set above -- it identifies the *record*, and after the change above both channels' records
        // genuinely describe one instant. Allocating it per broadcast instead (the old
        // `Interlocked.Increment` inside ProcessChannel) gave L and R of the same tick consecutive
        // but different numbers, which left a client no way to tell a matched L/R pair from a stale
        // one; see useSpectrumStream.ts, where the oscilloscope's two traces are re-paired by it.
        // Sweep point frames already use the header's seq this way (both channels of one frequency
        // share the point index), so this is the same convention rather than a new one.
        //
        // Only advanced when the tick actually has something to analyze, so an idle pipeline does not
        // burn sequence numbers.
        bool anyPending = false;
        for (int ch = 0; ch < _channelCount; ch++)
            anyPending |= pending[ch] is not null;
        if (!anyPending)
            return;

        uint seq = Interlocked.Increment(ref _seq);

        for (int ch = 0; ch < _channelCount; ch++)
        {
            var p = pending[ch];
            if (p is null)
                continue;

            try
            {
                ProcessChannel(ch, options, p.Value.TimeBuf, p.Value.WrittenAtSnapshot, seq, generation);
            }
            finally
            {
                ArrayPool<double>.Shared.Return(p.Value.TimeBuf);
            }
        }
    }

    /// <summary>
    /// The least fresh signal, as a fraction of one record, that a <em>free-running</em> channel is
    /// re-analyzed for — a floor under <see cref="SpectrumPipelineOptions.AverageHopFraction"/>, and
    /// the only thing that ties the live display's cost to the record length rather than to the
    /// tick rate.
    ///
    /// <para><b>Why.</b> <see cref="SpectrumPipelineOptions.TargetFrameRateHz"/> paces the worker at
    /// 20 Hz whatever <see cref="SpectrumPipelineOptions.FftSize"/> is, so at a long record almost
    /// every tick re-transformed almost the same samples. Measured against the simulator at
    /// 192 kHz, both channels, on this machine: 1,048,576 points ran <b>10.5 FFTs per record</b>,
    /// each carrying <b>9.6 % new signal</b>, for <b>169 % of one core</b> — and it never reached
    /// 20 Hz anyway (1.92, against 8.0 at 262,144 and 12.8 at 65,536), because past about 65,536
    /// points the worker is simply saturated and the achieved frame rate is a property of the
    /// machine rather than of the setting.</para>
    ///
    /// <para><b>And the fresh samples were not worth transforming for.</b> They arrive at the
    /// <em>end</em> of the record, which is exactly where a taper has thrown them away: under a
    /// Hann window the newest 9.6 % of a record carries <b>0.041 %</b> of the window's power, so
    /// those ten extra transforms per record answered the same question to four decimal places.
    /// The weight climbs very steeply — 0.15 % at a hop of 1/8, 1.4 % at 1/5, <b>3.8 % at 1/4</b>,
    /// 12.7 % at 1/3 — and a quarter is the first of them at which a tick shows the user something
    /// a taper has not already suppressed. It still leaves four updates per record, so a long
    /// record's display moves several times while it fills rather than appearing to stall.</para>
    ///
    /// <para>It cannot bind on a short record, which is the case the 20 Hz tick exists for: at
    /// 4096 points and 192 kHz a tick already brings about three whole records of new signal.
    /// At 192 kHz it starts to bind above roughly 48,000 points, and at 48 kHz above 12,000.
    /// Measured on a QA403 at 192 kHz with both channels, before and after: 4096 points 15.9
    /// updates/s either way, 65,536 12.8 -> 9.9, 262,144 8.0 -> 2.75, 1,048,576 1.92 -> 0.75, with
    /// CPU falling from 169 % of one core to 59 % (85 % over real USB) and no dropped chunks or
    /// blocks.</para>
    ///
    /// <para><b>It slows only the free-running redraw, never the response to anything the user
    /// does.</b> Every stimulus change flushes, and <see cref="Flush"/> zeroes
    /// <c>_lastAverageWritten</c>, so the first full record after any generator, range, FFT-size or
    /// start change is analyzed the moment it exists — the first-analysis rule below. What the
    /// floor removes is the redraw of a window the user has not touched, which is the only case
    /// where the answer was already known. Verified on hardware: a SINGLE capture at 1,048,576
    /// points still holds after 6.14 s, i.e. one 5.46 s record plus the 300 ms capture delay.</para>
    /// </summary>
    private const double DisplayHopFloor = 0.25;

    /// <summary>
    /// Whether enough new signal has arrived since this channel's last committed record to justify
    /// analyzing another, per <see cref="SpectrumPipelineOptions.AverageHopFraction"/> and, for a
    /// free-running channel, <see cref="DisplayHopFloor"/>.
    ///
    /// <para>Always true for the first analysis of an epoch — <c>IsFull</c> has already established
    /// that a complete, post-flush record exists, and there is no previous record for it to overlap
    /// with.</para>
    ///
    /// <para>The floor is applied only when <see cref="SpectrumPipelineOptions.SingleShot"/> is
    /// off, and that is what keeps it off every measurement path: <em>every</em> runner sets
    /// SingleShot, so the only free-running consumer of this pipeline is the live FFT screen. The
    /// four runners that pin the hop to 0 (frequency response, impedance twice, impulse response)
    /// also set <c>AverageCount = 1</c>, so they take exactly one record per flush and the
    /// first-analysis rule above already lets it straight through — but gating on SingleShot says
    /// so outright rather than leaving a new runner to discover it.</para>
    ///
    /// <para>Note the floor moves the live screen's <em>averaging</em> as well, and in the
    /// direction <see cref="SpectrumPipelineOptions.AverageHopFraction"/>'s own remarks ask for: at
    /// a long record, tick-paced averages overlapped so heavily that eight of them spanned under
    /// two records of independent signal. Eight now span 2.75 and take proportionally longer on the
    /// clock, which is the honest price of an average that means something.</para>
    /// </summary>
    private bool HasFreshSignal(int channelIndex, SampleRingBuffer rb, SpectrumPipelineOptions options)
    {
        double hop = EffectiveHopFraction(options);
        if (hop <= 0.0)
            return true;

        long last = Volatile.Read(ref _lastAverageWritten[channelIndex]);
        if (last <= 0)
            return true;

        long needed = (long)Math.Ceiling(options.FftSize * Math.Min(hop, 1.0));
        return rb.SamplesWritten - last >= needed;
    }

    /// <summary>
    /// The hop <see cref="HasFreshSignal"/> actually enforces: the caller's
    /// <see cref="SpectrumPipelineOptions.AverageHopFraction"/>, raised to
    /// <see cref="DisplayHopFloor"/> for a free-running channel. Separated out so the rule can be
    /// asserted without timing a pipeline.
    /// </summary>
    internal static double EffectiveHopFraction(SpectrumPipelineOptions options) =>
        options.SingleShot ? options.AverageHopFraction : Math.Max(options.AverageHopFraction, DisplayHopFloor);

    /// <summary>
    /// Analyzes a window already captured by <see cref="ProcessTick"/> -- <paramref name="timeBuf"/>
    /// and <paramref name="writtenAtSnapshot"/> come from that channel's <see cref="SampleRingBuffer.SnapshotLatest"/>,
    /// taken with every other channel's before any channel's FFT ran, so that a multi-channel record
    /// actually describes one instant rather than one per channel. <paramref name="seq"/> identifies
    /// that record and is shared by every channel of the tick. <paramref name="generation"/> is
    /// the flush generation the snapshot was taken under: everything below describes the signal as
    /// it was then, and if a Flush lands before the commit, that description is of the *old*
    /// stimulus and must not be committed. See the check before the commit below.
    /// </summary>
    private void ProcessChannel(int channelIndex, SpectrumPipelineOptions options, double[] timeBuf, long writtenAtSnapshot, uint seq, long generation)
    {
        int n = options.FftSize;
        int specLen = (n / 2) + 1;

        double[] windowed = ArrayPool<double>.Shared.Rent(n);
        Complex[] spec = ArrayPool<Complex>.Shared.Rent(specLen);
        double[] ampBuf = ArrayPool<double>.Shared.Rent(specLen);
        double[] psdBuf = ArrayPool<double>.Shared.Rent(specLen);
        double[] psdWeightedBuf = ArrayPool<double>.Shared.Rent(specLen);
        try
        {
            var window = Window.Get(options.Window, n);
            Window.Apply(timeBuf.AsSpan(0, n), window, windowed.AsSpan(0, n));

            var specSpan = spec.AsSpan(0, specLen);
            _fft.Forward(windowed.AsSpan(0, n), specSpan);

            var amp = AmplitudeSpectrum.Compute(specSpan, window.Sums, ampBuf.AsSpan(0, specLen));
            var psdInstant = PsdSpectrum.Compute(specSpan, _sampleRateHz, window.Sums, psdBuf.AsSpan(0, specLen));
            double binSizeHz = psdInstant.BinSizeHz;

            // Average in power domain: square the amplitude-domain trace before averaging (never
            // average dBV directly, and never average across the two different normalisations).
            Span<double> ampPower = ampBuf.AsSpan(0, specLen);
            for (int i = 0; i < specLen; i++)
                ampPower[i] *= ampPower[i];
            _ampAveragers[channelIndex].Update(ampPower, options.Averaging, options.AverageCount);

            Span<double> psdValues = psdBuf.AsSpan(0, specLen);
            _psdAveragers[channelIndex].Update(psdValues, options.Averaging, options.AverageCount);
            var psd = PsdSpectrum.FromValues(psdValues, _sampleRateHz, binSizeHz, window.Sums);

            // Weighting, applied *after* the averagers. Both are per-bin linear so they commute,
            // and keeping the averagers' history unweighted means a toggle takes effect on the next
            // tick without ever blending weighted and unweighted records. The display trace
            // (ampPower, which is also what the retained snapshot saves and the sweep preview
            // reads) is weighted in place; the PSD is weighted into a *copy*, because only the
            // noise integrals may read the weighted one -- the fundamental, the harmonics, the
            // other-peaks list and IMD stay on `psd` so a weighting changes what "noise" means and
            // nothing else. See SpectrumPipelineOptions.Weighting.
            var psdWeighted = psd;
            ReadOnlySpan<double> powerGain = ResolvePowerGain(options, specLen, binSizeHz);
            if (!powerGain.IsEmpty)
            {
                for (int i = 0; i < specLen; i++)
                    ampPower[i] *= powerGain[i];
                var weightedValues = psdWeightedBuf.AsSpan(0, specLen);
                for (int i = 0; i < specLen; i++)
                    weightedValues[i] = psdValues[i] * powerGain[i];
                psdWeighted = PsdSpectrum.FromValues(weightedValues, _sampleRateHz, binSizeHz, window.Sums);
            }

            var channel = channelIndex == 0 ? StreamChannel.Left : StreamChannel.Right;

            // Either averager reports the same count (both Updated together, just above, with the
            // same mode/count) — _psdAveragers is the arbitrary pick.
            int averagesTaken = _psdAveragers[channelIndex].Count;

            // Off has no "reached count" signal of its own (SpectrumAverager.Count stays 0 when
            // averaging is off), so a single shot without averaging just holds after this one tick.
            // A count of 1 is "off" by the averager's own definition too (Update treats count <= 1
            // as Off and leaves Count at 0), so it has to be tested here the same way: against
            // `averagesTaken >= AverageCount` it was 0 >= 1 forever, and a Single with one average
            // never held -- the FFT screen kept running until Stop.
            bool notAveraging = options.Averaging == AveragingMode.Off || options.AverageCount <= 1;
            bool singleShotComplete = options.SingleShot && (notAveraging || averagesTaken >= options.AverageCount);

            // A count of 1 still records one spectrum, and the "Linear x1 (1/1)" readout should say
            // so rather than repeat the averager's 0 -- Off has no readout to feed.
            if (notAveraging && options.Averaging != AveragingMode.Off)
                averagesTaken = 1;

            // A Flush while this tick was running means the samples it just analyzed predate a
            // stimulus change (a new generator frequency, a new range, a restart). Committing them
            // would re-arm `held` and overwrite the retained snapshot with pre-change audio — which
            // is exactly what made a THD-vs-frequency sweep report its first point's spectrum at
            // every subsequent frequency, and made the flush's own in-flight-discard pointless.
            // Drop the record instead; the next tick reads a window that is entirely post-change.
            //
            // Ahead of the `held` write below, not after it: a discarded record must leave no trace
            // at all, and holding the channel on one would stop the pipeline from ever producing the
            // fresh record that replaces it.
            if (Interlocked.Read(ref _flushGeneration) != generation)
                return;

            // Past the discard check, so this record is genuinely part of the average -- advance the
            // hop clock only now, or a dropped record would push the next accepted one a full hop
            // further out for no reason.
            Volatile.Write(ref _lastAverageWritten[channelIndex], writtenAtSnapshot);

            (float peakDbv, float totalRmsDbv) = ComputeTimeDomainLevels(timeBuf.AsSpan(0, n));
            var meters = ComputeMeters(psd, psdWeighted, peakDbv, totalRmsDbv, options, averagesTaken,
                (float)(externalGain?.GainDb(channelIndex) ?? 0.0));
            var noise = ComputeNoiseFigures(psdWeighted, meters, window.Sums.EnbwHz(_sampleRateHz), options);

            // Past the flush-generation check above, so a record that gets discarded is never logged
            // as though it had been displayed -- the log has to describe what the user actually saw.
            // No-ops unless diagnostics have been armed; see DiagnosticRecorder for the cost.
            diagnostics?.Observe(channelIndex, seq, timeBuf.AsSpan(0, n), _sampleRateHz, meters, writtenAtSnapshot);

            // Only computed for a sweep (see SpectrumPipelineOptions.CapturePreview) -- the ~2400x
            // more expensive full-resolution retained trace already exists for the live view, and a
            // second decimation of it on every one of ~20 ticks/sec would be pure waste there.
            SweepPreviewSeries? previewFft = null;
            SweepPreviewSeries? previewScope = null;
            if (options.CapturePreview)
            {
                previewFft = BuildFftPreview(ampPower, binSizeHz, options.DisplayLoFreqHz);
                previewScope = BuildScopePreview(timeBuf.AsSpan(0, n), _sampleRateHz, meters?.FundamentalFreqHz);
            }

            // The frequency-response measurement needs phase, which AmplitudeSpectrum throws away.
            // Off by default so the live path is untouched: on at ~20 ticks/sec this would copy a
            // 65537-element Complex array per channel per tick for nobody. Note the spectrum is of
            // the *windowed* signal, so a caller doing anything with the phase wants
            // WindowType.Rectangular -- which is also the correct window for a stimulus that is
            // exactly periodic in the record. See FrequencyResponseRunner.
            var retainedComplex = options.RetainComplexSpectrum
                ? specSpan
                : ReadOnlySpan<Complex>.Empty;

            UpdateRetainedSnapshot(
                channelIndex, ampPower, binSizeHz, n, options.Window, window.Sums, meters, noise, options.Weighting,
                seq, previewFft, previewScope, retainedComplex);

            // Only now, once the trace this signal refers to is actually retained. `held` is the
            // "your capture is ready" flag SingleShotComplete exposes, and ThdSweepRunner reads
            // LatestSnapshots() the moment it sees it — setting it before UpdateRetainedSnapshot
            // left a window where the capture was complete but the snapshot was still empty (or
            // still the previous point's), which showed up as a sweep point silently going missing.
            if (singleShotComplete)
                Volatile.Write(ref _singleShotHeld[channelIndex], true);

            // Ahead of the spectrum frame it describes: the client stamps each spectrum frame with
            // the ENBW of the most recent noise frame, which is what converts it to dBV/sqrt(Hz).
            hub.Broadcast(StreamFrame.EncodeNoiseFigures(channel, seq, noise, singleShotComplete));
            BroadcastSpectrumFrame(channel, seq, ampPower, binSizeHz, options);
            if (meters is not null)
            {
                BroadcastMetersFrame(channel, seq, meters, singleShotComplete);
                // Its own frame rather than a meters-frame extension, so the meters layout (and
                // every client that decodes it) is untouched when no IMD is configured.
                if (meters.Intermodulation is { } intermodulation)
                    hub.Broadcast(StreamFrame.EncodeIntermodulation(channel, seq, intermodulation, singleShotComplete));
                else if (options.Intermodulation is not null)
                    hub.Broadcast(StreamFrame.EncodeIntermodulationUnresolved(channel, seq));
                if (meters.Multitone is { } multitone)
                    hub.Broadcast(StreamFrame.EncodeMultitone(channel, seq, multitone, singleShotComplete));
            }
            // The sine fit is taken here, *ahead* of the raw scope frame, although its own frame is
            // sent after it: the client pairs the two by seq and closes the group on a 40 ms idle,
            // so anything slow standing between the two sends splits them -- and the fit is the
            // slow half by three orders of magnitude (ResidualFitMaxSamples has the numbers). With
            // the fit already taken, all that remains between the frames is a 2048-sample subtract.
            // Measured on a QA403 at 1,048,576 points and 192 kHz, 1 kHz snapped: the residual
            // arrived 403-700 ms after its raw frame with the fit between the sends, and in the
            // same millisecond with the fit ahead of them. Wire order is unchanged: raw first, so
            // the client still has something to trigger on.
            double fundamentalFreqHz = meters?.FundamentalFreqHz ?? 0.0;
            SineFitResult? residualFit = null;
            int residualFitCount = 0;
            if (options.ScopeResidual && fundamentalFreqHz > 0.0 && fundamentalFreqHz < _sampleRateHz / 2.0)
            {
                residualFitCount = ResidualFitCount(n, _sampleRateHz, fundamentalFreqHz);
                residualFit = SineFit.Fit(timeBuf.AsSpan(n - residualFitCount, residualFitCount), _sampleRateHz, fundamentalFreqHz);
            }

            BroadcastScopeFrame(channel, seq, timeBuf.AsSpan(0, n), _sampleRateHz);
            if (residualFit is { } fit)
                BroadcastScopeResidualFrame(channel, seq, timeBuf.AsSpan(n - residualFitCount, residualFitCount), _sampleRateHz, fit);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(windowed);
            ArrayPool<Complex>.Shared.Return(spec);
            ArrayPool<double>.Shared.Return(ampBuf);
            ArrayPool<double>.Shared.Return(psdBuf);
            ArrayPool<double>.Shared.Return(psdWeightedBuf);
        }
    }

    /// <summary>
    /// The per-bin power gain for the weighting in effect, or empty when nothing weighs anything
    /// (no standard and no populated user slot) -- so the unweighted path costs nothing at all.
    /// A user slot that is checked but empty resolves to flat, which the panel says out loud.
    /// </summary>
    private ReadOnlySpan<double> ResolvePowerGain(SpectrumPipelineOptions options, int specLen, double binSizeHz)
    {
        var spec = options.Weighting;
        int userVersion = userWeighting?.Version ?? 0;
        var key = (spec, userVersion, specLen, binSizeHz);
        if (_weightingKey == key)
            return _weightingPowerGain is null ? ReadOnlySpan<double>.Empty : _weightingPowerGain.AsSpan(0, specLen);

        var curve = BuildCurve(spec, userWeighting);
        _weightingKey = key;
        if (curve is null)
        {
            _weightingPowerGain = null;
            return ReadOnlySpan<double>.Empty;
        }

        if (_weightingPowerGain is null || _weightingPowerGain.Length != specLen)
            _weightingPowerGain = new double[specLen];
        WeightingTable.FillPowerGains(curve, binSizeHz, _weightingPowerGain);
        return _weightingPowerGain;
    }

    /// <summary>The combined curve for <paramref name="spec"/>, or null when it weighs nothing.</summary>
    internal static IWeightingCurve? BuildCurve(WeightingSpec spec, UserWeightingSetting? userWeighting)
    {
        var parts = new List<IWeightingCurve>(3);
        if (spec.Standard != WeightingStandard.None)
            parts.Add(StandardWeighting.For(spec.Standard));
        if (spec.User1 && userWeighting?.CurveFor(1) is { } user1)
            parts.Add(user1);
        if (spec.User2 && userWeighting?.CurveFor(2) is { } user2)
            parts.Add(user2);

        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => new CompositeWeighting([.. parts]),
        };
    }

    private static string DescribeWeighting(WeightingSpec spec)
    {
        if (spec.IsNone)
            return "none";
        var parts = new List<string>(3);
        if (spec.Standard != WeightingStandard.None)
            parts.Add(spec.Standard.ToString());
        if (spec.User1)
            parts.Add("User1");
        if (spec.User2)
            parts.Add("User2");
        return string.Join("+", parts);
    }

    /// <summary>
    /// How much narrower than the full span a client's zoom window has to be before it is worth
    /// encoding a separate, denser frame for it.
    ///
    /// Measured on the log axis, because that is what the column budget is spread over: a window
    /// covering 1/4 of the span's decades receives 1/4 of the overview's columns, so a dedicated
    /// frame gives it 4x the resolution. Below this the gain does not justify a second decimation
    /// and encode per tick per distinct window — and the overwhelmingly common case (nobody
    /// zoomed) then costs exactly what it did before this existed: one decimation, one broadcast.
    /// </summary>
    private const double ZoomedFrameMinFactor = 4.0;

    private void BroadcastSpectrumFrame(StreamChannel channel, uint seq, ReadOnlySpan<double> ampPower, double binSizeHz, SpectrumPipelineOptions options)
    {
        var clients = hub.ClientViewports();
        if (clients.Length == 0)
            return;

        int specLen = ampPower.Length;
        int overviewLoBin = Math.Clamp((int)Math.Round(options.DisplayLoFreqHz / binSizeHz), 1, specLen - 2);
        int overviewHiBin = specLen - 1;
        double fullDecades = LogSpan(overviewLoBin, overviewHiBin);

        // Group the zoomed clients by window so N clients looking at the same range cost one
        // encode, not N. Everyone else shares the single overview frame.
        Dictionary<SpectrumViewport, List<Guid>>? zoomed = null;
        List<Guid>? overview = null;

        foreach (var (id, viewport) in clients)
        {
            if (TryResolveZoom(viewport, binSizeHz, specLen, overviewLoBin, fullDecades, out _, out _))
            {
                zoomed ??= [];
                if (!zoomed.TryGetValue(viewport!, out var ids))
                    zoomed[viewport!] = ids = [];
                ids.Add(id);
            }
            else
            {
                (overview ??= []).Add(id);
            }
        }

        if (overview is not null)
        {
            byte[] frame = BuildSpectrumFrame(
                channel, seq, ampPower, binSizeHz, overviewLoBin, overviewHiBin, options.DisplayColumns, zoomed: false);
            foreach (var id in overview)
                hub.Send(id, frame);
        }

        if (zoomed is null)
            return;

        foreach (var (viewport, ids) in zoomed)
        {
            TryResolveZoom(viewport, binSizeHz, specLen, overviewLoBin, fullDecades, out int loBin, out int hiBin);
            byte[] frame = BuildSpectrumFrame(
                channel, seq, ampPower, binSizeHz, loBin, hiBin, options.DisplayColumns, zoomed: true);
            foreach (var id in ids)
                hub.Send(id, frame);
        }
    }

    private static double LogSpan(int loBin, int hiBin) =>
        loBin >= 1 && hiBin > loBin ? Math.Log((double)hiBin / loBin) : 0.0;

    /// <summary>
    /// Turns a client's requested window into a bin range, and decides whether it is zoomed in far
    /// enough to earn its own frame. Returns false (and leaves the bins unset) for no viewport, a
    /// degenerate one, or anything under <see cref="ZoomedFrameMinFactor"/>.
    /// </summary>
    private static bool TryResolveZoom(
        SpectrumViewport? viewport, double binSizeHz, int specLen, int overviewLoBin, double fullDecades,
        out int loBin, out int hiBin)
    {
        loBin = 0;
        hiBin = 0;

        if (viewport is null || fullDecades <= 0.0)
            return false;

        loBin = Math.Clamp((int)Math.Floor(viewport.LoFreqHz / binSizeHz), overviewLoBin, specLen - 2);
        hiBin = Math.Clamp((int)Math.Ceiling(viewport.HiFreqHz / binSizeHz), loBin + 1, specLen - 1);

        return LogSpan(loBin, hiBin) * ZoomedFrameMinFactor <= fullDecades;
    }

    private static byte[] BuildSpectrumFrame(
        StreamChannel channel, uint seq, ReadOnlySpan<double> ampPower, double binSizeHz,
        int loBin, int hiBin, int maxColumns, bool zoomed)
    {
        // Never more columns than there are bins to put in them: past that point extra columns just
        // repeat single bins and cost bandwidth for nothing.
        int columnCount = Math.Clamp(hiBin - loBin + 1, 1, maxColumns);

        var columns = new MinMax[columnCount];
        var centerBins = new double[columnCount];
        // DecimateLogWithCenters (not the plain DecimateLog + a client-side reconstruction
        // formula) because the column-to-bin mapping is only a smooth log curve when there are
        // many more source bins than columns; for a small FFT decimated to ~1600 columns, most
        // low-frequency columns collapse to single adjacent bins and a formula-based frequency
        // would be wrong by orders of magnitude. The actual center bin this call used is the only
        // thing safe to send as "this column's frequency".
        //
        // Slicing at hiBin is what makes a zoomed frame work: the decimator spreads its columns
        // across [loBin, end-of-span], so a shorter span is a denser one for the same budget.
        SpectrumDecimator.DecimateLogWithCenters(ampPower[..(hiBin + 1)], columns, centerBins, loBin);

        var columnFreqsHz = new float[columnCount];

        // ampPower is the square of AmplitudeSpectrum.PeakVoltsPerBin, i.e. volts *peak* squared —
        // so dBV = 10*log10(power / 2), the /2 being (Vpk/sqrt2)^2. Dropping it put every value on
        // this chart, trace and noise floor alike, exactly 3.01 dB high, and disagreeing by that
        // much with the meters frame (which reaches dBV through PsdSpectrum/HarmonicAnalyzer and
        // was the only path the tests checked). See AmplitudeSpectrum.VrmsAt/DbvAt, and the
        // golden-test convention in CLAUDE.md: 1 kHz at 1.0 V peak, Hann, reads -3.01 dBV.
        //
        // Converting only the ~2*columnCount decimated values, not all specLen bins, is why
        // decimation runs before this conversion rather than after.
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = new MinMax(
                10.0 * Math.Log10(Math.Max(columns[i].Min / 2.0, double.Epsilon)),
                10.0 * Math.Log10(Math.Max(columns[i].Max / 2.0, double.Epsilon)));
            columnFreqsHz[i] = (float)(centerBins[i] * binSizeHz);
        }

        return StreamFrame.EncodeSpectrum(
            channel, seq, (float)(loBin * binSizeHz), (float)(hiBin * binSizeHz),
            columnFreqsHz, columns, StreamMsgType.Spectrum, logSpaced: true, zoomed: zoomed);
    }

    /// <summary>
    /// True instantaneous peak and broadband RMS from the raw (unwindowed) time-domain snapshot —
    /// not spectral estimates. Peak is expressed as its RMS-equivalent (peakVpk/sqrt2), matching
    /// every other dBV value in this app's convention (see <c>SignalSynthesizer.DbvToPeakV</c>).
    ///
    /// The DC component is removed first, for the same reason the noise integrations exclude bin 0:
    /// a DC-coupled input's offset is not part of the signal being measured. Leaving it in added
    /// itself straight onto the peak — a 4.6 mV offset read as +0.03 dBV on a 1 V tone, which is
    /// why Pk Vrms sat above 1.0000 for a 1.0000 Vrms generator.
    /// </summary>
    private static (float PeakDbv, float TotalRmsDbv) ComputeTimeDomainLevels(ReadOnlySpan<double> timeDomainSamples)
    {
        double mean = 0.0;
        foreach (double v in timeDomainSamples)
            mean += v;
        mean /= timeDomainSamples.Length;

        double peakVpk = 0.0;
        double sumSquares = 0.0;
        foreach (double sample in timeDomainSamples)
        {
            double v = sample - mean;
            double a = Math.Abs(v);
            if (a > peakVpk)
                peakVpk = a;
            sumSquares += v * v;
        }

        double peakVrms = peakVpk / Math.Sqrt(2.0);
        double totalRmsVrms = Math.Sqrt(sumSquares / timeDomainSamples.Length);

        float peakDbv = (float)(20.0 * Math.Log10(Math.Max(peakVrms, double.Epsilon)));
        float totalRmsDbv = (float)(20.0 * Math.Log10(Math.Max(totalRmsVrms, double.Epsilon)));
        return (peakDbv, totalRmsDbv);
    }

    /// <summary>
    /// Everything the meters panel shows, computed once per channel per tick and shared by both
    /// the live broadcast (<see cref="BroadcastMetersFrame"/>) and <see cref="LatestSnapshots"/> --
    /// so a saved measurement's metrics are exactly what was on screen, never re-derived
    /// separately. Returns <see langword="null"/> when no fundamental (and so no harmonics) could
    /// be found, matching the old inline logic's "nothing to broadcast this tick" behaviour.
    /// </summary>
    private static SpectrumMeters? ComputeMeters(
        PsdSpectrum psd, PsdSpectrum psdWeighted, float peakDbv, float totalRmsDbv, SpectrumPipelineOptions options, int averagesTaken,
        float externalGainDb = 0f)
    {
        int minSearchBin = Math.Max(1, (int)Math.Round(options.FundamentalMinSearchHz / psd.BinSizeHz));
        int fundamentalBin = FindGlobalPeakBin(psd.ValuesV2PerHz, minSearchBin);
        if (fundamentalBin <= 0)
            return null;

        // The measurement bandwidth for every noise-integrated figure below, and the upper bound
        // on which harmonics count toward THD. See SpectrumPipelineOptions.MeasurementLoFreqHz —
        // passing this rather than the full spectrum is what keeps DC and out-of-band noise out
        // of THD+N/SNR.
        var band = psd.BandOf(options.MeasurementLoFreqHz, options.MeasurementHiFreqHz);

        // Harmonics may be counted past the measurement band -- see
        // SpectrumPipelineOptions.HarmonicMaxFreqHz. Everything else below stays on `band`.
        int harmonicMaxBin = options.HarmonicMaxFreqHz is { } harmonicMaxHz
            ? Math.Min(psd.ValuesV2PerHz.Length - 1, psd.BinOfFrequency(harmonicMaxHz))
            : band.HiBinInclusive;

        int harmonicCount = Math.Max(1, options.HarmonicCount);
        var harmonicsBuf = new Harmonic[harmonicCount];
        int written = HarmonicAnalyzer.Analyze(
            psd, fundamentalBin, harmonicCount, harmonicsBuf, maxBinInclusive: harmonicMaxBin);
        if (written == 0)
            return null;

        var harmonics = written == harmonicsBuf.Length ? harmonicsBuf : harmonicsBuf[..written];
        var fundamental = harmonics[0];

        float fundamentalFreqHz = (float)(fundamentalBin * psd.BinSizeHz);
        float thdPercent = (float)(SpectrumMetrics.Thd(harmonics) * 100.0);
        // The two noise-integrated figures read the *weighted* PSD; their reference, the fundamental,
        // was measured above on the unweighted one -- every standard curve is 0 dB at 1 kHz, but a
        // tone elsewhere must still be reported at its true level.
        float thdNPercent = (float)(SpectrumMetrics.ThdPlusN(psdWeighted, fundamental, band, options.NotchOctaves) * 100.0);
        float snrDb = (float)SpectrumMetrics.SnrDb(psdWeighted, harmonics, band, options.NotchOctaves);
        float fundamentalDbv = (float)fundamental.AmplitudeDbv;
        float noiseFloorDbv = fundamentalDbv - snrDb;

        var otherPeaks = FindOtherPeaks(psd, harmonics, fundamentalDbv, options);

        IntermodulationResult? intermodulation = null;
        if (options.Intermodulation is { } imdSpec)
        {
            // Products above the band limit are omitted, not zero — see IntermodulationSpec.
            int maxProductBin = imdSpec.MaxProductFreqHz is { } maxProductHz
                ? Math.Min(psd.ValuesV2PerHz.Length - 1, psd.BinOfFrequency(maxProductHz))
                : band.HiBinInclusive;
            intermodulation = IntermodulationAnalyzer.Analyze(
                psd, psd.BinOfFrequency(imdSpec.ToneLowHz), psd.BinOfFrequency(imdSpec.ToneHighHz),
                imdSpec.Method, imdSpec.MaxOrder, maxProductBin);
        }

        // Same band-limit convention as IMD: gaps above MeasurementHiFreqHz (or the spec's own
        // limit) do not count, so the figure is in-band distortion the way THD+N is.
        MultitoneResult? multitone = null;
        if (options.Multitone is { } multitoneSpec && multitoneSpec.ToneFreqsHz.Count >= 2)
        {
            int maxGapBin = multitoneSpec.MaxProductFreqHz is { } maxGapHz
                ? Math.Min(psd.ValuesV2PerHz.Length - 1, psd.BinOfFrequency(maxGapHz))
                : band.HiBinInclusive;
            int[] toneBins = new int[multitoneSpec.ToneFreqsHz.Count];
            for (int i = 0; i < toneBins.Length; i++)
                toneBins[i] = psd.BinOfFrequency(multitoneSpec.ToneFreqsHz[i]);
            multitone = MultitoneAnalyzer.Analyze(psd, toneBins, maxGapBin);
        }

        return new SpectrumMeters(
            fundamentalFreqHz, (float)psd.BinSizeHz, thdPercent, thdNPercent, snrDb,
            fundamentalDbv, noiseFloorDbv, peakDbv, totalRmsDbv, harmonics, otherPeaks, averagesTaken,
            intermodulation, multitone, externalGainDb);
    }

    /// <summary>
    /// The band RMS and the noise density, on the weighted PSD, whether or not a fundamental was
    /// found -- <see cref="ComputeMeters"/> returns null without one and the meters frame is then not
    /// sent at all, which is fine for THD and useless for a noise measurement with the generator
    /// off. With meters, the density excludes what SNR excludes (the fundamental's notch and the
    /// harmonics' lobes); without, it is the mean over the whole band. Never NaN: an empty band reads
    /// as the floor of the float scale rather than poisoning a JSON body.
    /// </summary>
    private static NoiseFigures ComputeNoiseFigures(PsdSpectrum psdWeighted, SpectrumMeters? meters, double enbwHz, SpectrumPipelineOptions options)
    {
        var band = psdWeighted.BandOf(options.MeasurementLoFreqHz, options.MeasurementHiFreqHz);
        double bandRmsVrms = SpectrumMetrics.BandRmsVrms(psdWeighted, band);
        double densityV2PerHz = meters is { Harmonics.Length: > 0 }
            ? SpectrumMetrics.NoiseDensityV2PerHz(psdWeighted, meters.Harmonics, band, options.NotchOctaves)
            : SpectrumMetrics.NoiseDensityV2PerHz(psdWeighted, band);

        return new NoiseFigures(
            (float)(20.0 * Math.Log10(Math.Max(bandRmsVrms, double.Epsilon))),
            (float)(10.0 * Math.Log10(Math.Max(densityV2PerHz, double.Epsilon))),
            (float)enbwHz,
            (float)options.MeasurementLoFreqHz,
            (float)options.MeasurementHiFreqHz,
            options.Weighting);
    }

    /// <summary>
    /// How many of the most recent time-domain samples the oscilloscope view gets per tick,
    /// independent of FFT size — a 1,048,576-point FFT's whole window would be both far more data
    /// than a chart can usefully show and a multi-second-long "instant" that isn't a waveform
    /// snapshot anymore. Fixed rather than a fraction of FftSize so the on-screen time span only
    /// depends on sample rate (e.g. ~43 ms at 48 kHz, ~11 ms at 192 kHz), not on unrelated
    /// spectrum-analysis settings.
    /// </summary>
    private const int ScopeSampleCount = 2048;

    /// <summary>
    /// How many whole periods of the fundamental <see cref="ResidualFitCount"/> asks for. Measured
    /// on a deliberately dirty 20 Hz record (tone + 2 % DC offset + a wandering LF component + the
    /// simulator's harmonic stack), against the stack's own RMS over the subtracted tail: 0.4
    /// periods reads −14.4 dB (the fit has eaten the harmonics), 0.9 periods −2.7 dB, 2.2 periods
    /// −0.8 dB, and from 3.4 periods up it is within 0.2 dB at every window out to the whole
    /// record. Four is the first setting comfortably inside that.
    /// </summary>
    private const int ResidualFitPeriods = 4;

    /// <summary>
    /// The shortest residual fit window. Well above <see cref="ScopeSampleCount"/> (the tail has to
    /// lie *inside* the window) and already 42 periods of 1 kHz at 192 kHz, so the periods term
    /// above only binds below about 94 Hz there.
    /// </summary>
    private const int ResidualFitMinSamples = 8192;

    /// <summary>
    /// The longest residual fit window, and the reason this bound exists at all: the fit is O(n) per
    /// Gauss–Newton iteration with two <see cref="Math.SinCos(double)"/> calls and twenty
    /// multiply-accumulates per sample, and it runs inline on the pipeline's tick worker. Over a
    /// whole 1,048,576-point record that measured <b>2.65 s per channel</b> — against a 50 ms tick —
    /// so the worker never came up for air, the scope updated once every few seconds, and the
    /// residual frame reached the browser seconds after the raw frame of its own tick, past the
    /// client's 40 ms scope-group deadline: the oscilloscope alternated between a residual it could
    /// not pair with a raw frame and a raw frame with no residual, which is the "flickering, and it
    /// says no fundamental to remove" report of 2026-09-28. At this cap one fit is ~32 ms, and the
    /// cap only binds below about 47 Hz at 192 kHz, where it still buys 3.4 periods at 20 Hz.
    /// <para><b>That was one cause of those symptoms and not the only one</b> — this comment claimed
    /// the report outright until 2026-10-02, when the second was measured. The transport splits a
    /// tick the same way: <see cref="SpectrumBroadcastHub"/> evicted a client's oldest queued frame
    /// silently, and with the residual on there are 12 frames per tick against what was then a
    /// capacity of 8, so under ordinary CPU contention a real browser client lost ~3 % of them —
    /// every loss a group this client cannot pair. Nothing distinguished the two, which is why both
    /// were fixed blind. The hub counts its evictions now and
    /// <see cref="Endpoints.DeviceStatusResponse.DroppedStreamFramesMax"/> carries the count to the
    /// status bar, so the next report of this says which half it is.</para>
    /// <para>Bounding is not a compromise against the whole-record fit, it is better at the tail:
    /// on the dirty record above, the whole-record fit read <b>+0.44 to +0.75 dB</b> over the
    /// harmonic stack it should have left behind, because it averages a wandering LF component over
    /// the entire record instead of tracking it where the subtraction actually happens, while every
    /// bounded window from 3.4 periods up stayed within 0.2 dB.</para>
    /// </summary>
    private const int ResidualFitMaxSamples = 32768;

    /// <summary>
    /// Broadcasts the tail of this tick's already-captured time-domain window as a scope frame —
    /// reuses <paramref name="timeDomainSamples"/> (the same raw, unwindowed snapshot
    /// <see cref="ComputeTimeDomainLevels"/> reads) rather than touching the ring buffer again, so
    /// this costs one more encode+broadcast per tick, not another snapshot.
    /// </summary>
    private void BroadcastScopeFrame(StreamChannel channel, uint seq, ReadOnlySpan<double> timeDomainSamples, double sampleRateHz)
    {
        int count = Math.Min(ScopeSampleCount, timeDomainSamples.Length);
        var tail = timeDomainSamples[^count..];

        float[] samples = ArrayPool<float>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
                samples[i] = (float)tail[i];

            byte[] frame = StreamFrame.EncodeScope(channel, seq, (float)(1.0 / sampleRateHz), samples.AsSpan(0, count));
            hub.Broadcast(frame);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples);
        }
    }

    /// <summary>
    /// The window the residual's fundamental is fitted over: a slice at the <em>end</em> of the
    /// record, long enough to hold <see cref="ResidualFitPeriods"/> whole periods of the
    /// fundamental and never shorter than <see cref="ResidualFitMinSamples"/> or longer than
    /// <see cref="ResidualFitMaxSamples"/>.
    ///
    /// <para>It is a slice of the end rather than of the beginning because the tail this fit is
    /// subtracted from is at the end: evaluating the fit inside the window it was made on keeps a
    /// frequency error from integrating into a phase error across a gap. It is longer than that
    /// tail because the tail alone cannot carry the fit — a 20 Hz tone is under one period in 2048
    /// samples at 48 kHz, where amplitude, phase and the DC term are barely separable. And it is
    /// bounded because the whole record can be 1,048,576 samples; see
    /// <see cref="ResidualFitMaxSamples"/> for what that cost and what it broke.</para>
    /// </summary>
    internal static int ResidualFitCount(int recordLength, double sampleRateHz, double fundamentalFreqHz)
    {
        long forPeriods = (long)Math.Ceiling(ResidualFitPeriods * sampleRateHz / fundamentalFreqHz);
        int count = (int)Math.Clamp(forPeriods, ResidualFitMinSamples, ResidualFitMaxSamples);

        // A record shorter than the window is the whole window there is. It can never fall below
        // the tail this way: the tail is itself clamped to the record length below, and the floor
        // above is four times ScopeSampleCount.
        return Math.Min(count, recordLength);
    }

    /// <summary>
    /// The raw scope tail with the fundamental removed. <paramref name="fitWindow"/> is the slice
    /// <see cref="ResidualFitCount"/> chose -- the end of the record -- and the subtraction is
    /// evaluated on that window's own tail. <paramref name="fit"/> was taken by the caller, before
    /// the raw scope frame went out, so that the expensive half of this does not sit between the
    /// two frames of one tick; see the call site.
    /// </summary>
    private void BroadcastScopeResidualFrame(
        StreamChannel channel, uint seq, ReadOnlySpan<double> fitWindow, double sampleRateHz, in SineFitResult fit)
    {
        int count = Math.Min(ScopeSampleCount, fitWindow.Length);
        float[] samples = ArrayPool<float>.Shared.Rent(count);
        try
        {
            SineFit.Subtract(fitWindow, fit, sampleRateHz, fitWindow.Length - count, samples.AsSpan(0, count));
            hub.Broadcast(StreamFrame.EncodeScope(channel, seq, (float)(1.0 / sampleRateHz), samples.AsSpan(0, count), StreamFlags.ScopeResidual));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples);
        }
    }

    private void BroadcastMetersFrame(StreamChannel channel, uint seq, SpectrumMeters meters, bool held)
    {
        byte[] frame = StreamFrame.EncodeMeters(
            channel, seq, meters.FundamentalFreqHz, meters.BinSizeHz,
            meters.ThdPercent, meters.ThdNPercent, meters.SnrDb, meters.FundamentalDbv, meters.NoiseFloorDbv,
            meters.PeakDbv, meters.TotalRmsDbv, meters.Harmonics, meters.OtherPeaks, (uint)meters.AveragesTaken, held);
        hub.Broadcast(frame);
    }

    private void UpdateRetainedSnapshot(
        int channelIndex, ReadOnlySpan<double> ampPower, double binSizeHz, int fftSize,
        WindowType windowType, WindowSums sums, SpectrumMeters? meters, NoiseFigures noise, WeightingSpec weighting, uint seq,
        SweepPreviewSeries? previewFft, SweepPreviewSeries? previewScope,
        ReadOnlySpan<Complex> complexSpectrum)
    {
        lock (_retainedLock)
        {
            var retained = _retained[channelIndex];
            if (retained.AmpPower is null || retained.AmpPower.Length != ampPower.Length)
                retained.AmpPower = new double[ampPower.Length];

            ampPower.CopyTo(retained.AmpPower);
            retained.CapturedUtc = DateTimeOffset.UtcNow;
            retained.SampleRateHz = _sampleRateHz;
            retained.BinSizeHz = binSizeHz;
            retained.FftSize = fftSize;
            retained.Window = windowType;
            retained.Sums = sums;
            retained.Meters = meters;
            retained.Noise = noise;
            retained.Weighting = weighting;
            retained.Seq = seq;
            retained.PreviewFft = previewFft;
            retained.PreviewScope = previewScope;

            // Copied rather than aliased: `spec` is a pooled buffer that ProcessChannel returns the
            // moment it finishes, and the retained snapshot outlives the tick that produced it.
            if (complexSpectrum.IsEmpty)
            {
                retained.Complex = null;
            }
            else
            {
                if (retained.Complex is null || retained.Complex.Length != complexSpectrum.Length)
                    retained.Complex = new Complex[complexSpectrum.Length];
                complexSpectrum.CopyTo(retained.Complex);
            }
        }
    }

    /// <summary>
    /// At most this many (x, min, max) columns in a decimated sweep-point preview — small enough
    /// that a whole stereo sweep's worth of previews is negligible next to the full-resolution
    /// spectra a saved FFT result stores, generous enough that a waveform or a spectrum's harmonic
    /// structure still reads clearly on a small chart.
    /// </summary>
    public const int PreviewMaxColumns = 200;

    /// <summary>
    /// Decimates the current tick's (already-averaged) power spectrum into a small FFT preview,
    /// the same log-spaced min/max convention <see cref="BuildSpectrumFrame"/> uses for the live
    /// chart, just capped at far fewer columns. <paramref name="ampPower"/> is peak-volts squared,
    /// same as everywhere else in this class -- converted to dBV only for the handful of columns
    /// actually kept, not the full spectrum.
    /// </summary>
    private static SweepPreviewSeries BuildFftPreview(ReadOnlySpan<double> ampPower, double binSizeHz, double displayLoFreqHz)
    {
        int specLen = ampPower.Length;
        int loBin = Math.Clamp((int)Math.Round(displayLoFreqHz / binSizeHz), 1, specLen - 2);
        int hiBin = specLen - 1;
        int columnCount = Math.Clamp(hiBin - loBin + 1, 1, PreviewMaxColumns);

        var columns = new MinMax[columnCount];
        var centerBins = new double[columnCount];
        SpectrumDecimator.DecimateLogWithCenters(ampPower[..(hiBin + 1)], columns, centerBins, loBin);

        var freqHz = new float[columnCount];
        var dbvMin = new float[columnCount];
        var dbvMax = new float[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            freqHz[i] = (float)(centerBins[i] * binSizeHz);
            dbvMin[i] = (float)(10.0 * Math.Log10(Math.Max(columns[i].Min / 2.0, double.Epsilon)));
            dbvMax[i] = (float)(10.0 * Math.Log10(Math.Max(columns[i].Max / 2.0, double.Epsilon)));
        }

        return new SweepPreviewSeries(freqHz, dbvMin, dbvMax);
    }

    /// <summary>
    /// Decimates a window of this tick's raw (unwindowed) time-domain samples into a small
    /// oscilloscope preview, spanning <see cref="ScopeDisplayWindow.SecondsFor"/> of the located
    /// fundamental so a high-frequency sweep point isn't drawn as a barely-visible sliver. Falls
    /// back to the same span the live oscilloscope's own frame covers
    /// (<see cref="ScopeSampleCount"/>) when no fundamental was found -- the noise-floor pass has no
    /// tone to size a period-based window around.
    ///
    /// The window starts at the first rising zero-crossing rather than at a fixed tail offset --
    /// the same trigger search <c>OscilloscopeChart.vue</c>'s <c>findTriggerWindow</c> does for the
    /// live FFT screen's oscilloscope, ported here because this preview is decimated server-side
    /// (min/max, ≤ <see cref="PreviewMaxColumns"/> points): the raw per-sample resolution a crossing
    /// search needs does not survive that decimation, so unlike the live view this has to trigger
    /// before it shrinks the data, not after. Each channel triggers off its own crossing rather than
    /// sharing one the way the live view's two channels do (see that component's remarks on why it
    /// picks one trigger source for both) -- SpectrumPipeline processes channels independently and
    /// has no per-tick view of the other one's samples to share a trigger point with, and for a
    /// diagnostic preview (not a phase measurement) each trace reading as a clean waveform matters
    /// more than the two staying phase-locked to each other.
    ///
    /// Values are decimated linearly (not log, unlike the FFT preview): a waveform's x-axis is
    /// time, which has no meaningful log spacing.
    /// </summary>
    private SweepPreviewSeries BuildScopePreview(ReadOnlySpan<double> timeDomainSamples, double sampleRateHz, float? fundamentalFreqHz)
    {
        int n = timeDomainSamples.Length;
        double displaySeconds = fundamentalFreqHz is { } f && f > 0.0
            ? ScopeDisplayWindow.SecondsFor(f)
            : ScopeSampleCount / sampleRateHz;

        int sampleCount = Math.Clamp((int)Math.Round(displaySeconds * sampleRateHz), 1, n);
        ReadOnlySpan<double> window;

        if (fundamentalFreqHz is { } freqHz && freqHz > 0.0)
        {
            // Widened by one period so a crossing found near the end of the search buffer still
            // leaves room for the full display window after it -- matching a display window
            // narrower than what was searched (see findTriggerWindow's own "available" clamp) is
            // the exception, not the rule.
            double periodSamples = sampleRateHz / freqHz;
            int searchBufferCount = Math.Clamp(sampleCount + (int)Math.Ceiling(periodSamples), 1, n);
            var searchBuffer = timeDomainSamples[^searchBufferCount..];

            // Bounded to one period, same as the live view: a periodic signal cannot need more than
            // that to find its own rising edge, and an unbounded search risks eating the very buffer
            // the window is measured against on a tone riding a large low-frequency component.
            int searchLimit = Math.Min(searchBuffer.Length, (int)Math.Ceiling(periodSamples) + 1);
            int crossingIndex = 0;
            for (int i = 1; i < searchLimit; i++)
            {
                if (searchBuffer[i - 1] < 0.0 && searchBuffer[i] >= 0.0)
                {
                    crossingIndex = i;
                    break;
                }
            }

            // No crossing found means there is no clean one to find (the live view's own fallback);
            // starting at the raw beginning of the search buffer is no worse.
            int available = searchBuffer.Length - crossingIndex;
            window = searchBuffer.Slice(crossingIndex, Math.Min(sampleCount, available));
        }
        else
        {
            window = timeDomainSamples[^sampleCount..];
        }

        int columnCount = Math.Clamp(window.Length, 1, PreviewMaxColumns);
        var columns = new MinMax[columnCount];
        SpectrumDecimator.DecimateLinear(window, columns);

        // The decimator's own column boundaries are evenly spaced up to integer rounding, so a
        // column's center time is recovered from its index rather than threaded out of Decimate as
        // a second array (as DecimateLogWithCenters does for the log case, where the boundaries are
        // NOT evenly spaced and reconstructing them would be wrong) -- see that method's remarks.
        double actualSeconds = window.Length / sampleRateHz;
        var timeS = new float[columnCount];
        var vMin = new float[columnCount];
        var vMax = new float[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            timeS[i] = (float)((i + 0.5) / columnCount * actualSeconds);
            vMin[i] = (float)columns[i].Min;
            vMax[i] = (float)columns[i].Max;
        }

        return new SweepPreviewSeries(timeS, vMin, vMax);
    }

    /// <summary>Mutable per-channel retention slot backing <see cref="_retained"/> -- see its own remarks.</summary>
    private sealed class RetainedSpectrum
    {
        public double[]? AmpPower;
        public DateTimeOffset CapturedUtc;
        public double SampleRateHz;
        public double BinSizeHz;
        public int FftSize;
        public WindowType Window;
        public WindowSums Sums;
        public SpectrumMeters? Meters;
        public NoiseFigures? Noise;
        public WeightingSpec Weighting = WeightingSpec.None;
        public uint Seq;
        public SweepPreviewSeries? PreviewFft;
        public SweepPreviewSeries? PreviewScope;
        public Complex[]? Complex;
    }

    /// <summary>
    /// Everything else worth calling out in the spectrum: mains hum, its own harmonics, and real
    /// intermodulation/mixing products between multiple simultaneous generator tones (the shared
    /// soft-clip in SimulatedDevice is a genuine nonlinearity, so these are real products, not
    /// synthesized ones). Scanned from <see cref="SpectrumPipelineOptions.DisplayLoFreqHz"/>, not
    /// <see cref="SpectrumPipelineOptions.FundamentalMinSearchHz"/> — hum at 50/100 Hz is exactly
    /// what this is for.
    /// </summary>
    private static (float FreqHz, float Dbc)[] FindOtherPeaks(
        PsdSpectrum psd, ReadOnlySpan<Harmonic> harmonics, float fundamentalDbv, SpectrumPipelineOptions options)
    {
        int otherPeaksCount = Math.Max(0, options.OtherPeaksCount);
        if (otherPeaksCount == 0)
            return [];

        Span<BinRange> excluded = harmonics.Length <= 32 ? stackalloc BinRange[harmonics.Length] : new BinRange[harmonics.Length];
        for (int i = 0; i < harmonics.Length; i++)
        {
            excluded[i] = new BinRange(
                harmonics[i].Bin - HarmonicAnalyzer.DefaultSkirtHalfWidthBins, harmonics[i].Bin + HarmonicAnalyzer.DefaultSkirtHalfWidthBins);
        }

        int minBin = Math.Max(1, (int)Math.Round(options.DisplayLoFreqHz / psd.BinSizeHz));
        Span<SpectralPeak> peaks = otherPeaksCount <= 32 ? stackalloc SpectralPeak[otherPeaksCount] : new SpectralPeak[otherPeaksCount];
        int written = PeakFinder.FindPeaks(psd, excluded, otherPeaksCount, peaks, minBin);

        var result = new (float FreqHz, float Dbc)[written];
        for (int i = 0; i < written; i++)
            result[i] = ((float)(peaks[i].Bin * psd.BinSizeHz), (float)(peaks[i].AmplitudeDbv - fundamentalDbv));

        return result;
    }

    private static int FindGlobalPeakBin(ReadOnlySpan<double> psdValues, int loBinInclusive)
    {
        if (loBinInclusive >= psdValues.Length)
            return -1;

        int peak = loBinInclusive;
        double peakValue = psdValues[loBinInclusive];
        for (int k = loBinInclusive + 1; k < psdValues.Length; k++)
        {
            if (psdValues[k] > peakValue)
            {
                peakValue = psdValues[k];
                peak = k;
            }
        }

        return peak;
    }
}
