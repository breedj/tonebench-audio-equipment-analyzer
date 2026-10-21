using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Tonebench.Data;
using Tonebench.Device;

namespace Tonebench.App.Diagnostics;

/// <summary>
/// Everything about an acquisition that is not in its samples, written beside them so the pair can
/// be read a week later by someone who was not at the bench.
/// </summary>
/// <param name="SampleRateHz">The acquisition's rate; both WAVs carry it.</param>
/// <param name="InputChannelCount">Channels in the input stream — the stimulus is always two.</param>
/// <param name="Analyzer">Product name, as the status bar shows it.</param>
/// <param name="SerialNumber">Null on a device that has none.</param>
/// <param name="FirmwareVersion">Null likewise.</param>
/// <param name="DeviceKind">Hardware, Simulated or SoundCard.</param>
/// <param name="InputRangeDbv">At the moment recording started; a later step is an event.</param>
/// <param name="OutputRangeDbv">Likewise.</param>
/// <param name="ExternalGainLeftDb">What the pipeline is dividing the input by — a recording in analyzer volts cannot be read as DUT volts without it.</param>
/// <param name="ExternalGainRightDb">Likewise.</param>
/// <param name="CaptureDelayMs">The settle time every stimulus change in this file was given.</param>
/// <param name="GeneratorLeft">The stimulus as configured when recording started, formatted the way <c>DiagnosticRecorder</c> formats it.</param>
/// <param name="GeneratorRight">Likewise.</param>
public sealed record StreamRecordingContext(
    double SampleRateHz,
    int InputChannelCount,
    string Analyzer,
    string? SerialNumber,
    string? FirmwareVersion,
    string DeviceKind,
    double InputRangeDbv,
    double OutputRangeDbv,
    double ExternalGainLeftDb,
    double ExternalGainRightDb,
    double CaptureDelayMs,
    string GeneratorLeft,
    string GeneratorRight);

/// <summary>What the Diagnostics panel shows, and what <c>GET /api/config/stream-recording</c> returns.</summary>
public sealed record StreamRecordingStatus(
    bool Enabled,
    int MaxFileMegabytes,
    string Directory,
    int KeptRecordings,
    bool Recording,
    string? CurrentName,
    string? LastName,
    double LastSeconds,
    long LastInputFrames,
    long LastStimulusFrames,
    long LastBytes,
    bool LastReachedCap,
    long DroppedBuffers);

/// <summary>
/// Records a whole acquisition — the input stream as the analyzer digitized it, and the stimulus as
/// the DAC was told to play it — as two float32 WAVs in volts, with a JSON sidecar naming the
/// device, the settings and everything that happened during the run.
///
/// <para><b>What it is for.</b> A log line can say a chunk was dropped; it cannot say what the
/// records either side of it looked like, and a user who reports "the trace jumped" has nothing to
/// send but a screenshot. <c>DiagnosticRecorder</c> answers the same question for <i>one</i> record
/// it decided was suspect; this answers it for a whole measurement, including the parts nothing
/// triggered on. The two are complementary and independent switches.</para>
///
/// <para><b>Why the output stream too, and what it cannot show.</b> See
/// <see cref="IStimulusTap"/>: the submitted stimulus is always continuous, so this file can never
/// contain a DAC starvation gap. It can contain the <i>wrong stimulus</i>, which is a whole class
/// of bug here (a flush that did not outrun the DAC queue, a range applied after the prime, the
/// simulator's one-block latency), and it is the only way to see one without an oscilloscope.</para>
///
/// <para><b>Off by default, capped, and purged.</b> 192 kHz stereo float32 is 1.5 MB/s <i>per
/// direction</i>, so an unattended continuous run would fill a disk overnight. Recording stops at
/// <see cref="MaxFileMegabytes"/> and says so in the log and the sidecar; the acquisition itself is
/// untouched. After each recording all but the newest <see cref="KeptRecordings"/>
/// file sets are deleted, so the folder holds ten at most.</para>
///
/// <para><b>Cost, and why it is safe on the threads that call it.</b> Every producer does one copy
/// into a pooled buffer and one write to a bounded channel; all file I/O is on
/// <see cref="RunWriterAsync"/>'s task. The channel is <see cref="BoundedChannelFullMode.DropWrite"/>
/// — a recorder that fell behind must degrade its own file rather than stall the acquisition it is
/// observing, which is the same rule the rest of the pipeline follows. A dropped buffer is a splice
/// in the file, so it is counted and reported in the sidecar rather than passed off as audio.</para>
/// </summary>
public sealed class StreamRecorder : IStimulusTap
{
    /// <summary>File sets kept in the folder, oldest purged first. Joost's figure, and the reason the cap exists at all.</summary>
    public const int KeptRecordings = 10;

    /// <summary>
    /// Default cap per file. 256 MB is about 2.8 minutes of 192 kHz stereo float32, which covers a
    /// typical sweep, and ten of them is 2.5 GB per direction worst case.
    /// </summary>
    public const int DefaultMaxFileMegabytes = 256;

    /// <summary>
    /// How long the queue can absorb a writer that has stopped draining. The queue exists for
    /// stalls — a disk hiccup, a thread-pool pause — not for a throughput shortfall, which no depth
    /// would survive; the measured stalls here were under a second, so this is roughly double the
    /// worst one seen.
    /// </summary>
    private const double QueueSeconds = 1.5;

    /// <summary>
    /// Buffers in flight, and it is <b>a time budget rather than a buffer count</b> — the same
    /// lesson as <c>Qa40xUsbDevice.QueueDepthFor</c> and the sound card's output priming. A fixed
    /// count buys half as much time whenever the sample rate doubles, which is exactly when the
    /// writer has twice as much to do: at 512 buffers this held 1.2 s at 192 kHz and 0.6 s at
    /// 384 kHz, so enabling 384 kHz silently halved the margin.
    ///
    /// <para>Sized against the worst case the app can produce: the top sample rate, with the
    /// smallest buffers either side — QA403 TX chunks of 512 frames (750/s at 384 kHz) plus input
    /// blocks of <c>AcquisitionConfig.DefaultBlockFrameCount</c> (94/s), i.e. 844 buffers a second.
    /// Full, that is a few tens of megabytes of pooled arrays and it is the price of not losing the
    /// file the user is trying to send in.</para>
    /// </summary>
    private const int QueueCapacity =
        (int)(QueueSeconds * ((MaxSupportedSampleRateHz / Qa40xStimulusChunkFrames)
            + (MaxSupportedSampleRateHz / AcquisitionConfig.DefaultBlockFrameCount)));

    /// <summary>The experimental 384 kHz rate — the fastest anything here can arrive at.</summary>
    private const double MaxSupportedSampleRateHz = 384_000.0;

    /// <summary>The QA403's TX chunk, which is the smallest buffer the recorder is ever handed.</summary>
    private const double Qa40xStimulusChunkFrames = 512.0;

    /// <summary>Events kept in the sidecar. Past this the run is logging its own hunting, and the file is the log's job.</summary>
    private const int MaxEvents = 2000;

    /// <summary>The three files one recording is, which <see cref="Purge"/> deletes as a set.</summary>
    private static readonly string[] RecordingSuffixes = ["-in.wav", "-out.wav", "-info.json"];

    private readonly ILogger<StreamRecorder> _logger;
    private readonly Channel<IRecorderCommand> _queue;
    private readonly Task _writer;

    /// <summary>
    /// Read by every producer before it copies anything, so a disarmed recorder costs one volatile
    /// read per block. Cleared by the writer when the cap is reached, which is why it is not simply
    /// <see cref="Enabled"/> — a capped recording stops copying without the user disarming anything.
    /// </summary>
    private volatile bool _recording;

    private long _droppedBuffers;

    // Writer-task-only below this line: no synchronisation, because exactly one task touches them.
    private WavAppender? _inputWav;
    private WavAppender? _stimulusWav;
    private StreamRecordingContext? _context;
    private string? _stem;
    private DateTimeOffset _startedAt;
    private long _inputFrames;
    private long _stimulusFrames;
    private bool _reachedCap;
    private readonly List<string> _events = [];
    private int _eventsDropped;

    /// <param name="logger">Where the two lines naming a recording go.</param>
    /// <param name="directory">
    /// Where the files go; the per-user recordings folder when omitted, which is what the app
    /// passes. A parameter purely so the tests can write somewhere disposable -- this type creates
    /// directories and deletes files in them, which is not something to point at a developer's own
    /// data folder from a test run.
    /// </param>
    /// <param name="maxQueuedBuffers">
    /// The queue's depth, for a test that has to <b>force</b> an overflow. Defaults to
    /// <see cref="QueueCapacity"/>, which is the only value the app ever uses.
    ///
    /// <para>A seam rather than a timing assumption, because the alternative was one: the test that
    /// pins the per-recording drop count used to flood a full-size queue with 8000 buffers and hope
    /// the producer outran the writer, which it does on a developer's machine and did not on CI —
    /// where the writer's buffered file writes kept up, nothing was dropped, and the test failed on
    /// its own "this is no longer forcing the case it exists for" guard. With a depth of a few
    /// buffers the producer has to win by a handful out of thousands rather than by a coin toss.</para>
    /// </param>
    public StreamRecorder(ILogger<StreamRecorder> logger, string? directory = null, int? maxQueuedBuffers = null)
    {
        _logger = logger;
        Directory = directory ?? TonebenchAppData.RecordingsDirectory;

        _queue = Channel.CreateBounded<IRecorderCommand>(
            new BoundedChannelOptions(maxQueuedBuffers ?? QueueCapacity)
            {
                // Wait, although nothing here ever waits for a sample buffer: the dropping is done
                // by Enqueue instead, and that is the whole point. A DropWrite channel drops
                // whatever is being written WITHOUT telling the writer -- TryWrite returns true and
                // WriteAsync completes -- so there is no way to put a command on it that is
                // guaranteed to arrive, and Begin and End are exactly that. With Wait, TryWrite
                // returns false when the queue is full, which is the signal Enqueue needs to drop a
                // buffer deliberately and WriteAsync genuinely waits for room.
                //
                // The buffer dropped is still the *new* one, as it was: a recording's value is that
                // it is contiguous, and keeping the newest audio by discarding the middle of the
                // file loses the same samples while also moving everything after the hole in time.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        _writer = Task.Run(RunWriterAsync);
    }

    /// <summary>The master switch, from the configuration database. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Per-file cap; recording stops when either file reaches it. See <see cref="DefaultMaxFileMegabytes"/>.</summary>
    public int MaxFileMegabytes { get; set; } = DefaultMaxFileMegabytes;

    /// <summary>Where the files go: <c>%LOCALAPPDATA%\Tonebench\recordings</c>, beside the logs and the captures.</summary>
    public string Directory { get; }

    /// <summary>Whether samples are being copied right now — false when disarmed, between acquisitions, or once the cap was hit.</summary>
    public bool Recording => _recording;

    /// <summary>Buffers the writer could not keep up with, over the life of the process. Non-zero means a file has a splice in it.</summary>
    /// <summary>
    /// Buffers thrown away because the queue was full, <b>for the recording in progress or the one
    /// that just finished</b> -- zeroed when a recording opens, never a running total.
    ///
    /// <para>The only question this number is ever asked is whether <i>this file</i> has splices in
    /// it, and a process-lifetime total cannot answer it: it was one, and eight consecutive
    /// recordings on 2026-10-05 therefore all reported the same 444 although only the first two had
    /// dropped anything, which read as every file being damaged. Same rule, and the same reason, as
    /// <c>DroppedRxChunks</c> and <c>DroppedBlocks</c> being per-acquisition.</para>
    /// </summary>
    public long DroppedBuffers => Interlocked.Read(ref _droppedBuffers);

    /// <summary>The last finished recording, for the panel and the status. Published as a whole record, so a reader never sees half of one.</summary>
    public FinishedRecording? Last { get; private set; }

    /// <summary>What the panel shows. <see cref="CurrentName"/> is the recording in progress, if any.</summary>
    public string? CurrentName { get; private set; }

    public StreamRecordingStatus Describe() => new(
        Enabled,
        MaxFileMegabytes,
        Directory,
        KeptRecordings,
        _recording,
        CurrentName,
        Last?.Name,
        Last?.Seconds ?? 0.0,
        Last?.InputFrames ?? 0,
        Last?.StimulusFrames ?? 0,
        Last?.Bytes ?? 0,
        Last?.ReachedCap ?? false,
        DroppedBuffers);

    /// <summary>One finished recording, as the panel reports it.</summary>
    public sealed record FinishedRecording(
        string Name, double Seconds, long InputFrames, long StimulusFrames, long Bytes, bool ReachedCap);

    /// <summary>
    /// Opens a recording for the acquisition that is starting. A no-op while disarmed, and while one
    /// is already open — a sweep is one acquisition and therefore one file, which is the whole point
    /// of hanging this off the acquisition rather than off each capture.
    /// </summary>
    public void Begin(StreamRecordingContext context)
    {
        if (!Enabled || _recording)
            return;

        // Both set here rather than on the writer task: the producers gate on _recording, and the
        // stimulus tap fires every 2.7 ms — waiting for the writer to open a file would lose the
        // first chunks. Buffers that arrive before the Begin command is processed queue up behind it
        // in order, and a drop among them belongs to THIS recording, which is the other half of why
        // the counter is zeroed here: zeroed on the writer task instead, those drops would be wiped
        // when the open finally ran, and until it ran the previous recording's total would still be
        // what anyone reading this saw.
        Interlocked.Exchange(ref _droppedBuffers, 0);
        _recording = true;

        // Not Enqueue either, for the same reason as End: a dropped Begin opens no files, so every
        // buffer after it is written to nothing and the run reports success having recorded
        // silence. It cannot wait -- this is the acquisition's own thread -- so a full queue means
        // the previous recording is still draining and this one is declined, out loud.
        if (!_queue.Writer.TryWrite(new BeginRecording(context, DateTimeOffset.Now)))
        {
            _recording = false;
            _logger.LogWarning(
                "Stream recording not started: the writer is still draining the previous recording");
        }
    }

    /// <summary>
    /// One input block, as the device delivered it: interleaved, calibrated, in <b>analyzer</b>
    /// volts — before <c>SpectrumPipeline</c>'s external-gain multiply, since that is a claim about
    /// the fixture rather than a measurement. The sidecar records the gain so the file can still be
    /// read as DUT volts.
    /// </summary>
    public void WriteInput(PcmBlock block)
    {
        if (!_recording)
            return;

        var samples = block.Samples;
        float[] copy = ArrayPool<float>.Shared.Rent(samples.Length);
        samples.CopyTo(copy);
        Enqueue(new SampleBuffer(copy, samples.Length, block.ChannelCount, block.SampleRateHz, Direction.Input));

        if (block.StartsAfterDiscontinuity)
        {
            Note(string.Create(
                CultureInfo.InvariantCulture,
                $"input block {block.SequenceNumber} starts after a discontinuity: the samples before it are missing"));
        }
    }

    /// <inheritdoc />
    public void WriteStimulus(
        ReadOnlySpan<float> interleavedVolts, int channelCount, double sampleRateHz, long startFrameIndex)
    {
        if (!_recording)
            return;

        float[] copy = ArrayPool<float>.Shared.Rent(interleavedVolts.Length);
        interleavedVolts.CopyTo(copy);
        Enqueue(new SampleBuffer(copy, interleavedVolts.Length, channelCount, sampleRateHz, Direction.Stimulus));
    }

    /// <summary>
    /// Records something that happened during the recording — a stimulus change, a range step, a
    /// discontinuity. Cheap and fire-and-forget; dropped silently while nothing is recording.
    /// </summary>
    public void Note(string message)
    {
        if (!_recording)
            return;

        Enqueue(new RecordEvent(message, DateTimeOffset.Now));
    }

    /// <summary>
    /// Closes the recording and waits for the files to be complete, so a caller may hand the path
    /// to a user the moment this returns. A no-op when nothing is open.
    /// </summary>
    public async Task EndAsync()
    {
        if (_inputWav is null && _stimulusWav is null && !_recording)
            return;

        _recording = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Not Enqueue: an End that gets dropped leaves a half-written file open for ever, and the
        // caller waiting on it. That was true, and it happened -- under DropWrite this WriteAsync
        // silently discarded the End whenever the queue was full, which is a hung acquisition, and
        // it hid behind a race with the writer (the test that fills the queue passed in 23 ms once
        // and then hung for fifteen minutes). The channel waits now, so this does what it says.
        await _queue.Writer.WriteAsync(new EndRecording(done)).ConfigureAwait(false);
        await done.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// The droppable path: audio and events, never a Begin or an End. A full queue throws the new
    /// item away rather than making the producer wait — a diagnostic must never stall the
    /// acquisition it is observing — and only audio is counted, because the count means "this file
    /// has splices in it" and a lost event is reported separately.
    /// </summary>
    private void Enqueue(IRecorderCommand command)
    {
        if (_queue.Writer.TryWrite(command))
            return;

        command.Dispose();
        if (command is SampleBuffer)
            Interlocked.Increment(ref _droppedBuffers);
    }

    private async Task RunWriterAsync()
    {
        await foreach (var command in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (command)
                {
                    case BeginRecording begin:
                        await OpenAsync(begin).ConfigureAwait(false);
                        break;
                    case SampleBuffer buffer:
                        await AppendAsync(buffer).ConfigureAwait(false);
                        break;
                    case RecordEvent note:
                        AddEvent(note);
                        break;
                    case EndRecording end:
                        await CloseAsync(reachedCap: false).ConfigureAwait(false);
                        end.Done.TrySetResult();
                        break;
                }
            }
            catch (Exception ex)
            {
                // A diagnostic must never take down what it is observing, and a failing disk must
                // not turn into one log line per block: the recording is abandoned, once, loudly.
                _recording = false;
                _logger.LogError(ex, "Stream recording failed and was stopped");
                try
                {
                    DiscardWriters();
                }
                catch
                {
                    // Nothing further to do about a file that cannot even be closed.
                }

                if (command is EndRecording failed)
                    failed.Done.TrySetResult();
            }
            finally
            {
                command.Dispose();
            }
        }
    }

    private async Task OpenAsync(BeginRecording begin)
    {
        System.IO.Directory.CreateDirectory(Directory);

        _context = begin.Context;
        _startedAt = begin.StartedAt;
        _stem = string.Create(CultureInfo.InvariantCulture, $"stream-{begin.StartedAt:yyyyMMdd-HHmmss}");
        _inputFrames = 0;
        _stimulusFrames = 0;
        _reachedCap = false;
        _events.Clear();
        _eventsDropped = 0;
        CurrentName = _stem;

        int rate = (int)Math.Round(begin.Context.SampleRateHz);
        _inputWav = await WavAppender.CreateAsync(
            Path.Combine(Directory, _stem + "-in.wav"), rate, Math.Max(1, begin.Context.InputChannelCount))
            .ConfigureAwait(false);
        _stimulusWav = await WavAppender.CreateAsync(
            Path.Combine(Directory, _stem + "-out.wav"), rate, 2).ConfigureAwait(false);

        // Named in the log on purpose: "send me the recording" is only a useful request when the log
        // the user is already sending says which file it is.
        _logger.LogInformation(
            "Stream recording started: {InputFile} and {OutputFile} in {Directory} "
                + "({Rate:0} Hz, {Channels} input channels, stopping at {CapMb} MB per file, keeping {Kept} recordings)",
            _stem + "-in.wav", _stem + "-out.wav", Directory,
            begin.Context.SampleRateHz, Math.Max(1, begin.Context.InputChannelCount), MaxFileMegabytes, KeptRecordings);
    }

    private async Task AppendAsync(SampleBuffer buffer)
    {
        var target = buffer.Direction == Direction.Input ? _inputWav : _stimulusWav;
        if (target is null)
            return;

        await target.AppendAsync(buffer.Samples.AsMemory(0, buffer.Length)).ConfigureAwait(false);

        int frames = buffer.ChannelCount > 0 ? buffer.Length / buffer.ChannelCount : 0;
        if (buffer.Direction == Direction.Input)
            _inputFrames += frames;
        else
            _stimulusFrames += frames;

        long cap = (long)MaxFileMegabytes * 1024 * 1024;
        if (cap > 0 && ((_inputWav?.DataBytes ?? 0) >= cap || (_stimulusWav?.DataBytes ?? 0) >= cap))
        {
            // Both files stop together, so the two have the same timebase and the same end.
            _recording = false;
            await CloseAsync(reachedCap: true).ConfigureAwait(false);
        }
    }

    private void AddEvent(RecordEvent note)
    {
        if (_inputWav is null && _stimulusWav is null)
            return;

        if (_events.Count >= MaxEvents)
        {
            _eventsDropped++;
            return;
        }

        double elapsedMs = (note.At - _startedAt).TotalMilliseconds;
        _events.Add(string.Create(
            CultureInfo.InvariantCulture, $"{elapsedMs:0} ms: {note.Message}"));
    }

    private async Task CloseAsync(bool reachedCap)
    {
        var input = _inputWav;
        var stimulus = _stimulusWav;
        if (input is null && stimulus is null)
            return;

        _reachedCap |= reachedCap;
        _inputWav = null;
        _stimulusWav = null;

        long bytes = 0;
        if (input is not null)
        {
            await input.CloseAsync().ConfigureAwait(false);
            bytes += input.DataBytes;
        }

        if (stimulus is not null)
        {
            await stimulus.CloseAsync().ConfigureAwait(false);
            bytes += stimulus.DataBytes;
        }

        double seconds = _context is { SampleRateHz: > 0 } context ? _inputFrames / context.SampleRateHz : 0.0;
        string stem = _stem ?? "stream";
        await WriteSidecarAsync(stem, seconds, bytes).ConfigureAwait(false);

        Last = new FinishedRecording(stem, seconds, _inputFrames, _stimulusFrames, bytes, _reachedCap);
        CurrentName = null;

        _logger.LogInformation(
            "Stream recording finished: {Name} -- {Seconds:0.0} s, {InputFrames} input frames and "
                + "{StimulusFrames} stimulus frames, {Megabytes:0.0} MB{Capped}{Dropped}",
            stem, seconds, _inputFrames, _stimulusFrames, bytes / 1024.0 / 1024.0,
            _reachedCap
                ? $"; STOPPED at the {MaxFileMegabytes} MB cap, so the acquisition continued past the end of the file"
                : string.Empty,
            DroppedBuffers > 0
                ? $"; {DroppedBuffers} buffers dropped, so THIS file has splices in it"
                : string.Empty);

        // Last, and deliberately after the log line: the recording is complete and reported before
        // the folder is trimmed, so a slow delete cannot delay either. See Purge.
        Purge();
    }

    /// <summary>Drops both writers without patching their headers — for a failure, where the sizes are the least of it.</summary>
    private void DiscardWriters()
    {
        _inputWav?.Dispose();
        _stimulusWav?.Dispose();
        _inputWav = null;
        _stimulusWav = null;
        CurrentName = null;
    }

    /// <summary>
    /// Deletes all but the newest <see cref="KeptRecordings"/> file sets. Grouped by stem, because a
    /// set is three files and deleting a WAV while leaving its sidecar behind would leave the folder
    /// describing recordings that are not there. The stems are timestamps, so ordinal sort is
    /// chronological.
    ///
    /// <para><b>Run after a recording closes, never when one opens</b>, and that is not a tidiness
    /// preference. This runs on the writer's own task, so while it is deleting, nothing is draining
    /// the queue — and at the start of a recording the queue is filling at the full sample rate.
    /// Deleting a backlog of multi-hundred-megabyte sets takes on the order of a second, which is
    /// longer than the queue holds, so the first second of the new recording went in the bin: that is
    /// what the 280 and 164 dropped buffers of 2026-10-05 were, both at the start of a run that
    /// followed a 512 MB one, with nothing dropped for the remaining 2500 s of recording. After a
    /// close there is no stream to fall behind, so the same work costs nothing.</para>
    /// </summary>
    private void Purge()
    {
        var stems = System.IO.Directory
            .EnumerateFiles(Directory, "stream-*-in.wav")
            .Select(path => Path.GetFileName(path)[..^"-in.wav".Length])
            .OrderBy(stem => stem, StringComparer.Ordinal)
            .ToList();

        // KeptRecordings, not KeptRecordings - 1: this runs after the new set is written, so the set
        // being kept room for is already in the list.
        int toDelete = stems.Count - KeptRecordings;
        for (int i = 0; i < toDelete; i++)
        {
            foreach (string suffix in RecordingSuffixes)
            {
                string path = Path.Combine(Directory, stems[i] + suffix);
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    // A file someone has open in an editor is not a reason to abandon the recording.
                    _logger.LogWarning(ex, "Could not purge the old recording {Path}", path);
                }
            }
        }
    }

    /// <summary>
    /// Everything needed to read the two WAVs, written with <see cref="Utf8JsonWriter"/> rather than
    /// reflection-based serialization so it stays correct under <c>PublishTrimmed</c> — the same
    /// reason <c>DiagnosticRecorder</c>'s sidecar is written this way.
    /// </summary>
    private async Task WriteSidecarAsync(string stem, double seconds, long bytes)
    {
        var context = _context;
        string path = Path.Combine(Directory, stem + "-info.json");

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteString("startedAt", _startedAt.ToString("O", CultureInfo.InvariantCulture));
        json.WriteNumber("seconds", seconds);
        json.WriteString("inputFile", stem + "-in.wav");
        json.WriteString("outputFile", stem + "-out.wav");
        json.WriteString(
            "units",
            "Both files are 32-bit float WAV in volts. The input is what the analyzer digitized, "
                + "before the external input gain below; the output is what the DAC was told to play, "
                + "clipped against the output range, left channel first.");
        json.WriteString(
            "whatTheOutputCannotShow",
            "A DAC starvation gap. The submitted stimulus is always continuous -- a gap is the device "
                + "playing nothing while a transfer is late, which shows up in the transfer summary in "
                + "the log and as a held-constant span on the way back in.");
        json.WriteNumber("inputFrames", _inputFrames);
        json.WriteNumber("stimulusFrames", _stimulusFrames);
        json.WriteNumber("bytes", bytes);
        json.WriteBoolean("reachedCap", _reachedCap);
        json.WriteNumber("maxFileMegabytes", MaxFileMegabytes);
        json.WriteNumber("droppedBuffers", DroppedBuffers);

        if (context is not null)
        {
            json.WriteNumber("sampleRateHz", context.SampleRateHz);
            json.WriteNumber("inputChannelCount", context.InputChannelCount);

            json.WriteStartObject("analyzer");
            json.WriteString("name", context.Analyzer);
            json.WriteString("kind", context.DeviceKind);
            json.WriteString("serialNumber", context.SerialNumber);
            json.WriteString("firmwareVersion", context.FirmwareVersion);
            json.WriteEndObject();

            json.WriteStartObject("settingsAtStart");
            json.WriteNumber("inputRangeDbv", context.InputRangeDbv);
            json.WriteNumber("outputRangeDbv", context.OutputRangeDbv);
            json.WriteNumber("externalGainLeftDb", context.ExternalGainLeftDb);
            json.WriteNumber("externalGainRightDb", context.ExternalGainRightDb);
            json.WriteNumber("captureDelayMs", context.CaptureDelayMs);
            json.WriteString("generatorLeft", context.GeneratorLeft);
            json.WriteString("generatorRight", context.GeneratorRight);
            json.WriteEndObject();
        }

        json.WriteStartArray("events");
        foreach (string e in _events)
            json.WriteStringValue(e);
        if (_eventsDropped > 0)
            json.WriteStringValue($"... and {_eventsDropped} further events not recorded");
        json.WriteEndArray();

        json.WriteEndObject();
        await json.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Closes any open recording and stops the writer. Called at host shutdown, beside <c>DiagnosticRecorder.StopAsync</c>.</summary>
    public async Task StopAsync()
    {
        await EndAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private enum Direction
    {
        Input,
        Stimulus,
    }

    private interface IRecorderCommand : IDisposable;

    private sealed record BeginRecording(StreamRecordingContext Context, DateTimeOffset StartedAt) : IRecorderCommand
    {
        public void Dispose() { }
    }

    private sealed record RecordEvent(string Message, DateTimeOffset At) : IRecorderCommand
    {
        public void Dispose() { }
    }

    private sealed record EndRecording(TaskCompletionSource Done) : IRecorderCommand
    {
        public void Dispose() { }
    }

    private sealed record SampleBuffer(
        float[] Samples, int Length, int ChannelCount, double SampleRateHz, Direction Direction) : IRecorderCommand
    {
        public void Dispose() => ArrayPool<float>.Shared.Return(Samples);
    }

    /// <summary>
    /// A growing 32-bit-float WAV: the header is written with placeholder sizes, the samples are
    /// appended, and the two size fields are patched on close. Float and unnormalised, so a sample
    /// <i>is</i> the volt value and any editor's amplitude readout is directly comparable to the
    /// app's own dBV — the same choice <c>DiagnosticRecorder</c> makes for its single records.
    ///
    /// <para>A recording abandoned by a crash is still readable by most tools, which scan for the
    /// data chunk rather than trusting its length; the cap keeps every file far inside RIFF's 4 GB
    /// ceiling, so the patched sizes can never overflow.</para>
    /// </summary>
    private sealed class WavAppender : IDisposable
    {
        private const int HeaderBytes = 44;

        private readonly FileStream _stream;
        private byte[]? _scratch;

        private WavAppender(FileStream stream)
        {
            _stream = stream;
        }

        /// <summary>Sample bytes written so far, which is what the cap is measured against.</summary>
        public long DataBytes { get; private set; }

        public static async Task<WavAppender> CreateAsync(string path, int sampleRateHz, int channelCount)
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            var appender = new WavAppender(stream);
            await stream.WriteAsync(Header(sampleRateHz, channelCount, dataBytes: 0)).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            return appender;
        }

        public async Task AppendAsync(ReadOnlyMemory<float> samples)
        {
            int bytes = samples.Length * sizeof(float);
            _scratch ??= new byte[bytes];
            if (_scratch.Length < bytes)
                _scratch = new byte[bytes];

            var span = samples.Span;
            for (int i = 0; i < span.Length; i++)
                BitConverter.TryWriteBytes(_scratch.AsSpan(i * sizeof(float)), span[i]);

            await _stream.WriteAsync(_scratch.AsMemory(0, bytes)).ConfigureAwait(false);
            DataBytes += bytes;
        }

        public async Task CloseAsync()
        {
            // The sizes are the only thing a reader needs that was not known when the file was
            // opened, so they are patched in place rather than the header being rewritten whole.
            _stream.Position = 4;
            await _stream.WriteAsync(BitConverter.GetBytes((int)(36 + DataBytes))).ConfigureAwait(false);
            _stream.Position = 40;
            await _stream.WriteAsync(BitConverter.GetBytes((int)DataBytes)).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        public void Dispose() => _stream.Dispose();

        private static byte[] Header(int sampleRateHz, int channelCount, int dataBytes)
        {
            var header = new byte[HeaderBytes];
            using var writer = new BinaryWriter(new MemoryStream(header), Encoding.ASCII);

            int blockAlign = channelCount * sizeof(float);

            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8.ToArray());

            writer.Write("fmt "u8.ToArray());
            writer.Write(16);                            // PCM-style fmt chunk size
            writer.Write((short)3);                      // WAVE_FORMAT_IEEE_FLOAT
            writer.Write((short)channelCount);
            writer.Write(sampleRateHz);
            writer.Write(sampleRateHz * blockAlign);     // byte rate
            writer.Write((short)blockAlign);
            writer.Write((short)32);                     // bits per sample

            writer.Write("data"u8.ToArray());
            writer.Write(dataBytes);
            return header;
        }
    }
}
