using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.Device;
using Tonebench.Device.Hardware;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Services;

/// <summary>A load that answers whatever a test tells it to, and counts how often it was asked.</summary>
internal sealed class FakeProgrammableLoad : IProgrammableLoad
{
    public int Reads { get; private set; }

    public int Connects { get; private set; }

    public bool Disposed { get; private set; }

    public bool SafeStateCalled { get; private set; }

    public Exception? ConnectFailure { get; set; }

    public Exception? ReadFailure { get; set; }

    public LoadFaults Faults { get; set; } = LoadFaults.None;

    public LoadInfo? Info { get; private set; }

    public bool IsConnected { get; private set; }

    public IReadOnlyList<LoadImpedance> SupportedImpedances { get; } =
        [LoadImpedance.Open, LoadImpedance.FourOhm, LoadImpedance.EightOhm];

    public LoadImpedance? Impedance { get; private set; }

    public bool? DutPowerOn { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Connects++;
        if (ConnectFailure is not null)
            throw ConnectFailure;

        Info = new LoadInfo("QA451B", 39, 39, 12345678, "C39491461B001B00");
        IsConnected = true;
        Impedance = LoadImpedance.Open;
        DutPowerOn = false;
        return Task.CompletedTask;
    }

    public Task SetImpedanceAsync(LoadImpedance impedance, CancellationToken cancellationToken = default)
    {
        Impedance = impedance;
        return Task.CompletedTask;
    }

    public Task SetDutPowerAsync(bool on, CancellationToken cancellationToken = default)
    {
        DutPowerOn = on;
        return Task.CompletedTask;
    }

    public Task<LoadReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        Reads++;
        if (ReadFailure is not null)
            throw ReadFailure;

        return Task.FromResult(new LoadReading(
            22, 23, DutPowerOn == true ? 0.097 : 0.0, Faults, false, 0x040, Impedance, DutPowerOn == true));
    }

    public Task SetSafeStateAsync(CancellationToken cancellationToken = default)
    {
        SafeStateCalled = true;
        Impedance = LoadImpedance.Open;
        DutPowerOn = false;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Disposed = true;
        IsConnected = false;
        Info = null;
    }
}

/// <summary>Hands out whatever load the test prepared, instead of opening real hardware.</summary>
internal sealed class StubLoadFactory(Func<ProgrammableLoadKind, IProgrammableLoad?> create)
    : ProgrammableLoadFactory(NullLoggerFactory.Instance)
{
    public List<ProgrammableLoadKind> Created { get; } = [];

    public override IProgrammableLoad? Create(ProgrammableLoadKind kind)
    {
        Created.Add(kind);
        return create(kind);
    }
}

/// <summary>
/// The load session: what it connects, what it reports, and what it does when the device goes away.
/// Nothing here touches an analyzer, which is the point of keeping the measurement-running rule in
/// the endpoints instead.
/// </summary>
public class LoadSessionServiceTests
{
    private static (LoadSessionService Session, LoadKindSetting Setting, StubLoadFactory Factory) Build(
        ProgrammableLoadKind kind, IProgrammableLoad? load)
    {
        var setting = new LoadKindSetting { Kind = kind };
        var factory = new StubLoadFactory(_ => load);
        return (new LoadSessionService(factory, setting, NullLogger<LoadSessionService>.Instance), setting, factory);
    }

    [Fact]
    public async Task WithNoLoadSelected_NothingIsBuiltAndTheStatusSaysSo()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, StubLoadFactory factory) = Build(ProgrammableLoadKind.None, fake);
        using (session)
        {
            await session.ConnectAsync();
            LoadStatusResponse status = await session.GetStatusAsync();

            Assert.Equal(ProgrammableLoadKind.None, status.Kind);
            Assert.False(status.Connected);
            Assert.Null(status.Model);
            Assert.Null(status.Reading);
            Assert.Null(status.Error);
            Assert.Empty(factory.Created);
            Assert.Equal(0, fake.Connects);
        }
    }

    [Fact]
    public async Task Connect_ReportsTheIdentityAndTakesAReading()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();
            LoadStatusResponse status = await session.GetStatusAsync();

            Assert.True(status.Connected);
            Assert.Equal("QA451B", status.Model);
            Assert.Equal(39, status.FirmwareVersion);
            Assert.True(status.FirmwareSupported);
            Assert.Equal("C39491461B001B00", status.UsbSerial);
            Assert.Equal([0, 4, 8], status.SupportedImpedances);
            Assert.Equal(0, status.ImpedanceOhms);
            Assert.False(status.DutPowerOn);
            Assert.Equal(22, status.Reading!.LeftTemperatureC);
            Assert.Equal(23, status.Reading.RightTemperatureC);
        }
    }

    [Fact]
    public async Task Connect_Twice_DoesNotReopenTheDevice()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();
            await session.ConnectAsync();

            Assert.Equal(1, fake.Connects);
        }
    }

    [Fact]
    public async Task Connect_WhenNothingIsAttached_ThrowsAndLeavesTheReasonOnTheStatus()
    {
        var fake = new FakeProgrammableLoad { ConnectFailure = new Qa451DeviceNotFoundException("nothing attached") };
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await Assert.ThrowsAsync<Qa451DeviceNotFoundException>(() => session.ConnectAsync());

            LoadStatusResponse status = await session.GetStatusAsync();
            Assert.False(status.Connected);
            Assert.Equal("nothing attached", status.Error);
            Assert.True(fake.Disposed);
        }
    }

    /// <summary>
    /// The poll is throttled, because every open browser tab makes it and a load has nothing to gain
    /// from being asked faster than it changes.
    /// </summary>
    [Fact]
    public async Task TheStatusPoll_DoesNotReadTheDeviceEveryTime()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();

            for (int i = 0; i < 10; i++)
                await session.GetStatusAsync();

            Assert.Equal(1, fake.Reads);
        }
    }

    /// <summary>
    /// But a command makes the cached reading stale by definition, so the next poll takes a fresh
    /// one — otherwise the screen shows the previous state for up to half a second after the user
    /// pressed something.
    /// </summary>
    [Fact]
    public async Task AfterACommand_TheNextPollReadsAgainImmediately()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();
            await session.GetStatusAsync();
            int before = fake.Reads;

            await session.SetImpedanceAsync(8);
            LoadStatusResponse status = await session.GetStatusAsync();

            Assert.Equal(before + 1, fake.Reads);
            Assert.Equal(8, status.ImpedanceOhms);
        }
    }

    [Fact]
    public async Task SetDutPower_IsReportedBothWays()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();

            await session.SetDutPowerAsync(on: true);
            Assert.True((await session.GetStatusAsync()).DutPowerOn);

            await session.SetDutPowerAsync(on: false);
            Assert.False((await session.GetStatusAsync()).DutPowerOn);
        }
    }

    [Fact]
    public async Task AnImpedanceTheHardwareDoesNotHave_IsRefusedBeforeItReachesTheDevice()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetImpedanceAsync(16));
            Assert.Equal(LoadImpedance.Open, fake.Impedance);
        }
    }

    [Fact]
    public async Task ACommandWithNothingConnected_IsRefused()
    {
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, new FakeProgrammableLoad());
        using (session)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetImpedanceAsync(4));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetDutPowerAsync(on: true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetSafeStateAsync());
        }
    }

    /// <summary>
    /// A lost device clears the connection rather than only recording the error — a session holding
    /// a dead handle reports a healthy load, which is the analyzer's rule and the reason it has one.
    /// </summary>
    [Fact]
    public async Task WhenTheDeviceIsLostDuringAPoll_TheStatusFlipsToDisconnected()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();
            await session.GetStatusAsync();
            fake.ReadFailure = new Qa451DeviceLostException("unplugged");
            await Task.Delay(600);

            LoadStatusResponse status = await session.GetStatusAsync();

            Assert.False(status.Connected);
            Assert.Equal("unplugged", status.Error);
            Assert.True(fake.Disposed);
        }
    }

    /// <summary>
    /// A timeout is a hiccup on a device that is still there, following the QA403's rule: reported,
    /// but the session survives it.
    /// </summary>
    [Fact]
    public async Task WhenAPollTimesOut_TheSessionStaysConnected()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();
            await session.GetStatusAsync();
            fake.ReadFailure = new TimeoutException("no report");
            await Task.Delay(600);

            LoadStatusResponse status = await session.GetStatusAsync();

            Assert.True(status.Connected);
            Assert.Equal("no report", status.Error);
            Assert.False(fake.Disposed);
        }
    }

    [Fact]
    public async Task AFaultyReading_ReachesTheStatusAsFlags()
    {
        var fake = new FakeProgrammableLoad { Faults = LoadFaults.OverTemperature | LoadFaults.OverCurrent };
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();
            LoadStatusResponse status = await session.GetStatusAsync();

            Assert.True(status.Reading!.OverTemperature);
            Assert.True(status.Reading.OverCurrent);
        }
    }

    [Fact]
    public async Task SwitchingKind_TearsTheOldOneDownFirstAndRemembersTheChoice()
    {
        var first = new FakeProgrammableLoad();
        var second = new FakeProgrammableLoad();
        var setting = new LoadKindSetting { Kind = ProgrammableLoadKind.Qa451 };
        var factory = new StubLoadFactory(kind =>
            kind == ProgrammableLoadKind.Qa451 ? first : second);
        using var session = new LoadSessionService(factory, setting, NullLogger<LoadSessionService>.Instance);

        await session.ConnectAsync();
        await session.SwitchKindAsync(ProgrammableLoadKind.Simulated);

        Assert.True(first.Disposed);
        Assert.Equal(1, second.Connects);
        Assert.Equal(ProgrammableLoadKind.Simulated, setting.Kind);
        Assert.Equal(ProgrammableLoadKind.Simulated, (await session.GetStatusAsync()).Kind);
    }

    /// <summary>
    /// Switching to <see cref="ProgrammableLoadKind.None"/> is how a bench says it has no load, so it
    /// must release the one it had rather than leave a handle open on it.
    /// </summary>
    [Fact]
    public async Task SwitchingToNone_ReleasesTheDevice()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();
            await session.SwitchKindAsync(ProgrammableLoadKind.None);

            Assert.True(fake.Disposed);
            Assert.False((await session.GetStatusAsync()).Connected);
        }
    }

    /// <summary>
    /// Shutdown commands the safe state rather than trusting the hardware to protect itself. Of
    /// everything this app touches, a load across an amplifier's output is the one to be sure about.
    /// </summary>
    [Fact]
    public async Task Shutdown_PutsTheLoadAwayAndClosesIt()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();
            await session.SetImpedanceAsync(4);
            await session.SetDutPowerAsync(on: true);

            await session.ShutdownAsync();

            Assert.True(fake.SafeStateCalled);
            Assert.Equal(LoadImpedance.Open, fake.Impedance);
            Assert.False(fake.DutPowerOn);
            Assert.True(fake.Disposed);
        }
    }

    [Fact]
    public async Task Shutdown_WhenTheDeviceHasAlreadyGone_DoesNotThrow()
    {
        var fake = new FakeProgrammableLoad();
        (LoadSessionService session, _, _) = Build(ProgrammableLoadKind.Qa451, fake);
        using (session)
        {
            await session.ConnectAsync();
            fake.Dispose();

            await session.ShutdownAsync();
        }
    }

    /// <summary>
    /// The whole point of the simulated load: the load screens are drivable with nothing on the
    /// bench, through exactly the same session.
    /// </summary>
    [Fact]
    public async Task AgainstTheSimulatedLoad_TheSessionWorksEndToEnd()
    {
        var setting = new LoadKindSetting { Kind = ProgrammableLoadKind.Simulated };
        var factory = new StubLoadFactory(_ => new SimulatedProgrammableLoad(softStartSettleMs: 0));
        using var session = new LoadSessionService(factory, setting, NullLogger<LoadSessionService>.Instance);

        await session.ConnectAsync();
        await session.SetImpedanceAsync(8);
        await session.SetDutPowerAsync(on: true);
        LoadStatusResponse status = await session.GetStatusAsync();

        Assert.True(status.Connected);
        Assert.Equal("Simulated load", status.Model);
        Assert.Equal(8, status.ImpedanceOhms);
        Assert.True(status.DutPowerOn);
        Assert.Equal(0x045, status.Reading!.RawStatusBits);
        Assert.True(status.Reading.CurrentA > 0.0);
    }
}
