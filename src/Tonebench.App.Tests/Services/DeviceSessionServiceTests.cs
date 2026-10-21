using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Services;

public class DeviceSessionServiceTests
{
    private static DeviceSessionService CreateService() => new(
        new SimulatedDevice(),
        new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
        NullLogger<DeviceSessionService>.Instance);

    /// <summary>
    /// Both drop counters have to reach the status response. Only <c>DroppedRxChunks</c> did, and
    /// because the counters are deliberately separate (they name different halves of the pipeline)
    /// that meant the API and the UI reported a clean stream through runs that dropped 14, 74 and
    /// 439 assembled blocks. A counter that is logged but never surfaced sends the next person
    /// looking in the wrong place, which is exactly what it did.
    /// </summary>
    [Fact]
    public async Task GetStatus_ReportsBothDropCounters()
    {
        var device = new StubDevice { RxChunkDrops = 7, BlockDrops = 3 };
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);

        await session.ConnectAsync(CancellationToken.None);
        var status = session.GetStatus();

        Assert.Equal(7, status.DroppedRxChunks);
        Assert.Equal(3, status.DroppedBlocks);
    }

    /// <summary>
    /// The analyzer's own sensors reach the status response, and reach it whole: three figures in
    /// three different units, where a mapping that crossed two of them would still serialize happily.
    /// </summary>
    [Fact]
    public async Task GetStatus_ReportsTheDevicesTelemetry()
    {
        var device = new StubDevice
        {
            TelemetryReading = new DeviceTelemetry(
                UsbVoltageV: 4.864, UsbCurrentMa: 830.0, TemperatureC: 25.0),
        };
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);

        await session.ConnectAsync(CancellationToken.None);
        var telemetry = session.GetStatus().Telemetry;

        Assert.NotNull(telemetry);
        Assert.Equal(4.864, telemetry.UsbVoltageV, 6);
        Assert.Equal(830.0, telemetry.UsbCurrentMa, 6);
        Assert.Equal(25.0, telemetry.TemperatureC, 6);
    }

    /// <summary>
    /// A device with no sensors reports none — null, not a record of zeroes. The status bar hides
    /// the readout on null, and zeroes would instead put "0.000 V" beside a perfectly healthy
    /// simulator, which reads as a fault rather than as an absence.
    /// </summary>
    [Fact]
    public async Task GetStatus_WithoutTelemetry_ReportsNoneRatherThanZeroes()
    {
        var session = CreateService();

        await session.ConnectAsync(CancellationToken.None);

        Assert.Null(session.GetStatus().Telemetry);
    }

    /// <summary>
    /// The external input gain rides on the status so the status bar can say a divider is in the
    /// path -- a fixture the user must not forget about, since every level on screen is corrected by it.
    /// </summary>
    [Fact]
    public async Task GetStatus_ReportsTheExternalGain_ConnectedOrNot()
    {
        var gain = new ExternalGainSetting { LeftDb = -20.0, RightDb = 3.0 };
        var session = new DeviceSessionService(
            new StubDevice(),
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance, externalGain: gain),
            new CaptureDelaySetting(), NullLogger<DeviceSessionService>.Instance, gain);

        var before = session.GetStatus();
        Assert.Equal(-20.0, before.ExternalGainLeftDb);
        Assert.Equal(3.0, before.ExternalGainRightDb);

        await session.ConnectAsync(CancellationToken.None);
        var after = session.GetStatus();
        Assert.Equal(-20.0, after.ExternalGainLeftDb);
        Assert.Equal(3.0, after.ExternalGainRightDb);
    }

    [Fact]
    public async Task StartAcquisitionAsync_RejectsBlockFrameCountBelowHardwareQueueDepth()
    {
        var session = CreateService();
        await session.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 1);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.StartAcquisitionAsync(config, CancellationToken.None));
        Assert.Contains(DeviceSessionService.MinBlockFrameCount.ToString(), ex.Message);
        Assert.False(session.Acquiring);
    }

    [Fact]
    public async Task StartAcquisitionAsync_AcceptsBlockFrameCountAtTheFloor()
    {
        var session = CreateService();
        await session.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: DeviceSessionService.MinBlockFrameCount);

        await session.StartAcquisitionAsync(config, CancellationToken.None);
        Assert.True(session.Acquiring);

        await session.StopAcquisitionAsync();
        Assert.False(session.Acquiring);
    }

    [Fact]
    public async Task ConnectAsync_PushesTheDefaultRangesToTheDeviceInsteadOfOnlyCachingThem()
    {
        // The status response and the device's own clip points must agree from the first frame:
        // caching a default here without sending it left the UI reporting one full scale while the
        // device clipped at another. Output defaults to the largest range so a default generator
        // level can't arrive pre-clipped.
        var device = new SimulatedDevice();
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);

        await session.ConnectAsync(CancellationToken.None);
        var status = session.GetStatus();

        Assert.Equal(device.Capabilities.InputRangesDbv[0], status.InputRangeDbv);
        Assert.Equal(device.Capabilities.OutputRangesDbv[^1], status.OutputRangeDbv);

        // Now prove the device actually received the 18 dBV output range rather than keeping its
        // own 8 dBV field default: a +12 dBV tone (peak 5.63 V) passes cleanly through an 18 dBV
        // DAC full scale but would be truncated at 3.55 V by an 8 dBV one. The input range is
        // opened right up first so the ADC's soft clip can't be what we end up measuring.
        await session.SetInputRangeAsync(device.Capabilities.InputRangesDbv[^1], CancellationToken.None);
        device.SignalProfile = new SimulatedSignalProfile
        {
            HarmonicsEnabled = false, HumEnabled = false, NoiseEnabled = false, PinkNoiseEnabled = false,
        };
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 12.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None);

        double expectedPeakV = Math.Sqrt(2.0) * Math.Pow(10.0, 12.0 / 20.0);
        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 1, BlockFrameCount: 256);
        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                double peak = 0.0;
                for (int i = 0; i < block.SampleCount; i++)
                    peak = Math.Max(peak, Math.Abs(block.Samples[i]));

                Assert.Equal(expectedPeakV, peak, precision: 4);
            }

            break;
        }
    }

    [Fact]
    public async Task TrySetSimulatedProfile_TogglesNoiseHumAndHarmonicsAndShowsUpInStatus()
    {
        var session = CreateService();
        await session.ConnectAsync(CancellationToken.None);

        // A directly constructed SimulatedDevice ships with all three on
        // (SimulatedSignalProfile.NoiseEnabled/HumEnabled/HarmonicsEnabled); the app's factory is
        // what turns hum off — see AnalyzerDeviceFactory_HandsOutTheSimulatorWithNoiseAndHarmonicsOnAndHumOff.
        Assert.True(session.SimulatedNoiseEnabled);
        Assert.True(session.SimulatedHumEnabled);
        Assert.True(session.SimulatedHarmonicsEnabled);

        Assert.True(session.TrySetSimulatedProfile(noiseEnabled: false, humEnabled: false, harmonicsEnabled: false));

        Assert.False(session.SimulatedNoiseEnabled);
        Assert.False(session.SimulatedHumEnabled);
        Assert.False(session.SimulatedHarmonicsEnabled);
        Assert.False(session.GetStatus().SimulatedNoiseEnabled);
        Assert.False(session.GetStatus().SimulatedHumEnabled);
        Assert.False(session.GetStatus().SimulatedHarmonicsEnabled);
    }

    /// <summary>
    /// The user's simulator starts nearly clean: −90 dBV of hum and a −150 dBV/√Hz floor are
    /// impairments patched in, and each is a checkbox to switch on rather than a number to explain
    /// away. The generator's 0.51 % harmonic stack stays on, so the FFT screen has a THD to show on
    /// a linear model. The device type's own defaults stay on, because the bench most tests are
    /// written against has them.
    /// </summary>
    [Fact]
    public void AnalyzerDeviceFactory_HandsOutTheSimulatorWithNoiseAndHarmonicsOnAndHumOff()
    {
        var factory = new AnalyzerDeviceFactory(NullLoggerFactory.Instance, DeviceKind.Simulated);

        var device = Assert.IsType<SimulatedDevice>(factory.Create(DeviceKind.Simulated));

        // Noise on since 2026-10-06, and the reason is in CreateSimulator's own remarks: a bench
        // with no noise at all is not a clean one, it is one where a stopband bin is *exactly* zero
        // and the phase of that bin is whatever atan2(0, 0) made of it. Coherence reads 1.0
        // throughout, so nothing downstream can tell it is looking at an artefact.
        Assert.True(device.SignalProfile.NoiseEnabled);
        Assert.False(device.SignalProfile.HumEnabled);
        Assert.True(device.SignalProfile.HarmonicsEnabled);
        // Everything else is the device's own default — the factory only touches the one switch.
        Assert.Equal(new SimulatedDevice().SignalProfile.Dut, device.SignalProfile.Dut);
        Assert.Equal(new SimulatedDevice().SignalProfile.GeneratorLeft, device.SignalProfile.GeneratorLeft);
    }

    [Fact]
    public async Task TrySetSimulatedProfile_PatchesCrosstalk_AndLeavesTheRestAlone()
    {
        var session = CreateService();
        await session.ConnectAsync(CancellationToken.None);

        // Off out of the box: every ratio measurement would otherwise carry it.
        Assert.False(session.SimulatedCrosstalkEnabled);
        Assert.False(session.GetStatus().SimulatedCrosstalkEnabled);

        Assert.True(session.TrySetSimulatedProfile(noiseEnabled: null, humEnabled: null, crosstalkEnabled: true));

        Assert.True(session.SimulatedCrosstalkEnabled);
        Assert.True(session.GetStatus().SimulatedCrosstalkEnabled);
        // The other three switches are untouched.
        Assert.True(session.SimulatedNoiseEnabled);
        Assert.True(session.SimulatedHumEnabled);
        Assert.True(session.SimulatedHarmonicsEnabled);
    }

    [Fact]
    public async Task TrySetSimulatedProfile_PatchesTheOutputStageAndLoad_AndLeavesTheRestAlone()
    {
        var session = CreateService();
        await session.ConnectAsync(CancellationToken.None);

        // An ideal source with nothing on it out of the box: no existing measurement sees a divider.
        Assert.Equal(SimulatedOutputStageModel.None, session.SimulatedOutputStage);
        Assert.Equal(SimulatedLoad.Open, session.SimulatedLoad);
        Assert.Equal("None", session.GetStatus().SimulatedOutputStage);
        Assert.Equal("Open", session.GetStatus().SimulatedLoad);

        Assert.True(session.TrySetSimulatedProfile(noiseEnabled: null, humEnabled: null, outputStage: SimulatedOutputStageModel.SolidState));
        Assert.Equal(SimulatedOutputStageModel.SolidState, session.SimulatedOutputStage);
        Assert.Equal(SimulatedLoad.Open, session.SimulatedLoad);

        Assert.True(session.TrySetSimulatedProfile(noiseEnabled: null, humEnabled: null, load: SimulatedLoad.EightOhms));
        Assert.Equal(SimulatedOutputStageModel.SolidState, session.SimulatedOutputStage);
        Assert.Equal("EightOhms", session.GetStatus().SimulatedLoad);
        Assert.True(session.SimulatedNoiseEnabled);
        Assert.False(session.SimulatedCrosstalkEnabled);
    }

    [Fact]
    public async Task TrySetSimulatedProfile_LeavesOmittedSettingsAlone()
    {
        // Patch semantics: the UI's three checkboxes each send only their own field, so toggling one
        // must not reset the others.
        var session = CreateService();
        await session.ConnectAsync(CancellationToken.None);

        Assert.True(session.TrySetSimulatedProfile(noiseEnabled: false, humEnabled: null));

        Assert.False(session.SimulatedNoiseEnabled);
        Assert.True(session.SimulatedHumEnabled);
        Assert.True(session.SimulatedHarmonicsEnabled);

        Assert.True(session.TrySetSimulatedProfile(noiseEnabled: null, humEnabled: false));

        Assert.False(session.SimulatedNoiseEnabled);
        Assert.False(session.SimulatedHumEnabled);
        Assert.True(session.SimulatedHarmonicsEnabled);

        Assert.True(session.TrySetSimulatedProfile(noiseEnabled: null, humEnabled: null, harmonicsEnabled: false));

        Assert.False(session.SimulatedNoiseEnabled);
        Assert.False(session.SimulatedHumEnabled);
        Assert.False(session.SimulatedHarmonicsEnabled);
    }

    [Fact]
    public async Task TrySetSimulatedProfile_ReportsFailureForANonSimulatedDevice()
    {
        // Real hardware has no such knob, so the endpoint must be able to answer with a 400 rather
        // than silently accepting a setting that does nothing. Status reports null for the same reason.
        var session = new DeviceSessionService(
            new StubDevice(),
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        Assert.False(session.TrySetSimulatedProfile(noiseEnabled: false, humEnabled: false, harmonicsEnabled: false));
        Assert.Null(session.SimulatedNoiseEnabled);
        Assert.Null(session.SimulatedHumEnabled);
        Assert.Null(session.SimulatedHarmonicsEnabled);
        Assert.Null(session.GetStatus().SimulatedNoiseEnabled);
        Assert.Null(session.GetStatus().SimulatedHumEnabled);
        Assert.Null(session.GetStatus().SimulatedHarmonicsEnabled);
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_EnablingAChannelStartsTheStreamIndependentlyOfAcquisitionAndStopsWhenDisabled()
    {
        // Regression test for the bug where enabling the generator did nothing until Start Acquisition
        // was pressed: DeviceSessionService must bring the device stream up on its own when a generator
        // channel is enabled, without flipping on Acquiring/the spectrum pipeline, and tear it back down
        // once every channel is off again (mirroring StopAcquisitionAsync's own teardown).
        var device = new RecordingDevice();
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var streamHint = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 4096);
        var generatorConfig = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]), ChannelGeneratorConfig.Off);

        await session.ConfigureGeneratorAsync(generatorConfig, CancellationToken.None, streamHint);

        // EnsureStreamRunningAsync only schedules the pump loop via Task.Run; give it a moment to
        // actually start enumerating StreamAsync.
        for (int i = 0; i < 100 && device.StreamStartCount == 0; i++)
            await Task.Delay(10);

        Assert.Equal(1, device.StreamStartCount);
        Assert.False(session.Acquiring);

        await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);

        Assert.True(device.StopRequested);
        Assert.Equal(1, device.StreamEndCount);
    }

    /// <summary>
    /// The capture delay is the DUT's settle time, and it is applied at the one place every stimulus
    /// change passes through — not in the browser's Run handler, where it used to live and where no
    /// sweep point ever saw it. Measured against the simulator's tube-amp model with no settle: the
    /// 12 Hz high-pass ringing on the tone step sat at −77 dBV across the bins below the tone in
    /// every point of a THD sweep, read as DC, and was gone once the record started 300 ms after
    /// the step. Three things pinned here: a generator change while acquiring discards the device
    /// latency <i>plus</i> the settle; an input-range change discards the latency alone (the DUT
    /// never sees an ADC gain step); and the applied value is recorded for the saved result.
    /// </summary>
    [Fact]
    public async Task ConfigureGeneratorAsync_WhileAcquiring_DiscardsTheStimulusLatencyPlusTheCaptureDelay()
    {
        var device = new RecordingDevice { LatencyFrames = 3584 };
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var delay = new CaptureDelaySetting { Ms = 100.0 };
        var session = new DeviceSessionService(device, pipeline, delay, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 4096);
        await session.StartAcquisitionAsync(config, CancellationToken.None);
        Assert.Null(session.LastAppliedCaptureDelayMs);

        var tone = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]), ChannelGeneratorConfig.Off);
        await session.ConfigureGeneratorAsync(tone, CancellationToken.None);

        // 100 ms at 48 kHz is 4800 frames, on top of the 3584 the device reports in flight.
        Assert.Equal(3584 + 4800, pipeline.LastFlushDiscardFrames);
        Assert.Equal(100.0, session.LastAppliedCaptureDelayMs);

        await session.SetInputRangeAsync(6.0, CancellationToken.None);
        Assert.Equal(3584, pipeline.LastFlushDiscardFrames);

        // Edited between two stimulus changes: the next one uses the new value, and the record follows it.
        delay.Ms = 250.0;
        await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);
        Assert.Equal(3584 + 12_000, pipeline.LastFlushDiscardFrames);
        Assert.Equal(250.0, session.LastAppliedCaptureDelayMs);

        await session.ShutdownAsync();
    }

    /// <summary>
    /// An autorange step is reported in band, on the first block captured after it, so the news
    /// reaches <c>PumpAsync</c> a block late — measured 47 ms on a QA403 at 192 kHz. A sweep runner
    /// has moved on by then: <c>ThdSweepRunner</c> programs the next point's input range and then
    /// immediately configures its tone, and that configure books the stimulus latency plus the
    /// capture delay. Answering the late flag with a bare <c>Flush()</c> ran
    /// <c>SampleRingBuffer.Reset(0)</c>, which <i>overwrites</i> the counter, so the discard was
    /// deleted and nothing re-booked it — the point's averages began at the tone step instead of a
    /// capture delay after it.
    ///
    /// <para>On a real EL84 amplifier, 2026-09-29, that was a 500x THD spike and SNR down from
    /// 109 dB to 32 on exactly the two points of a 122-point sweep that followed a range change
    /// (404.3 Hz: THD 0.0374 %, SNR 34.1 dB; 1160.2 Hz: 0.0617 %, 31.4 dB), and on no others. The
    /// mechanism is pinned in <c>SpectrumPipelineFlushTests</c>; this pins the call site, which is
    /// the half that has to be the range-step flush rather than the bare one.</para>
    /// </summary>
    [Fact]
    public async Task ALateRangeStepReport_DoesNotCancelTheDiscardTheNextPointsToneBooked()
    {
        var device = new RecordingDevice { LatencyFrames = 3584, ScriptedBlocks = true };
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var delay = new CaptureDelaySetting { Ms = 100.0 };
        var session = new DeviceSessionService(device, pipeline, delay, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 4096);
        await session.StartAcquisitionAsync(config, CancellationToken.None);

        // The next sweep point, in the runner's order: program the predicted range, then the tone.
        await session.SetInputRangeAsync(6.0, CancellationToken.None);
        var tone = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]), ChannelGeneratorConfig.Off);
        await session.ConfigureGeneratorAsync(tone, CancellationToken.None);

        const int booked = 3584 + 4800;
        Assert.Equal(booked, pipeline.LastFlushDiscardFrames);
        Assert.Equal(booked, pipeline.PendingDiscardFrames);

        // ...and only now does the device's report of that range step arrive.
        device.PushBlock(config, startsAfterDiscontinuity: true);

        // The flagged block is enqueued after the flush, so it consumes its own 4096 frames of the
        // discard and no more. With the bug the flush zeroed the counter first and the block landed
        // in the record instead.
        for (int i = 0; i < 500 && pipeline.PendingDiscardFrames == booked; i++)
            await Task.Delay(10);

        Assert.Equal(booked - config.BlockFrameCount, pipeline.PendingDiscardFrames);

        await session.ShutdownAsync();
    }

    /// <summary>
    /// The FFT screen's Run configures the generator <i>before</i> acquisition starts, so the
    /// generator change has no window to flush and the settle would be applied to nothing. The
    /// acquisition start applies it instead — the settle alone, with no stimulus latency, since
    /// nothing changed on the wire — and only when a generator channel is on: a generator-less
    /// capture has nothing to settle.
    ///
    /// <para>The Single button is the same start with <c>SingleShot</c> already set on the pipeline,
    /// and the settle discard must still be the <i>last</i> flush to land. It used to be armed by a
    /// separate call after the start that did a bare <c>Flush()</c>, which zeroed the settle just
    /// booked: a Single's first record then began at the tone step while Run's began a capture
    /// delay later, and on the simulator's tube amp that was a bump around 50 Hz that no number of
    /// averages removed.</para>
    /// </summary>
    [Fact]
    public async Task StartAcquisitionAsync_WithTheGeneratorAlreadyOn_DiscardsTheCaptureDelay()
    {
        var device = new RecordingDevice { LatencyFrames = 3584 };
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var delay = new CaptureDelaySetting { Ms = 100.0 };
        var session = new DeviceSessionService(device, pipeline, delay, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 4096);

        // Generator off: Start()'s own bare flush is the last one.
        await session.StartAcquisitionAsync(config, CancellationToken.None);
        Assert.Equal(0, pipeline.LastFlushDiscardFrames);
        Assert.Null(session.LastAppliedCaptureDelayMs);
        await session.StopAcquisitionAsync();

        var tone = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]), ChannelGeneratorConfig.Off);
        await session.ConfigureGeneratorAsync(tone, CancellationToken.None, config);
        await session.StartAcquisitionAsync(config, CancellationToken.None);

        Assert.Equal(4800, pipeline.LastFlushDiscardFrames);
        Assert.Equal(100.0, session.LastAppliedCaptureDelayMs);
        await session.StopAcquisitionAsync();

        // Single: the flag is set ahead of the start (as /api/acquisition/start does), and the
        // start's flushes arm it -- the settle discard is still what the record waits for.
        pipeline.Options = pipeline.Options with { SingleShot = true };
        await session.StartAcquisitionAsync(config, CancellationToken.None);

        Assert.True(pipeline.Options.SingleShot);
        Assert.False(pipeline.SingleShotComplete);
        Assert.Equal(4800, pipeline.LastFlushDiscardFrames);

        await session.ShutdownAsync();
    }

    [Fact]
    public async Task ShutdownAsync_StopsTheStreamEvenWhenTheGeneratorIsOnAndAcquisitionNeverStarted()
    {
        // The two states the app is normally left in, and the two StopAcquisitionAsync tears nothing
        // down in: it returns immediately when acquisition was never started, and it deliberately
        // keeps the stream up while the generator is enabled. Either way the pump was still running
        // when DI disposed the device, so TransferPump's finally — the only thing that writes
        // StreamingStop and collects in-flight EP2 transfers — never ran before libusb_close, and the
        // QA403 was left streaming and unreachable to every app until power-cycled.
        var device = new RecordingDevice();
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var streamHint = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 4096);
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -20.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None,
            streamHint);

        for (int i = 0; i < 100 && device.StreamStartCount == 0; i++)
            await Task.Delay(10);
        Assert.Equal(1, device.StreamStartCount);
        Assert.False(session.Acquiring);

        // What StopAcquisitionAsync would have done here: nothing at all.
        await session.StopAcquisitionAsync();
        Assert.Equal(0, device.StreamEndCount);

        await session.ShutdownAsync();

        Assert.True(device.StopRequested);
        Assert.Equal(1, device.StreamEndCount);
    }

    [Fact]
    public async Task ShutdownAsync_IsSafeWhenNothingWasEverStarted()
    {
        // Runs on every exit, including one where the user only ever looked at the UI.
        var device = new RecordingDevice();
        var session = new DeviceSessionService(
            device,
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        await session.ShutdownAsync();

        Assert.Equal(0, device.StreamStartCount);
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_SnapsToneFrequenciesToTheNearestCoherentBin()
    {
        var device = new RecordingDevice();
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 131072 },
        };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        // 1000 Hz at 192 kHz over 131072 points lands on bin 682.67 — about as far off-bin as it
        // gets, and the reason a requested "1000" showed leakage skirts around the fundamental.
        // Bin 683 is coprime to 2^17, so the snap lands there: 683 * 192000 / 131072 = 1000.4883 Hz.
        // That is the same frequency QuantAsylum's own app reports as "1.0004 kHz" for these settings.
        var hint = new AcquisitionConfig(SampleRateHz: 192_000, ChannelCount: 2, BlockFrameCount: 4096);
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -6.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None,
            hint);

        Assert.Equal(1000.4883, session.GetStatus().GeneratorLeft.Tones[0].FreqHz, precision: 4);

        // And the status reports the effective frequency, so the UI shows what is really playing
        // rather than what was typed.
        Assert.True(session.GetStatus().GeneratorSnapToBin);
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_SnapToBinOff_LeavesTheRequestedFrequencyAlone()
    {
        var device = new RecordingDevice();
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 131072 },
        };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var hint = new AcquisitionConfig(SampleRateHz: 192_000, ChannelCount: 2, BlockFrameCount: 4096);
        var config = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -6.0)]), ChannelGeneratorConfig.Off)
        {
            SnapToBin = false,
        };

        await session.ConfigureGeneratorAsync(config, CancellationToken.None, hint);

        Assert.Equal(1000.0, session.GetStatus().GeneratorLeft.Tones[0].FreqHz, precision: 6);
    }

    [Fact]
    public async Task ReapplyGeneratorAsync_ResnapsFromTheOriginalRequestNotThePreviouslySnappedValue()
    {
        // Coherence depends on the analysis length, so changing FFT size must re-snap — and it must
        // re-snap the frequency the user asked for. Re-snapping an already-snapped value would let
        // the tone walk a bin at a time every time the FFT size changed.
        var device = new RecordingDevice();
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance)
        {
            Options = new SpectrumPipelineOptions { FftSize = 131072 },
        };
        var session = new DeviceSessionService(device, pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        var hint = new AcquisitionConfig(SampleRateHz: 192_000, ChannelCount: 2, BlockFrameCount: 4096);
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -6.0)]), ChannelGeneratorConfig.Off),
            CancellationToken.None,
            hint);
        Assert.Equal(1000.4883, session.GetStatus().GeneratorLeft.Tones[0].FreqHz, precision: 4);

        // 16384 points at 192 kHz: bin 11.71875 Hz, 1000 Hz -> bin 85.33 -> 85 -> 996.09375 Hz.
        pipeline.Options = pipeline.Options with { FftSize = 16384 };
        await session.ReapplyGeneratorAsync(CancellationToken.None);

        Assert.Equal(996.09375, session.GetStatus().GeneratorLeft.Tones[0].FreqHz, precision: 5);
    }

    /// <summary>
    /// Records stream lifecycle calls so tests can observe whether/when <see cref="IAnalyzerDevice.StreamAsync"/>
    /// is actually running, independent of <see cref="DeviceSessionService.Acquiring"/>.
    /// </summary>
    private sealed class RecordingDevice : IAnalyzerDevice
    {
        public event EventHandler? DeviceLost { add { } remove { } }

        public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

        public int StreamStartCount { get; private set; }

        public int StreamEndCount { get; private set; }

        public bool StopRequested { get; private set; }

        /// <summary>0 by default, like the simulator once was; a test that cares sets a hardware-like figure.</summary>
        public int LatencyFrames { get; init; }

        public int StimulusLatencyFrames(double sampleRateHz) => LatencyFrames;

        public Task<DeviceInfo> ConnectAsync(CancellationToken ct) =>
            Task.FromResult(new DeviceInfo("QA403", "HW-0001", "1.0", IsSimulated: false));

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);

        public double InputRangeDbv { get; private set; }

        public long DroppedRxChunks => 0;

        public long DroppedBlocks => 0;

        public bool AutoRangeEnabled { get; set; }

        public double AutoRangeStepDownDelayMs { get; set; }

        public bool AutoRangeStepDownEnabled { get; set; } = true;

        public Task SetInputRangeAsync(double dbv, CancellationToken ct)
        {
            InputRangeDbv = dbv;
            return Task.CompletedTask;
        }

        public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => Task.CompletedTask;

        public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync()
        {
            StopRequested = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Hands the stream over to <see cref="PushBlock"/> instead of free-running, so a test can
        /// place one block at one instant relative to the session calls around it — which is what
        /// the in-band range-step report needs: its whole point is that it arrives <i>late</i>.
        /// Opt-in, and <c>init</c>-only, so every other test keeps the free-running stream.
        /// </summary>
        public bool ScriptedBlocks { get; init; }

        private readonly Channel<PcmBlock> _scripted = Channel.CreateUnbounded<PcmBlock>();

        /// <summary>Queues one silent block for a scripted stream to yield.</summary>
        public void PushBlock(AcquisitionConfig config, bool startsAfterDiscontinuity = false)
        {
            int sampleCount = config.BlockFrameCount * config.ChannelCount;
            float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);
            Array.Clear(buffer, 0, sampleCount);
            _scripted.Writer.TryWrite(new PcmBlock(
                buffer, sampleCount, config.SampleRateHz, config.ChannelCount, 0, 0, startsAfterDiscontinuity));
        }

        public async IAsyncEnumerable<PcmBlock> StreamAsync(AcquisitionConfig config, [EnumeratorCancellation] CancellationToken ct)
        {
            StreamStartCount++;
            try
            {
                if (ScriptedBlocks)
                {
                    while (await _scripted.Reader.WaitToReadAsync(ct))
                    {
                        while (_scripted.Reader.TryRead(out var scripted))
                            yield return scripted;
                    }

                    yield break;
                }

                while (!ct.IsCancellationRequested)
                {
                    float[] buffer = ArrayPool<float>.Shared.Rent(config.BlockFrameCount * config.ChannelCount);
                    yield return new PcmBlock(
                        buffer, config.BlockFrameCount * config.ChannelCount, config.SampleRateHz, config.ChannelCount, 0, 0);
                    await Task.Delay(5, CancellationToken.None);
                }
            }
            finally
            {
                StreamEndCount++;
            }
        }
    }

    private static DeviceSessionService CreateStubService(StubDevice device) => new(
        device,
        new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
        NullLogger<DeviceSessionService>.Instance);

    /// <summary>8.6 dBV on the left — the bench tone the ranging comparison was made with. Needs the 18 dBV output range and clips against anything below it.</summary>
    private static GeneratorConfig LoudTone() =>
        new(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, 8.6)]), ChannelGeneratorConfig.Off);

    /// <summary>
    /// Pink noise at 12 dBV: its crest factor puts the peak near 16 V, where the widest output range
    /// delivers 11.2 — the level measured clipping on the bench, and the one Auto derives onto the
    /// top range and still cannot carry.
    /// </summary>
    private static ChannelGeneratorConfig LoudNoiseChannel() =>
        new(Enabled: true, Tones: [])
        {
            Kind = GeneratorKind.PinkNoise,
            Noise = new WhiteNoiseSettings { LevelDbv = 12.0 },
        };

    /// <summary>-40 dBV: quiet enough that the derivation picks the *smallest* output range, so a pinned wide one is visibly not it.</summary>
    private static GeneratorConfig QuietTone() =>
        new(new ChannelGeneratorConfig(true, [new GeneratorTone(1000.0, -40.0)]), ChannelGeneratorConfig.Off);

    /// <summary>
    /// The point of manual ranging: the output range stops following the generator. Auto derives it
    /// on every generator change (an analog attenuator whose setting sets the DAC's noise floor, so
    /// the app normally picks the quietest one that fits), and a "Manual" that kept doing that would
    /// pin nothing at all — the next generator change would take the range straight back.
    /// </summary>
    [Fact]
    public async Task ManualRanging_StopsTheOutputRangeFollowingTheGenerator()
    {
        var session = CreateStubService(new StubDevice());
        await session.ConnectAsync(CancellationToken.None);

        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);
        await session.SetOutputRangeAsync(18.0, CancellationToken.None);

        await session.ConfigureGeneratorAsync(QuietTone(), CancellationToken.None);

        Assert.Equal(18.0, session.OutputRangeDbv);
    }

    /// <summary>
    /// And the same call under Auto still derives, so the test above is pinning behaviour rather
    /// than recording that the derivation was deleted.
    /// </summary>
    [Fact]
    public async Task AutoRanging_StillDerivesTheOutputRangeFromTheGenerator()
    {
        var session = CreateStubService(new StubDevice());
        await session.ConnectAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(QuietTone(), CancellationToken.None);
        Assert.Equal(-12.0, session.OutputRangeDbv);

        await session.ConfigureGeneratorAsync(LoudTone(), CancellationToken.None);
        Assert.Equal(18.0, session.OutputRangeDbv);
    }

    /// <summary>
    /// Going back to Auto re-derives immediately rather than at the next generator change. Waiting
    /// would leave a pinned range in force under a status bar that had gone back to saying "Auto" —
    /// and in the direction that matters, a narrow pin left behind a loud tone keeps clipping.
    /// </summary>
    [Fact]
    public async Task SwitchingBackToAuto_RederivesTheOutputRangeAtOnce()
    {
        var session = CreateStubService(new StubDevice());
        await session.ConnectAsync(CancellationToken.None);

        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);
        await session.SetOutputRangeAsync(18.0, CancellationToken.None);
        await session.ConfigureGeneratorAsync(QuietTone(), CancellationToken.None);
        Assert.Equal(18.0, session.OutputRangeDbv);

        await session.SetRangingModeAsync(RangingMode.Auto, CancellationToken.None);

        Assert.Equal(-12.0, session.OutputRangeDbv);
    }

    /// <summary>
    /// Switching to Manual while stopped must pin the range the last stimulus ran on, not the one
    /// silence left behind.
    ///
    /// <para>Stop disables both generator channels, and Auto then derives the smallest output range
    /// — right for a noise-floor measurement, useless as a starting point for a pinned one. Since
    /// Manual is almost always selected while stopped, the dropdown opened on -12 dBV every time and
    /// the first Run after it clipped against it. Reported from the bench as "it jumps always to
    /// -12 dBV".</para>
    /// </summary>
    [Fact]
    public async Task SelectingManual_WhileStopped_PinsTheRangeTheLastStimulusUsed()
    {
        var session = CreateStubService(new StubDevice());
        await session.ConnectAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(LoudTone(), CancellationToken.None);
        Assert.Equal(18.0, session.OutputRangeDbv);

        // What Stop does: both channels off. Auto correctly parks on the quietest range.
        await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);
        Assert.Equal(-12.0, session.OutputRangeDbv);

        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);

        Assert.Equal(18.0, session.OutputRangeDbv);
    }

    /// <summary>
    /// And the seeding happens once, on the way in. A second Manual is not a reset — the pinned
    /// range is the user's by then, and re-deriving it would undo the choice they just made.
    /// </summary>
    [Fact]
    public async Task SelectingManual_Twice_DoesNotReseedThePinnedRange()
    {
        var session = CreateStubService(new StubDevice());
        await session.ConnectAsync(CancellationToken.None);
        await session.ConfigureGeneratorAsync(LoudTone(), CancellationToken.None);
        await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);

        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);
        await session.SetOutputRangeAsync(-2.0, CancellationToken.None);

        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);

        Assert.Equal(-2.0, session.OutputRangeDbv);
    }

    /// <summary>
    /// With nothing ever played there is no stimulus to seed from, so the connect's own choice — the
    /// widest range, maximum headroom — stands. Guessing a narrow one for a stimulus nobody has
    /// described yet is the clipping case again, from the other direction.
    /// </summary>
    [Fact]
    public async Task SelectingManual_WithNothingEverPlayed_KeepsTheWidestOutputRange()
    {
        var session = CreateStubService(new StubDevice());
        await session.ConnectAsync(CancellationToken.None);

        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);

        Assert.Equal(18.0, session.OutputRangeDbv);
    }

    /// <summary>
    /// The other half of the mode. Without this the device's own ranger walks the pinned input range
    /// away within a block or two, and every runner's "leave a manually pinned range alone" branch
    /// (they all test <c>AutoRangeEnabled != false</c>) never fires.
    /// </summary>
    [Fact]
    public async Task ManualRanging_TurnsTheDevicesOwnRangerOff_AndAutoTurnsItBackOn()
    {
        var device = new StubDevice();
        var session = CreateStubService(device);
        await session.ConnectAsync(CancellationToken.None);
        Assert.True(device.AutoRangeEnabled);

        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);
        Assert.False(device.AutoRangeEnabled);
        Assert.Equal(RangingMode.Manual, session.RangingMode);

        await session.SetRangingModeAsync(RangingMode.Auto, CancellationToken.None);
        Assert.True(device.AutoRangeEnabled);
    }

    /// <summary>
    /// A device brought up while the session is already in Manual has to arrive with its ranger off.
    /// Devices have their own field defaults and a connect that only pushed the two range values
    /// would leave the third unsynced — the same reasoning that makes ConnectAsync write the input
    /// and output ranges rather than only caching them.
    /// </summary>
    [Fact]
    public async Task Connecting_AppliesTheRangingModeTheSessionIsAlreadyIn()
    {
        var device = new StubDevice { AutoRangeEnabled = true };
        var session = CreateStubService(device);

        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);
        await session.ConnectAsync(CancellationToken.None);

        Assert.False(device.AutoRangeEnabled);
        Assert.Equal("Manual", session.GetStatus().RangingMode);
    }

    /// <summary>
    /// A pinned range the generator overruns clips the DAC, and nothing else on screen says so: the
    /// generator panel still reads the level that was asked for and the spectrum still shows a tone
    /// at the right frequency. The second half is Auto re-deriving a range that fits <i>this</i>
    /// tone — not the general claim that Auto cannot clip, which is false and is what
    /// <see cref="OutputRangeClipping_IsReportedInAutoWhenTheLevelOverrunsTheTopRange"/> pins.
    /// </summary>
    [Fact]
    public async Task OutputRangeClipping_IsReportedWhenAPinnedRangeCannotCarryTheGenerator()
    {
        var session = CreateStubService(new StubDevice());
        await session.ConnectAsync(CancellationToken.None);

        await session.SetRangingModeAsync(RangingMode.Manual, CancellationToken.None);
        await session.SetOutputRangeAsync(-12.0, CancellationToken.None);
        await session.ConfigureGeneratorAsync(LoudTone(), CancellationToken.None);

        Assert.True(session.GetStatus().OutputRangeClipping);

        await session.SetRangingModeAsync(RangingMode.Auto, CancellationToken.None);
        Assert.False(session.GetStatus().OutputRangeClipping);
    }

    /// <summary>
    /// <b>Auto clips too, which this file and the status record both used to deny.</b>
    /// <see cref="OutputRangeSelector.Select"/> ends with "or the largest supported range when
    /// nothing fits (better a range that clips than one that clips harder)", so a level above the top
    /// range's full scale is derived onto that range and clips against it. The old comment —
    /// "under Auto the range is derived from this very config, so it is false by construction" —
    /// held only for levels the ladder can actually carry.
    ///
    /// <para>Pink noise at 12 dBV is the case measured on the bench: its crest factor puts the peak
    /// near 16 V where the 18 dBV range delivers 11.2, and the recorded stimulus came back with its
    /// crest collapsed and 0.11 % of samples at the rail — with the coherence reading 0.996, which
    /// is why a flag is the only thing that can report it.</para>
    /// </summary>
    [Fact]
    public async Task OutputRangeClipping_IsReportedInAutoWhenTheLevelOverrunsTheTopRange()
    {
        var device = new StubDevice();
        var session = CreateStubService(device);
        await session.ConnectAsync(CancellationToken.None);

        Assert.Equal("Auto", session.GetStatus().RangingMode);

        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(
                LoudNoiseChannel(),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        // Derived onto the widest range there is, and still not enough.
        Assert.Equal(device.Capabilities.OutputRangesDbv[^1], session.OutputRangeDbv);
        Assert.True(session.GetStatus().OutputRangeClipping);
    }

    /// <summary>
    /// The clipping a <b>sweep</b> does is gone by the time anyone looks, so it is remembered for the
    /// length of the acquisition.
    ///
    /// <para>Every point of a sweep configures its own stimulus. A run whose loudest points overran
    /// the DAC therefore leaves <see cref="DeviceSessionService.OutputRangeClipping"/> false the
    /// moment it steps down again, and the curve on screen has nothing anywhere to say that part of
    /// it was measured through a clipped stimulus — worst on the distortion sweeps, where clipping
    /// adds exactly the harmonics being plotted. Zeroed by the next acquisition, like the two drop
    /// counters and for the reason given there.</para>
    /// </summary>
    [Fact]
    public async Task OutputClippedDuringAcquisition_SurvivesTheStimulusThatCausedIt_AndIsClearedByTheNextRun()
    {
        var session = CreateStubService(new StubDevice());
        await session.ConnectAsync(CancellationToken.None);
        await session.StartAcquisitionAsync(new AcquisitionConfig(48000.0, 2, 1024), CancellationToken.None);

        Assert.False(session.GetStatus().OutputClippedDuringAcquisition);

        // One loud point of a sweep...
        await session.ConfigureGeneratorAsync(
            new GeneratorConfig(
                LoudNoiseChannel(),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);
        Assert.True(session.GetStatus().OutputRangeClipping);

        // ...and the next one is quiet again. The live flag clears; the run's own does not.
        await session.ConfigureGeneratorAsync(QuietTone(), CancellationToken.None);
        Assert.False(session.GetStatus().OutputRangeClipping);
        Assert.True(session.GetStatus().OutputClippedDuringAcquisition);

        // A fresh acquisition is a fresh measurement: a stale "it clipped" reads exactly like one
        // that just happened, which is the DroppedRxChunks lesson.
        await session.StopAcquisitionAsync();
        await session.StartAcquisitionAsync(new AcquisitionConfig(48000.0, 2, 1024), CancellationToken.None);
        Assert.False(session.GetStatus().OutputClippedDuringAcquisition);
    }

    /// <summary>Stands in for real hardware: an <see cref="IAnalyzerDevice"/> that is not the simulator.</summary>
    private sealed class StubDevice : IAnalyzerDevice
    {
        public event EventHandler? DeviceLost { add { } remove { } }

        public DeviceCapabilities Capabilities => DeviceCapabilities.Qa403;

        /// <summary>Non-zero, like real hardware — this stub stands in for a device that commits audio to the DAC ahead of time.</summary>
        public int StimulusLatencyFrames(double sampleRateHz) => 3584;

        public Task<DeviceInfo> ConnectAsync(CancellationToken ct) =>
            Task.FromResult(new DeviceInfo("QA403", "HW-0001", "1.0", IsSimulated: false));

        public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);

        public double InputRangeDbv { get; private set; }

        /// <summary>What <see cref="IAnalyzerDevice.Telemetry"/> answers; null stands in for a device with no sensors.</summary>
        public DeviceTelemetry? TelemetryReading { get; init; }

        public DeviceTelemetry? Telemetry => TelemetryReading;

        /// <summary>Settable, and independently of <see cref="BlockDrops"/>, so a status response that summed the two could not pass this test.</summary>
        public long RxChunkDrops { get; init; }

        public long BlockDrops { get; init; }

        public long DroppedRxChunks => RxChunkDrops;

        public long DroppedBlocks => BlockDrops;

        public bool AutoRangeEnabled { get; set; }

        public double AutoRangeStepDownDelayMs { get; set; }

        public bool AutoRangeStepDownEnabled { get; set; } = true;

        public Task SetInputRangeAsync(double dbv, CancellationToken ct)
        {
            InputRangeDbv = dbv;
            return Task.CompletedTask;
        }

        public Task SetOutputRangeAsync(double dbv, CancellationToken ct) => Task.CompletedTask;

        public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct) => Task.CompletedTask;

        public IAsyncEnumerable<PcmBlock> StreamAsync(AcquisitionConfig config, CancellationToken ct) =>
            AsyncEnumerable.Empty<PcmBlock>();

        public Task StopAsync() => Task.CompletedTask;
    }
}
