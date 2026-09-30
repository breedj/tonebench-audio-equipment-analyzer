using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device.SoundCard;

namespace Qa403.App.Endpoints;

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
        group.MapGet("/devices", (bool? refresh) =>
            Results.Ok(SoundCardCatalog.Enumerate(refresh ?? false)));

        // Whether a particular pairing works, and at which rates. Separate from the listing because
        // it has to be re-asked whenever either side or either channel offset changes, and because
        // probing every possible pair up front would be N² native calls.
        group.MapGet("/probe", (
            string inputHostApi, string inputDevice, string outputHostApi, string outputDevice,
            int? inputChannelOffset, int? outputChannelOffset) =>
            Results.Ok(SoundCardCatalog.Probe(
                inputHostApi, inputDevice, inputChannelOffset ?? 0,
                outputHostApi, outputDevice, outputChannelOffset ?? 0)));

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

/// <param name="PlayingDbFs">The level currently playing, or null when no tone is up.</param>
public sealed record SoundCardCalibrationState(
    bool Running, double? PlayingDbFs, double ToneHz, IReadOnlyList<double> OutputLevelsDbFs);
