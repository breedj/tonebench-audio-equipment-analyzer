using Tonebench.App.Diagnostics;
using Tonebench.Device.Hardware;

namespace Tonebench.App.Services;

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
    ILogger<DeviceLifecycleHostedService> logger,
    // Optional for the same reason the session's own recorder parameter is: the container always
    // supplies it, and the tests that construct this by hand are about startup surviving a missing
    // device, not about diagnostics.
    StreamRecorder? streamRecorder = null) : IHostedService
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
            // No stack trace: nothing is wrong, there is simply no QA40x on the bus. Anything else
            // (interface busy, verify failure, calibration read) does get one — those are faults.
            logger.LogWarning(
                "No QA40x analyzer found on USB — starting without one. Attach the device and press Connect in the status bar.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not connect to the QA40x at startup ({Reason}) — starting without a device. Press Connect in the status bar to retry.",
                ex.Message);
        }
    }

    /// <summary>
    /// Session teardown, then the recorder's writer. In that order: the session's shutdown is what
    /// closes an open recording and patches its WAV headers, and stopping the writer first would
    /// leave it with nobody to write that close.
    ///
    /// <para>Bounded, as everything in this shutdown is (see <c>Program</c>'s nested budget): the
    /// recorder's queue holds at most a few megabytes of already-copied samples, and the close is
    /// two four-byte writes.</para>
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await session.ShutdownAsync();
        if (streamRecorder is not null)
            await streamRecorder.StopAsync();
    }
}
