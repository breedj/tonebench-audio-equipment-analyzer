using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// Guards the two halves of one rule: the ranger may not judge samples it has already told the
/// consumer to discard, and its trip point may not sit level with the headroom the range selectors
/// park a signal on. Both are about a <em>steady</em> signal — nothing here changes level — and both
/// showed up as the input range walking away from the rung that was right for it.
///
/// <para>Only measurable against real hardware: <c>TransferPump</c> is the thing under test and it
/// has no transport to fake. Needs a path from L out to L in, which may be the collection's plain
/// loopback or anything roughly linear in between — the tone level is derived from a measured probe
/// rather than assumed, because what has to be reproduced is an input sitting at
/// <see cref="TargetInputDbv"/>, not a particular generator setting.</para>
///
/// <para><b>What it reproduced.</b> Measured on serial 1077527945 (fw 60) on 2026-10-13 through a
/// 20 dB amplifier, the left input sitting at a steady −0.05 dBV: one legitimate step up from the
/// 0 dBV range became three — <c>0 → 6</c> on a peak of 0.61 dBV (real), then <c>6 → 12</c> on
/// 6.12 dBV and <c>12 → 18</c> on <b>11.84 dBV</b>, each reading the previous step's own gain-step
/// transient, which lives inside the settle window the consumer drops. Over a 20-point sweep it hit
/// 4 points, each left on a range 6 dB too wide, costing those points a re-measure and — on a quiet
/// DUT, where the analyzer's own residual is the floor — about 7 dB of SNR.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa403HardwareCollection))]
public class Qa40xAutoRangeSettleHardwareTests
{
    private const double SampleRateHz = 48_000.0;
    private const double ToneFreqHz = 1000.0;
    private const int BlockFrames = 4096;

    /// <summary>
    /// Where the tone has to land at the input, and it is 0 dBV for a reason: that is the worst case
    /// the 6 dB ladder can produce. <c>InputRangeSelector</c> gives it the 6 dBV rung, leaving
    /// exactly <see cref="Qa40xAutoRanger.HeadroomDb"/> — so before
    /// <see cref="Qa40xAutoRanger.StepUpMarginDb"/> existed, the step-up trip point sat 1 dB away
    /// from a signal the ladder had just decided was comfortable there.
    /// </summary>
    private const double TargetInputDbv = 0.0;

    private const double StartRangeDbv = 0.0;
    private const double SettledRangeDbv = 6.0;

    /// <summary>One of the four the device offers (−12 / −2 / 8 / 18), with room above any tone these tests play.</summary>
    private const double OutputRangeDbv = 18.0;

    /// <summary>Wide enough to read the probe below without clipping, whatever the path's gain turns out to be.</summary>
    private const double ProbeRangeDbv = 30.0;

    /// <summary>Quiet enough not to drive an amplifier in the loop anywhere near its limits.</summary>
    private const double ProbeToneDbv = -30.0;

    private const double MaxGeneratorDbv = 18.0;

    /// <summary>
    /// 3.4 s of blocks — long enough for the step, for a cascade to have run its course (each one
    /// lands within a block or two of the last) and for the 1 s step-down delay to have had its say.
    /// </summary>
    private const int BlockCount = 40;

    [Fact]
    public async Task AgainstAnUnchangingTone_TheRangeStepsOnceAndStaysThere()
    {
        double[] rangesSeen = await StreamAtAsync(StartRangeDbv);

        // The whole sequence, because the cascade *is* a sequence: one step to the rung the ladder
        // wants, and nothing after it. Under the old code this read [0, 6, 12, 18].
        Assert.Equal([StartRangeDbv, SettledRangeDbv], rangesSeen);
    }

    /// <summary>
    /// The same tone started from the rung the selectors would have chosen for it: the ranger must
    /// leave it alone entirely. What moves it otherwise is the stream's <i>own</i> opening — a settle
    /// booked while idle, discarded off the front of the stream — so this fails the same way the
    /// cascade above does, at <c>[6, 12]</c>, with the settle window scanned.
    ///
    /// <para><see cref="Qa40xAutoRanger.StepUpMarginDb"/> is deliberately <b>not</b> what either of
    /// these pins, and that was measured rather than assumed: with the margin set to 0 and the settle
    /// window still respected, both pass. The margin earns its keep where a tone <em>changes</em> —
    /// a sweep's per-point stimulus step put the peak at 1.03 and 1.30 dBV against a 1 dBV trip point
    /// at 2 of 20 points — and what it protects is arithmetic, so it is pinned in
    /// <c>Qa40xAutoRangerTests</c> and <c>InputRangeSelectorTests</c> instead.</para>
    /// </summary>
    [Fact]
    public async Task StartedOnTheRungTheLadderChose_TheRangeIsNotTouchedAtAll()
    {
        double[] rangesSeen = await StreamAtAsync(SettledRangeDbv);

        Assert.Equal([SettledRangeDbv], rangesSeen);
    }

    /// <summary>
    /// Streams <see cref="BlockCount"/> blocks of an unchanging tone with autoranging live, and
    /// reports every input range the device passed through, in order.
    ///
    /// <para>The ranges are read off the device rather than counted as flagged blocks: the first
    /// block of a stream can carry <see cref="PcmBlock.StartsAfterDiscontinuity"/> for a reason that
    /// has nothing to do with ranging — a settle booked by a range written while idle is discarded
    /// off the front of the next stream — so a flag count cannot tell a step from an opening.</para>
    /// </summary>
    private static async Task<double[]> StreamAtAsync(double startRangeDbv)
    {
        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);

        double toneDbv = TargetInputDbv - await MeasurePathGainDbAsync(device);
        Assert.True(
            toneDbv <= MaxGeneratorDbv,
            $"the path from L out to L in loses too much to put {TargetInputDbv:0.#} dBV on the input: "
                + $"that would need {toneDbv:0.##} dBV out of a generator that stops at {MaxGeneratorDbv:0.#}");

        await device.SetInputRangeAsync(startRangeDbv, CancellationToken.None);
        await ConfigureToneAsync(device, toneDbv);
        device.AutoRangeEnabled = true;

        // Seeded before the stream, so the first step is visible as a transition: the pump can take
        // it inside the first block, which is 8 chunks long.
        var ranges = new List<double> { device.InputRangeDbv };
        int seen = 0;

        await foreach (var block in device.StreamAsync(Config, CancellationToken.None))
        {
            block.Dispose();

            // The pump's own authoritative value, which is what every consumer of this device reads.
            double rangeDbv = device.InputRangeDbv;
            if (ranges[^1] != rangeDbv)
                ranges.Add(rangeDbv);

            if (++seen >= BlockCount)
                break;
        }

        await device.StopAsync();

        // Silence, so the next test in the collection does not inherit a tone.
        await device.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);

        return [.. ranges];
    }

    /// <summary>
    /// Gain of whatever sits between L out and L in, in dB, from one quiet tone read on a range wide
    /// enough not to clip it. Autoranging is off for this: the probe is deliberately well under
    /// <see cref="ProbeRangeDbv"/>, which is exactly what the ranger would walk away from.
    /// </summary>
    private static async Task<double> MeasurePathGainDbAsync(Qa40xUsbDevice device)
    {
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(ProbeRangeDbv, CancellationToken.None);
        await ConfigureToneAsync(device, ProbeToneDbv);

        double measuredDbv = double.NegativeInfinity;
        int seen = 0;

        await foreach (var block in device.StreamAsync(Config, CancellationToken.None))
        {
            using (block)
            {
                // The second block, not the first: the first carries the stream's own opening
                // transient. One block of a clean tone read as an RMS — this only has to place the
                // tone in the right rung, where a few tenths of a dB is neither here nor there.
                if (seen == 1)
                    measuredDbv = RmsDbv(block);
            }

            if (++seen >= 2)
                break;
        }

        await device.StopAsync();

        Assert.True(
            double.IsFinite(measuredDbv),
            $"nothing came back on the left input at {ProbeToneDbv:0.#} dBV out — check the L out to L in path");

        return measuredDbv - ProbeToneDbv;
    }

    private static AcquisitionConfig Config => new(SampleRateHz, ChannelCount: 1, BlockFrameCount: BlockFrames);

    private static async Task ConfigureToneAsync(Qa40xUsbDevice device, double ampDbv)
    {
        await device.SetOutputRangeAsync(OutputRangeDbv, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(ToneFreqHz, ampDbv)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);
    }

    private static double RmsDbv(PcmBlock block)
    {
        var samples = block.Samples;
        double sum = 0.0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i] * samples[i];

        double vrms = Math.Sqrt(sum / samples.Length);
        return vrms > 0.0 ? 20.0 * Math.Log10(vrms) : double.NegativeInfinity;
    }
}
