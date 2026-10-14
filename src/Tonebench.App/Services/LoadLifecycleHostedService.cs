namespace Tonebench.App.Services;

/// <summary>
/// Connects the programmable load at startup and puts it away at shutdown.
///
/// <para><b>A missing load must never abort startup</b>, the same rule
/// <see cref="DeviceLifecycleHostedService"/> exists for and for the same reason: a hosted service
/// that throws out of <c>StartAsync</c> takes the whole host down, and a bench whose load is
/// unplugged would then get a browser tab opened on a server that never came up. The failure is
/// logged and the reason ends up on <c>GET /api/load</c>, where the Connect button is the retry.
/// </para>
///
/// <para>Stopping is where this service earns its place: <see cref="LoadSessionService.ShutdownAsync"/>
/// opens the load and switches the DUT supply off. The hardware is documented to do that for itself
/// when the host goes away, but commanding it is immediate and does not depend on firmware
/// behaviour this app has only read about — and of everything this app touches, a load across an
/// amplifier's output is the one worth being sure about.</para>
/// </summary>
public sealed class LoadLifecycleHostedService(
    LoadSessionService load, ILogger<LoadLifecycleHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (load.Kind == ProgrammableLoadKind.None)
        {
            return;
        }

        try
        {
            await load.ConnectAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "No programmable load connected at startup ({Kind}): {Message}", load.Kind, ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await load.ShutdownAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Programmable load shutdown failed");
        }
    }
}
