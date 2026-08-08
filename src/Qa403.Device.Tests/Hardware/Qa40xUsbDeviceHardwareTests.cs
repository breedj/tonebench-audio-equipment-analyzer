using Microsoft.Extensions.Logging.Abstractions;
using Qa403.Device.Hardware;

namespace Qa403.Device.Tests.Hardware;

/// <summary>
/// Requires a real QA403 connected over USB, with the output looped back to the input (task's
/// debugging note: "The output of channel L is connected to the input channel L. The output of
/// channel R is connected to the input channel R."). Excluded from the default run — see
/// code/CLAUDE.md for how to run this suite explicitly.
/// </summary>
[Trait("Category", "Hardware")]
public class Qa40xUsbDeviceHardwareTests
{
    /// <summary>Fixed generator level for the loopback test: comfortably inside the fixed 8 dBV output range used below, and measurable at every input range from 0 to 42 dBV.</summary>
    private const double LoopbackCommandedDbv = -6.0;

    private const double LoopbackOutputRangeDbv = 8.0;

    [Fact]
    public async Task EnumerateAndConnect_FindsRealQa403()
    {
        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);

        DeviceInfo info = await device.ConnectAsync(CancellationToken.None);

        Assert.False(info.IsSimulated);
        Assert.Equal("QA403", info.ProductName);
        Assert.False(string.IsNullOrWhiteSpace(info.SerialNumber));
    }

    /// <summary>
    /// The acceptance criterion for this whole task: until this passes on every input range,
    /// the calibration path (Qa40xCalibration) is not proven correct on real hardware.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(6.0)]
    [InlineData(12.0)]
    [InlineData(18.0)]
    [InlineData(24.0)]
    [InlineData(30.0)]
    [InlineData(36.0)]
    [InlineData(42.0)]
    public async Task Loopback_MeasuredAmplitudeMatchesCommanded_Within0Point1Db(double inputRangeDbv)
    {
        const double sampleRateHz = 48_000.0;
        const double toneFreqHz = 1000.0;
        const double captureSeconds = 2.0;

        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);
        // This test verifies calibration at a *forced* range — autorange would immediately start
        // correcting away from it, since the fixed loopback signal is under-range at the higher
        // input ranges (the QA403's own generator can't reach anywhere near their full scale).
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(inputRangeDbv, CancellationToken.None);
        await device.SetOutputRangeAsync(LoopbackOutputRangeDbv, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(toneFreqHz, LoopbackCommandedDbv)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var config = new AcquisitionConfig(sampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
        double sumSquares = 0.0;
        long samplesSeen = 0;
        long targetSamples = (long)(sampleRateHz * captureSeconds);
        bool firstBlock = true;

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                // Skip the first block: the range/generator writes above need a moment to settle
                // through the analog path before the loopback signal is representative.
                if (!firstBlock)
                {
                    foreach (var s in block.Samples)
                    {
                        sumSquares += (double)s * s;
                        samplesSeen++;
                    }
                }

                firstBlock = false;
            }

            if (samplesSeen >= targetSamples)
                break;
        }

        await device.StopAsync();

        double measuredVrms = Math.Sqrt(sumSquares / samplesSeen);
        double measuredDbv = 20.0 * Math.Log10(measuredVrms);

        Assert.True(
            Math.Abs(measuredDbv - LoopbackCommandedDbv) < 0.1,
            $"Input range {inputRangeDbv} dBV: commanded {LoopbackCommandedDbv} dBV, measured {measuredDbv} dBV");
    }

    /// <summary>
    /// Regression guard for the L/R channel crossing. The QA403 swaps left and right on the DAC only,
    /// so the swap belongs in the TX path alone; applying it on receive as well swaps twice and sends
    /// a logical-left tone back on logical right. Measured directly on this hardware: driving wire slot
    /// 1 alone returns at full level on RX slot 0, and vice versa.
    ///
    /// The eight-range loopback theory above cannot catch this, because it streams
    /// <c>ChannelCount: 1</c> — with both channels driven it would read a plausible level either way.
    /// This one drives *only* the left generator and asserts the tone is on left and absent from right,
    /// so a re-introduced swap fails loudly instead of silently reporting the wrong channel's audio.
    /// </summary>
    [Fact]
    public async Task Loopback_ToneOnLeftOnly_ArrivesOnLeftAndNotOnRight()
    {
        const double sampleRateHz = 48_000.0;
        const double toneFreqHz = 1000.0;

        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(6.0, CancellationToken.None);
        await device.SetOutputRangeAsync(LoopbackOutputRangeDbv, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(toneFreqHz, LoopbackCommandedDbv)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var config = new AcquisitionConfig(sampleRateHz, ChannelCount: 2, BlockFrameCount: 4096);
        double leftSumSquares = 0.0;
        double rightSumSquares = 0.0;
        long framesSeen = 0;
        bool firstBlock = true;

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                if (!firstBlock)
                {
                    var samples = block.Samples;
                    for (int i = 0; i + 1 < samples.Length; i += 2)
                    {
                        leftSumSquares += (double)samples[i] * samples[i];
                        rightSumSquares += (double)samples[i + 1] * samples[i + 1];
                        framesSeen++;
                    }
                }

                firstBlock = false;
            }

            if (framesSeen >= (long)sampleRateHz)
                break;
        }

        await device.StopAsync();

        double leftDbv = 20.0 * Math.Log10(Math.Sqrt(leftSumSquares / framesSeen));
        double rightDbv = 20.0 * Math.Log10(Math.Sqrt(rightSumSquares / framesSeen));

        Assert.True(
            Math.Abs(leftDbv - LoopbackCommandedDbv) < 0.1,
            $"Left channel should carry the tone: commanded {LoopbackCommandedDbv} dBV, measured {leftDbv} dBV");

        // The idle channel sees only its DC offset and noise floor, tens of dB down. A swap would put
        // the full-level tone here instead, so a generous margin still fails unambiguously.
        Assert.True(
            rightDbv < LoopbackCommandedDbv - 30.0,
            $"Right channel should be idle, but measured {rightDbv} dBV (left measured {leftDbv} dBV) — channels appear crossed");
    }

    /// <summary>
    /// Regression guard for the intermittent streaming hang: leaving the <c>await foreach</c> early
    /// disposes the iterator, and <see cref="Qa40xUsbDevice.StreamAsync"/> must still run its full
    /// teardown (drain every in-flight EP2 transfer, then write StreamingStop) on that path. When it
    /// did not, transfers stayed submitted to libusb and the next thing to touch the connection —
    /// another stream, or Dispose() calling libusb_close — could block in native code with no way to
    /// interrupt it. Three consecutive short bursts on one connection: each is an early break, so a
    /// regression leaves transfers in flight from burst 1 and this test hangs or faults rather than
    /// silently passing.
    /// </summary>
    [Fact]
    public async Task RepeatedShortStreams_OnOneConnection_EachTearsDownCleanly()
    {
        const double sampleRateHz = 48_000.0;

        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(12.0, CancellationToken.None);

        var config = new AcquisitionConfig(sampleRateHz, ChannelCount: 2, BlockFrameCount: 4096);

        for (int burst = 0; burst < 3; burst++)
        {
            int blocksSeen = 0;
            await foreach (var block in device.StreamAsync(config, CancellationToken.None))
            {
                using (block)
                    blocksSeen++;

                if (blocksSeen == 2)
                    break; // early exit — the path that used to skip teardown entirely
            }

            Assert.Equal(2, blocksSeen);
        }
    }

    [Fact]
    public async Task SustainedCapture_60SecondsAt192kHz_HasNoDroppedSequenceNumbers()
    {
        const double sampleRateHz = 192_000.0;
        const double targetSeconds = 60.0;

        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);

        var config = new AcquisitionConfig(sampleRateHz, ChannelCount: 2, BlockFrameCount: 4096);
        long expectedSequence = 0;
        double secondsSeen = 0.0;

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                Assert.Equal(expectedSequence, block.SequenceNumber);
                expectedSequence++;
                secondsSeen += block.FrameCount / sampleRateHz;
            }

            if (secondsSeen >= targetSeconds)
                break;
        }

        await device.StopAsync();
        Assert.True(secondsSeen >= targetSeconds);
    }
}
