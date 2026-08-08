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
        Assert.InRange(Qa40xUsbDevice.QueueDepthFor(1_000), 4, 48);
        Assert.InRange(Qa40xUsbDevice.QueueDepthFor(10_000_000), 4, 48);
    }
}
