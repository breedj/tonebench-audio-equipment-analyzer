using Qa403.Device.Hardware;

namespace Qa403.Device.Tests.Hardware;

/// <summary>
/// The DAC underruns — scope-visible gaps in the generator output — whenever the queued TX transfers
/// drain before the host submits more. What must therefore stay constant across sample rates is the
/// <em>time</em> those queued transfers represent, not their count.
///
/// A fixed transfer count is what the original <c>QueueDepth = 2</c> (later 4) actually was, and it is
/// exactly why the reported dropouts got worse as the sample rate went up: the same host-side hiccup
/// that a 48 kHz queue absorbed comfortably underran the same queue at 192 kHz, because the queue was
/// four times shorter in time. These tests pin the scaling so that regression can't come back quietly.
/// </summary>
public class Qa40xUsbDeviceQueueDepthTests
{
    /// <summary>512 frames per transfer — <c>Qa40xUsbDevice.TransferChunkFrames</c>.</summary>
    private const int TransferChunkFrames = 512;

    /// <summary>The buffered time the design asks for — <c>Qa40xUsbDevice.TargetInFlightSeconds</c>.</summary>
    private const double TargetInFlightSeconds = 0.128;

    [Theory]
    [InlineData(48_000)]
    [InlineData(96_000)]
    [InlineData(192_000)]
    [InlineData(384_000)]
    public void QueueDepthFor_BuffersAtLeastFortyMillisecondsAtEverySupportedSampleRate(double sampleRateHz)
    {
        int depth = Qa40xUsbDevice.QueueDepthFor(sampleRateHz);

        double bufferedSeconds = depth * TransferChunkFrames / sampleRateHz;

        Assert.True(
            bufferedSeconds >= 0.040,
            $"{sampleRateHz} Hz: depth {depth} buffers only {bufferedSeconds * 1000:F1} ms — too little slack "
                + "to absorb a GC pause or a slow spectrum tick before the DAC runs dry.");
    }

    /// <summary>
    /// The assertion the test above should have been all along. It parameterised 384 kHz but asked
    /// only for 40 ms, which the clamped depth of 48 satisfied at 64 ms — so it <b>passed against the
    /// bug</b> for as long as <c>MaxQueueDepth</c> was 48: the clamp had been derived from 192 kHz
    /// being the top rate, and at 384 kHz it silently halved <c>TargetInFlightSeconds</c>, below four
    /// of the five pump stalls ever measured on this app.
    ///
    /// <para>"A time budget, not a transfer count" is only meaningful if the budget is the <i>same</i>
    /// budget at every rate, so this pins the actual number rather than a floor. 1 ms of tolerance is
    /// the rounding <c>QueueDepthFor</c>'s <c>Math.Ceiling</c> can introduce — one transfer is 10.7 ms
    /// at 48 kHz, so a genuinely wrong depth can never hide inside it.</para>
    /// </summary>
    [Theory]
    [InlineData(48_000)]
    [InlineData(96_000)]
    [InlineData(192_000)]
    [InlineData(384_000)]
    public void QueueDepthFor_BuffersTheSameTimeAtEverySupportedSampleRate(double sampleRateHz)
    {
        int depth = Qa40xUsbDevice.QueueDepthFor(sampleRateHz);

        double bufferedSeconds = depth * TransferChunkFrames / sampleRateHz;

        Assert.True(
            Math.Abs(bufferedSeconds - TargetInFlightSeconds) < 0.001,
            $"{sampleRateHz / 1000.0:0.###} kHz: depth {depth} buffers {bufferedSeconds * 1000:F1} ms, not the "
                + $"{TargetInFlightSeconds * 1000:F0} ms every rate is supposed to get. If this is short at the top "
                + "rate, MaxQueueDepth is clamping — it has to be re-derived whenever a faster rate is added, which "
                + "is exactly what adding 384 kHz exposed.");
    }

    [Fact]
    public void QueueDepthFor_ScalesWithSampleRateRatherThanStayingAFixedTransferCount()
    {
        // The actual bug: a constant depth means quadrupling the sample rate quarters the buffered
        // time. Depth must rise with the rate to hold that time roughly steady.
        int at48k = Qa40xUsbDevice.QueueDepthFor(48_000);
        int at192k = Qa40xUsbDevice.QueueDepthFor(192_000);

        Assert.True(at192k > at48k, $"192 kHz depth ({at192k}) must exceed 48 kHz depth ({at48k}).");

        double buffered48k = at48k * TransferChunkFrames / 48_000.0;
        double buffered192k = at192k * TransferChunkFrames / 192_000.0;

        // Within 2x of each other across a 4x sample-rate span — i.e. tracking time, not count.
        Assert.InRange(buffered192k / buffered48k, 0.5, 2.0);
    }

    [Fact]
    public void QueueDepthFor_StaysWithinTheClampAtAbsurdSampleRates()
    {
        // Clamped at both ends so a pathological rate can't ask for a 1-deep pipeline (instant
        // underrun) or thousands of simultaneously submitted transfers.
        Assert.InRange(Qa40xUsbDevice.QueueDepthFor(1_000), 4, 96);
        Assert.InRange(Qa40xUsbDevice.QueueDepthFor(10_000_000), 4, 96);
    }
}
