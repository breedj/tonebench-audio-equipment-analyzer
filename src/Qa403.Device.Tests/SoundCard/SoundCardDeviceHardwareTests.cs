using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.Device.SoundCard;
using Xunit.Abstractions;

namespace Qa403.Device.Tests.SoundCard;

/// <summary>
/// The device against a real card. Tagged Hardware and excluded by default.
///
/// <para>Each test finds a full-duplex device for itself and skips quietly when there is none, so
/// the class stays runnable on a machine with no ASIO driver — where, per
/// <see cref="PortAudioNativeHardwareTests"/>, there is no full-duplex device at all on Windows.</para>
/// </summary>
[Trait("Category", "Hardware")]
public class SoundCardDeviceHardwareTests(ITestOutputHelper output)
{
    private SoundCardDevice? BuildForAFullDuplexCard(out string name, double sampleRateHz = 48_000.0)
    {
        var pa = PortAudioNative.Shared;
        var enumerator = new SoundCardEnumerator(pa);

        var card = pa.Devices
            .Where(d => d.IsFullDuplex && SoundCardEnumerator.Trust(d.HostApiKind) != SoundCardHostApiTrust.Resamples)
            .OrderBy(d => SoundCardEnumerator.PreferenceOf(d.HostApiKind))
            .FirstOrDefault();

        if (card is null)
        {
            name = string.Empty;
            output.WriteLine("no trustworthy full-duplex device on this machine — skipping");
            return null;
        }

        name = $"{card.HostApiName} / {card.Name}";
        var rates = enumerator.SupportedRatesHz(card, card, exclusive: true);
        if (!rates.Contains(sampleRateHz))
        {
            output.WriteLine($"{name} does not accept {sampleRateHz} Hz (offers {string.Join(", ", rates)}) — skipping");
            return null;
        }

        var settings = new SoundCardSettings
        {
            InputHostApi = card.HostApiName,
            InputDeviceName = card.Name,
            OutputHostApi = card.HostApiName,
            OutputDeviceName = card.Name,
            SampleRateHz = sampleRateHz,
        };

        return new SoundCardDevice(
            settings, SoundCardCalibration.Uncalibrated, NullLogger<SoundCardDevice>.Instance, pa);
    }

    [Fact]
    public async Task Connect_ReportsTheCardAndTheRatesItAccepts()
    {
        var device = BuildForAFullDuplexCard(out string name);
        if (device is null)
            return;

        DeviceInfo info = await device.ConnectAsync(CancellationToken.None);

        output.WriteLine($"{name}");
        output.WriteLine($"  product   : {info.ProductName}");
        output.WriteLine($"  firmware  : {info.FirmwareVersion}");
        output.WriteLine($"  rates     : {string.Join(", ", device.Capabilities.SampleRatesHz)}");
        output.WriteLine($"  input rung: {device.Capabilities.InputRangesDbv[0]:0.00} dBV (uncalibrated, so dBFS)");

        Assert.NotEmpty(device.Capabilities.SampleRatesHz);
        Assert.Single(device.Capabilities.InputRangesDbv);
        Assert.False(device.HasInputAutoRanging);
    }

    /// <summary>
    /// The end-to-end one: a real duplex stream, paced by the card's own clock, for about a second.
    /// It asserts the stream keeps time — blocks must arrive at roughly the rate the audio they
    /// carry implies, which is what proves the loop is paced by the device and not spinning.
    /// </summary>
    [Fact]
    public async Task StreamAsync_RunsAtTheCardsOwnClock_WithoutDroppingAnything()
    {
        var device = BuildForAFullDuplexCard(out string name);
        if (device is null)
            return;

        await device.ConnectAsync(CancellationToken.None);

        // A quiet tone rather than silence, so the written audio is real work.
        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(1_000.0, -40.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int blocks = 0;
        long frames = 0;
        double peak = 0.0;
        var clock = Stopwatch.StartNew();

        await foreach (PcmBlock block in device.StreamAsync(new AcquisitionConfig(48_000.0, 2), cts.Token))
        {
            using (block)
            {
                blocks++;
                frames += block.FrameCount;
                foreach (float v in block.Samples)
                    peak = Math.Max(peak, Math.Abs(v));
            }

            if (frames >= 48_000)
                break;
        }

        clock.Stop();
        double audioSeconds = frames / 48_000.0;

        output.WriteLine($"{name}");
        output.WriteLine($"  {blocks} blocks, {frames} frames = {audioSeconds:0.000} s of audio in {clock.Elapsed.TotalSeconds:0.000} s");
        output.WriteLine($"  peak in: {peak:0.00000}  dropped chunks: {device.DroppedRxChunks}  dropped blocks: {device.DroppedBlocks}");

        Assert.True(blocks > 0, "no blocks arrived from the card");
        Assert.Equal(0, device.DroppedBlocks);

        // Paced by the card: a second of audio cannot arrive in much less than a second. The upper
        // bound is loose because the priming queue and the first transfer are real time too.
        Assert.InRange(clock.Elapsed.TotalSeconds, audioSeconds * 0.8, audioSeconds + 2.0);
    }

    /// <summary>
    /// Whether the two channels stay in their own lanes. With a loopback cable this reads the
    /// generator back; without one it reads the card's own noise, so it only reports rather than
    /// asserting a level — the channel-order claim itself is settled by
    /// <c>Loopback_ToneOnLeftOnly_ArrivesOnLeftAndNotOnRight</c> once a cable is fitted.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ReportsWhatEachInputChannelSees()
    {
        var device = BuildForAFullDuplexCard(out string name);
        if (device is null)
            return;

        await device.ConnectAsync(CancellationToken.None);

        var left = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(1_000.0, -20.0)]);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(left, ChannelGeneratorConfig.Off), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        double peakLeft = 0.0;
        double peakRight = 0.0;
        long frames = 0;

        await foreach (PcmBlock block in device.StreamAsync(new AcquisitionConfig(48_000.0, 2), cts.Token))
        {
            using (block)
            {
                Span<float> s = block.Samples;
                for (int i = 0; i + 1 < s.Length; i += 2)
                {
                    peakLeft = Math.Max(peakLeft, Math.Abs(s[i]));
                    peakRight = Math.Max(peakRight, Math.Abs(s[i + 1]));
                }

                frames += block.FrameCount;
            }

            if (frames >= 24_000)
                break;
        }

        output.WriteLine($"{name}: generator on LEFT only at -20 dBFS");
        output.WriteLine($"  peak left  {peakLeft:0.00000}");
        output.WriteLine($"  peak right {peakRight:0.00000}");
        output.WriteLine("  (equal and tiny means no loopback cable is fitted)");
    }
}
