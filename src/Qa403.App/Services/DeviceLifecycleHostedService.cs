namespace Qa403.App.Services;

/// <summary>Connects the device on host startup and stops acquisition + releases it on shutdown.</summary>
public sealed class DeviceLifecycleHostedService(DeviceSessionService session) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => session.ConnectAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => session.StopAcquisitionAsync();
}
