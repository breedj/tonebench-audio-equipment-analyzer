using System.Text.Json;
using Tonebench.App.Json;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.Hardware;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Endpoints;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/device");

        // Async because this poll is also the app's disconnect detector while nothing is streaming
        // — see DeviceSessionService.GetStatusAsync.
        //
        // The hub's drop counts are stamped on here, like DatabaseProblem and for the same reason:
        // the session owns the device, not the WebSocket fan-out, and this poll is the one place
        // the status bar already reads every second.
        group.MapGet("", async (
            DeviceSessionService session, DatabaseHealth health, SpectrumBroadcastHub hub, CancellationToken ct) =>
        {
            var (droppedMax, droppedTotal) = hub.DroppedFrames();
            return Results.Ok((await session.GetStatusAsync(ct)) with
            {
                DatabaseProblem = health.Problem,
                ConfigDatabaseRepairable = health.ConfigProblem is not null,
                DroppedStreamFramesMax = droppedMax,
                DroppedStreamFramesTotal = droppedTotal,
            });
        });

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
                loggers.CreateLogger("Tonebench.App.Endpoints.Device").LogWarning(ex, "Connect failed");
                return Results.Text(ConnectFailure.MessageFor(ex), "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        group.MapGet("/ranges", (DeviceSessionService session, ClockDriftSetting clockDrift) =>
        {
            var caps = session.Capabilities;

            // Read here rather than in the session, which knows nothing about the configuration
            // database -- the same division DatabaseProblem on the status response follows.
            var drift = ClockDriftCorrection.ForBench(session.BenchClocks, clockDrift.Config);

            return Results.Ok(new DeviceRangesResponse(
                caps.SampleRatesHz, caps.InputRangesDbv, caps.OutputRangesDbv, caps.MaxChannelCount,
                session.MinStimulusFrequencyHz, session.MaxGeneratorSampleRateHz,
                drift?.Ppm, drift?.MeasuredUtc,
                caps.OutputSampleRatesHz, session.GeneratorSampleRateHz,
                session.GeneratorRateOverrideHz, session.BenchClocks is not null,
                session.SweepSampleRatesHz));
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

        // Writes the device, the live setting and the stored document together, which is why it
        // takes all three. It is on /device rather than /config because the refusal below is a
        // property of the attached analyzer — but the value it writes outlives this session and this
        // device: AnalyzerDeviceFactory applies it to every device it builds afterwards, which is
        // what carries it across a restart and across an analyzer switch. Until 2026-10-06 it only
        // wrote the device, and the delay went back to 1000 ms the next time the app started.
        group.MapPost("/auto-range-step-down-delay", async (
            AutoRangeStepDownDelayRequest request, DeviceSessionService session,
            AutoRangeStepDownDelaySetting setting, ConfigRepository config, CancellationToken ct) =>
        {
            if (session.AutoRangeStepDownDelayMs is null)
                return Results.BadRequest("The connected device has no input autoranging.");

            // Validated before anything is written: a NaN reaches the ranger as a comparison that is
            // false forever, i.e. step-down silently off, and the document would then restore it on
            // every start.
            var stored = new AutoRangeStepDownDelayConfig(request.DelayMs);
            if (!stored.IsValid)
                return Results.BadRequest(
                    $"The step-down delay must be between 0 and {AutoRangeStepDownDelayConfig.MaxDelayMs:0} ms.");

            session.SetAutoRangeStepDownDelayMs(stored.DelayMs);
            setting.Ms = stored.DelayMs;
            await config.SetAsync(
                ConfigRepository.AutoRangeStepDownDelayKey,
                JsonSerializer.Serialize(stored, AppJsonContext.Default.AutoRangeStepDownDelayConfig),
                ct);

            return Results.Ok(session.GetStatus());
        });

        // Switches between the real QA403 and the simulator without a restart. Its own endpoint
        // rather than part of /connect: connect retries whatever is already selected, while this
        // tears one analyzer down and stands another up in its place.
        group.MapPost("/kind", async (
            DeviceKindRequest request, DeviceSessionService session, AnalyzerKindSetting selected,
            ConfigRepository config, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.Kind, DeviceKind.Hardware, out DeviceKind kind, out var error))
                return Results.BadRequest(error);

            try
            {
                await session.SwitchDeviceAsync(kind, ct);
            }
            catch (InvalidOperationException ex)
            {
                // A measurement is running, or the session is pinned — both states the UI can reach.
                return Results.Conflict(ex.Message);
            }

            // Remembered for the next run, and only after the switch has actually succeeded: a
            // kind that was refused is not the one the user ended up on, and storing it would put
            // the app on an analyzer nobody chose. The write is not allowed to fail the request —
            // the switch has already happened, and a configuration database that cannot be written
            // is a separate problem the status bar already reports.
            selected.Kind = kind;
            try
            {
                await config.SetAsync(
                    ConfigRepository.AnalyzerKindKey,
                    JsonSerializer.Serialize(new AnalyzerKindConfig(kind.ToString()), AppJsonContext.Default.AnalyzerKindConfig),
                    ct);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Could not remember the selected analyzer.");
            }

            return Results.Ok(session.GetStatus());
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
            "No QA40x analyzer was found. Check that it is connected over USB and switched on, then press Connect again.",

        // Unwrapped so the half that failed keeps the message its own kind of device earns -- a
        // composite's QA40x half still says to check the USB cable -- while the sentence names which
        // half it was. On a two-device bench "no analyzer was found" alone leaves the user guessing.
        Tonebench.Device.Split.SplitSideException split =>
            $"The {split.Side} ({split.Label}) did not connect. {MessageFor(split.InnerException!)}",

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
    /// <see cref="Tonebench.Device.IAnalyzerDevice.DeviceLost"/>). Always false for the simulator.
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
    /// <summary>RX chunks dropped since connect. Non-zero means FFT records contain splices and noise readings are suspect — see <see cref="Tonebench.Device.IAnalyzerDevice.DroppedRxChunks"/>.</summary>
    long DroppedRxChunks,
    /// <summary>
    /// Assembled blocks dropped since connect — the *other* half of the pipeline, and just as much a
    /// hole in the sample stream. Reported separately rather than summed with
    /// <see cref="DroppedRxChunks"/> so a non-zero value still names which side fell behind; see
    /// <see cref="Tonebench.Device.IAnalyzerDevice.DroppedBlocks"/> for why leaving it out of this
    /// response was a bug rather than an omission.
    /// </summary>
    long DroppedBlocks,
    /// <summary>
    /// What the startup <c>quick_check</c> found wrong with a database file, or null when both files
    /// are sound. Carried on the status poll so a damaged file is announced from the first second
    /// instead of as a 500 on whichever read first touches the bad page — see
    /// <see cref="Tonebench.App.Services.DatabaseHealth"/>. Set by the endpoint, not by the session, which
    /// knows nothing about storage.
    /// </summary>
    string? DatabaseProblem = null,
    /// <summary>
    /// Whether <see cref="DatabaseProblem"/> is the <i>configuration</i> file's, and can therefore
    /// be offered a rebuild — <c>POST /api/config/repair-database</c>.
    ///
    /// <para>Its own flag rather than something the client infers from the message, because the two
    /// halves of <see cref="Tonebench.App.Services.DatabaseHealth"/> want different words and
    /// different offers: a damaged <i>project</i> file holds measurements and has no repair here,
    /// while the configuration file holds settings, the recent list and the two calibrations, and
    /// the user's projects are untouched by it. Telling them apart by reading the sentence would be
    /// a parser over prose.</para>
    /// </summary>
    bool ConfigDatabaseRepairable = false,
    /// <summary>
    /// Stream frames the worst-off connected client has had evicted from its WebSocket queue since
    /// this acquisition started, and the sum over every connected client — see
    /// <see cref="Streaming.SpectrumBroadcastHub.DroppedFrames"/>. Unlike the two counters above
    /// this is not a hole in the <i>sample</i> stream: the measurement is sound and the stored
    /// result is unaffected, but one tick's frames are a set the client pairs by seq, so a non-zero
    /// value means what is drawn may be torn — the oscilloscope's "no fundamental to remove"
    /// flicker, with no other way to tell it apart from a server-side cause. Set by the endpoint,
    /// not by the session, which knows nothing about the hub.
    /// </summary>
    long DroppedStreamFramesMax = 0,
    /// <summary>The sum of the same counter over every connected client. Both, because one stalled browser tab says nothing about the others.</summary>
    long DroppedStreamFramesTotal = 0,
    /// <summary>Whether the simulator couples its channels (<see cref="Tonebench.Device.Simulation.SimulatedCrosstalk"/>), or null when the device isn't the simulator.</summary>
    bool? SimulatedCrosstalkEnabled = null,
    /// <summary>The simulated amplifier's output stage (<see cref="Tonebench.Device.Simulation.SimulatedOutputStageModel"/> name), or null when the device isn't the simulator.</summary>
    string? SimulatedOutputStage = null,
    /// <summary>What hangs on the simulated amplifier's output (<see cref="Tonebench.Device.Simulation.SimulatedLoad"/> name), or null when the device isn't the simulator.</summary>
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
    /// The generator is asking for more than the output range's full scale <i>right now</i>, so what
    /// reaches the DUT is clipped. Reachable under either ranging mode — see
    /// <see cref="Services.DeviceSessionService.OutputRangeClipping"/>, whose remarks correct the
    /// "manual only" claim this used to repeat.
    /// </summary>
    bool OutputRangeClipping = false,
    /// <summary>
    /// The generator clipped at some point since the current acquisition started, which the live
    /// flag above cannot say: a sweep configures a stimulus per point, so the loudest ones can
    /// overrun the DAC and be gone again before anyone looks. Per acquisition, like the two drop
    /// counters — see
    /// <see cref="Services.DeviceSessionService.OutputClippedDuringAcquisition"/>.
    /// </summary>
    bool OutputClippedDuringAcquisition = false,
    /// <summary>
    /// The analyzer's housekeeping sensors, or null on a device that has none (the simulator) or
    /// before the first reading has landed — the UI hides the readout in that case rather than
    /// showing zeroes. A cached last reading: it is refreshed on the same throttled liveness
    /// exchange this response already triggers, and deliberately not while a stream is running, so
    /// during an acquisition it holds whatever it last read. See
    /// <see cref="Tonebench.Device.IAnalyzerDevice.Telemetry"/>.
    /// </summary>
    DeviceTelemetryResponse? Telemetry = null,
    /// <summary>
    /// The chosen input, or null when the analyzer is not the sound card — the same null convention
    /// as the <c>Simulated*</c> fields, so the status bar gates its sound-card controls on this
    /// rather than on a device-kind string comparison.
    /// </summary>
    string? SoundCardInputDevice = null,
    /// <summary>The chosen output, same convention.</summary>
    string? SoundCardOutputDevice = null,
    /// <summary>
    /// False until both calibration steps have been run. An uncalibrated card still measures every
    /// <i>ratio</i> exactly — THD, IMD, crosstalk, the response shape — because a consistent wrong
    /// full-scale constant cancels; what it cannot do is name a voltage. The bar says so, because a
    /// card reading "0.00 dBV" it has never measured is the failure this feature exists to avoid.
    /// </summary>
    bool? SoundCardCalibrated = null,
    /// <summary>
    /// Whether the bar reads the two external gains above in dB or as a ratio — <c>"Db"</c> or
    /// <c>"Times"</c>. Display only: the stored value is dB, which is what every measurement's
    /// parameters record.
    ///
    /// <para>The <i>effective</i> unit, the way <see cref="ExternalGainLeftDb"/> is the effective
    /// gain: it follows whichever bench is live, because each stores its own. Never null — there is
    /// always some unit the figures are being read in.</para>
    /// </summary>
    string ExternalGainUnit = "Db",
    /// <summary>
    /// On a composite analyzer, the <c>DeviceKind</c> name of the capturing half; null on every
    /// other kind. The bar names both devices from these, and the telemetry rows are gated on them:
    /// a QA40x inside a split still has its three sensors, and the row it reserves is part of what
    /// the bar's width is laid out around.
    /// </summary>
    string? SplitInputKind = null,
    /// <summary>The generating half's kind. See <see cref="SplitInputKind"/>.</summary>
    string? SplitOutputKind = null);

/// <summary>
/// Wire shape of <see cref="Tonebench.Device.DeviceTelemetry"/>: the figures QuantAsylum's own software
/// shows beside the firmware version, less the isolated-rail current, which a QA403 does not report
/// (see <see cref="Tonebench.Device.Hardware.Qa40xRegisters.TelemetryIsoCurrent"/>). Units are settled
/// here — volts, milliamps and degrees Celsius — so the client formats rather than scales.
/// </summary>
public sealed record DeviceTelemetryResponse(
    double UsbVoltageV,
    double UsbCurrentMa,
    double TemperatureC);

/// <summary>Wire shape matches the request-side <c>GeneratorToneRequest</c> in GeneratorEndpoints.cs (freqHz/ampDbv), not the internal <c>Tonebench.Device.GeneratorTone</c>'s property names.</summary>
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
    EchoChirpResponse Chirp,
    PeriodicWaveResponse Wave,
    ToneBurstResponse Burst,
    DcLevelResponse Dc,
    ArbitraryResponse Arbitrary);

public sealed record MultitoneResponse(int TonesPerOctave, double TotalRmsDbv, double RolloffDbPerOctave);

public sealed record WhiteNoiseResponse(double LevelDbv);

public sealed record EchoChirpResponse(double AmplitudeDbv, double StartFreqHz, double EndFreqHz, SmoothingOctaves Smoothing);

/// <summary>
/// The shape block, shared by square, triangle, sawtooth and pulse. <b>Both the frequency and the
/// duty are the <i>resolved</i> ones</b> once a generator has been configured: the frequency is
/// quantised to a whole even sample period and the duty to a whole number of samples (see
/// <c>Tonebench.Device.PeriodicWave</c>), and <c>DeviceSessionService.ResolveWaves</c> rewrites
/// both, so the panel can show what is playing beneath what was typed.
/// </summary>
public sealed record PeriodicWaveResponse(double FreqHz, double LevelDbv, double DutyPercent);

/// <summary>
/// The tone-burst block. <c>FreqHz</c> is the resolved carrier and <c>LevelDbv</c> is the carrier's
/// own rms while it plays — not the stimulus's overall rms, which is lower by the square root of
/// the duty. See <c>Tonebench.Device.ToneBurst</c>.
/// </summary>
public sealed record ToneBurstResponse(double FreqHz, double LevelDbv, int CyclesOn, int CyclesOff);

/// <summary>The DC block, in volts and signed — see <c>Tonebench.Device.DcLevelSettings</c>.</summary>
public sealed record DcLevelResponse(double Volts);

/// <summary>
/// The arbitrary block: what was chosen, and — once a generator has been configured — what the
/// file turned out to be.
///
/// <para>Every figure past <c>LevelDbv</c> is <b>null until the file has been loaded</b>, which
/// happens when the generator is configured rather than when a measurement is selected: a stored
/// type carries the path and nothing else, so a panel showing a measurement that has not been run
/// has a path and no readings. That is also why none of them is 0 instead — a loop of 0 frames is
/// a claim, and "not loaded yet" is not one.</para>
/// </summary>
/// <param name="Frames">The loop's length at the analyzer's rate, which is also its period.</param>
/// <param name="DurationSeconds">How long one loop lasts.</param>
/// <param name="RepeatHz">How often it repeats — what a scope's timebase is set from.</param>
/// <param name="CrestFactorDb">The file's own peak-to-rms, measured. What decides the output range.</param>
/// <param name="SourceSampleRateHz">The file's own rate, which is usually not the analyzer's.</param>
/// <param name="SourceFrames">And its own length, before resampling.</param>
public sealed record ArbitraryResponse(
    string? Path,
    ArbitraryChannel Channel,
    double LevelDbv,
    int? Frames = null,
    double? DurationSeconds = null,
    double? RepeatHz = null,
    double? CrestFactorDb = null,
    double? SourceSampleRateHz = null,
    int? SourceFrames = null);

public sealed record DeviceRangesResponse(
    IReadOnlyList<double> SampleRatesHz,
    IReadOnlyList<double> InputRangesDbv,
    IReadOnlyList<double> OutputRangesDbv,
    int MaxChannelCount,
    /// <summary>
    /// The lowest frequency this analyzer can excite, in Hz; 0 on one with no floor. The chirp
    /// panels warn against it before a run, so the raising is not a surprise afterwards.
    /// </summary>
    double MinStimulusFrequencyHz = 0.0,
    /// <summary>
    /// The highest sample rate this analyzer can play a stimulus at, in Hz, or <b>null</b> on one
    /// with no ceiling — never infinity, which would truncate this response (see
    /// <see cref="Tonebench.App.Services.DeviceSessionService.MaxGeneratorSampleRateHz"/>). The FFT
    /// screen's Generator panel stands itself down above it; see <c>generatorRateCeiling.ts</c>.
    /// </summary>
    double? MaxGeneratorSampleRateHz = null,
    /// <summary>
    /// How far the input's clock runs from the output's, in ppm, or <b>null</b> when no chirp
    /// measurement on this bench can be corrected for it — see
    /// <see cref="Tonebench.App.Services.ClockDriftCorrection.ForBench"/>, which carries the four
    /// separate reasons that answer can be null.
    ///
    /// <para>On this response rather than on the status poll because it is a capability of the
    /// bench and not a reading from it: it changes only when the drift test is run or the card
    /// pairing is changed, both of which already re-read the ranges (the sound-card dialog's close
    /// does it, and the drift dialog is nested inside it). The two chirp panels offer their
    /// correction checkbox on it exactly as they take their chirp floor from
    /// <see cref="MinStimulusFrequencyHz"/>.</para>
    /// </summary>
    double? ClockDriftPpm = null,
    /// <summary>When the figure above was measured, for the caption beside the checkbox. Null when there is no figure.</summary>
    DateTimeOffset? ClockDriftMeasuredUtc = null,
    /// <summary>
    /// The rates this analyzer's <i>generator</i> can be driven at, which on every device with one
    /// clock is the same list as <see cref="SampleRatesHz"/>. The two differ only on a composite,
    /// where they are deliberately not intersected — see
    /// <see cref="Tonebench.Device.DeviceCapabilities.OutputSampleRatesHz"/>.
    /// </summary>
    IReadOnlyList<double>? OutputSampleRatesHz = null,
    /// <summary>
    /// The rate the generator is being driven at right now. The client's mirror of the square and
    /// triangle quantisation reads it (<c>periodicWave.ts</c>), because that period is a count of
    /// the <i>generator's</i> frames: a trigger computed at the capture rate walks the trace across
    /// the screen on a bench whose two halves differ.
    /// </summary>
    double? GeneratorSampleRateHz = null,
    /// <summary>
    /// The rate the generator is <i>pinned</i> to, or 0 where it follows the capture rate — the
    /// setting rather than the reading above, which is the last rate anything streamed at.
    ///
    /// <para>The generator panel needs the difference: pinned, the staged capture rate cannot make
    /// the generator fail and there is nothing to warn about; following, the staged rate <b>is</b>
    /// the rate the generator will be asked for, and a bench whose generating half cannot play it
    /// should say so before Run rather than answering a 409.</para>
    /// </summary>
    double GeneratorRateOverrideHz = 0.0,
    /// <summary>
    /// Whether this bench is running on two clocks — <c>DeviceSessionService.BenchClocks</c>.
    ///
    /// <para>Carried here so the two chirp panels can say what to do about it when there is no
    /// stored figure to correct with: <see cref="ClockDriftPpm"/> being null covers both "one clock,
    /// nothing to correct" and "two clocks, never measured", and only the second is worth a line.
    /// </para>
    /// </summary>
    bool BenchHasTwoClocks = false,
    /// <summary>
    /// The rates a <i>stepped sweep</i> may choose among on this bench — see
    /// <c>DeviceSessionService.SweepSampleRatesHz</c>, which is the one place that intersects the
    /// capture and generator ladders.
    ///
    /// <para>Sent rather than derived on the client, although the client already has both lists.
    /// Two reasons: a sweep ladder is the server's rule (the run is refused against it, by
    /// <c>RequireGeneratorRateUsable</c>), and the four sweep panels only need to <i>show</i> what
    /// the server will do. Deriving it in each of them is four copies of an intersection that can
    /// drift from the one the runner stamps on.</para>
    /// </summary>
    IReadOnlyList<double>? SweepSampleRatesHz = null);

public sealed record InputRangeRequest(double Dbv);

public sealed record OutputRangeRequest(double Dbv);

/// <param name="Mode">"Auto" or "Manual" — see <see cref="Tonebench.App.Services.RangingMode"/>.</param>
public sealed record RangingModeRequest(string? Mode);

public sealed record AutoRangeStepDownDelayRequest(double DelayMs);

/// <param name="Dut">
/// Which simulated device under test to patch in. Omitted leaves it alone, same as the two
/// switches — see <see cref="Tonebench.Device.Simulation.SimulatedDutModel"/>.
/// </param>
public sealed record SimulatedProfileRequest(
    bool? NoiseEnabled, bool? HumEnabled, bool? HarmonicsEnabled = null, string? Dut = null, bool? CrosstalkEnabled = null,
    string? OutputStage = null, string? Load = null);

/// <param name="Kind">"Hardware" or "Simulated" — see <see cref="Tonebench.App.Services.DeviceKind"/>.</param>
public sealed record DeviceKindRequest(string? Kind);
