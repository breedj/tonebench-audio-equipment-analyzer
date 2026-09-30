using Microsoft.Extensions.Logging.Abstractions;
using Qa403.Device.SoundCard;
using Xunit.Abstractions;

namespace Qa403.Device.Tests.SoundCard;

/// <summary>
/// Which output actually reaches which input, measured rather than assumed.
///
/// <para>A diagnostic rather than an assertion: it plays one output channel at a time and prints
/// what every input pair heard. On a bench where a loopback misbehaves this is the first thing worth
/// knowing, and it answers questions no single test can — whether a patch is on the pair you think,
/// whether an interface's own internal loopback is in the way, whether a channel is dead.</para>
///
/// <para>It asserts only that <i>something</i> was heard somewhere, so it fails on a card with
/// nothing patched at all rather than passing silently, and it skips when there is no full-duplex
/// device to drive.</para>
/// </summary>
[Trait("Category", "Hardware")]
public class SoundCardRoutingHardwareTests(ITestOutputHelper output)
{
    /// <summary>Channel pairs to try on each side, as offsets. 0 is channels 1/2, 2 is channels 3/4.</summary>
    private static readonly int[] Offsets = [0, 2];

    [Fact]
    public async Task Routing_PrintsWhichOutputReachesWhichInput()
    {
        var pa = PortAudioNative.Shared;
        var enumerator = new SoundCardEnumerator(pa);

        var card = pa.Devices
            .Where(d => d.IsFullDuplex && SoundCardEnumerator.Trust(d.HostApiKind) != SoundCardHostApiTrust.Resamples)
            .OrderBy(d => SoundCardEnumerator.PreferenceOf(d.HostApiKind))
            .FirstOrDefault();

        if (card is null)
        {
            output.WriteLine("no trustworthy full-duplex device — skipping");
            return;
        }

        output.WriteLine($"{card.HostApiName} / {card.Name}: {card.MaxInputChannels} in, {card.MaxOutputChannels} out");
        output.WriteLine("");
        output.WriteLine("played                     heard on in 1/2        heard on in 3/4");

        double loudest = 0.0;

        foreach (int outOffset in Offsets)
        {
            if (card.MaxOutputChannels < outOffset + 2)
                continue;

            for (int side = 0; side < 2; side++)
            {
                string played = $"out {outOffset + 1 + side}";
                var row = new List<string>();

                foreach (int inOffset in Offsets)
                {
                    if (card.MaxInputChannels < inOffset + 2)
                    {
                        row.Add("        —        ");
                        continue;
                    }

                    (double left, double right) = await MeasureAsync(card, outOffset, inOffset, side);
                    loudest = Math.Max(loudest, Math.Max(left, right));
                    row.Add($"{Db(left),8} {Db(right),8}");
                }

                output.WriteLine($"{played,-26} {string.Join("   ", row)}");
            }
        }

        output.WriteLine("");
        output.WriteLine("Each cell is the peak on that pair's left and right input, in dBFS.");

        Assert.True(
            loudest > 0.002,
            "nothing was heard on any input from any output — no loopback is patched anywhere on this card.");
    }

    private static string Db(double peak) => peak > 1e-7 ? $"{20.0 * Math.Log10(peak):0.0}" : "silent";

    /// <summary>Plays −20 dBFS on one output channel of a pair and returns both inputs' peaks.</summary>
    private static async Task<(double Left, double Right)> MeasureAsync(
        SoundCardDeviceDescriptor card, int outOffset, int inOffset, int side)
    {
        var settings = new SoundCardSettings
        {
            InputHostApi = card.HostApiName,
            InputDeviceName = card.Name,
            OutputHostApi = card.HostApiName,
            OutputDeviceName = card.Name,
            InputChannelOffset = inOffset,
            OutputChannelOffset = outOffset,
            SampleRateHz = 48_000.0,
        };

        using var device = new SoundCardDevice(
            settings, SoundCardCalibration.Uncalibrated, NullLogger<SoundCardDevice>.Instance, PortAudioNative.Shared);

        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(1_000.0, -20.0)]);
        await device.ConfigureGeneratorAsync(
            side == 0
                ? new GeneratorConfig(tone, ChannelGeneratorConfig.Off)
                : new GeneratorConfig(ChannelGeneratorConfig.Off, tone),
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        double left = 0.0;
        double right = 0.0;
        long seen = 0;
        const int Frames = 24_000;

        await foreach (PcmBlock block in device.StreamAsync(new AcquisitionConfig(48_000.0, 2), cts.Token))
        {
            using (block)
            {
                Span<float> s = block.Samples;

                // Skip the first quarter: the queue primed before the stimulus reached the DAC still
                // carries the previous state.
                if (seen >= Frames / 4)
                {
                    for (int i = 0; i + 1 < s.Length; i += 2)
                    {
                        left = Math.Max(left, Math.Abs(s[i]));
                        right = Math.Max(right, Math.Abs(s[i + 1]));
                    }
                }

                seen += block.FrameCount;
            }

            if (seen >= Frames)
                break;
        }

        return (left, right);
    }
}
