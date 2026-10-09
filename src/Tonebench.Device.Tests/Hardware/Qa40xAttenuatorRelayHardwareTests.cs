using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// Guards the 24 dB input attenuator's settle, which is the one range change on this device that
/// does not take effect by the time the register write returns. See
/// <see cref="Qa40xAutoRanger.AttenuatorReleaseSettleMs"/> for the measurement; the short version is
/// that *releasing* the attenuator lags the write by about a second, during which the front end is
/// still attenuating while <see cref="Qa40xCalibration"/> already scales for the new range, so every
/// sample reads exactly 24 dB low.
///
/// <para>Needs the same loopback cabling as <see cref="Qa40xUsbDeviceHardwareTests"/>: L out to L in.
/// Asserts on the *first* block of each capture, because that is the one the settle exists to
/// protect and the one every previous version of this code got wrong.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa403HardwareCollection))]
public class Qa40xAttenuatorRelayHardwareTests
{
    private const double CommandedDbv = -6.0;
    private const double OutputRangeDbv = 8.0;
    private const double SampleRateHz = 48_000.0;
    private const double ToneFreqHz = 1000.0;

    /// <summary>
    /// A range set while idle has no stream to hang settle frames on, so the device carries the
    /// window across the gap and discards it off the front of the next stream. Every sweep runner
    /// pre-ranges exactly this way, which is why it is not only the tests that need it.
    /// </summary>
    [Theory]
    [InlineData(30.0, 0.0)]   // releases the attenuator — the failing case
    [InlineData(42.0, 18.0)]  // releases it from the far end, to the top of the unattenuated set
    [InlineData(24.0, 18.0)]  // the minimum crossing
    [InlineData(12.0, 0.0)]   // a gain step, no relay
    [InlineData(30.0, 24.0)]  // a gain step inside the attenuated set
    [InlineData(0.0, 30.0)]   // engaging it, which has always been immediate
    public async Task AfterARangeChangeMadeWhileIdle_TheFirstBlockIsAlreadyAtTheRightLevel(
        double previousRangeDbv, double inputRangeDbv)
    {
        await CaptureFirstBlockDbv(previousRangeDbv);
        double measuredDbv = await CaptureFirstBlockDbv(inputRangeDbv);

        Assert.True(
            Math.Abs(measuredDbv - CommandedDbv) < 0.1,
            $"{previousRangeDbv} -> {inputRangeDbv} dBV: commanded {CommandedDbv} dBV, first block read {measuredDbv:F3} dBV"
                + (measuredDbv < CommandedDbv - 20.0 ? " (24 dB low: the attenuator had not released)" : ""));
    }

    /// <summary>
    /// The same change on a running stream, which is the path the app's own autoranging and its
    /// manual range control both take. The device books the settle as frames on the chunk after its
    /// register write, so the first block delivered past the step must already be right — only that
    /// block is flagged <see cref="PcmBlock.StartsAfterDiscontinuity"/>, and a consumer has no other
    /// way to know the ones behind it were suspect.
    /// </summary>
    [Theory]
    [InlineData(30.0, 0.0)]
    [InlineData(24.0, 18.0)]
    [InlineData(12.0, 0.0)]
    public async Task AfterARangeChangeMidStream_TheFirstBlockPastTheStepIsAlreadyAtTheRightLevel(
        double startRangeDbv, double newRangeDbv)
    {
        using var device = await ConnectedDeviceAsync(startRangeDbv);

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
        int seen = 0;
        bool changed = false;
        double? firstAfterStep = null;

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                if (changed && firstAfterStep is null)
                    firstAfterStep = Dbv(block);
            }

            // Let the stream settle at the starting range first, so the step is the only thing
            // under test rather than the stream's own opening transient.
            if (seen == 13)
            {
                await device.SetInputRangeAsync(newRangeDbv, CancellationToken.None);
                changed = true;
            }

            seen++;
            if (firstAfterStep is not null || seen >= 40)
                break;
        }

        await device.StopAsync();

        Assert.NotNull(firstAfterStep);
        Assert.True(
            Math.Abs(firstAfterStep.Value - CommandedDbv) < 0.1,
            $"mid-stream {startRangeDbv} -> {newRangeDbv} dBV: commanded {CommandedDbv} dBV, "
                + $"first block past the step read {firstAfterStep.Value:F3} dBV"
                + (firstAfterStep.Value < CommandedDbv - 20.0 ? " (24 dB low: the attenuator had not released)" : ""));
    }

    private static async Task<Qa40xUsbDevice> ConnectedDeviceAsync(double inputRangeDbv)
    {
        var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);

        // Forced, like the loopback theory's: the fixed loopback signal is under-range at the wider
        // settings, so autoranging would walk straight off whichever range is under test.
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(inputRangeDbv, CancellationToken.None);
        await device.SetOutputRangeAsync(OutputRangeDbv, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(ToneFreqHz, CommandedDbv)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        return device;
    }

    private static async Task<double> CaptureFirstBlockDbv(double inputRangeDbv)
    {
        using var device = await ConnectedDeviceAsync(inputRangeDbv);

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
        double measuredDbv = double.NaN;
        int seen = 0;

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                // The first block carries the stream's own opening transient (measured: 0.05 dB on
                // the broadband RMS, and a peak 10% high), so the tone is read coherently rather
                // than as an RMS. A 24 dB error is not a tolerance question either way.
                if (seen == 0)
                    measuredDbv = Dbv(block);
            }

            seen++;
            if (seen >= 2)
                break;
        }

        await device.StopAsync();
        return measuredDbv;
    }

    /// <summary>The block's 1 kHz tone level in dBV, by Hann-windowed single-bin DFT.</summary>
    private static double Dbv(PcmBlock block)
    {
        var samples = block.Samples;
        int n = samples.Length;
        double re = 0.0, im = 0.0, s1 = 0.0;
        double w = 2.0 * Math.PI * ToneFreqHz / SampleRateHz;
        for (int i = 0; i < n; i++)
        {
            double hann = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / n));
            s1 += hann;
            double v = samples[i] * hann;
            re += v * Math.Cos(w * i);
            im -= v * Math.Sin(w * i);
        }

        double vrms = 2.0 * Math.Sqrt(re * re + im * im) / s1 / Math.Sqrt(2.0);
        return vrms <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(vrms);
    }
}
