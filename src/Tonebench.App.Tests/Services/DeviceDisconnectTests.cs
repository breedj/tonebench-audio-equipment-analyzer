using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// Unplugging a QA403 the app is already connected to. Two things used to go wrong, and they are
/// the same bug seen from either end: nothing noticed the device was gone until the user pressed
/// something, and what they got then was a raw <c>UsbException: Input/Output Error</c> from inside a
/// register write — a 500 and a stack trace, with the status bar still showing the device connected.
/// </summary>
public class DeviceDisconnectTests
{
    [Fact]
    public async Task GetStatusAsync_WhileIdle_NoticesTheDeviceIsGoneWithoutAnyUserAction()
    {
        // The whole point of the liveness ping: with no stream running, this poll is the only thing
        // touching the device, so without it the status bar keeps claiming "connected" indefinitely.
        var device = new FakeAnalyzerDevice();
        var session = CreateSession(device);
        await session.ConnectAsync(CancellationToken.None);

        device.Unplugged = true;

        var status = await session.GetStatusAsync(CancellationToken.None);

        Assert.False(status.Connected);
        Assert.True(status.DeviceLost);
        Assert.False(session.Connected);
    }

    [Fact]
    public async Task DeviceOperation_AfterAnUnplug_ReportsItAsADisconnectRatherThanAUsbFailure()
    {
        var device = new FakeAnalyzerDevice();
        var session = CreateSession(device);
        await session.ConnectAsync(CancellationToken.None);

        device.Unplugged = true;

        // InvalidOperationException is what the endpoints map to 409 with the message shown in the
        // status bar; a Qa40xDeviceLostException reaching them is a 500 and a dev exception page.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SetOutputRangeAsync(-12.0, CancellationToken.None));

        Assert.Contains("disconnected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connect", ex.Message);
        Assert.False(session.Connected);
    }

    [Fact]
    public async Task ConnectAsync_AfterAnUnplugAndReplug_BringsTheSessionBackWithoutARestart()
    {
        // Only reachable because losing the device clears _info: while the session still counted as
        // connected, ConnectAsync no-opped and the only way back was restarting the app.
        var device = new FakeAnalyzerDevice();
        var session = CreateSession(device);
        await session.ConnectAsync(CancellationToken.None);

        device.Unplugged = true;
        await session.GetStatusAsync(CancellationToken.None);
        Assert.False(session.Connected);

        await session.ConnectAsync(CancellationToken.None);

        var status = session.GetStatus();
        Assert.True(status.Connected);
        Assert.False(status.DeviceLost);
        Assert.Equal(2, device.ConnectAttempts);
    }

    [Fact]
    public async Task GetStatusAsync_WhileConnected_DoesNotPingOnEveryCall()
    {
        // Several browser tabs poll this at 2 s each; unthrottled that is a register exchange per
        // poll per tab, on a device the user is trying to measure with.
        var device = new CountingPingDevice();
        var session = CreateSession(device);
        await session.ConnectAsync(CancellationToken.None);

        for (int i = 0; i < 10; i++)
            await session.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, device.PingCount);
    }

    private static DeviceSessionService CreateSession(IAnalyzerDevice device) => new(
        device,
        new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
        NullLogger<DeviceSessionService>.Instance);

    private sealed class CountingPingDevice : FakeAnalyzerDevice
    {
        public int PingCount { get; private set; }

        public override Task<bool> PingAsync(CancellationToken ct)
        {
            PingCount++;
            return base.PingAsync(ct);
        }
    }
}
