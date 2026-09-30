using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;
using Qa403.Device.SoundCard;

namespace Qa403.App.Services;

/// <summary>What a calibration step produced, and what it means.</summary>
public sealed record SoundCardCalibrationResult(
    double? OutputFullScalePeakMv,
    double? InputFullScalePeakMvLeft,
    double? InputFullScalePeakMvRight,
    double? MeasuredDbFs = null,
    double? ChannelDifferenceDb = null,
    string? Warning = null);

/// <summary>
/// Runs the two ARTA-style calibration steps: what the card's full scale is worth, in volts, on each
/// side.
///
/// <para><b>Server-side, and registered as an <see cref="IMeasurementRun"/>.</b> That is the point
/// rather than a detail: it makes <c>MeasurementRunGuard</c> refuse every sweep while a calibration
/// tone is playing, and refuse a calibration while a sweep runs, with the 409s already wired. A
/// dialog driving <c>/api/generator</c> and <c>/api/acquisition</c> itself would be invisible to
/// that guard — nothing would stop a THD sweep starting under a 400 Hz calibration tone — and would
/// leave the tone playing if the browser tab were closed mid-step.</para>
///
/// <para>The two steps are deliberately shaped differently, because the physical work is. The
/// <b>output</b> step has to hold a tone across requests while the user reads a voltmeter, so it is
/// start / apply / cancel. The <b>input</b> step has nothing happening during the capture — the user
/// connects a known source first, then presses Measure — so it is one request.</para>
/// </summary>
public sealed class SoundCardCalibrationRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    SoundCardSetting soundCard,
    ConfigRepository config,
    ILogger<SoundCardCalibrationRunner> logger) : IMeasurementRun
{
    /// <summary>ARTA's tone, and a good one: well above any mains hum, well below any card's roll-off.</summary>
    public const double ToneHz = 400.0;

    /// <summary>The levels the dialog offers, in dB below full scale.</summary>
    public static readonly double[] OutputLevelsDbFs = [-3.0, -6.0, -9.0, -12.0, -20.0];

    /// <summary>
    /// A reading this close to full scale means the input was clipping, so the level it reports is
    /// not the source's and the calibration derived from it would be wrong.
    /// </summary>
    public const double ClippingGuardDb = -0.5;

    /// <summary>Below this, nothing is plugged in — calibrating against the card's own noise floor is worse than not calibrating.</summary>
    public const double SilenceGuardDbFs = -60.0;

    private readonly Lock _gate = new();
    private bool _running;
    private double _playingDbFs;

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>The level currently being played, or null when no tone is up.</summary>
    public double? PlayingDbFs
    {
        get
        {
            lock (_gate)
            {
                return _running ? _playingDbFs : null;
            }
        }
    }

    public async Task CancelAsync()
    {
        if (!Running)
            return;

        await SilenceAsync(CancellationToken.None);
    }

    /// <summary>
    /// Starts the 400 Hz tone at <paramref name="dbFs"/> below full scale and leaves it playing, so
    /// the user can read a voltmeter across it.
    /// </summary>
    public async Task StartOutputToneAsync(double dbFs, CancellationToken ct)
    {
        RequireSoundCard();

        if (!OutputLevelsDbFs.Contains(dbFs))
            throw new ArgumentOutOfRangeException(nameof(dbFs), dbFs, $"Pick one of {string.Join(", ", OutputLevelsDbFs)} dB.");

        lock (_gate)
        {
            _running = true;
            _playingDbFs = dbFs;
        }

        try
        {
            // Full scale is the one output rung, so a tone dbFs below it is that rung plus dbFs.
            double levelDbv = session.Capabilities.OutputRangesDbv[0] + dbFs;
            var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(ToneHz, levelDbv)])
            {
                Kind = GeneratorKind.Tones,
            };

            if (!session.Acquiring)
                await session.StartAcquisitionAsync(new AcquisitionConfig(session.Capabilities.SampleRatesHz[0], 2), ct);

            await session.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), ct);

            logger.LogInformation(
                "Sound card calibration: playing {Tone:0} Hz at {DbFs:0} dBFS ({Dbv:0.00} dBV by the current calibration)",
                ToneHz, dbFs, levelDbv);
        }
        catch
        {
            await SilenceAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Takes the voltmeter reading and turns it into the output's full scale, then silences the tone
    /// and reopens the device — both range rungs come from the calibration, and
    /// <c>DeviceCapabilities</c> is only read at connect.
    /// </summary>
    public async Task<SoundCardCalibrationResult> ApplyOutputAsync(
        double measured, VoltageEntryUnit unit, CancellationToken ct)
    {
        RequireSoundCard();

        double dbFs;
        lock (_gate)
        {
            if (!_running)
                throw new InvalidOperationException("Start the calibration tone before entering what the voltmeter read.");

            dbFs = _playingDbFs;
        }

        double measuredPeakV = VoltageEntry.MillivoltsToPeakVolts(measured, unit);
        double fullScaleV = SoundCardCalibration.OutputFullScaleFromMeasurement(measuredPeakV, dbFs);

        await SilenceAsync(ct);

        SoundCardConfig updated = soundCard.Config with
        {
            OutputFullScalePeakMv = fullScaleV * 1000.0,
            CalibratedUtc = DateTimeOffset.UtcNow,
        };

        await SaveAndReopenAsync(updated, ct);

        logger.LogInformation(
            "Sound card calibration: output full scale {Mv:0.0} mV peak, from {Measured:0.0} mV {Unit} at {DbFs:0} dBFS",
            updated.OutputFullScalePeakMv, measured, unit, dbFs);

        return new SoundCardCalibrationResult(
            updated.OutputFullScalePeakMv, updated.InputFullScalePeakMvLeft, updated.InputFullScalePeakMvRight);
    }

    /// <summary>
    /// Measures one input against the card's <b>own output</b>, through a loopback cable.
    ///
    /// <para>Once the output has been calibrated against a voltmeter, the app knows what it is
    /// playing in volts — so a cable from that output to the input under test is a source of known
    /// voltage, and no second signal generator is needed. ARTA asks for one because it assumes
    /// separate equipment; a bench with a patch cable does not need it.</para>
    ///
    /// <para><b>It refuses while the output is uncalibrated</b>, and that is the whole safety of it:
    /// the known voltage is derived from the output's full scale, so running this against a guessed
    /// one would write an input calibration that looks measured and is invented. The error compounds
    /// rather than cancelling — any error in the output figure lands directly in the input figure —
    /// which is why the external-source step stays, and stays the more trustworthy of the two.</para>
    /// </summary>
    public async Task<SoundCardCalibrationResult> MeasureInputViaLoopbackAsync(
        double dbFs, StreamChannel channel, CancellationToken ct)
    {
        RequireSoundCard();

        if (!OutputLevelsDbFs.Contains(dbFs))
            throw new ArgumentOutOfRangeException(nameof(dbFs), dbFs, $"Pick one of {string.Join(", ", OutputLevelsDbFs)} dB.");

        SoundCardCalibration calibration = soundCard.Calibration;
        if (!calibration.IsOutputCalibrated)
        {
            throw new InvalidOperationException(
                "Calibrate the output first. This measures the input against what the card is playing, and until the output is calibrated that voltage is not known — the result would look measured and be invented.");
        }

        // What the card is about to play, in volts, from the output calibration.
        double knownPeakV = calibration.OutputFullScaleV * Math.Pow(10.0, dbFs / 20.0);

        lock (_gate)
        {
            _running = true;
        }

        try
        {
            // On the channel being measured and nowhere else: a loopback pairs one output with one
            // input, and a tone on the other side would only reach this one as crosstalk.
            var tone = new ChannelGeneratorConfig(
                Enabled: true,
                Tones: [new GeneratorTone(ToneHz, session.Capabilities.OutputRangesDbv[0] + dbFs)])
            {
                Kind = GeneratorKind.Tones,
            };

            GeneratorConfig generator = channel == StreamChannel.Right
                ? new GeneratorConfig(ChannelGeneratorConfig.Off, tone)
                : new GeneratorConfig(tone, ChannelGeneratorConfig.Off);

            if (!session.Acquiring)
                await session.StartAcquisitionAsync(new AcquisitionConfig(session.Capabilities.SampleRatesHz[0], 2), ct);

            await session.ConfigureGeneratorAsync(generator, ct);

            double measuredDbFs = await ReadPeakDbFsAsync(channel, ct);
            await SilenceAsync(ct);

            if (measuredDbFs > ClippingGuardDb)
            {
                throw new InvalidOperationException(
                    $"That input is at {measuredDbFs:0.0} dBFS, which is clipping. Pick a quieter output level and measure again.");
            }

            if (measuredDbFs < SilenceGuardDbFs)
            {
                throw new InvalidOperationException(
                    $"The {channel} input read {measuredDbFs:0.0} dBFS while the card was playing — that is silence. Check the loopback cable runs from the output channel to the input channel being calibrated.");
            }

            double fullScaleMv = SoundCardCalibration.InputFullScaleFromMeasurement(knownPeakV, measuredDbFs) * 1000.0;

            SoundCardConfig updated = channel == StreamChannel.Right
                ? soundCard.Config with { InputFullScalePeakMvRight = fullScaleMv, CalibratedUtc = DateTimeOffset.UtcNow }
                : soundCard.Config with { InputFullScalePeakMvLeft = fullScaleMv, CalibratedUtc = DateTimeOffset.UtcNow };

            await SaveAndReopenAsync(updated, ct);

            logger.LogInformation(
                "Sound card calibration: {Channel} input full scale {Mv:0.0} mV peak, from a loopback of {Known:0.0} mV peak measured at {DbFs:0.00} dBFS",
                channel, fullScaleMv, knownPeakV * 1000.0, measuredDbFs);

            double? difference = updated.InputFullScalePeakMvLeft is { } l && updated.InputFullScalePeakMvRight is { } r
                ? 20.0 * Math.Log10(r / l)
                : null;

            return new SoundCardCalibrationResult(
                updated.OutputFullScalePeakMv,
                updated.InputFullScalePeakMvLeft,
                updated.InputFullScalePeakMvRight,
                measuredDbFs,
                difference,
                difference is { } d && Math.Abs(d) > 3.0
                    ? $"The two inputs differ by {d:0.00} dB, which is more than two channels of one card usually do — check the wiring and the channel pair."
                    : null);
        }
        finally
        {
            await SilenceAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Measures one input against a source of known voltage. The generator is silenced first: the
    /// source is external, and anything this app is playing would be measured with it.
    /// </summary>
    public async Task<SoundCardCalibrationResult> MeasureInputAsync(
        double known, VoltageEntryUnit unit, StreamChannel channel, CancellationToken ct)
    {
        RequireSoundCard();

        double knownPeakV = VoltageEntry.MillivoltsToPeakVolts(known, unit);

        lock (_gate)
        {
            _running = true;
        }

        try
        {
            var off = new GeneratorConfig(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off);
            if (!session.Acquiring)
                await session.StartAcquisitionAsync(new AcquisitionConfig(session.Capabilities.SampleRatesHz[0], 2), ct);

            await session.ConfigureGeneratorAsync(off, ct);

            double measuredDbFs = await ReadPeakDbFsAsync(channel, ct);

            if (measuredDbFs > ClippingGuardDb)
            {
                throw new InvalidOperationException(
                    $"That input is at {measuredDbFs:0.0} dBFS, which is clipping. Turn the source down and measure again — a level read at the clip point is not the source's.");
            }

            if (measuredDbFs < SilenceGuardDbFs)
            {
                throw new InvalidOperationException(
                    $"That input reads {measuredDbFs:0.0} dBFS, which is the card's own noise floor. Check the source is connected to the channel being calibrated.");
            }

            double fullScaleV = SoundCardCalibration.InputFullScaleFromMeasurement(knownPeakV, measuredDbFs);
            double fullScaleMv = fullScaleV * 1000.0;

            SoundCardConfig updated = channel == StreamChannel.Right
                ? soundCard.Config with { InputFullScalePeakMvRight = fullScaleMv, CalibratedUtc = DateTimeOffset.UtcNow }
                : soundCard.Config with { InputFullScalePeakMvLeft = fullScaleMv, CalibratedUtc = DateTimeOffset.UtcNow };

            await SaveAndReopenAsync(updated, ct);

            logger.LogInformation(
                "Sound card calibration: {Channel} input full scale {Mv:0.0} mV peak, from {Known:0.0} mV {Unit} measured at {DbFs:0.00} dBFS",
                channel, fullScaleMv, known, unit, measuredDbFs);

            double? difference = updated.InputFullScalePeakMvLeft is { } l && updated.InputFullScalePeakMvRight is { } r
                ? 20.0 * Math.Log10(r / l)
                : null;

            return new SoundCardCalibrationResult(
                updated.OutputFullScalePeakMv,
                updated.InputFullScalePeakMvLeft,
                updated.InputFullScalePeakMvRight,
                measuredDbFs,
                difference,
                // A few tenths is two ordinary channels; several dB is a wiring or channel-pair
                // mistake, which is what ARTA's Diff column is for.
                difference is { } d && Math.Abs(d) > 3.0
                    ? $"The two inputs differ by {d:0.00} dB, which is more than two channels of one card usually do — check the wiring and the channel pair."
                    : null);
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    /// <summary>Stops the tone and ends the run, whatever state it was in.</summary>
    public async Task SilenceAsync(CancellationToken ct)
    {
        try
        {
            if (session.Connected)
            {
                await session.ConfigureGeneratorAsync(
                    new GeneratorConfig(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off), ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not silence the sound card calibration tone.");
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    /// <summary>
    /// The loudest the chosen input saw, as dB below the card's digital full scale.
    ///
    /// <para>Derived rather than read directly, because the pipeline deals in volts: the meters
    /// report the DUT's level, adding the external gain back gives the analyzer's, and dividing by
    /// whatever full scale the current calibration claims gives the fraction of full scale that was
    /// actually there. That makes the step self-correcting — running it twice converges rather than
    /// compounding.</para>
    /// </summary>
    private async Task<double> ReadPeakDbFsAsync(StreamChannel channel, CancellationToken ct)
    {
        double currentFullScaleV = soundCard.Calibration.InputFullScalePeakV(channel == StreamChannel.Right ? 1 : 0);
        double best = double.NegativeInfinity;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        int seen = 0;

        while (DateTime.UtcNow < deadline && seen < 8)
        {
            await Task.Delay(120, ct);

            foreach (SpectrumSnapshot snapshot in pipeline.LatestSnapshots())
            {
                if (snapshot.Channel != channel || snapshot.Meters is not { } meters)
                    continue;

                seen++;

                // AnalyzerPeakDbv is RMS-referenced peak dBV at the jack; √2 takes it back to peak
                // volts, which is the unit full scale is expressed in.
                double analyzerPeakV = Math.Pow(10.0, meters.AnalyzerPeakDbv / 20.0) * Math.Sqrt(2.0);
                double fraction = analyzerPeakV / currentFullScaleV;
                if (fraction > 0.0)
                    best = Math.Max(best, 20.0 * Math.Log10(fraction));
            }
        }

        if (double.IsNegativeInfinity(best))
            throw new InvalidOperationException("The analyzer produced no reading to calibrate against — check it is connected and streaming.");

        return best;
    }

    private async Task SaveAndReopenAsync(SoundCardConfig updated, CancellationToken ct)
    {
        await config.SetAsync(
            ConfigRepository.SoundCardKey,
            System.Text.Json.JsonSerializer.Serialize(updated, Json.AppJsonContext.Default.SoundCardConfig),
            ct);
        soundCard.Config = updated;

        lock (_gate)
        {
            _running = false;
        }

        await session.ReopenAsync(ct);
    }

    private void RequireSoundCard()
    {
        if (session.Kind != DeviceKind.SoundCard)
            throw new InvalidOperationException("The analyzer is not a sound card, so there is nothing to calibrate.");

        if (!session.Connected)
            throw new InvalidOperationException("Connect the sound card before calibrating it.");
    }
}
