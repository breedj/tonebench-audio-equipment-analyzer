using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// The commands that move something: the reset, all three load settings and the DUT high-side
/// switch. This is the only test in the suite with a physical consequence, and it exists to verify
/// the five guarded opcode/argument pairs taken from the vendor application — the one part of the
/// protocol that no read can confirm.
///
/// <para><b>Requires the load terminals to be bare.</b> It engages 8 Ω and then 4 Ω, and it closes
/// the DUT supply switch. With an amplifier wired up, the first of those shorts its output into a
/// load it was not asked to drive, and the last applies a bench supply to whatever is connected.
/// Run it with nothing attached (confirmed by Joost on 2026-10-10 for the run recorded in
/// docs/qa451-protocol.md) — or not at all.</para>
///
/// <para>What can be asserted is limited by the device: a QA451 cannot be asked what its relays are
/// doing, so there is no read-back to compare against. The checks are therefore that every command
/// is accepted, that the device stays healthy across the whole sequence, and that nothing draws
/// current — the only observable a bare-terminal bench offers. The status word is printed at every
/// step, because whether any of its seven undocumented bits track relay position is exactly the
/// question a run like this can answer.</para>
///
/// <para>The sequence ends in <see cref="Qa451Load.SetSafeStateAsync"/> from a <c>finally</c>, so a
/// failure half way through still leaves the load open and the supply off. That matters more here
/// than the assertion does: the hardware keeps whatever it was last told across this process
/// exiting.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa451HardwareCollection))]
public class Qa451LoadRelayHardwareTests(ITestOutputHelper output)
{
    /// <summary>
    /// Time allowed for a relay to settle and for the firmware to refresh its own status word
    /// before the step is read back. Generous: the whole point is to read a settled device, and the
    /// vendor application's own poll runs at 1 s.
    /// </summary>
    private const int SettleMs = 300;

    [Fact]
    public async Task EveryLoadSetting_AndTheDutSwitch_AreAcceptedAndLeaveTheDeviceHealthy()
    {
        using Qa451Load load = new(NullLogger<Qa451Load>.Instance);

        // With the reset this time: it is one of the commands under test, and it is what puts the
        // relays in a known state to start from.
        await load.ConnectAsync(resetToKnownState: true);
        output.WriteLine($"connected: {load.Info!.Model}, firmware {load.Info.FirmwareVersion}");

        // The load and the supply switch are driven together part way through, which is what
        // establishes that their status bits are independent flags rather than a code.
        var steps = new List<(string Step, LoadImpedance Expected, bool ExpectPower, LoadReading Reading)>();
        try
        {
            steps.Add(("after reset", LoadImpedance.Open, false, await ReadStep(load)));

            await load.SetImpedanceAsync(LoadImpedance.EightOhm);
            steps.Add(("8 ohm engaged", LoadImpedance.EightOhm, false, await ReadStep(load)));

            await load.SetDutPowerAsync(on: true);
            steps.Add(("8 ohm + DUT power", LoadImpedance.EightOhm, true, await ReadStep(load)));

            await load.SetImpedanceAsync(LoadImpedance.FourOhm);
            steps.Add(("4 ohm + DUT power", LoadImpedance.FourOhm, true, await ReadStep(load)));

            await load.SetImpedanceAsync(LoadImpedance.Open);
            steps.Add(("open + DUT power", LoadImpedance.Open, true, await ReadStep(load)));

            await load.SetDutPowerAsync(on: false);
            steps.Add(("DUT power off", LoadImpedance.Open, false, await ReadStep(load)));
        }
        finally
        {
            await load.SetSafeStateAsync();
        }

        foreach ((string step, LoadImpedance expected, bool expectPower, LoadReading reading) in steps)
        {
            // The device's own account of its relays, which is the only read-back there is: every
            // command must show up in the status word it is supposed to move, and in no other.
            Assert.Equal(expected, reading.EngagedImpedance);
            Assert.Equal(expectPower, reading.DutPowerEngaged);

            // Nothing in this sequence may trip anything: the loads are switched across bare
            // terminals and the supply switch feeds nothing.
            Assert.Equal(LoadFaults.None, reading.Faults);
            Assert.False(reading.RequiresReplug);

            // No path for current to flow with the terminals bare. The ADC's own zero is the only
            // thing this should see.
            Assert.InRange(reading.CurrentA, 0.0, 0.05);

            Assert.InRange(reading.LeftTemperatureC, -20, 120);
            Assert.InRange(reading.RightTemperatureC, -20, 120);
        }

        // Switching a load across bare terminals dissipates nothing, so a heatsink that moved would
        // mean something other than this test was going on.
        Assert.InRange(
            Math.Abs(steps[^1].Reading.LeftTemperatureC - steps[0].Reading.LeftTemperatureC), 0, 5);

        Assert.Equal(LoadImpedance.Open, load.Impedance);
        Assert.False(load.DutPowerOn);

        output.WriteLine(string.Empty);
        output.WriteLine("status words: " + string.Join(
            ", ", steps.Select(s => $"{s.Step} 0x{s.Reading.RawStatusBits:x3}")));
    }

    private async Task<LoadReading> ReadStep(Qa451Load load)
    {
        await Task.Delay(SettleMs);
        LoadReading reading = await load.ReadAsync();
        output.WriteLine(
            $"{reading.LeftTemperatureC,4} °C / {reading.RightTemperatureC,4} °C   "
                + $"{reading.CurrentA,7:F3} A   status 0x{reading.RawStatusBits:x3}   faults {reading.Faults}");
        return reading;
    }
}
