using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.SoundCard;

namespace Tonebench.App.Services;

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

    /// <summary>
    /// The record length every step analyses at, instead of inheriting the measurement screen's.
    ///
    /// <para>These steps read one number — the loudest sample a 400 Hz tone produced — and a record
    /// is the unit of latency in getting it: the step cannot answer until a whole one has been
    /// filled and analysed. Inheriting the screen's size made that latency a property of a setting
    /// on an unrelated screen, which is how the input step came to fail intermittently:
    /// <see cref="SpectrumPipelineOptions"/>'s default alone is 65536 points, 1.37 s at 48 kHz, and
    /// a bench left at 131072 pays 2.73 s. 16384 is 341 ms at 48 kHz and 85 ms at 192 kHz, which is
    /// as much resolution as a peak reading has any use for.</para>
    /// </summary>
    public const int CalibrationFftSize = 16384;

    private readonly Lock _gate = new();
    private bool _running;

    /// <summary>What the measurement screen had before a step turned both channels on for itself.</summary>
    private SpectrumPipelineOptions? _savedOptions;
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

            // Both channels analysed while the tone plays, so the dialog's level bars show both.
            // ProcessRight is off by default -- the FFT screen's own left-only default, which the
            // pipeline keeps -- so without this the right bar never moves.
            _savedOptions ??= pipeline.Options;
            pipeline.Options = pipeline.Options with
            {
                ProcessLeft = true,
                ProcessRight = true,
                FftSize = CalibrationFftSize,
            };

            await StartAcquisitionIfNeededAsync(ct);

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
    /// <summary>
    /// Measures <b>both</b> inputs at once against the card's own output, through loopback cables.
    ///
    /// <para>Both together rather than one at a time, which is what a stereo loopback makes
    /// possible and what the two level bars show: it is one press instead of two, the two figures
    /// are measured under identical conditions, and the difference between them is available
    /// immediately — that difference is the reading that catches a wiring mistake.</para>
    ///
    /// <para><b>It refuses while the output is uncalibrated</b>, and that is the whole safety of it:
    /// the known voltage is derived from the output's full scale, so running this against a guessed
    /// one would write an input calibration that looks measured and is invented. The error compounds
    /// rather than cancelling — any error in the output figure lands directly in both input figures
    /// — which is why the external-source step stays, and stays the more trustworthy of the two.</para>
    /// </summary>
    public async Task<SoundCardCalibrationResult> MeasureInputViaLoopbackAsync(
        double dbFs, CancellationToken ct)
    {
        RequireSoundCard();

        if (!OutputLevelsDbFs.Contains(dbFs))
            throw new ArgumentOutOfRangeException(nameof(dbFs), dbFs, $"Pick one of {string.Join(", ", OutputLevelsDbFs)} dB.");

        SoundCardCalibration calibration = soundCard.Calibration;
        if (!calibration.IsOutputCalibrated)
        {
            throw new InvalidOperationException(
                "Calibrate the output first. This measures the inputs against what the card is playing, and until the output is calibrated that voltage is not known — the result would look measured and be invented.");
        }

        // What the card is about to play, in volts, from the output calibration.
        double knownPeakV = calibration.OutputFullScaleV * Math.Pow(10.0, dbFs / 20.0);

        lock (_gate)
        {
            _running = true;
        }

        SpectrumPipelineOptions savedOptions = pipeline.Options;

        try
        {
            // Both channels analysed, explicitly. ProcessRight is off by default -- the FFT screen's
            // own left-only default, which the pipeline keeps -- so inheriting it meant the right
            // input was never analysed at all and the step came back "no reading to calibrate
            // against". Every runner sets its own options for this reason; this one has to as well.
            pipeline.Options = savedOptions with
            {
                ProcessLeft = true,
                ProcessRight = true,
                FftSize = CalibrationFftSize,
            };

            var tone = new ChannelGeneratorConfig(
                Enabled: true,
                Tones: [new GeneratorTone(ToneHz, session.Capabilities.OutputRangesDbv[0] + dbFs)])
            {
                Kind = GeneratorKind.Tones,
            };

            await StartAcquisitionIfNeededAsync(ct);

            await session.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), ct);

            // One budget for both channels, taken after the stream is up so it carries that
            // stream's settle -- see ReadBudget.
            TimeSpan budget = ReadBudget();
            double leftDbFs = await ReadPeakDbFsAsync(StreamChannel.Left, LoopbackAdvice, budget, ct);
            double rightDbFs = await ReadPeakDbFsAsync(StreamChannel.Right, LoopbackAdvice, budget, ct);
            await SilenceAsync(ct);

            RequireUsableLevel(leftDbFs, StreamChannel.Left);
            RequireUsableLevel(rightDbFs, StreamChannel.Right);

            double leftMv = SoundCardCalibration.InputFullScaleFromMeasurement(knownPeakV, leftDbFs) * 1000.0;
            double rightMv = SoundCardCalibration.InputFullScaleFromMeasurement(knownPeakV, rightDbFs) * 1000.0;

            SoundCardConfig updated = soundCard.Config with
            {
                InputFullScalePeakMvLeft = leftMv,
                InputFullScalePeakMvRight = rightMv,
                CalibratedUtc = DateTimeOffset.UtcNow,
            };

            await SaveAndReopenAsync(updated, ct);

            logger.LogInformation(
                "Sound card calibration: input full scale {Left:0.0} / {Right:0.0} mV peak, from a loopback of {Known:0.0} mV peak measured at {LeftDb:0.00} / {RightDb:0.00} dBFS",
                leftMv, rightMv, knownPeakV * 1000.0, leftDbFs, rightDbFs);

            double difference = 20.0 * Math.Log10(rightMv / leftMv);

            return new SoundCardCalibrationResult(
                updated.OutputFullScalePeakMv,
                leftMv,
                rightMv,
                // The louder of the two, which is the one a clipping check would care about.
                Math.Max(leftDbFs, rightDbFs),
                difference,
                Math.Abs(difference) > 3.0
                    ? $"The two inputs differ by {difference:0.00} dB, which is more than two channels of one card usually do — check the cables and the channel pair."
                    : null);
        }
        finally
        {
            pipeline.Options = savedOptions;
            await SilenceAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Refuses a reading that cannot mean what it says: one at the clip point is not the source's
    /// level, and one in the noise floor usually means nothing is connected to that input.
    /// </summary>
    private static void RequireUsableLevel(double measuredDbFs, StreamChannel channel)
    {
        if (measuredDbFs > ClippingGuardDb)
        {
            throw new InvalidOperationException(
                $"The {channel} input is at {measuredDbFs:0.0} dBFS, which is clipping. Pick a quieter output level and measure again.");
        }

        if (measuredDbFs < SilenceGuardDbFs)
        {
            throw new InvalidOperationException(
                $"The {channel} input read {measuredDbFs:0.0} dBFS while the card was playing — that is silence. Check the loopback cable on that channel: both inputs are measured together, so both need one.");
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

        SpectrumPipelineOptions savedOptions = pipeline.Options;

        try
        {
            // Both channels analysed, for the same reason as the loopback step above: ProcessRight
            // is off by default, so the right input would never be analysed.
            pipeline.Options = savedOptions with
            {
                ProcessLeft = true,
                ProcessRight = true,
                FftSize = CalibrationFftSize,
            };

            var off = new GeneratorConfig(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off);
            await StartAcquisitionIfNeededAsync(ct);

            await session.ConfigureGeneratorAsync(off, ct);

            double measuredDbFs = await ReadPeakDbFsAsync(channel, ExternalSourceAdvice, ReadBudget(), ct);

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
            // The loopback step's teardown, which this one did not have: it restored nothing and
            // called nothing, so a step that threw (a clipped source, a silent one) left the
            // screen's ProcessRight switched on, left this runner's FFT size in place of the
            // screen's, and -- the one that bites -- left the acquisition it started running, which
            // is what keeps the browser's ranging watcher live and refuses the card's own control
            // panel. SilenceAsync is the single place that undoes all of it.
            pipeline.Options = savedOptions;
            await SilenceAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Whether <i>this runner</i> was what started the acquisition, and therefore what has to stop
    /// it again.
    ///
    /// <para>It used to start one and leave it running, which was not merely untidy: a session
    /// left acquiring keeps the browser's own ranging watcher live, and that watcher then posts an
    /// input range into the next calibration step — "a measurement is running and controls the
    /// input range", on the second press of a button that worked on the first. It also left the
    /// card's control panel refused for the rest of the session, since that is refused while
    /// acquiring. Reported 2026-10-01.</para>
    /// </summary>
    private bool _startedAcquisition;

    /// <summary>
    /// Starts an acquisition for a step that needs one, remembering that it was this runner that
    /// did so. An acquisition that was already running belongs to the measurement screen and is
    /// left alone, both here and in <see cref="SilenceAsync"/>.
    /// </summary>
    private async Task StartAcquisitionIfNeededAsync(CancellationToken ct)
    {
        if (session.Acquiring)
            return;

        await session.StartAcquisitionAsync(
            new AcquisitionConfig(session.Capabilities.SampleRatesHz[0], 2), ct);

        _startedAcquisition = true;
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

        // And stop the acquisition again if this runner is what started it -- see the remarks on
        // _startedAcquisition. Separate from the try above because silencing and stopping fail
        // independently, and a generator that would not switch off must not leave the stream up.
        try
        {
            if (_startedAcquisition && session.Connected)
                await session.StopAcquisitionAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not stop the acquisition the sound card calibration started.");
        }
        finally
        {
            _startedAcquisition = false;
        }

        // Hand the pipeline back whatever the measurement screen had. The steps turn both
        // channels on for themselves, and leaving that behind would quietly switch the right
        // channel on for an FFT screen the user had set to left only. Unconditional, because
        // everything above it catches its own failures.
        if (_savedOptions is { } saved)
        {
            pipeline.Options = saved;
            _savedOptions = null;
        }

        lock (_gate)
        {
            _running = false;
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
    /// <summary>
    /// What each input is seeing right now, as dB below full scale, or null where nothing has been
    /// analysed yet. Drives the dialog's level bars, and is the same derivation the calibration
    /// itself uses -- one definition, so the bars and the stored figure cannot disagree.
    /// </summary>
    public (double? LeftDbFs, double? RightDbFs) CurrentInputLevels()
    {
        double? left = null;
        double? right = null;

        foreach (SpectrumSnapshot snapshot in pipeline.LatestSnapshots())
        {
            if (snapshot.Meters is not { } meters)
                continue;

            double dbFs = ToDbFs(meters.AnalyzerPeakDbv, snapshot.Channel);
            if (snapshot.Channel == StreamChannel.Right)
                right = dbFs;
            else
                left = dbFs;
        }

        return (left, right);
    }

    /// <summary>
    /// An analyzer-referred level as a fraction of this card's full scale, in dB.
    ///
    /// <para>Derived rather than read directly, because the pipeline deals in volts: the meters
    /// report the DUT's level, adding the external gain back gives the analyzer's, and dividing by
    /// whatever full scale the current calibration claims gives the fraction of full scale that was
    /// actually there. That is what makes the calibration self-correcting -- running it twice
    /// converges rather than compounding.</para>
    /// </summary>
    private double ToDbFs(double analyzerPeakDbv, StreamChannel channel)
    {
        double fullScaleV = soundCard.Calibration.InputFullScalePeakV(channel == StreamChannel.Right ? 1 : 0);

        // AnalyzerPeakDbv is RMS-referenced peak dBV at the jack; root two takes it back to peak
        // volts, which is the unit full scale is expressed in.
        double analyzerPeakV = Math.Pow(10.0, analyzerPeakDbv / 20.0) * Math.Sqrt(2.0);
        double fraction = analyzerPeakV / fullScaleV;

        return fraction > 0.0 ? 20.0 * Math.Log10(fraction) : double.NegativeInfinity;
    }

    /// <summary>
    /// What to tell the user when a step hears nothing at all. The two routes fail for different
    /// physical reasons and the message is the only place that can say which, so each passes its
    /// own — the generic "check it is connected and streaming" named the analyzer, which is the one
    /// thing that is almost never the problem here: this runner started the stream itself.
    /// </summary>
    private const string ExternalSourceAdvice =
        "Nothing is arriving on the {0} input. This step measures the voltage your generator is producing, so it needs that generator connected to the {0} input and switched on — check the cable and the level, then try again.";

    private const string LoopbackAdvice =
        "Nothing is arriving on the {0} input. Both inputs are measured together, so both need a patch cable from their own output — check the cable on the {0} channel, then try again.";

    /// <summary>
    /// How long a step waits for its first analysed record — derived from what the stream will
    /// actually cost, never a fixed figure.
    ///
    /// <para><b>This is the rule every runner already follows</b> (see
    /// <see cref="DeviceSessionService.StreamSettleMs"/>): a sound card withholds frames while it
    /// re-clocks, those frames are genuinely not coming, and a budget that does not allow for them
    /// reads a settling card as a silent input. This runner alone had a hard-coded 5 s, and that is
    /// the whole of the "Nothing is arriving on the left input" fault reported 2026-10-04 —
    /// <i>on the second press</i>, which is the tell. Every step ends in
    /// <see cref="SaveAndReopenAsync"/>, a reopen builds a new device, and a new device's
    /// <c>_lastOpenedSampleRateHz</c> is 0 — so the next stream re-clocks and withholds the settle,
    /// where the press before it may not have. Arithmetic on the old budget, at the defaults of the
    /// day: 300 ms throwaway re-clock + 3000 ms withheld + one 65536-point record at 48 kHz
    /// (1365 ms) = 4.7 s against a 5 s deadline, i.e. 0.3 s of slack decided by scheduling. At
    /// 131072 points it could not pass at all. Both terms are now in the budget and the record is
    /// this runner's own short one (<see cref="CalibrationFftSize"/>), so the slack is seconds
    /// rather than milliseconds.</para>
    ///
    /// <para>Taken after the stream is up, so <c>StreamSettleMs</c> is that stream's rather than the
    /// previous one's, and the capture delay is in it because every step configures a generator and
    /// therefore books one.</para>
    /// </summary>
    private TimeSpan ReadBudget()
    {
        double sampleRateHz = session.StreamConfig?.SampleRateHz ?? session.Capabilities.SampleRatesHz[0];
        double recordMs = pipeline.Options.FftSize / sampleRateHz * 1000.0;

        // Two records, not one: the flush the generator booked is consumed before the window
        // starts filling, so the first analysis lands up to two records after the configure.
        return TimeSpan.FromMilliseconds(
            session.StreamSettleMs + session.CaptureDelayMs + (recordMs * 2.0) + 2000.0);
    }

    private async Task<double> ReadPeakDbFsAsync(
        StreamChannel channel, string advice, TimeSpan budget, CancellationToken ct)
    {
        double best = double.NegativeInfinity;
        DateTime deadline = DateTime.UtcNow + budget;
        int seen = 0;

        while (DateTime.UtcNow < deadline && seen < 8)
        {
            await Task.Delay(120, ct);

            foreach (SpectrumSnapshot snapshot in pipeline.LatestSnapshots())
            {
                if (snapshot.Channel != channel || snapshot.Meters is not { } meters)
                    continue;

                seen++;
                best = Math.Max(best, ToDbFs(meters.AnalyzerPeakDbv, channel));
            }
        }

        // "Nothing arrived" and "what arrived was digital silence" are different faults and the
        // guard used to answer both with the first message. A record that was analysed and read
        // -inf dBFS is a connected input with no signal on it, which is what the silence guard's
        // own wording describes ("read -0.0 dBFS while the card was playing -- that is silence"),
        // so that case is returned and left to RequireUsableLevel. Only a step that saw no record
        // at all in the whole budget says nothing is arriving -- and after this change that means
        // the pipeline really did not produce one, rather than that it was still settling.
        if (seen == 0)
            throw new InvalidOperationException(string.Format(advice, channel.ToString().ToLowerInvariant()));

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
