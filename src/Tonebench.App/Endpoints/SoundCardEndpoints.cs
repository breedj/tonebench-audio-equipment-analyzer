using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device.SoundCard;

namespace Tonebench.App.Endpoints;

/// <summary>
/// Reading the machine's audio devices. The sound card's <i>settings</i> live with every other
/// setting under <c>/api/config/soundcard</c>; this group is the part that asks the hardware.
/// </summary>
public static class SoundCardEndpoints
{
    public static void MapSoundCardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/soundcard");

        // Enumeration is a native call on PortAudio's own thread and takes a few milliseconds, so it
        // is a GET the dialog makes when it opens rather than something on the status poll.
        //
        // `refresh` re-reads the device table, and it is honest about its limits: PortAudio caches
        // the list at Pa_Initialize and the only way to rebuild it -- terminate and initialise again
        // -- crashes the process once ASIO is in the build. WASAPI's own rescan is used where it
        // exists; a newly plugged ASIO interface needs the app restarted, which is what every other
        // ASIO host asks for too.
        group.MapGet("/devices", (bool? refresh, ILoggerFactory loggers) =>
        {
            SoundCardInventory catalog = SoundCardCatalog.Enumerate(refresh ?? false);

            // **Which host APIs this build of PortAudio actually carries, logged once per
            // enumeration.** It belongs in the log and it cannot go in the startup line: the
            // answer requires Pa_Initialize, which calls ASIOInit on every registered driver and
            // is exactly the call one faulting third-party driver used to take the process down
            // with (see packaging/native/portaudio.md). A QA403 bench must not pay that on every
            // start -- but a sound-card bench has already paid it by the time this GET runs.
            //
            // Worth having because "ASIO is not in the list" and "your card has no ASIO driver"
            // look identical from a screenshot, and only the first is ours. The count beside each
            // name says which of them found anything.
            loggers.CreateLogger("Tonebench.SoundCard").LogInformation(
                "Audio devices enumerated by {Version}: available {Available}, host APIs {HostApis}, "
                    + "{Inputs} input(s), {Outputs} output(s), {FullDuplex} full duplex{Problem}",
                catalog.Version is { Length: > 0 } v ? v : "PortAudio",
                catalog.Available,
                string.Join(
                    ", ",
                    catalog.Inputs.Concat(catalog.Outputs)
                        .GroupBy(o => o.HostApi)
                        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(g => $"{g.Key} ({g.Count()})")),
                catalog.Inputs.Count,
                catalog.Outputs.Count,
                catalog.Inputs.Count(o => o.IsFullDuplex),
                catalog.Problem is { Length: > 0 } p ? $" — {p}" : string.Empty);

            return Results.Ok(catalog);
        });

        // Whether a particular pairing works, and at which rates. Separate from the listing because
        // it has to be re-asked whenever either side or either channel offset changes, and because
        // probing every possible pair up front would be N² native calls.
        group.MapGet("/probe", (
            string inputHostApi, string inputDevice, string outputHostApi, string outputDevice,
            int? inputChannelOffset, int? outputChannelOffset) =>
            Results.Ok(SoundCardCatalog.Probe(
                inputHostApi, inputDevice, inputChannelOffset ?? 0,
                outputHostApi, outputDevice, outputChannelOffset ?? 0)));

        // The driver's own settings panel. A POST although it reads nothing, because it is an
        // action with a side effect on the machine, and it does not return until the user closes
        // the panel -- there is no non-blocking way to raise it.
        //
        // Refused while acquiring, and that is not politeness: the panel blocks the one STA thread
        // PortAudio is driven from, which is the thread a running stream's transfer loop lives on.
        // Raising it mid-stream would stop the transfers for as long as the dialog stayed up.
        group.MapPost("/control-panel", (
            SoundCardControlPanelRequest request, DeviceSessionService session,
            IEnumerable<IMeasurementRun> runs) =>
        {
            if (session.Acquiring || MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict("Stop the measurement first — the card's control panel holds the audio thread while it is open.");

            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                if (!SoundCardCatalog.ShowControlPanel(request.HostApi, request.DeviceName))
                    return Results.Conflict("That card has no control panel of its own. Only an ASIO driver provides one.");

                // The driver reported success, which is all ASIO can tell us -- there is no way to
                // ask whether a window actually appeared. A panel that is genuinely shown is modal
                // and this call does not return until it is dismissed, so a return inside a second
                // means the driver accepted the request and did nothing. Measured on a MOTU M4
                // (2026-09-30): immediate, because that driver keeps its settings in a separate
                // application. Reported as a flag rather than an error, because it is not one.
                return Results.Ok(new SoundCardControlPanelResult(
                    NothingAppeared: started.Elapsed < TimeSpan.FromSeconds(1)));
            }
            catch (SoundCardNotFoundException ex)
            {
                return Results.Conflict(ex.Message);
            }
            catch (PortAudioException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // What the inputs are hearing right now, for the dialog's level bars. A small poll rather
        // than a socket: it is only read while that dialog is open, and the levels it wants are the
        // calibration's own derivation rather than anything the spectrum stream already carries.
        group.MapGet("/levels", (SoundCardCalibrationRunner runner, DeviceSessionService session) =>
        {
            (double? left, double? right) = session.Acquiring
                ? runner.CurrentInputLevels()
                : (null, null);

            return Results.Ok(new SoundCardLevels(left, right, session.Acquiring, runner.PlayingDbFs));
        });

        // The two calibration steps. Sequenced here rather than in the dialog so the run guard sees
        // them: a calibration tone playing is a measurement in progress, and a sweep started over it
        // would take the generator away mid-step.
        //
        // The output step holds a tone across requests because the user is reading a voltmeter while
        // it plays; the input step is one request because nothing happens during its capture.
        var calibration = group.MapGroup("/calibration");

        calibration.MapGet("/", (SoundCardCalibrationRunner runner) =>
            Results.Ok(new SoundCardCalibrationState(
                runner.Running, runner.PlayingDbFs, SoundCardCalibrationRunner.ToneHz,
                SoundCardCalibrationRunner.OutputLevelsDbFs)));

        calibration.MapPost("/output/start", async (
            SoundCardToneRequest request, SoundCardCalibrationRunner runner, DeviceSessionService session,
            IEnumerable<IMeasurementRun> runs, CancellationToken ct) =>
        {
            if (MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict(MeasurementRunGuard.Message);

            try
            {
                await runner.StartOutputToneAsync(request.DbFs, ct);
                return Results.Ok(new SoundCardCalibrationState(
                    runner.Running, runner.PlayingDbFs, SoundCardCalibrationRunner.ToneHz,
                    SoundCardCalibrationRunner.OutputLevelsDbFs));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        calibration.MapPost("/output/apply", async (
            SoundCardMeasurementRequest request, SoundCardCalibrationRunner runner, CancellationToken ct) =>
        {
            if (!EnumParsing.TryParse(request.Unit, VoltageEntryUnit.Rms, out VoltageEntryUnit unit, out string unitError))
                return Results.BadRequest(unitError);

            try
            {
                return Results.Ok(await runner.ApplyOutputAsync(request.Value, unit, ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        calibration.MapPost("/input", async (
            SoundCardInputMeasurementRequest request, SoundCardCalibrationRunner runner,
            DeviceSessionService session, IEnumerable<IMeasurementRun> runs, CancellationToken ct) =>
        {
            if (MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict(MeasurementRunGuard.Message);

            if (!EnumParsing.TryParse(request.Unit, VoltageEntryUnit.Rms, out VoltageEntryUnit unit, out string unitError))
                return Results.BadRequest(unitError);

            if (!EnumParsing.TryParse(request.Channel, StreamChannel.Left, out StreamChannel channel, out string channelError))
                return Results.BadRequest(channelError);

            try
            {
                return Results.Ok(await runner.MeasureInputAsync(request.Value, unit, channel, ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // The same input step, but against the card's own output through a patch cable instead of
        // a second signal generator. Refused while the output is uncalibrated -- the known voltage
        // comes from the output's full scale, so without it the result would look measured and be
        // invented.
        calibration.MapPost("/input/loopback", async (
            SoundCardLoopbackRequest request, SoundCardCalibrationRunner runner,
            DeviceSessionService session, IEnumerable<IMeasurementRun> runs, CancellationToken ct) =>
        {
            if (MeasurementRunGuard.AnotherRunIsActive(session, runs))
                return Results.Conflict(MeasurementRunGuard.Message);

            try
            {
                return Results.Ok(await runner.MeasureInputViaLoopbackAsync(request.DbFs, ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });

        // Also reached from the client's own teardown, so a dialog closed mid-step never leaves a
        // tone playing.
        calibration.MapPost("/cancel", async (SoundCardCalibrationRunner runner, CancellationToken ct) =>
        {
            await runner.SilenceAsync(ct);
            return Results.Ok(new SoundCardCalibrationState(
                runner.Running, runner.PlayingDbFs, SoundCardCalibrationRunner.ToneHz,
                SoundCardCalibrationRunner.OutputLevelsDbFs));
        });
    }
}

/// <param name="DbFs">How far below full scale to play, one of <see cref="SoundCardCalibrationRunner.OutputLevelsDbFs"/>.</param>
public sealed record SoundCardToneRequest(double DbFs);

/// <param name="Unit">"Rms" or "Peak" — a card calibrated against the wrong one is 3.01 dB out.</param>
public sealed record SoundCardMeasurementRequest(double Value, string Unit);

/// <param name="Channel">"Left" or "Right": which input the known source is on.</param>
public sealed record SoundCardInputMeasurementRequest(double Value, string Unit, string Channel);

/// <param name="DbFs">How far below full scale to play. Both channels are driven and both inputs measured.</param>
public sealed record SoundCardLoopbackRequest(double DbFs);

/// <param name="HostApi">The host API the device was listed under — an ASIO device's panel is the only kind there is.</param>
/// <param name="DeviceName">The device's name, as the listing gives it. Resolved afresh, because PortAudio's indices move.</param>
public sealed record SoundCardControlPanelRequest(string HostApi, string DeviceName);

/// <param name="NothingAppeared">
/// True when the driver accepted the request and returned at once, which means no panel was shown
/// — some ASIO drivers keep their settings in a separate application instead. A guess, and the
/// only one available: ASIO offers no way to ask whether a window appeared.
/// </param>
public sealed record SoundCardControlPanelResult(bool NothingAppeared);

/// <param name="Acquiring">False when nothing is streaming, so the bars read as idle rather than as silence.</param>
/// <param name="PlayingDbFs">
/// The level of the calibration tone, or null when none is playing — carried here, on the poll the
/// dialog is already making, so its idea of whether a tone is up cannot drift from the server's.
/// It used to be updated only by the requests the dialog itself made, which left "Estimate peak
/// output mV" enabled over a card that had stopped if anything silenced the tone by another path.
/// </param>
public sealed record SoundCardLevels(double? LeftDbFs, double? RightDbFs, bool Acquiring, double? PlayingDbFs);

/// <param name="PlayingDbFs">The level currently playing, or null when no tone is up.</param>
public sealed record SoundCardCalibrationState(
    bool Running, double? PlayingDbFs, double ToneHz, IReadOnlyList<double> OutputLevelsDbFs);
