using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// The current measurement (<see cref="Qa451Protocol.ReadCurrent"/>) against a known current. Every
/// other hardware test here has only ever seen 0.000 A, which confirms nothing about the scaling:
/// a reply read as amps instead of milliamps, or with its bytes the wrong way round, reads exactly
/// zero on an idle bench too.
///
/// <para><b>Bench setup.</b> The sense path runs through the high-side switch, so a current only
/// flows when there is a circuit: supply positive to <b>S+</b>, <b>S−</b> through
/// <see cref="LoadResistorOhms"/> to <b>DUT GND</b> (the supply's negative). The resistor is not
/// optional — with only wire in the path the supply current-limits instead of holding its voltage
/// up, and the device then declines to close its switch at all (measured 2026-10-10).</para>
///
/// <para>Two datasheet facts matter. The switch <b>is not bidirectional</b>: S+ must always be the
/// higher potential, or current flows through the MOSFET's body diode. And closing it runs a soft
/// start that takes about 1.2 s, which <see cref="Qa451Load.SetDutPowerAsync"/> waits out — see
/// <see cref="Qa451Protocol.SoftStartSettleMs"/> for why that is a wait and not a timeout.</para>
///
/// <para>Keep the supply gentle: these figures are far from the 10 A continuous rating and the
/// >15 A trip. If the bench is set to something else, change the two constants below — this test is
/// meant to fail loudly when what is on the bench is not what it says, exactly like the loopback
/// cabling the QA403's hardware tests assume.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa451HardwareCollection))]
public class Qa451LoadCurrentHardwareTests(ITestOutputHelper output)
{
    /// <summary>The supply's voltage setting, recorded so the run can be reproduced. Not measured here.</summary>
    private const double SupplyVoltageV = 5.0;

    /// <summary>The resistor in the sense path, which is what sets the current.</summary>
    private const double LoadResistorOhms = 47.0;

    /// <summary>
    /// What the path should draw, by Ohm's law rather than by a figure typed in — so the number and
    /// the bench cannot drift apart. The supply's limit must be above this, or it ends up in
    /// constant-current mode and the switch never closes.
    /// </summary>
    private const double ExpectedCurrentA = SupplyVoltageV / LoadResistorOhms;

    /// <summary>
    /// Tolerance on the comparison: a fixed floor for the QA451's own quantisation and the supply's
    /// offset, plus a proportional part for the two instruments' gain error. Wide enough that it is
    /// testing the scaling and not either instrument's calibration — an amps/milliamps mistake is a
    /// factor of a thousand, and a byte-order one is further out still. Measured 2026-10-10: the
    /// QA451B read 0.097 A where the bench supply's display said 105 mA and Ohm's law says 106 mA.
    /// </summary>
    private const double ToleranceA = 0.010 + (0.10 * ExpectedCurrentA);

    /// <summary>
    /// How many readings to take. Several, because single samples land high now and then: 0.146 A
    /// turned up about once in twenty-five against a steady 0.097 A, on a bench where the supply's
    /// own display never moved. The vendor application's own REST client averages this reading for
    /// what is presumably the same reason.
    /// </summary>
    private const int Samples = 7;

    /// <summary>
    /// How long to let the current reading catch up with a change of switch state.
    ///
    /// <para>It lags, measured 2026-10-10 at both ends of the same run: the first sample after the
    /// soft start read 0.000 A while the status word already said the switch was engaged, and a read
    /// taken straight after switching off reported the switch open with 0.097 A still showing. So
    /// the device refreshes that register on its own schedule and a reading is up to one of its
    /// update intervals old — which also means a measurement should be taken from a settled switch,
    /// never immediately after moving one.</para>
    /// </summary>
    private const int CurrentSettleMs = 500;

    [Fact]
    public async Task WithASupplyAndResistorOnTheDutSwitch_TheReadingMatchesOhmsLaw()
    {
        using Qa451Load load = new(NullLogger<Qa451Load>.Instance);
        await load.ConnectAsync(resetToKnownState: true);
        output.WriteLine(
            $"bench: {SupplyVoltageV:F1} V through {LoadResistorOhms:F0} Ω "
                + $"=> {ExpectedCurrentA:F3} A expected, tolerance ±{ToleranceA:F3} A");

        LoadReading switchedOff;
        var settled = new List<LoadReading>();
        try
        {
            switchedOff = await Report("switch open", load);

            // Returns once the soft start has run, so the next read is a settled one.
            await load.SetDutPowerAsync(on: true);

            LoadReading afterSoftStart = await Report("after soft start", load);
            Assert.True(
                afterSoftStart.DutPowerEngaged,
                $"The QA451 reports its DUT switch open (status 0x{afterSoftStart.RawStatusBits:x3}) after a "
                    + "power-on command, so it declined to close it. The usual cause is a supply that "
                    + "current-limits instead of holding its voltage up: check the resistor is in the path "
                    + "and the limit is above the current it sets.");

            for (int i = 0; i < Samples; i++)
            {
                settled.Add(await Report($"sample {i + 1}", load));
                await Task.Delay(150);
            }
        }
        finally
        {
            await load.SetSafeStateAsync();
        }

        // Read the control twice: the first one is expected to still carry the previous current,
        // because the register lags the switch, and printing it keeps that on the record.
        await Report("just switched off", load);
        await Task.Delay(CurrentSettleMs);
        LoadReading switchedOffAgain = await Report("switch open again", load);

        // An open switch is the control: it proves the reading follows the switched path rather than
        // being an offset that happens to look like a current.
        Assert.InRange(switchedOff.CurrentA, 0.0, 0.010);
        Assert.InRange(switchedOffAgain.CurrentA, 0.0, 0.010);
        Assert.False(switchedOff.DutPowerEngaged);

        double[] amps = [.. settled.Select(r => r.CurrentA).Order()];
        double median = amps[Samples / 2];
        int withinTolerance = amps.Count(a => Math.Abs(a - ExpectedCurrentA) <= ToleranceA);
        output.WriteLine(
            $"median {median:F3} A against {ExpectedCurrentA:F3} A expected; "
                + $"{withinTolerance}/{Samples} samples within ±{ToleranceA:F3} A; "
                + $"spread {amps[0]:F3}..{amps[^1]:F3} A");

        // The median carries the verdict, and a majority has to agree with it — an occasional high
        // sample is measured device behaviour, a systematically wrong scaling is not.
        Assert.Equal(ExpectedCurrentA, median, ToleranceA);
        Assert.True(
            withinTolerance > Samples / 2,
            $"Only {withinTolerance} of {Samples} samples were within ±{ToleranceA:F3} A of "
                + $"{ExpectedCurrentA:F3} A: {string.Join(", ", amps.Select(a => a.ToString("F3")))}");

        foreach (LoadReading reading in settled)
        {
            Assert.True(reading.DutPowerEngaged);
            Assert.Equal(LoadFaults.None, reading.Faults);
            Assert.False(reading.RequiresReplug);
        }

        // The load relays stay out of this entirely — the sense path is the S+/S− switch.
        Assert.Equal(LoadImpedance.Open, load.Impedance);
        Assert.False(load.DutPowerOn);
    }

    private async Task<LoadReading> Report(string label, Qa451Load load)
    {
        LoadReading reading = await load.ReadAsync();
        output.WriteLine(
            $"{label,-18} {reading.CurrentA,7:F3} A   status 0x{reading.RawStatusBits:x3}   "
                + $"{reading.LeftTemperatureC} °C / {reading.RightTemperatureC} °C   faults {reading.Faults}");
        return reading;
    }
}
