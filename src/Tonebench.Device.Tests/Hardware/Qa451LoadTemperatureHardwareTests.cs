using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// The two heatsink temperatures, by heating <b>one</b> channel's load and watching which reading
/// moves. Two things are unverifiable without this: that the temperature path responds at all, and
/// <b>which byte belongs to which channel</b> — both sensors read room temperature on an idle bench,
/// so a decode with left and right swapped is indistinguishable from a correct one. This codebase
/// has been caught by exactly that kind of channel mapping before (see CLAUDE.md on the DAC's L/R
/// swap, where a reference implementation was wrong and following it crossed the channels).
///
/// <para><b>Bench setup.</b> A bench supply across the <see cref="HeatedChannel"/> load input only —
/// <see cref="SupplyVoltageV"/> into the <see cref="LoadUnderTest"/> load, i.e.
/// <see cref="ExpectedWatts"/> W. The other channel stays unconnected, which is the whole point. The
/// supply can be left switched on: with the load released the QA451 presents about 10 kΩ, so
/// nothing flows until this test engages the relay. <b>The test owns the heating</b>, and it
/// releases the load in a <c>finally</c>.</para>
///
/// <para><b>Skipped, because a bench supply cannot do it.</b> Attempted 2026-10-10 with 8 V into the
/// left 8 Ω load: the status word read 0x040 at every poll — the load was never engaged — and three
/// minutes produced no temperature rise at all. Joost, watching the front panel, saw the load come
/// on "for 1 second only"; a follow-up run at 100 ms resolution saw it refused outright, never
/// engaging. While that supply stayed connected the <i>DUT switch</i> stopped engaging as well, so
/// the device refuses relay control as a whole while DC is present on a load input — and recovers
/// the moment it is disconnected, with no replug and with no fault bit ever set. The manual's "the
/// loads are DC coupled, but it's expected that ONLY AC signals will be presented" is the context: a
/// single-rail Class D amplifier idles at half its supply, and 25 V DC into 8 Ω would be 78 W of
/// continuous dissipation.</para>
///
/// <para>To run this for real it needs an <b>amplifier driving a tone</b> into one channel — the
/// device's actual use case, where the load stays engaged — not a DC supply. The constants below are
/// kept as written so the test is ready when that bench is set up; everything except the source of
/// the power still applies.</para>
///
/// <para><b>Deliberately modest power.</b> QuantAsylum's guidance is to keep average power to
/// 5–10 W — the 200 W figure is a 220 ms burst rating — and the datasheet warns against extended
/// periods above the maximum input. This runs inside that guidance, which is also why it does
/// <i>not</i> try to provoke the over-temperature bit: reaching a trip threshold would mean
/// sustained power well beyond what the loads are specified for, to confirm one status bit that is
/// already read from the vendor application's own source. <see cref="SafetyCeilingC"/> aborts the
/// run long before anything is at risk.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa451HardwareCollection))]
public class Qa451LoadTemperatureHardwareTests(ITestOutputHelper output)
{
    /// <summary>Which channel's load input the supply is wired to. The assertions follow this.</summary>
    private const StreamChannelUnderTest HeatedChannel = StreamChannelUnderTest.Left;

    /// <summary>The load to engage, and therefore the resistance the supply drives.</summary>
    private const LoadImpedance LoadUnderTest = LoadImpedance.EightOhm;

    /// <summary>The supply's voltage setting. Recorded so the run can be reproduced.</summary>
    private const double SupplyVoltageV = 8.0;

    /// <summary>What that works out to, for the log — inside the 5–10 W average guidance.</summary>
    private const double ExpectedWatts = SupplyVoltageV * SupplyVoltageV / (int)LoadUnderTest;

    /// <summary>How long to heat for, at most.</summary>
    private const int MaxHeatingSeconds = 180;

    /// <summary>Stop as soon as the heated channel has risen this much: enough to be unambiguous.</summary>
    private const int TargetRiseC = 8;

    /// <summary>The rise that has to be seen for the test to pass at all.</summary>
    private const int MinimumRiseC = 3;

    /// <summary>Abort immediately above this, whatever else is going on.</summary>
    private const int SafetyCeilingC = 70;

    /// <summary>Which channel is being heated. Local to this test — the driver has no such concept.</summary>
    private enum StreamChannelUnderTest
    {
        Left,
        Right,
    }

    [Fact(Skip = "A bench supply cannot heat these loads: the device protects them against DC. "
        + "Needs an amplifier driving a tone instead — see the class remarks.")]
    public async Task HeatingOneChannel_MovesThatChannelsReadingAndNotTheOther()
    {
        using Qa451Load load = new(NullLogger<Qa451Load>.Instance);
        await load.ConnectAsync(resetToKnownState: true);
        output.WriteLine(
            $"bench: {SupplyVoltageV:F1} V into the {HeatedChannel} {(int)LoadUnderTest} Ω load "
                + $"=> {ExpectedWatts:F1} W, the other channel unconnected");

        LoadReading baseline = await load.ReadAsync();
        output.WriteLine(
            $"baseline: left {baseline.LeftTemperatureC} °C, right {baseline.RightTemperatureC} °C");

        var samples = new List<LoadReading>();
        try
        {
            await load.SetImpedanceAsync(LoadUnderTest);

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < MaxHeatingSeconds)
            {
                await Task.Delay(2000);
                LoadReading reading = await load.ReadAsync();
                samples.Add(reading);

                int heated = Heated(reading);
                int other = Other(reading);
                output.WriteLine(
                    $"  t+{clock.Elapsed.TotalSeconds,5:F0} s  left {reading.LeftTemperatureC,3} °C  "
                        + $"right {reading.RightTemperatureC,3} °C  (heated {heated - Heated(baseline):+#;-#;0}, "
                        + $"other {other - Other(baseline):+#;-#;0})  status 0x{reading.RawStatusBits:x3}  "
                        + $"faults {reading.Faults}");

                if (Math.Max(reading.LeftTemperatureC, reading.RightTemperatureC) >= SafetyCeilingC)
                {
                    output.WriteLine("  safety ceiling reached — releasing the load");
                    break;
                }

                if (heated - Heated(baseline) >= TargetRiseC)
                {
                    output.WriteLine($"  reached +{TargetRiseC} °C on the heated channel — enough");
                    break;
                }
            }
        }
        finally
        {
            await load.SetSafeStateAsync();
        }

        Assert.NotEmpty(samples);
        LoadReading last = samples[^1];
        int heatedRise = Heated(last) - Heated(baseline);
        int otherRise = Other(last) - Other(baseline);
        output.WriteLine(
            $"=> heated channel ({HeatedChannel}) rose {heatedRise} °C, the other rose {otherRise} °C");

        // The temperature path responds at all.
        Assert.True(
            heatedRise >= MinimumRiseC,
            $"The {HeatedChannel} reading rose only {heatedRise} °C with {ExpectedWatts:F1} W in that "
                + "channel's load. Either the supply is not connected to it, the load relay is not "
                + "passing current, or the temperature decode is not reading what it claims.");

        // And the bytes are the right way round: an unconnected channel's heatsink cannot outrun the
        // one dissipating eight watts. This is the assertion the whole test exists for.
        Assert.True(
            heatedRise > otherRise,
            $"The unheated channel rose {otherRise} °C against the heated channel's {heatedRise} °C, so "
                + "the two temperature bytes are the other way round from what Qa451Protocol decodes "
                + "(bits 8..15 left, bits 0..7 right).");

        // Nothing at this power should trip anything.
        Assert.All(samples, reading =>
        {
            Assert.Equal(LoadFaults.None, reading.Faults);
            Assert.False(reading.RequiresReplug);
        });

        Assert.Equal(LoadImpedance.Open, load.Impedance);
    }

    private static int Heated(LoadReading reading) =>
        HeatedChannel == StreamChannelUnderTest.Left ? reading.LeftTemperatureC : reading.RightTemperatureC;

    private static int Other(LoadReading reading) =>
        HeatedChannel == StreamChannelUnderTest.Left ? reading.RightTemperatureC : reading.LeftTemperatureC;
}
