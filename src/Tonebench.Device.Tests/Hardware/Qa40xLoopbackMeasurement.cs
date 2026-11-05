using Tonebench.Device.Hardware;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// The two loopback capture loops shared by the QA40x bench classes: stream, skip the first block,
/// accumulate sums of squares, report broadband RMS in dBV.
///
/// <para><b>Shared measurement, never a shared verdict.</b> <see cref="Qa402HardwareTests"/> and
/// <see cref="Qa402VirtualDeviceTests"/> measure the same two quantities and must not drift about
/// <i>how</i> — only about what they then assert. The assertion deliberately stays at the call site:
/// "a real QA402's channels must not be crossed" and "the emulator's channels are crossed" are
/// opposite claims about the same number, and keeping them in separate files is what stops a future
/// reader reconciling them with a conditional.</para>
///
/// <para><see cref="Qa40xUsbDeviceHardwareTests"/> deliberately does <b>not</b> use this. It is the
/// project's measurement of record, and its comments rest on the exact bodies that produced the
/// figures quoted in CLAUDE.md; rewriting it to share code here would cost that provenance for
/// nothing.</para>
///
/// <para>Neither method stops the stream. The caller owns <c>StopAsync</c>, as the original inline
/// bodies did — the stream is left running so a caller can take a second reading before stopping.</para>
/// </summary>
internal static class Qa40xLoopbackMeasurement
{
    /// <summary>
    /// Broadband RMS of a one-channel capture, in dBV.
    ///
    /// <para>The first block is skipped on purpose: <c>ConfigureGeneratorAsync</c> discards the audio
    /// already committed to the DAC, but the round trip back through the ADC is not covered by any
    /// latency figure the app measures, so the front of the first block can still carry pre-change
    /// audio.</para>
    /// </summary>
    public static async Task<double> SingleChannelRmsDbvAsync(
        Qa40xUsbDevice device,
        AcquisitionConfig config,
        double captureSeconds,
        CancellationToken ct)
    {
        double sumSquares = 0.0;
        long samplesSeen = 0;
        long targetSamples = (long)(config.SampleRateHz * captureSeconds);
        bool firstBlock = true;

        await foreach (var block in device.StreamAsync(config, ct))
        {
            using (block)
            {
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

        if (samplesSeen == 0)
            throw new InvalidOperationException(
                "No samples were captured — the stream ended before a second block arrived.");

        return 20.0 * Math.Log10(Math.Sqrt(sumSquares / samplesSeen));
    }

    /// <summary>
    /// Per-channel broadband RMS of an interleaved two-channel capture, in dBV.
    ///
    /// <para>Index 0 of each frame is logical <b>left</b>. That is not an arbitrary choice: the L/R
    /// swap is the DAC's alone, so <c>ProcessRxBuffer</c> reads the ADC straight through — measured
    /// on real hardware and recorded in CLAUDE.md under "The L/R swap is on the DAC only". Two of the
    /// four vendored references swap on receive as well, and following them crosses the channels.</para>
    /// </summary>
    public static async Task<(double LeftDbv, double RightDbv)> StereoRmsDbvAsync(
        Qa40xUsbDevice device,
        AcquisitionConfig config,
        long targetFrames,
        CancellationToken ct)
    {
        double leftSumSquares = 0.0;
        double rightSumSquares = 0.0;
        long frames = 0;
        bool firstBlock = true;

        await foreach (var block in device.StreamAsync(config, ct))
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
                        frames++;
                    }
                }

                firstBlock = false;
            }

            if (frames >= targetFrames)
                break;
        }

        if (frames == 0)
            throw new InvalidOperationException(
                "No frames were captured — the stream ended before a second block arrived.");

        return (
            20.0 * Math.Log10(Math.Sqrt(leftSumSquares / frames)),
            20.0 * Math.Log10(Math.Sqrt(rightSumSquares / frames)));
    }
}
