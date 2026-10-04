using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.SoundCard;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.SoundCard;

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
    private SoundCardDevice? BuildForAFullDuplexCard(
        out string name, double sampleRateHz = 48_000.0, int inputOffset = 0, int outputOffset = 0)
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

        if (card.MaxInputChannels < inputOffset + 2 || card.MaxOutputChannels < outputOffset + 2)
        {
            output.WriteLine($"{name} has no channel pair at offset {inputOffset}/{outputOffset} — skipping");
            return null;
        }

        var settings = new SoundCardSettings
        {
            InputHostApi = card.HostApiName,
            InputDeviceName = card.Name,
            OutputHostApi = card.HostApiName,
            OutputDeviceName = card.Name,
            InputChannelOffset = inputOffset,
            OutputChannelOffset = outputOffset,
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

    /// <summary>Which channel pair carries the loopback patch on this bench (offset 2 = channels 3/4).</summary>
    private const int LoopbackOffset = 2;

    /// <summary>
    /// The channel-order claim, measured rather than assumed. The QA403's own version of this test
    /// exists because "it should not swap" is exactly what was wrong there for a while, and a sound
    /// card deserves the same treatment rather than an inherited assumption.
    ///
    /// <para><b>Needs a loopback cable</b> on the pair named by <see cref="LoopbackOffset"/>. Without
    /// one the inputs read silence, which this reports and skips rather than failing, so the class
    /// stays runnable on an unpatched bench.</para>
    ///
    /// <para>It also exercises the channel-offset path, which under ASIO is the only way to reach a
    /// pair other than 1/2: the whole card is one device and the offset selects within it.</para>
    /// </summary>
    [Fact]
    public async Task SoundCardLoopback_ToneOnLeftOnly_ArrivesOnLeftAndNotOnRight()
    {
        var device = BuildForAFullDuplexCard(
            out string name, inputOffset: LoopbackOffset, outputOffset: LoopbackOffset);
        if (device is null)
            return;

        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(1_000.0, -20.0)]);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(tone, ChannelGeneratorConfig.Off), CancellationToken.None);

        (double leftOnlyL, double leftOnlyR) = await PeaksAsync(device);

        // The mirror is measured before anything is asserted, because the three cases together say
        // *which* fault it is. Left-only landing on the right alone is a swap; both channels equal
        // in both cases is a patch that sums, which is what an interface's own internal loopback
        // does and is not a fault in this code at all.
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(ChannelGeneratorConfig.Off, tone), CancellationToken.None);
        (double rightOnlyL, double rightOnlyR) = await PeaksAsync(device);

        output.WriteLine($"{name}, channels {LoopbackOffset + 1}/{LoopbackOffset + 2}, tone at -20 dBFS");
        output.WriteLine($"  played LEFT  -> left {Db(leftOnlyL)}, right {Db(leftOnlyR)}");
        output.WriteLine($"  played RIGHT -> left {Db(rightOnlyL)}, right {Db(rightOnlyR)}");

        if (leftOnlyL < 0.002 && leftOnlyR < 0.002)
        {
            output.WriteLine("  nothing came back — no loopback on this pair, skipping the assertions");
            return;
        }

        bool sums = leftOnlyL > 0.002 && leftOnlyR > 0.002 && rightOnlyL > 0.002 && rightOnlyR > 0.002;
        if (sums)
        {
            output.WriteLine(
                "  both inputs carry either channel, so this patch sums — an interface's internal loopback does that. Channel order cannot be judged through it.");
            return;
        }

        // −20 dBFS is 0.1 of full scale. Each channel must come back on its own side.
        Assert.InRange(leftOnlyL, 0.02, 0.5);
        Assert.True(
            leftOnlyL > leftOnlyR * 10.0,
            $"a tone played on left alone came back on right (left {Db(leftOnlyL)}, right {Db(leftOnlyR)}) — a channel swap or a crossed patch.");
        Assert.True(
            rightOnlyR > rightOnlyL * 10.0,
            $"a tone played on right alone came back on left (left {Db(rightOnlyL)}, right {Db(rightOnlyR)}).");
    }

    private static string Db(double peak) =>
        peak > 0 ? $"{peak:0.00000} ({20.0 * Math.Log10(peak):0.0} dBFS)" : "0";

    /// <summary>
    /// The loudest each input saw over about a second, ignoring the first quarter: the queue primed
    /// before a stimulus change still carries the previous one, which is what
    /// <c>StimulusLatencyFrames</c> describes.
    /// </summary>
    private static async Task<(double Left, double Right)> PeaksAsync(SoundCardDevice device, int frames = 48_000)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        double left = 0.0;
        double right = 0.0;
        long seen = 0;

        await foreach (PcmBlock block in device.StreamAsync(new AcquisitionConfig(48_000.0, 2), cts.Token))
        {
            using (block)
            {
                Span<float> s = block.Samples;
                if (seen >= frames / 4)
                {
                    for (int i = 0; i + 1 < s.Length; i += 2)
                    {
                        left = Math.Max(left, Math.Abs(s[i]));
                        right = Math.Max(right, Math.Abs(s[i + 1]));
                    }
                }

                seen += block.FrameCount;
            }

            if (seen >= frames)
                break;
        }

        return (left, right);
    }
}
