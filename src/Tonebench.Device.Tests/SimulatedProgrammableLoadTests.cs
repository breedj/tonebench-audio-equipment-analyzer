using System.Diagnostics;
using Tonebench.Device;
using Tonebench.Device.Simulation;

namespace Tonebench.Device.Tests;

/// <summary>
/// The simulated load, pinned against the hardware behaviour it exists to imitate — the measured
/// quirks rather than an idealised load, because the quirks are what a UI gets wrong.
/// </summary>
public class SimulatedProgrammableLoadTests
{
    private static SimulatedProgrammableLoad Load() => new(softStartSettleMs: 0);

    [Fact]
    public async Task Connect_ReportsItselfAndStartsInAKnownState()
    {
        using SimulatedProgrammableLoad load = Load();

        await load.ConnectAsync();

        Assert.True(load.IsConnected);
        Assert.Equal("Simulated load", load.Info!.Model);
        Assert.True(load.Info.FirmwareIsSupported);
        Assert.Null(load.Info.UsbSerial);
        Assert.Equal(LoadImpedance.Open, load.Impedance);
        Assert.False(load.DutPowerOn);
        Assert.Equal([LoadImpedance.Open, LoadImpedance.FourOhm, LoadImpedance.EightOhm], load.SupportedImpedances);
    }

    [Theory]
    [InlineData(LoadImpedance.Open, 0x040)]
    [InlineData(LoadImpedance.FourOhm, 0x042)]
    [InlineData(LoadImpedance.EightOhm, 0x044)]
    public async Task EachLoad_ReportsTheStatusBitsTheHardwareWould(LoadImpedance impedance, int expectedStatus)
    {
        using SimulatedProgrammableLoad load = Load();
        await load.ConnectAsync();

        await load.SetImpedanceAsync(impedance);
        LoadReading reading = await load.ReadAsync();

        Assert.Equal(impedance, reading.EngagedImpedance);
        Assert.Equal(expectedStatus, reading.RawStatusBits);
    }

    /// <summary>
    /// The measured bits, including the one that is set whenever nothing is wrong — so anything
    /// reading the raw word sees the same shape it would on a bench.
    /// </summary>
    [Fact]
    public async Task WithTheLoadAndTheSupplyOn_TheStatusBitsAreIndependentFlags()
    {
        using SimulatedProgrammableLoad load = Load();
        await load.ConnectAsync();

        await load.SetImpedanceAsync(LoadImpedance.EightOhm);
        await load.SetDutPowerAsync(on: true);
        LoadReading reading = await load.ReadAsync();

        Assert.Equal(0x045, reading.RawStatusBits);
        Assert.True(reading.DutPowerEngaged);
        Assert.Equal(LoadImpedance.EightOhm, reading.EngagedImpedance);
    }

    [Fact]
    public async Task TheCurrentReading_IsQuantisedLikeTheHardwares()
    {
        using SimulatedProgrammableLoad load = Load();
        await load.ConnectAsync();
        load.DutCurrentA = 0.105;

        Assert.Equal(0.0, (await load.ReadAsync()).CurrentA);

        await load.SetDutPowerAsync(on: true);
        double amps = (await load.ReadAsync()).CurrentA;

        // 0.105 A is 2.16 quanta, so it reads as two of them — which is exactly what the bench did
        // against a supply showing 105 mA.
        Assert.Equal(2 * SimulatedProgrammableLoad.CurrentQuantumA, amps, 9);
        Assert.Equal(0.0, amps % SimulatedProgrammableLoad.CurrentQuantumA, 9);
    }

    [Fact]
    public async Task TheTwoTemperatures_AreNotTheSame()
    {
        // Two identical readings cannot show a left/right mix-up, which is the whole reason the
        // defaults differ.
        using SimulatedProgrammableLoad load = Load();
        await load.ConnectAsync();

        LoadReading reading = await load.ReadAsync();

        Assert.NotEqual(reading.LeftTemperatureC, reading.RightTemperatureC);
    }

    /// <summary>
    /// The fault paths cannot be reached on real hardware — over-temperature needs 60 °C and
    /// over-current needs 15 A and a replug — so injection is the only way anything downstream of
    /// them is ever exercised.
    /// </summary>
    [Fact]
    public async Task AnInjectedFault_IsReportedWithItsStatusBitsAndCanBeCleared()
    {
        using SimulatedProgrammableLoad load = Load();
        await load.ConnectAsync();

        load.InjectFault(LoadFaults.OverTemperature);
        LoadReading hot = await load.ReadAsync();
        Assert.Equal(LoadFaults.OverTemperature, hot.Faults);
        Assert.False(hot.RequiresReplug);
        Assert.NotEqual(0, hot.RawStatusBits & 0x0008);

        load.InjectFault(LoadFaults.OverCurrent, requiresReplug: true);
        LoadReading tripped = await load.ReadAsync();
        Assert.Equal(LoadFaults.OverCurrent, tripped.Faults);
        Assert.True(tripped.RequiresReplug);
        Assert.NotEqual(0, tripped.RawStatusBits & 0x0010);

        load.InjectFault(LoadFaults.None);
        LoadReading cleared = await load.ReadAsync();
        Assert.Equal(LoadFaults.None, cleared.Faults);
        Assert.False(cleared.RequiresReplug);
    }

    /// <summary>
    /// A clear fault cannot leave a latch behind: the pair is set together so no caller can produce
    /// "nothing is wrong but you must replug".
    /// </summary>
    [Fact]
    public async Task ClearingTheFault_AlsoClearsTheLatch()
    {
        using SimulatedProgrammableLoad load = Load();
        await load.ConnectAsync();
        load.InjectFault(LoadFaults.OverCurrent, requiresReplug: true);

        // Even asking for the latch: no fault means nothing to replug for, so the pair cannot
        // produce "nothing is wrong but you must replug".
        load.InjectFault(LoadFaults.None, requiresReplug: true);
        LoadReading reading = await load.ReadAsync();

        Assert.Equal(LoadFaults.None, reading.Faults);
        Assert.False(reading.RequiresReplug);
    }

    /// <summary>
    /// The soft start is modelled, not skipped: a caller written against an instant close is a
    /// caller that will look wrong against hardware.
    /// </summary>
    [Fact]
    public async Task ClosingTheDutSwitch_TakesTheSoftStart()
    {
        using SimulatedProgrammableLoad load = new(softStartSettleMs: 250);
        await load.ConnectAsync();

        var clock = Stopwatch.StartNew();
        await load.SetDutPowerAsync(on: true);
        long closing = clock.ElapsedMilliseconds;

        clock.Restart();
        await load.SetDutPowerAsync(on: false);

        Assert.InRange(closing, 200, 2000);
        Assert.InRange(clock.ElapsedMilliseconds, 0, 150);
    }

    [Fact]
    public async Task SafeState_OpensTheLoadAndCutsTheSupply()
    {
        using SimulatedProgrammableLoad load = Load();
        await load.ConnectAsync();
        await load.SetImpedanceAsync(LoadImpedance.FourOhm);
        await load.SetDutPowerAsync(on: true);

        await load.SetSafeStateAsync();
        LoadReading reading = await load.ReadAsync();

        Assert.Equal(LoadImpedance.Open, reading.EngagedImpedance);
        Assert.False(reading.DutPowerEngaged);
        Assert.Equal(0.0, reading.CurrentA);
    }

    [Fact]
    public async Task AnyCommand_BeforeConnect_IsRefused()
    {
        using SimulatedProgrammableLoad load = Load();

        await Assert.ThrowsAsync<InvalidOperationException>(() => load.SetImpedanceAsync(LoadImpedance.FourOhm));
        await Assert.ThrowsAsync<InvalidOperationException>(() => load.SetDutPowerAsync(on: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => load.ReadAsync());
    }

    [Fact]
    public async Task AnImpedanceTheHardwareDoesNotHave_IsRefused()
    {
        using SimulatedProgrammableLoad load = Load();
        await load.ConnectAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => load.SetImpedanceAsync((LoadImpedance)16));
    }

    [Fact]
    public async Task AfterDispose_ItIsNotConnected()
    {
        SimulatedProgrammableLoad load = Load();
        await load.ConnectAsync();

        load.Dispose();

        Assert.False(load.IsConnected);
        Assert.Null(load.Info);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => load.ReadAsync());
    }
}
