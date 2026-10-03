using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Diagnostics;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Diagnostics;

/// <summary>
/// The seam, rather than the recorder: an acquisition that is armed has to arrive in both files
/// without anything else in the app being asked to help.
///
/// <para>Three things here can break independently and each one leaves a plausible-looking
/// recording. The session has to attach the device's stimulus tap <i>before</i> the stream comes up,
/// or the first chunks are missing from the output file; it has to copy every input block, which
/// happens on the pump's own path; and the tap has to be detached afterwards, or every later run
/// pays for copying chunks nobody records. The simulator implements the tap for exactly this reason
/// — it is the device the recording is developed against.</para>
/// </summary>
public sealed class RecordedAcquisitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qa403-recacq-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task AnArmedAcquisition_RecordsTheInputStreamAndTheStimulusThatProducedIt()
    {
        var device = new SimulatedDevice();
        var recorder = new StreamRecorder(NullLogger<StreamRecorder>.Instance, _dir) { Enabled = true };
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            new CaptureDelaySetting { Ms = 0 },
            NullLogger<DeviceSessionService>.Instance,
            recorder);

        await session.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 4096);
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -6.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None,
            config);

        await session.StartAcquisitionAsync(config, CancellationToken.None);

        // The simulator streams at real-time cadence, so this is four blocks or so rather than a
        // spin: 4096 frames at 48 kHz is 85 ms.
        Assert.True(recorder.Recording);
        Assert.NotNull(device.StimulusTap);
        for (int i = 0; i < 100 && recorder.Last is null && !File.Exists(Path.Combine(_dir, (recorder.CurrentName ?? "x") + "-in.wav")); i++)
            await Task.Delay(10);

        await Task.Delay(400);
        await session.StopAcquisitionAsync();

        // Detached again, so an unrecorded run does not pay for the copy.
        Assert.Null(device.StimulusTap);
        Assert.False(recorder.Recording);

        var finished = recorder.Last;
        Assert.NotNull(finished);
        Assert.True(finished.InputFrames > 0, "no input frames were recorded");
        Assert.True(finished.StimulusFrames > 0, "no stimulus frames were recorded");

        // The output file has to hold the tone that was asked for, in volts: -6 dBV is 0.501 V rms,
        // so its peak is 0.709 V. Read generously -- the point is that the stimulus is in there at
        // the right level, not a measurement of the simulator.
        float[] stimulus = ReadSamples(Path.Combine(_dir, finished.Name + "-out.wav"));
        double peakLeft = 0.0;
        double peakRight = 0.0;
        for (int i = 0; i + 1 < stimulus.Length; i += 2)
        {
            peakLeft = Math.Max(peakLeft, Math.Abs(stimulus[i]));
            peakRight = Math.Max(peakRight, Math.Abs(stimulus[i + 1]));
        }

        Assert.InRange(peakLeft, 0.69, 0.72);

        // The right generator channel was off, and a recording whose channels were crossed would put
        // the tone here instead -- which is the failure a stereo WAV makes easy to miss by ear.
        Assert.Equal(0.0, peakRight, 6);

        // And the input file holds what the analyzer digitized: the simulator loops the generator
        // back, so the left input carries the same tone.
        float[] input = ReadSamples(Path.Combine(_dir, finished.Name + "-in.wav"));
        double inputPeakLeft = 0.0;
        for (int i = 0; i + 1 < input.Length; i += 2)
            inputPeakLeft = Math.Max(inputPeakLeft, Math.Abs(input[i]));

        Assert.InRange(inputPeakLeft, 0.60, 0.80);

        await recorder.StopAsync();
        session.Dispose();
    }

    private static float[] ReadSamples(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int dataBytes = BitConverter.ToInt32(bytes, 40);
        var samples = new float[dataBytes / sizeof(float)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToSingle(bytes, 44 + (i * sizeof(float)));
        return samples;
    }
}
