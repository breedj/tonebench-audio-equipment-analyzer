using Qa403.App.Services;
using Qa403.Device;

namespace Qa403.App.Endpoints;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/device");

        // Async because this poll is also the app's disconnect detector while nothing is streaming
        // — see DeviceSessionService.GetStatusAsync.
        group.MapGet("", async (DeviceSessionService session, CancellationToken ct) =>
            Results.Ok(await session.GetStatusAsync(ct)));

        // Retry hook for a device that wasn't attached when the app started — a missing QA403 no
        // longer aborts startup (see DeviceLifecycleHostedService), so this is how the session gets
        // one afterwards. Idempotent: connecting an already-connected session just returns status.
        // 503 rather than 500 on failure because "no device found" is a transient condition the user
        // fixes by plugging one in, and the message is what the status bar shows them.
        group.MapPost("/connect", async (DeviceSessionService session, CancellationToken ct) =>
        {
            try
            {
                await session.ConnectAsync(ct);
                return Results.Ok(session.GetStatus());
            }
            catch (Exception ex)
            {
                return Results.Text(ex.Message, "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/ranges", (DeviceSessionService session) =>
        {
            var caps = session.Capabilities;
            return Results.Ok(new DeviceRangesResponse(
                caps.SampleRatesHz, caps.InputRangesDbv, caps.OutputRangesDbv, caps.MaxChannelCount));
        });

        group.MapPost("/input-range", async (InputRangeRequest request, DeviceSessionService session, CancellationToken ct) =>
        {
            try
            {
                await session.SetInputRangeAsync(request.Dbv, ct);
                return Results.Ok(session.GetStatus());
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/output-range", async (OutputRangeRequest request, DeviceSessionService session, CancellationToken ct) =>
        {
            try
            {
                await session.SetOutputRangeAsync(request.Dbv, ct);
                return Results.Ok(session.GetStatus());
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/auto-range", (AutoRangeRequest request, DeviceSessionService session) =>
        {
            if (session.AutoRangeEnabled is null)
                return Results.BadRequest("The connected device has no input autoranging.");

            session.SetAutoRangeEnabled(request.Enabled);
            return Results.Ok(session.GetStatus());
        });

        group.MapPost("/auto-range-step-down-delay", (AutoRangeStepDownDelayRequest request, DeviceSessionService session) =>
        {
            if (session.AutoRangeStepDownDelayMs is null)
                return Results.BadRequest("The connected device has no input autoranging.");

            session.SetAutoRangeStepDownDelayMs(request.DelayMs);
            return Results.Ok(session.GetStatus());
        });

        // Simulator-only: shapes the synthesized input signal, which real hardware cannot do.
        // Exposed so the noise floor and hum can be taken out of the way while reading a spectrum.
        // Patch-style — an omitted field leaves that setting alone.
        group.MapPost("/sim-profile", (SimulatedProfileRequest request, DeviceSessionService session) =>
        {
            if (!session.TrySetSimulatedProfile(request.NoiseEnabled, request.HumEnabled))
                return Results.BadRequest("The connected device is not the simulator, so it has no signal-profile switches.");

            return Results.Ok(session.GetStatus());
        });
    }
}

public sealed record DeviceStatusResponse(
    bool Connected,
    string ProductName,
    string SerialNumber,
    string FirmwareVersion,
    bool IsSimulated,
    double InputRangeDbv,
    double OutputRangeDbv,
    bool Acquiring,
    GeneratorChannelResponse GeneratorLeft,
    GeneratorChannelResponse GeneratorRight,
    /// <summary>Simulated DUT noise floor on/off, or null when the device isn't the simulator — the UI hides the switch in that case.</summary>
    bool? SimulatedNoiseEnabled,
    /// <summary>Simulated DUT mains hum on/off, same null convention as <see cref="SimulatedNoiseEnabled"/>.</summary>
    bool? SimulatedHumEnabled,
    /// <summary>
    /// True once the transport has reported the physical device disconnected mid-session (see
    /// <see cref="Qa403.Device.IAnalyzerDevice.DeviceLost"/>). Always false for the simulator.
    /// Sticky for the life of the process — <c>POST /api/device/connect</c> brings up a session that
    /// never had a device, not one whose transport died under it, so this stays a terminal state the
    /// UI should surface plainly rather than something to poll past.
    /// </summary>
    bool DeviceLost,
    /// <summary>Input autoranging on/off, or null when the device has no autoranging (the simulator) — the UI hides the checkbox in that case.</summary>
    bool? AutoRangeEnabled,
    /// <summary>
    /// How long, in milliseconds, a peak must stay under-range before hardware autoranging steps to
    /// a more sensitive range. Same null-means-no-autoranging convention as
    /// <see cref="AutoRangeEnabled"/>.
    /// </summary>
    double? AutoRangeStepDownDelayMs,
    /// <summary>Whether generator tones are snapped to the nearest coherent FFT bin. The Tones lists above are the snapped (effective) frequencies.</summary>
    bool GeneratorSnapToBin,
    /// <summary>RX chunks dropped since connect. Non-zero means FFT records contain splices and noise readings are suspect — see <see cref="Qa403.Device.IAnalyzerDevice.DroppedRxChunks"/>.</summary>
    long DroppedRxChunks);

/// <summary>Wire shape matches the request-side <c>GeneratorToneRequest</c> in GeneratorEndpoints.cs (freqHz/ampDbv), not the internal <c>Qa403.Device.GeneratorTone</c>'s property names.</summary>
public sealed record GeneratorToneResponse(double FreqHz, double AmpDbv);

/// <summary>
/// One channel's generator state. Every per-kind block is always present (filled with the server's
/// defaults when that kind has never been configured) so the UI can seed all of its forms from one
/// status read. <c>Tones</c> is the effective tone list — for <c>Multitone</c> it holds the derived
/// comb, not the user's parameters.
/// </summary>
public sealed record GeneratorChannelResponse(
    bool Enabled,
    IReadOnlyList<GeneratorToneResponse> Tones,
    GeneratorKind Kind,
    MultitoneResponse Multitone,
    WhiteNoiseResponse Noise,
    EchoChirpResponse Chirp);

public sealed record MultitoneResponse(int TonesPerOctave, double TotalRmsDbv, double RolloffDbPerOctave);

public sealed record WhiteNoiseResponse(double LevelDbv);

public sealed record EchoChirpResponse(double AmplitudeDbv, double StartFreqHz, double EndFreqHz, SmoothingOctaves Smoothing);

public sealed record DeviceRangesResponse(
    IReadOnlyList<double> SampleRatesHz,
    IReadOnlyList<double> InputRangesDbv,
    IReadOnlyList<double> OutputRangesDbv,
    int MaxChannelCount);

public sealed record InputRangeRequest(double Dbv);

public sealed record OutputRangeRequest(double Dbv);

public sealed record AutoRangeRequest(bool Enabled);

public sealed record AutoRangeStepDownDelayRequest(double DelayMs);

public sealed record SimulatedProfileRequest(bool? NoiseEnabled, bool? HumEnabled);
