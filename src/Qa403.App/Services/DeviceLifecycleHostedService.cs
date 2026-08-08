namespace Qa403.App.Services;

/// <summary>
/// Connects the device on host startup and brings the USB stream fully down on shutdown.
///
/// <see cref="StopAsync"/> must call <see cref="DeviceSessionService.ShutdownAsync"/>, never
/// <c>StopAcquisitionAsync</c> — see that method's remarks for why the latter left the device
/// streaming (and therefore unreachable until power-cycled) in both of the app's usual end states.
/// Hosted services are stopped before the DI container disposes singletons, which is exactly the
/// ordering the device needs: pump joined and StreamingStop written first, handle closed second.
/// </summary>
public sealed class DeviceLifecycleHostedService(DeviceSessionService session) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => session.ConnectAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => session.ShutdownAsync();
}
