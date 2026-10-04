using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// Startup must survive there being no QA403 on the bus. Launching with the device unplugged used
/// to throw <c>Qa40xDeviceNotFoundException</c> out of <see cref="IHostedService.StartAsync"/>,
/// which aborts host startup — the process logged "Host terminated unexpectedly" and exited, after
/// the browser had already been pointed at a server that never came up.
/// </summary>
public class DeviceLifecycleHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WithNoDeviceAttached_StartsTheHostAnywayAndReportsDisconnected()
    {
        var device = new FakeAnalyzerDevice { ConnectFailure = new InvalidOperationException("No QA403 found (VID 0x16c0, PID 0x4e39).") };
        var session = CreateSession(device);

        await new DeviceLifecycleHostedService(session, NullLogger<DeviceLifecycleHostedService>.Instance)
            .StartAsync(CancellationToken.None);

        Assert.False(session.Connected);
        Assert.False(session.GetStatus().Connected);
    }

    [Fact]
    public async Task ConnectAsync_AfterAFailedAttempt_SucceedsOnceTheDeviceIsAttached()
    {
        // What the status bar's Connect button does: the failed startup attempt must leave the
        // session retryable, not permanently device-less until a restart.
        var device = new FakeAnalyzerDevice { ConnectFailure = new InvalidOperationException("No QA403 found.") };
        var session = CreateSession(device);

        await new DeviceLifecycleHostedService(session, NullLogger<DeviceLifecycleHostedService>.Instance)
            .StartAsync(CancellationToken.None);

        device.ConnectFailure = null;
        await session.ConnectAsync(CancellationToken.None);

        Assert.True(session.Connected);
        Assert.Equal(2, device.ConnectAttempts);

        // One subscription for however many attempts it took — re-hooking per attempt would raise
        // DeviceLost (and log it) once per attempt ever made.
        Assert.Equal(1, device.DeviceLostSubscribers);
    }

    [Fact]
    public async Task ConnectAsync_WhenAlreadyConnected_DoesNotOpenTheDeviceASecondTime()
    {
        // A second Open() over a live transport claims the interface twice; the button is one click
        // away from a double-connect, and status polling makes the disconnected window racy.
        var device = new FakeAnalyzerDevice();
        var session = CreateSession(device);

        await session.ConnectAsync(CancellationToken.None);
        await session.ConnectAsync(CancellationToken.None);

        Assert.True(session.Connected);
        Assert.Equal(1, device.ConnectAttempts);
    }

    [Fact]
    public async Task StartAcquisitionAsync_WithNoDevice_FailsLoudlyInsteadOfFaultingTheStreamInTheBackground()
    {
        // Pressing Run with nothing attached used to answer 200 with acquiring:false while the pump
        // task threw "Device is not connected" onto a background thread — a Run button that visibly
        // does nothing. The endpoint maps this exception to 409 with the message.
        var session = CreateSession(new FakeAnalyzerDevice { ConnectFailure = new InvalidOperationException("No QA403 found.") });
        await new DeviceLifecycleHostedService(session, NullLogger<DeviceLifecycleHostedService>.Instance)
            .StartAsync(CancellationToken.None);

        var config = new AcquisitionConfig(SampleRateHz: 48_000, ChannelCount: 2, BlockFrameCount: 4096);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAcquisitionAsync(config, CancellationToken.None));
        Assert.Contains("No device connected", ex.Message);
        Assert.False(session.Acquiring);
    }

    [Fact]
    public async Task ConfigureGeneratorAsync_WithNoDevice_SilencesWithoutComplainingButRefusesToPlay()
    {
        // stopRun() disables both channels on every Stop and every measurement switch, including
        // while disconnected, so that path must stay quiet. Asking for actual output must not.
        var session = CreateSession(new FakeAnalyzerDevice { ConnectFailure = new InvalidOperationException("No QA403 found.") });
        await new DeviceLifecycleHostedService(session, NullLogger<DeviceLifecycleHostedService>.Instance)
            .StartAsync(CancellationToken.None);

        await session.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);

        var playing = new GeneratorConfig(
            new ChannelGeneratorConfig(Enabled: true, [new GeneratorTone(1000.0, -20.0)]),
            ChannelGeneratorConfig.Off);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConfigureGeneratorAsync(playing, CancellationToken.None));
        Assert.Contains("No device connected", ex.Message);
    }

    private static DeviceSessionService CreateSession(IAnalyzerDevice device) => new(
        device,
        new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
        NullLogger<DeviceSessionService>.Instance);
}
