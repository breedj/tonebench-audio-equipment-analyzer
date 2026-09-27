using Qa403.App.Services;
using Qa403.Device;
using Qa403.Device.Hardware;
using Qa403.Device.Simulation;

namespace Qa403.App.Endpoints;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/device");

        // Async because this poll is also the app's disconnect detector while nothing is streaming
        // — see DeviceSessionService.GetStatusAsync.
        group.MapGet("", async (DeviceSessionService session, DatabaseHealth health, CancellationToken ct) =>
            Results.Ok((await session.GetStatusAsync(ct)) with { DatabaseProblem = health.Problem }));

        // Retry hook for a device that wasn't attached when the app started — a missing QA403 no
        // longer aborts startup (see DeviceLifecycleHostedService), so this is how the session gets
        // one afterwards. Idempotent: connecting an already-connected session just returns status.
        // 503 rather than 500 on failure because "no device found" is a transient condition the user
        // fixes by plugging one in, and the message is what the status bar shows them.
        // The body is what the status bar shows, so it is written for the person at the bench: the
        // transport's own message names a VID and a PID, which tells a tester nothing they can act
        // on. The precise message still goes to the log, where it belongs.
        group.MapPost("/connect", async (DeviceSessionService session, ILoggerFactory loggers, CancellationToken ct) =>
        {
            try
            {
                await session.ConnectAsync(ct);
                return Results.Ok(session.GetStatus());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggers.CreateLogger("Qa403.App.Endpoints.Device").LogWarning(ex, "Connect failed");
                return Results.Text(ConnectFailure.MessageFor(ex), "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/ranges", (DeviceSessionService session) =>
        {
            var caps = session.Capabilities;
            return Results.Ok(new DeviceRangesResponse(
                caps.SampleRatesHz, caps.InputRangesDbv, caps.OutputRangesDbv, caps.MaxChannelCount));
        });

        group.MapPost("/input-range", async (
            InputRangeRequest request, DeviceSessionService session, IEnumerable<IMeasurementRun> runs, CancellationToken ct) =>
        {
            // A measurement's input range is its own while it runs. Every runner ranges for itself
            // (a probe from the widest range, a prediction from the target, a per-point ratchet),
            // and a range written under it from outside is a flush that lands inside whatever it is
            // capturing — the level probe then reads an empty capture and the run fails with a
            // message about the pipeline. That is how the browser's own live ranger, which does the
            // simulator's ranging since the device has none, was found stepping the range mid-probe
            // in one run out of eight. The session itself is an IMeasurementRun too (its acquiring
            // state guards the project switch), and a plain FFT-screen acquisition is exactly what
            // this endpoint is for, so it is excluded here.
            if (runs.Any(run => !ReferenceEquals(run, session) && run.Running))
                return Results.Conflict("A measurement is running and controls the input range; stop it first.");

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

        group.MapPost("/output-range", async (
            OutputRangeRequest request, DeviceSessionService session, IEnumerable<IMeasurementRun> runs, CancellationToken ct) =>
        {
            // Refused under Auto rather than accepted and forgotten: the range would survive exactly
            // until the next generator change, which ConfigureGeneratorAsync re-derives it on. A
            // setting that silently reverts within a second is worse than one that says no.
            if (session.RangingMode == RangingMode.Auto)
                return Results.Conflict(
                    "The output range is derived from the generator amplitude while ranging is Auto. Switch to Manual to set it.");

            // Same rule and same reasoning as /input-range: a measurement owns both ranges while it runs.
            if (MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict("A measurement is running and controls the output range; stop it first.");

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

        // Replaces the old /auto-range, which spoke only for the input attenuator. Both ranges move
        // together now (see RangingMode), and two endpoints for one mode would be two ways to reach
        // half a state each.
        group.MapPost("/ranging", async (
            RangingModeRequest request, DeviceSessionService session, IEnumerable<IMeasurementRun> runs, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.Mode, RangingMode.Auto, out RangingMode mode, out var error))
                return Results.BadRequest(error);

            if (MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict("A measurement is running and controls the ranges; stop it first.");

            try
            {
                await session.SetRangingModeAsync(mode, ct);
                return Results.Ok(session.GetStatus());
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        group.MapPost("/auto-range-step-down-delay", (AutoRangeStepDownDelayRequest request, DeviceSessionService session) =>
        {
            if (session.AutoRangeStepDownDelayMs is null)
                return Results.BadRequest("The connected device has no input autoranging.");

            session.SetAutoRangeStepDownDelayMs(request.DelayMs);
            return Results.Ok(session.GetStatus());
        });

        // Switches between the real QA403 and the simulator without a restart. Its own endpoint
        // rather than part of /connect: connect retries whatever is already selected, while this
        // tears one analyzer down and stands another up in its place.
        group.MapPost("/kind", async (
            DeviceKindRequest request, DeviceSessionService session, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.Kind, DeviceKind.Hardware, out DeviceKind kind, out var error))
                return Results.BadRequest(error);

            try
            {
                await session.SwitchDeviceAsync(kind, ct);
                return Results.Ok(session.GetStatus());
            }
            catch (InvalidOperationException ex)
            {
                // A measurement is running, or the session is pinned — both states the UI can reach.
                return Results.Conflict(ex.Message);
            }
        });

        // Simulator-only: shapes the synthesized input signal, which real hardware cannot do.
        // Exposed so the noise floor, hum and the generator's harmonic stack can be taken out of the
        // way while reading a spectrum.
        // Patch-style — an omitted field leaves that setting alone.
        group.MapPost("/sim-profile", (SimulatedProfileRequest request, DeviceSessionService session) =>
        {
            SimulatedDutModel? dut = null;
            if (request.Dut is not null)
            {
                if (!EnumParsing.TryParse(request.Dut, SimulatedDutModel.StraightWire, out SimulatedDutModel parsed, out var dutError))
                    return Results.BadRequest(dutError);

                dut = parsed;
            }

            SimulatedOutputStageModel? outputStage = null;
            if (request.OutputStage is not null)
            {
                if (!EnumParsing.TryParse(request.OutputStage, SimulatedOutputStageModel.None, out SimulatedOutputStageModel parsedStage, out var stageError))
                    return Results.BadRequest(stageError);

                outputStage = parsedStage;
            }

            SimulatedLoad? load = null;
            if (request.Load is not null)
            {
                if (!EnumParsing.TryParse(request.Load, SimulatedLoad.Open, out SimulatedLoad parsedLoad, out var loadError))
                    return Results.BadRequest(loadError);

                load = parsedLoad;
            }

            if (!session.TrySetSimulatedProfile(request.NoiseEnabled, request.HumEnabled, request.HarmonicsEnabled, dut, request.CrosstalkEnabled, outputStage, load))
                return Results.BadRequest("The connected device is not the simulator, so it has no signal-profile switches.");

            return Results.Ok(session.GetStatus());
        });
    }
}

/// <summary>The line the status bar shows when Connect fails — plain language, no USB identifiers.</summary>
internal static class ConnectFailure
{
    public static string MessageFor(Exception ex) => ex switch
    {
        Qa40xDeviceNotFoundException =>
            "No QA403 was found. Check that it is connected over USB and switched on, then press Connect again.",
        _ => ex.Message,
    };
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
    /// The simulated generator's fixed harmonic stack (−46 dBc H2, 0.51 % THD) on/off, same null
    /// convention. The four nonlinear DUT models drop the stack whatever this says, so the UI greys
    /// the switch out under those rather than offering a checkbox that does nothing.
    /// </summary>
    bool? SimulatedHarmonicsEnabled,
    /// <summary>
    /// Which simulated device under test is patched in ("StraightWire", "TubeAmplifier",
    /// "Loudspeaker", "BandpassFilter"), or null when the device isn't the simulator. Unlike the two
    /// switches above this changes the DUT's <i>response</i> rather than what is added to it.
    /// </summary>
    string? SimulatedDut,
    /// <summary>
    /// Which analyzer the session is talking to — "Hardware" or "Simulated". Reported separately
    /// from <see cref="IsSimulated"/>, which describes a <i>connected</i> device and so reads false
    /// while the simulator is selected but not yet up.
    /// </summary>
    string DeviceKind,
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
    long DroppedRxChunks,
    /// <summary>
    /// Assembled blocks dropped since connect — the *other* half of the pipeline, and just as much a
    /// hole in the sample stream. Reported separately rather than summed with
    /// <see cref="DroppedRxChunks"/> so a non-zero value still names which side fell behind; see
    /// <see cref="Qa403.Device.IAnalyzerDevice.DroppedBlocks"/> for why leaving it out of this
    /// response was a bug rather than an omission.
    /// </summary>
    long DroppedBlocks,
    /// <summary>
    /// What the startup <c>quick_check</c> found wrong with a database file, or null when both files
    /// are sound. Carried on the status poll so a damaged file is announced from the first second
    /// instead of as a 500 on whichever read first touches the bad page — see
    /// <see cref="Qa403.App.Services.DatabaseHealth"/>. Set by the endpoint, not by the session, which
    /// knows nothing about storage.
    /// </summary>
    string? DatabaseProblem = null,
    /// <summary>Whether the simulator couples its channels (<see cref="Qa403.Device.Simulation.SimulatedCrosstalk"/>), or null when the device isn't the simulator.</summary>
    bool? SimulatedCrosstalkEnabled = null,
    /// <summary>The simulated amplifier's output stage (<see cref="Qa403.Device.Simulation.SimulatedOutputStageModel"/> name), or null when the device isn't the simulator.</summary>
    string? SimulatedOutputStage = null,
    /// <summary>What hangs on the simulated amplifier's output (<see cref="Qa403.Device.Simulation.SimulatedLoad"/> name), or null when the device isn't the simulator.</summary>
    string? SimulatedLoad = null,
    /// <summary>
    /// The gain of whatever sits between the DUT and the left input, in dB (a 10:1 divider is −20),
    /// undone on every sample so the levels the app reports are the DUT's -- see
    /// <see cref="Services.ExternalGainSetting"/>. 0 with nothing in the path.
    /// </summary>
    double ExternalGainLeftDb = 0.0,
    /// <summary>The same for the right input.</summary>
    double ExternalGainRightDb = 0.0,
    /// <summary>
    /// Who is choosing the two analog ranges — "Auto" or "Manual", see
    /// <see cref="Services.RangingMode"/>. Not null-when-unavailable like
    /// <see cref="AutoRangeEnabled"/>: the simulator has no hardware ranger but it does have both
    /// attenuators, so the mode means the same thing on either analyzer.
    /// </summary>
    string RangingMode = "Auto",
    /// <summary>
    /// The generator is asking for more than the output range's full scale, so what reaches the DUT
    /// is clipped. Only reachable under manual ranging — see
    /// <see cref="Services.DeviceSessionService.OutputRangeClipping"/>.
    /// </summary>
    bool OutputRangeClipping = false,
    /// <summary>
    /// The analyzer's housekeeping sensors, or null on a device that has none (the simulator) or
    /// before the first reading has landed — the UI hides the readout in that case rather than
    /// showing zeroes. A cached last reading: it is refreshed on the same throttled liveness
    /// exchange this response already triggers, and deliberately not while a stream is running, so
    /// during an acquisition it holds whatever it last read. See
    /// <see cref="Qa403.Device.IAnalyzerDevice.Telemetry"/>.
    /// </summary>
    DeviceTelemetryResponse? Telemetry = null);

/// <summary>
/// Wire shape of <see cref="Qa403.Device.DeviceTelemetry"/>: the figures QuantAsylum's own software
/// shows beside the firmware version, less the isolated-rail current, which a QA403 does not report
/// (see <see cref="Qa403.Device.Hardware.Qa40xRegisters.TelemetryIsoCurrent"/>). Units are settled
/// here — volts, milliamps and degrees Celsius — so the client formats rather than scales.
/// </summary>
public sealed record DeviceTelemetryResponse(
    double UsbVoltageV,
    double UsbCurrentMa,
    double TemperatureC);

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

/// <param name="Mode">"Auto" or "Manual" — see <see cref="Qa403.App.Services.RangingMode"/>.</param>
public sealed record RangingModeRequest(string? Mode);

public sealed record AutoRangeStepDownDelayRequest(double DelayMs);

/// <param name="Dut">
/// Which simulated device under test to patch in. Omitted leaves it alone, same as the two
/// switches — see <see cref="Qa403.Device.Simulation.SimulatedDutModel"/>.
/// </param>
public sealed record SimulatedProfileRequest(
    bool? NoiseEnabled, bool? HumEnabled, bool? HarmonicsEnabled = null, string? Dut = null, bool? CrosstalkEnabled = null,
    string? OutputStage = null, string? Load = null);

/// <param name="Kind">"Hardware" or "Simulated" — see <see cref="Qa403.App.Services.DeviceKind"/>.</param>
public sealed record DeviceKindRequest(string? Kind);
