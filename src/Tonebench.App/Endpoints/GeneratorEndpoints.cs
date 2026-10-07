using Tonebench.App.Services;
using Tonebench.Device;

namespace Tonebench.App.Endpoints;

public static class GeneratorEndpoints
{
    public static void MapGeneratorEndpoints(this WebApplication app)
    {
        app.MapPost("/api/generator", async (GeneratorRequest request, DeviceSessionService session, CancellationToken ct) =>
        {
            if (!TryMapChannel(request.Left, "Left", out var left, out var error)
                || !TryMapChannel(request.Right, "Right", out var right, out error))
            {
                return Results.BadRequest(error);
            }

            // Only meaningful if this call needs to bring the underlying USB stream up on its own (no
            // acquisition running yet) — see ConfigureGeneratorAsync's doc comment. The UI sends its
            // current (possibly not-yet-started) Acquisition panel fields here so a generator-only
            // stream runs at the sample rate the user actually selected.
            AcquisitionConfig? streamConfigHint = request.SampleRateHz is { } sampleRateHz
                ? new AcquisitionConfig(sampleRateHz, request.ChannelCount ?? 2, request.BlockFrameCount ?? 4096)
                : null;

            var config = new GeneratorConfig(left, right) { SnapToBin = request.SnapToBin ?? true };
            try
            {
                await session.ConfigureGeneratorAsync(config, ct, streamConfigHint);
            }
            catch (InvalidOperationException ex)
            {
                // Asking for output with no device attached — the app starts without one, so this is
                // a state the UI can reach. 409 with the message, same as /acquisition/start, rather
                // than a 500 from a register write three levels down.
                return Results.Conflict(ex.Message);
            }

            return Results.Ok(session.GetStatus());
        });
    }

    /// <summary>
    /// Maps one channel's request onto the domain config. Absent blocks fall back to the settings
    /// records' own defaults, so a client only has to send the kind it actually selected. For
    /// <see cref="GeneratorKind.Multitone"/> the comb is derived here rather than in the device, so
    /// the device status reports the tones that are really playing.
    /// </summary>
    /// <remarks>Public so the mapping and its validation can be unit-tested without spinning up the host.</remarks>
    public static bool TryMapChannel(
        GeneratorChannelRequest request, string channelName, out ChannelGeneratorConfig config, out string error)
    {
        config = ChannelGeneratorConfig.Off;

        if (!EnumParsing.TryParse(request.Kind, GeneratorKind.Tones, out GeneratorKind kind, out error))
        {
            error = $"{channelName} channel: {error}";
            return false;
        }

        var multitone = ToMultitone(request.Multitone);
        var noise = ToNoise(request.Noise);
        if (!TryToWave(request.Wave, kind, channelName, out var wave, out error))
            return false;
        if (!TryToChirp(request.Chirp, out var chirp, out error))
        {
            error = $"{channelName} channel: {error}";
            return false;
        }

        IReadOnlyList<GeneratorTone> tones = request.Tones is { } requested
            ? requested.Select(t => new GeneratorTone(t.FreqHz, t.AmpDbv)).ToArray()
            : [];

        if (kind == GeneratorKind.Multitone)
        {
            try
            {
                tones = MultitoneGenerator.Build(multitone);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                error = $"{channelName} channel: {ex.Message}";
                return false;
            }
        }

        config = new ChannelGeneratorConfig(request.Enabled, tones)
        {
            Kind = kind,
            Multitone = multitone,
            Noise = noise,
            Chirp = chirp,
            Wave = wave,
        };

        return true;
    }

    /// <summary>
    /// The square/triangle block. Validated only for the kinds that read it — an untouched default
    /// must never block a stimulus that never looks at it, the rule the impedance request's two
    /// settings groups already follow.
    ///
    /// <para>Only the frequency is checked, and only against zero. The <i>upper</i> limit is
    /// <c>fs/4</c> and the sample rate is not known here (this endpoint's rate is a hint that may be
    /// absent), so a frequency above it is clamped by <see cref="PeriodicWave.PeriodFrames"/> and
    /// reported back through the effective tone list rather than refused — which is also what
    /// happens to one that is merely quantised, and the two are the same thing by degree.</para>
    /// </summary>
    private static bool TryToWave(
        PeriodicWaveRequest? request, GeneratorKind kind, string channelName,
        out PeriodicWaveSettings settings, out string error)
    {
        var defaults = new PeriodicWaveSettings();
        settings = defaults;
        error = "";

        if (request is null)
            return true;

        settings = defaults with
        {
            FrequencyHz = request.FreqHz ?? defaults.FrequencyHz,
            LevelDbv = request.LevelDbv ?? defaults.LevelDbv,
        };

        if (PeriodicWave.IsWaveKind(kind) && !(settings.FrequencyHz > 0.0))
        {
            error = $"{channelName} channel: {kind} FreqHz must be greater than 0 (got {settings.FrequencyHz}).";
            return false;
        }

        return true;
    }

    private static MultitoneSettings ToMultitone(MultitoneRequest? request)
    {
        var defaults = new MultitoneSettings();
        if (request is null)
            return defaults;

        return defaults with
        {
            TonesPerOctave = request.TonesPerOctave ?? defaults.TonesPerOctave,
            TotalRmsDbv = request.TotalRmsDbv ?? defaults.TotalRmsDbv,
            RolloffDbPerOctave = request.RolloffDbPerOctave ?? defaults.RolloffDbPerOctave,
        };
    }

    private static WhiteNoiseSettings ToNoise(WhiteNoiseRequest? request)
    {
        var defaults = new WhiteNoiseSettings();
        return request is null ? defaults : defaults with { LevelDbv = request.LevelDbv ?? defaults.LevelDbv };
    }

    private static bool TryToChirp(EchoChirpRequest? request, out EchoChirpSettings settings, out string error)
    {
        var defaults = new EchoChirpSettings();
        settings = defaults;
        error = "";

        if (request is null)
            return true;

        if (!EnumParsing.TryParse(request.Smoothing, defaults.Smoothing, out SmoothingOctaves smoothing, out error))
            return false;

        double startFreqHz = request.StartFreqHz ?? defaults.StartFrequencyHz;
        double endFreqHz = request.EndFreqHz ?? defaults.EndFrequencyHz;

        // An exponential sweep is only defined for 0 < f1 < f2 (its phase integral divides by
        // ln(f2/f1)), so reject the degenerate cases here instead of letting the simulator go silent.
        if (startFreqHz <= 0.0)
        {
            error = $"EchoChirp StartFreqHz must be greater than 0 (got {startFreqHz}).";
            return false;
        }

        if (endFreqHz <= startFreqHz)
        {
            error = $"EchoChirp EndFreqHz must be greater than StartFreqHz (got {endFreqHz} <= {startFreqHz}).";
            return false;
        }

        settings = defaults with
        {
            AmplitudeDbv = request.AmplitudeDbv ?? defaults.AmplitudeDbv,
            StartFrequencyHz = startFreqHz,
            EndFrequencyHz = endFreqHz,
            Smoothing = smoothing,
        };

        return true;
    }
}

public sealed record GeneratorToneRequest(double FreqHz, double AmpDbv);

public sealed record MultitoneRequest(int? TonesPerOctave, double? TotalRmsDbv, double? RolloffDbPerOctave);

public sealed record WhiteNoiseRequest(double? LevelDbv);

public sealed record EchoChirpRequest(double? AmplitudeDbv, double? StartFreqHz, double? EndFreqHz, string? Smoothing);

/// <summary>Square/triangle parameters — see <see cref="PeriodicWaveSettings"/>. <c>LevelDbv</c> is the waveform's total rms, not its peak.</summary>
public sealed record PeriodicWaveRequest(double? FreqHz, double? LevelDbv);

/// <summary>
/// One channel's generator request. <c>Kind</c> and the per-kind blocks are optional so the
/// original body shape (<c>{ enabled, tones }</c>) still means a plain sine tone list.
/// </summary>
public sealed record GeneratorChannelRequest(
    bool Enabled,
    IReadOnlyList<GeneratorToneRequest>? Tones,
    string? Kind = null,
    MultitoneRequest? Multitone = null,
    WhiteNoiseRequest? Noise = null,
    EchoChirpRequest? Chirp = null,
    PeriodicWaveRequest? Wave = null);

/// <summary>
/// <c>SampleRateHz</c>/<c>ChannelCount</c>/<c>BlockFrameCount</c> are only a hint for starting a
/// generator-only stream when no acquisition is running yet — see
/// <see cref="DeviceSessionService.ConfigureGeneratorAsync"/>. Omit them to leave the current stream
/// state (running or not) untouched.
/// </summary>
public sealed record GeneratorRequest(
    GeneratorChannelRequest Left, GeneratorChannelRequest Right,
    double? SampleRateHz = null, int? ChannelCount = null, int? BlockFrameCount = null,
    /// <summary>Snap tone frequencies to the nearest coherent FFT bin. Defaults to on — see <see cref="GeneratorConfig.SnapToBin"/>.</summary>
    bool? SnapToBin = null);
