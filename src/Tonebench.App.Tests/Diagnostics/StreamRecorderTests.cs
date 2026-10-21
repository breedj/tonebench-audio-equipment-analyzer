using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tonebench.App.Diagnostics;
using Tonebench.Device;

namespace Tonebench.App.Tests.Diagnostics;

/// <summary>
/// The stream recorder, which is the only diagnostic here whose output a user is asked to send in —
/// so what matters is that the files are complete, readable by something other than this app, and
/// bounded.
/// </summary>
public sealed class StreamRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qa403-rec-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// What the writer task said, so a failure here can name its own cause.
    ///
    /// <para>These used to run on a <c>NullLogger</c>, and the writer loop answers <i>any</i>
    /// exception by abandoning the recording and logging it — so on the one machine where that
    /// happened, CI, the only thing the test could report was a NullReferenceException on
    /// <c>Last</c> one line later, naming nothing. The reason was written and thrown away. Every
    /// assertion that an abandoned recording can reach now prints this.</para>
    /// </summary>
    private readonly List<string> _log = [];

    private StreamRecorder Create(bool enabled = true, int capMegabytes = 256, int? maxQueuedBuffers = null) =>
        new(new ListLogger(_log), _dir, maxQueuedBuffers) { Enabled = enabled, MaxFileMegabytes = capMegabytes };

    /// <summary>Whatever the writer logged, for an assertion message.</summary>
    private string LogText => _log.Count == 0 ? "(the recorder logged nothing)" : string.Join(" | ", _log);

    private sealed class ListLogger(List<string> lines) : ILogger<StreamRecorder>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (lines)
                lines.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : " -- " + exception)}");
        }
    }

    private static StreamRecordingContext Context(double sampleRateHz = 48000, int channels = 2) => new(
        SampleRateHz: sampleRateHz,
        InputChannelCount: channels,
        Analyzer: "QA403",
        SerialNumber: "1077527945",
        FirmwareVersion: "60",
        DeviceKind: "Hardware",
        InputRangeDbv: 6,
        OutputRangeDbv: -2,
        ExternalGainLeftDb: -20,
        ExternalGainRightDb: 0,
        CaptureDelayMs: 300,
        GeneratorLeft: "tones 1000Hz@-20dBV",
        GeneratorRight: "off");

    private static PcmBlock Block(int frames, int channels, double sampleRateHz, Func<int, int, float> sample)
    {
        float[] buffer = ArrayPool<float>.Shared.Rent(frames * channels);
        for (int f = 0; f < frames; f++)
            for (int ch = 0; ch < channels; ch++)
                buffer[(f * channels) + ch] = sample(f, ch);

        return new PcmBlock(buffer, frames * channels, sampleRateHz, channels, sequenceNumber: 0, timestampTicks: 0);
    }

    /// <summary>
    /// The whole contract in one test: two files per acquisition, with the samples that were handed
    /// in, in volts, readable as a 32-bit float WAV by anything that can read one — the levels in an
    /// audio editor have to match the app's own dBV, which is the entire reason this is float and
    /// unnormalised.
    /// </summary>
    [Fact]
    public async Task ARecordedAcquisition_WritesBothStreamsAsFloatWavsInVolts()
    {
        var recorder = Create();
        recorder.Begin(Context());

        using (var block = Block(256, 2, 48000, (f, ch) => ch == 0 ? 0.5f : -0.25f))
            recorder.WriteInput(block);

        float[] stimulus = new float[256 * 2];
        for (int f = 0; f < 256; f++)
        {
            stimulus[f * 2] = 1.5f;
            stimulus[(f * 2) + 1] = 0f;
        }

        recorder.WriteStimulus(stimulus, channelCount: 2, sampleRateHz: 48000, startFrameIndex: 0);
        await recorder.EndAsync();

        var finished = recorder.Last;
        Assert.NotNull(finished);
        Assert.Equal(256, finished.InputFrames);
        Assert.Equal(256, finished.StimulusFrames);
        Assert.False(finished.ReachedCap);

        var input = ReadWav(Path.Combine(_dir, finished.Name + "-in.wav"));
        Assert.Equal(2, input.ChannelCount);
        Assert.Equal(48000, input.SampleRateHz);
        Assert.Equal(512, input.Samples.Length);
        Assert.Equal(0.5f, input.Samples[0]);
        Assert.Equal(-0.25f, input.Samples[1]);

        var output = ReadWav(Path.Combine(_dir, finished.Name + "-out.wav"));
        Assert.Equal(2, output.ChannelCount);
        Assert.Equal(1.5f, output.Samples[0]);
        Assert.Equal(0f, output.Samples[1]);
    }

    /// <summary>
    /// The sidecar, which is the half that makes the samples interpretable: an input recording in
    /// analyzer volts cannot be read as DUT volts without the external gain, and a run whose
    /// stimulus changed in the middle cannot be read at all without the events.
    /// </summary>
    [Fact]
    public async Task TheSidecar_NamesTheDeviceTheSettingsAndWhatHappenedDuringTheRun()
    {
        var recorder = Create();
        recorder.Begin(Context());
        using (var block = Block(64, 2, 48000, (_, _) => 0.1f))
            recorder.WriteInput(block);
        recorder.Note("stimulus now L: tones 2000Hz@-20dBV, R: off");
        await recorder.EndAsync();

        string name = recorder.Last!.Name;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, name + "-info.json")));
        var root = document.RootElement;

        Assert.Equal(name + "-in.wav", root.GetProperty("inputFile").GetString());
        Assert.Equal(48000, root.GetProperty("sampleRateHz").GetDouble());
        Assert.Equal("1077527945", root.GetProperty("analyzer").GetProperty("serialNumber").GetString());
        Assert.Equal(-20, root.GetProperty("settingsAtStart").GetProperty("externalGainLeftDb").GetDouble());
        Assert.Equal(300, root.GetProperty("settingsAtStart").GetProperty("captureDelayMs").GetDouble());
        Assert.Contains(
            "2000Hz",
            string.Join("|", root.GetProperty("events").EnumerateArray().Select(e => e.GetString())));
    }

    /// <summary>
    /// A discontinuity is the one thing in an input recording that cannot be seen by looking at it:
    /// the samples either side are both valid, and what is missing is between them. So it is an
    /// event, from the flag the block carries.
    /// </summary>
    [Fact]
    public async Task ADiscontinuousBlock_IsRecordedAsAnEvent()
    {
        var recorder = Create();
        recorder.Begin(Context());

        float[] buffer = ArrayPool<float>.Shared.Rent(32);
        using (var block = new PcmBlock(buffer, 32, 48000, 2, sequenceNumber: 7, timestampTicks: 0, startsAfterDiscontinuity: true))
            recorder.WriteInput(block);

        await recorder.EndAsync();

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, recorder.Last!.Name + "-info.json")));
        string events = string.Join("|", document.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("discontinuity", events);
        Assert.Contains("block 7", events);
    }

    /// <summary>
    /// The cap stops the <i>recording</i>, not the acquisition — and it stops both files, so the two
    /// keep one timebase and one end. A file that grew until the disk filled would be a diagnostic
    /// that caused a worse fault than the one it was chasing.
    /// </summary>
    [Fact]
    public async Task AtTheCap_RecordingStopsAndTheAcquisitionIsLeftAlone()
    {
        var recorder = Create(capMegabytes: 16);
        recorder.Begin(Context());

        // 16 MB is four million floats, i.e. 512 blocks of this size; well past it.
        for (int i = 0; i < 700; i++)
        {
            using var block = Block(4096, 2, 48000, (_, _) => 0.01f);
            recorder.WriteInput(block);
        }

        await recorder.EndAsync();

        // Not `Last!`: the writer abandons a recording on any exception, and then Last is null and
        // the bare dereference reports a NullReferenceException naming nothing — which is exactly
        // how this failed on CI. This says which of the two happened.
        var finishedAtCap = recorder.Last;
        Assert.True(finishedAtCap is not null, $"the recording never finished. Recorder log: {LogText}");

        Assert.True(finishedAtCap!.ReachedCap, $"the cap was not reached. Recorder log: {LogText}");
        Assert.False(recorder.Recording);

        long bytes = new FileInfo(Path.Combine(_dir, finishedAtCap.Name + "-in.wav")).Length;
        Assert.InRange(bytes, 16L * 1024 * 1024, 17L * 1024 * 1024);

        // And the sidecar says so, because the file's own end does not: a recording that stopped at
        // the cap looks exactly like one whose acquisition ended there.
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, finishedAtCap.Name + "-info.json")));
        Assert.True(document.RootElement.GetProperty("reachedCap").GetBoolean());
    }

    /// <summary>
    /// Ten recordings kept, purged as whole sets. Deleting a WAV and leaving its sidecar would leave
    /// the folder describing recordings that are not there.
    /// </summary>
    [Fact]
    public async Task StartingAnEleventhRecording_PurgesTheOldestSetWhole()
    {
        var recorder = Create();

        // Twelve recordings, each distinguishable by name. The stem is a timestamp to the second, so
        // the files are seeded by hand rather than by twelve one-second-apart acquisitions.
        Directory.CreateDirectory(_dir);
        for (int i = 1; i <= 12; i++)
        {
            string stem = $"stream-20261001-00{i:00}00";
            foreach (string suffix in new[] { "-in.wav", "-out.wav", "-info.json" })
                await File.WriteAllTextAsync(Path.Combine(_dir, stem + suffix), "x");
        }

        recorder.Begin(Context());
        await recorder.EndAsync();

        var stems = Directory.EnumerateFiles(_dir, "stream-*-in.wav")
            .Select(path => Path.GetFileName(path)[..^"-in.wav".Length])
            .OrderBy(stem => stem, StringComparer.Ordinal)
            .ToList();

        // Nine of the seeded twelve survive, plus the new one.
        Assert.Equal(10, stems.Count);
        Assert.DoesNotContain("stream-20261001-000100", stems);
        Assert.DoesNotContain("stream-20261001-000300", stems);
        Assert.Contains("stream-20261001-001200", stems);
        Assert.Empty(Directory.EnumerateFiles(_dir, "stream-20261001-000100*"));
    }

    /// <summary>
    /// The drop count belongs to <i>this</i> recording, not to the process. It was a running total
    /// once, and the sidecar copied it verbatim, so on 2026-10-05 eight consecutive recordings all
    /// reported the same 444 although only the first two had dropped anything — six clean files that
    /// read as damaged, and no way to tell which of the eight was which. The same rule, and the same
    /// reason, as the device's own per-acquisition drop counters.
    ///
    /// <para>Forcing a real drop is the only way to test it, so the first recording is handed far
    /// more buffers than the queue can hold. <b>The queue is deliberately tiny here</b>: with the
    /// app's own depth this was a race between the producer and a writer doing one buffered file
    /// write per buffer, which the producer wins on a developer's machine and lost on CI — where
    /// nothing was dropped and this failed on its own guard below, twice. At a depth of 8 the
    /// producer has to win by a handful of buffers out of 8000 rather than by a coin toss. The
    /// assertion that matters is the second recording's.</para>
    ///
    /// <para>Filling the queue is also the only thing that reaches <see cref="StreamRecorder.EndAsync"/>'s
    /// own worst case — the End command being dropped like any other — so this doubles as the guard
    /// on that: it hung here for fifteen minutes before the retry was added.</para>
    /// </summary>
    [Fact]
    public async Task TheDropCount_BelongsToTheRecording_NotToTheProcess()
    {
        var recorder = Create(maxQueuedBuffers: 8);
        recorder.Begin(Context());

        // Several hundred times the queue's capacity, in buffers small enough that the cost is the
        // writer's per-buffer file write rather than the bytes.
        for (int i = 0; i < 8000; i++)
        {
            using var block = Block(64, 2, 48000, (_, _) => 0.1f);
            recorder.WriteInput(block);
        }

        // WaitAsync, not a bare await: a full queue used to swallow the End command outright, so the
        // failure this guards against is a test run that never finishes rather than one that fails.
        await recorder.EndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        // Thousands, not one: with a depth of 8 against 8000 buffers the writer would have to keep
        // up to within a percent while doing a file write per buffer. A handful of drops would mean
        // this is back to racing a full-size queue, which is the regression that put it here.
        Assert.True(
            recorder.DroppedBuffers > 100,
            $"The queue dropped {recorder.DroppedBuffers} buffers, so this test is no longer forcing "
                + $"the case it exists for. Recorder log: {LogText}");

        recorder.Begin(Context());
        Assert.Equal(0, recorder.DroppedBuffers);

        using (var block = Block(256, 2, 48000, (_, _) => 0.25f))
            recorder.WriteInput(block);

        await recorder.EndAsync();
        Assert.Equal(0, recorder.DroppedBuffers);

        var finished = recorder.Last;
        Assert.True(finished is not null, $"the second recording never finished. Recorder log: {LogText}");
        using var sidecar = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_dir, finished.Name + "-info.json")));
        Assert.Equal(0, sidecar.RootElement.GetProperty("droppedBuffers").GetInt64());
    }

    /// <summary>Disarmed, nothing is written and nothing is copied — the folder is not even created.</summary>
    [Fact]
    public async Task Disarmed_NothingIsWrittenAtAll()
    {
        var recorder = Create(enabled: false);
        recorder.Begin(Context());

        using (var block = Block(64, 2, 48000, (_, _) => 1f))
            recorder.WriteInput(block);

        await recorder.EndAsync();

        Assert.False(recorder.Recording);
        Assert.Null(recorder.Last);
        Assert.False(Directory.Exists(_dir));
    }

    /// <summary>
    /// A mono acquisition records a mono input file and a stereo stimulus one: the generator has two
    /// channels whatever the analyzer is reading, and a WAV whose header lied about its channel
    /// count would read at half speed in an editor.
    /// </summary>
    [Fact]
    public async Task AMonoAcquisition_RecordsAMonoInputAndAStereoStimulus()
    {
        var recorder = Create();
        recorder.Begin(Context(channels: 1));

        using (var block = Block(100, 1, 48000, (_, _) => 0.2f))
            recorder.WriteInput(block);

        recorder.WriteStimulus(new float[200], channelCount: 2, sampleRateHz: 48000, startFrameIndex: 0);
        await recorder.EndAsync();

        Assert.Equal(1, ReadWav(Path.Combine(_dir, recorder.Last!.Name + "-in.wav")).ChannelCount);
        Assert.Equal(2, ReadWav(Path.Combine(_dir, recorder.Last.Name + "-out.wav")).ChannelCount);
    }

    private sealed record WavFile(int ChannelCount, int SampleRateHz, float[] Samples);

    /// <summary>
    /// A deliberately independent reader — it parses the header fields rather than trusting the
    /// writer's own constants, which is the point: the file has to be readable by something that was
    /// not written alongside it.
    /// </summary>
    private static WavFile ReadWav(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(3, BitConverter.ToInt16(bytes, 20));            // WAVE_FORMAT_IEEE_FLOAT
        Assert.Equal(32, BitConverter.ToInt16(bytes, 34));           // bits per sample
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));

        int channels = BitConverter.ToInt16(bytes, 22);
        int rate = BitConverter.ToInt32(bytes, 24);
        int dataBytes = BitConverter.ToInt32(bytes, 40);

        // The two patched size fields have to agree with the file as it ended up, or a reader that
        // trusts them stops early -- which is exactly what an unpatched header does.
        Assert.Equal(bytes.Length - 44, dataBytes);
        Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));

        var samples = new float[dataBytes / sizeof(float)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToSingle(bytes, 44 + (i * sizeof(float)));

        return new WavFile(channels, rate, samples);
    }
}
