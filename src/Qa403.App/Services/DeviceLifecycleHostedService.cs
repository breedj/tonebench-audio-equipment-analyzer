using Qa403.Device.Hardware;

namespace Qa403.App.Services;

/// <summary>
/// Connects the device on host startup and brings the USB stream fully down on shutdown.
///
/// <see cref="StartAsync"/> must not propagate a connect failure. A hosted service that throws out
/// of StartAsync aborts host startup, so launching with no QA403 plugged in killed the whole app on
/// <c>Qa40xDeviceNotFoundException</c> — after the browser had already been opened on a server that
/// then never came up. Nothing about the rest of the app needs a device to exist: the UI, the
/// measurement database and every stored result are all still usable, status simply reports
/// disconnected, and <c>POST /api/device/connect</c> retries once the device is attached (see
/// <see cref="DeviceSessionService.ConnectAsync"/>, which is written to be retryable for exactly
/// this). Cancellation is left alone — that one is the host itself being shut down mid-startup,
/// not a missing device.
///
/// <see cref="StopAsync"/> must call <see cref="DeviceSessionService.ShutdownAsync"/>, never
/// <c>StopAcquisitionAsync</c> — see that method's remarks for why the latter left the device
/// streaming (and therefore unreachable until power-cycled) in both of the app's usual end states.
/// Hosted services are stopped before the DI container disposes singletons, which is exactly the
/// ordering the device needs: pump joined and StreamingStop written first, handle closed second.
/// </summary>
public sealed class DeviceLifecycleHostedService(
    DeviceSessionService session,
    ILogger<DeviceLifecycleHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await session.ConnectAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Qa40xDeviceNotFoundException)
        {
            // No stack trace: nothing is wrong, there is simply no QA403 on the bus. Anything else
            // (interface busy, verify failure, calibration read) does get one — those are faults.
            logger.LogWarning(
                "No QA403 found on USB — starting without one. Attach the device and press Connect in the status bar.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not connect to the QA403 at startup ({Reason}) — starting without a device. Press Connect in the status bar to retry.",
                ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => session.ShutdownAsync();
}
