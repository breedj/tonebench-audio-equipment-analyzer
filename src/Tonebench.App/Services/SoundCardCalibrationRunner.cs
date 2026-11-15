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
/// that guard — nothing would stop a THD sweep starting under a calibration tone — and would
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
    /// <summary>
    /// The calibration tone, well below any card's roll-off and well inside the band an ordinary
    /// bench voltmeter reads accurately — which is the one that matters, because the output step's
    /// whole answer is a figure the user reads off a DMM. 100 Hz rather than ARTA's 400 (Joost,
    /// 2026-10-10): a true-RMS handheld is specified to a few hundred hertz at best, and several
    /// read low well before that, so a reading taken at 400 Hz can carry the meter's own
    /// bandwidth error straight into the stored full scale. Nothing here is coherent — every step
    /// reads a broadband *peak* (see ToDbFs) — so the tone's frequency buys no resolution and is
    /// free to suit the instrument. Mains hum adds to that peak whatever the tone is, and the tone
    /// sits 60 dB or more above it at every offered level, so moving onto 2×50 Hz costs nothing
    /// measurable.
    /// </summary>
    public const double ToneHz = 100.0;

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
    /// <para>These steps read one number — the loudest sample the tone produced — and a record
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
    /// Starts the <see cref="ToneHz"/> tone at <paramref name="dbFs"/> below full scale and leaves it playing, so
    /// the user can read a voltmeter across it.
    /// </summary>
    public async Task StartOutputToneAsync(double dbFs, CancellationToken ct)
    {
        RequireCardOn(capturing: false);

        if (!OutputLevelsDbFs.Contains(dbFs))
            throw new ArgumentOutOfRangeException(nameof(dbFs), dbFs, $"Pick one of {string.Join(", ", OutputLevelsDbFs)} dB.");

        lock (_gate)
        {
            _running = true;
            _playingDbFs = dbFs;
        }

        try
        {
            double levelDbv = ToneDbvFor(dbFs);
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
        RequireCardOn(capturing: false);

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

        SoundCardConfig updated = (soundCard.Config with
        {
            OutputFullScalePeakMv = fullScaleV * 1000.0,
            CalibratedUtc = DateTimeOffset.UtcNow,
        }).StampOutputCalibration();

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
    /// <summary>
    /// The card's output full scale, measured with the <b>analyzer's own input</b> instead of a
    /// voltmeter: play a tone out of the card and read what arrives, in volts.
    ///
    /// <para><b>The mirror of <see cref="MeasureInputViaLoopbackAsync"/>, and it works for the same
    /// reason.</b> That step calibrates a card's input against a QA40x whose output level is a
    /// factory figure; this one calibrates a card's output against a QA40x whose <i>input</i> is
    /// one. Either way the known quantity is on the half that is not the card, which is what a
    /// split has and a single card never does — on a plain sound-card bench both ends are the same
    /// uncalibrated device, and the voltmeter is the only way in. That is why this is offered
    /// rather than made the default: it is reachable only on a bench whose capturing half is not a
    /// card.</para>
    ///
    /// <para><b>Wire the card's output straight to the analyzer's input.</b> Anything in between is
    /// measured as if it were the card, so the stored full scale would carry the attenuator's
    /// ratio — unless that divider is <i>declared</i>, which is the one case this handles: the
    /// reading is <c>PeakDbv</c>, the DUT-referred level, so a declared external gain is already
    /// divided back out and the figure is the card's own output either way.</para>
    ///
    /// <para>Both channels are read and the <b>mean</b> is stored, because a card has one output
    /// full scale and not one per channel. A pair that disagrees is reported rather than averaged
    /// silently: on one interface the two outputs should match within a few hundredths of a dB, so
    /// a real difference is a cable or a channel pair rather than the card.</para>
    /// </summary>
    public async Task<SoundCardCalibrationResult> MeasureOutputViaAnalyzerAsync(
        double dbFs, CancellationToken ct)
    {
        RequireCardOn(capturing: false);

        if (!OutputLevelsDbFs.Contains(dbFs))
            throw new ArgumentOutOfRangeException(nameof(dbFs), dbFs, $"Pick one of {string.Join(", ", OutputLevelsDbFs)} dB.");

        // The capturing half has to be something whose readings are already volts. A card's input
        // is exactly what is not known yet -- calibrating an output against it would be measuring
        // one unknown with another -- and on a plain sound-card bench both halves are that card.
        if (session.InputDeviceKind == DeviceKind.SoundCard)
        {
            throw new InvalidOperationException(
                "This measures the card's output with the analyzer's input, and on this bench the thing capturing is a sound card — whose own full scale is not known yet. Use a voltmeter, or put a QA40x on the capturing half.");
        }

        lock (_gate)
        {
            _running = true;
        }

        SpectrumPipelineOptions savedOptions = pipeline.Options;

        try
        {
            pipeline.Options = savedOptions with
            {
                ProcessLeft = true,
                ProcessRight = true,
                FftSize = CalibrationFftSize,
            };

            double toneDbv = ToneDbvFor(dbFs);
            var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(ToneHz, toneDbv)])
            {
                Kind = GeneratorKind.Tones,
            };

            await StartAcquisitionIfNeededAsync(ct);
            await session.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), ct);

            TimeSpan budget = ReadBudget();
            double leftPeakV = await ReadPeakVoltsAsync(StreamChannel.Left, AnalyzerSourceAdvice, budget, ct);
            double rightPeakV = await ReadPeakVoltsAsync(StreamChannel.Right, AnalyzerSourceAdvice, budget, ct);
            await SilenceAsync(ct);

            RequireUsableVolts(leftPeakV, StreamChannel.Left);
            RequireUsableVolts(rightPeakV, StreamChannel.Right);

            // The card played dbFs below its own full scale, so what arrived is FS x 10^(dbFs/20).
            double scale = Math.Pow(10.0, dbFs / 20.0);
            double leftFullScaleV = leftPeakV / scale;
            double rightFullScaleV = rightPeakV / scale;
            double fullScaleMv = (leftFullScaleV + rightFullScaleV) / 2.0 * 1000.0;

            double difference = 20.0 * Math.Log10(rightFullScaleV / leftFullScaleV);

            SoundCardConfig updated = (soundCard.Config with
            {
                OutputFullScalePeakMv = fullScaleMv,
                CalibratedUtc = DateTimeOffset.UtcNow,
            }).StampOutputCalibration();

            if (!updated.IsValid)
            {
                throw new InvalidOperationException(
                    $"That gives a full scale of {fullScaleMv:0.0} mV peak, which is outside the range this app stores. Check the cable and the output level, and that nothing is attenuating between the card and the analyzer.");
            }

            await SaveAndReopenAsync(updated, ct);

            logger.LogInformation(
                "Sound card calibration: output full scale {FullScale:0.0} mV peak, measured by {Source} at {Left:0.000} / {Right:0.000} V peak while playing {DbFs:0.0} dBFS",
                fullScaleMv, session.InputDeviceKind, leftPeakV, rightPeakV, dbFs);

            return new SoundCardCalibrationResult(
                fullScaleMv,
                updated.InputFullScalePeakMvLeft,
                updated.InputFullScalePeakMvRight,
                null,
                difference,
                Math.Abs(difference) > 1.0
                    ? $"The two outputs differ by {difference:0.00} dB, which is more than two channels of one card usually do — check the cables and the channel pair. The stored figure is their mean."
                    : null);
        }
        finally
        {
            pipeline.Options = savedOptions;
            await SilenceAsync(CancellationToken.None);
        }
    }

    public async Task<SoundCardCalibrationResult> MeasureInputViaLoopbackAsync(
        double dbFs, CancellationToken ct)
    {
        RequireCardOn(capturing: true);

        if (!OutputLevelsDbFs.Contains(dbFs))
            throw new ArgumentOutOfRangeException(nameof(dbFs), dbFs, $"Pick one of {string.Join(", ", OutputLevelsDbFs)} dB.");

        // Only where the thing playing is an uncalibrated card. A QA40x's output level is factory
        // calibrated, so on a split that plays through one the voltage *is* known and this step is
        // the better of the two rather than the fallback -- which is the whole point of letting a
        // capture-only converter be calibrated this way.
        if (session.OutputDeviceKind == DeviceKind.SoundCard && !soundCard.Calibration.IsOutputCalibrated)
        {
            throw new InvalidOperationException(
                "Calibrate the output first. This measures the inputs against what the card is playing, and until the output is calibrated that voltage is not known — the result would look measured and be invented.");
        }

        // What is about to be played, in volts peak.
        double toneDbv = ToneDbvFor(dbFs);
        double knownPeakV = Math.Sqrt(2.0) * Math.Pow(10.0, toneDbv / 20.0);

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

            var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(ToneHz, toneDbv)])
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

            SoundCardConfig updated = (soundCard.Config with
            {
                InputFullScalePeakMvLeft = leftMv,
                InputFullScalePeakMvRight = rightMv,
                CalibratedUtc = DateTimeOffset.UtcNow,
            }).StampInputCalibration();

            await SaveAndReopenAsync(updated, ct);

            logger.LogInformation(
                "Sound card calibration: input full scale {Left:0.0} / {Right:0.0} mV peak, from {Source} playing {Known:0.0} mV peak measured at {LeftDb:0.00} / {RightDb:0.00} dBFS",
                leftMv, rightMv, session.OutputDeviceKind, knownPeakV * 1000.0, leftDbFs, rightDbFs);

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
        RequireCardOn(capturing: true);

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

            SoundCardConfig updated = (channel == StreamChannel.Right
                ? soundCard.Config with { InputFullScalePeakMvRight = fullScaleMv, CalibratedUtc = DateTimeOffset.UtcNow }
                : soundCard.Config with { InputFullScalePeakMvLeft = fullScaleMv, CalibratedUtc = DateTimeOffset.UtcNow })
                .StampInputCalibration();

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

    /// <summary>
    /// Below this, nothing is connected. Ten microvolts is far under any card's output at the
    /// quietest level offered (-20 dBFS of a 1 V card is 100 mV) and far over the analyzer's own
    /// noise floor on its most sensitive range, so it separates "no cable" from "a quiet card"
    /// without having to know which range the analyzer picked.
    /// </summary>
    private const double SilenceGuardVolts = 1e-5;

    private const string AnalyzerSourceAdvice =
        "Nothing is arriving on the {0} input. Connect the card's output to the analyzer's input and try again.";

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

    /// <summary>
    /// The loudest peak seen on a channel, in <b>volts</b> rather than in the card's own dBFS.
    ///
    /// <para>The sibling of <see cref="ReadPeakDbFsAsync"/> and deliberately not a wrapper round
    /// it: that one divides by the <i>card's</i> stored input full scale, which is the right
    /// question when the card is capturing and a meaningless one when a QA40x is — there the
    /// pipeline already reports volts and the card's figure describes other hardware entirely.</para>
    ///
    /// <para><c>PeakDbv</c> rather than <c>AnalyzerPeakDbv</c>: a declared external gain is a claim
    /// about a divider between the DUT and the jack, so the DUT-referred figure is the card's own
    /// output. With nothing declared the two are the same number, which is the documented wiring
    /// for this step.</para>
    /// </summary>
    private async Task<double> ReadPeakVoltsAsync(
        StreamChannel channel, string advice, TimeSpan budget, CancellationToken ct)
    {
        double bestDbv = double.NegativeInfinity;
        DateTime deadline = DateTime.UtcNow + budget;
        int any = 0;
        int withSignal = 0;

        // **Stops early only once it has read a *level*, not merely a record.** The settle on this
        // step belongs to the half that is NOT doing the reading: a card re-clocks when a stream
        // asks for a rate it is not already running at, and plays nothing while it does -- but
        // here the capturing half is a QA40x, which withholds nothing and hands out records
        // throughout. Counting records, eight of them can be consumed inside that silence and the
        // step then reports the analyzer's own noise floor with seconds of its budget unspent.
        //
        // The dBFS reader opposite does count records, and is right to: there the *card* captures,
        // and SoundCardDevice withholds its settle frames rather than handing out silence, so a
        // record existing already means the card is past it.
        while (DateTime.UtcNow < deadline && withSignal < 8)
        {
            await Task.Delay(120, ct);

            foreach (SpectrumSnapshot snapshot in pipeline.LatestSnapshots())
            {
                if (snapshot.Channel != channel || snapshot.Meters is not { } meters)
                    continue;

                any++;
                bestDbv = Math.Max(bestDbv, meters.PeakDbv);

                if (PeakVolts(meters.PeakDbv) >= SilenceGuardVolts)
                    withSignal++;
            }
        }

        if (any == 0)
            throw new InvalidOperationException(string.Format(advice, channel.ToString().ToLowerInvariant()));

        return PeakVolts(bestDbv);
    }

    /// <summary>
    /// An RMS-referenced peak dBV as peak volts, which is the unit a full scale is stored in.
    /// </summary>
    private static double PeakVolts(double peakDbv) =>
        double.IsNegativeInfinity(peakDbv) ? 0.0 : Math.Pow(10.0, peakDbv / 20.0) * Math.Sqrt(2.0);

    /// <summary>
    /// Refuses a reading that cannot be the card playing a tone.
    ///
    /// <para>Only a floor, and no ceiling: the analyzer ranges itself, so a loud card is measured
    /// rather than clipped — which is the opposite of the dBFS guard, where the ceiling is the
    /// whole point because a card's input has one fixed range.</para>
    /// </summary>
    private static void RequireUsableVolts(double peakVolts, StreamChannel channel)
    {
        if (peakVolts < SilenceGuardVolts)
        {
            throw new InvalidOperationException(
                $"The {channel} channel read {peakVolts * 1000.0:0.000} mV while the card was playing — that is nothing. Connect the card's output straight to the analyzer's input: both channels are measured together, so both need a cable.");
        }
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

    /// <summary>
    /// The level a step plays, in dBV, <paramref name="dbFs"/> below the generating device's own
    /// full scale.
    ///
    /// <para><b>The top rung is that full scale, which is why this is not simply <c>[0]</c>.</b> A
    /// card has one output rung and it is both — so for a card this is bit-identical to the
    /// expression it replaces, including <c>OutputFullScaleV · 10^(dbFs/20)</c> once the peak is
    /// taken: a rung is <c>20·log10(FS/√2)</c>, so <c>√2 · 10^((rung + dbFs)/20)</c> is
    /// <c>FS · 10^(dbFs/20)</c> exactly. A QA40x has four rungs and only the loudest is its full
    /// scale, so reading <c>[0]</c> there would ask for 30 dB less than the slider says.</para>
    ///
    /// <para>Max rather than <c>[^1]</c>: both ladders ascend today and nothing here should depend
    /// on that. On a split <c>Capabilities.OutputRangesDbv</c> is already the generating half's, so
    /// this needs no branch of its own.</para>
    /// </summary>
    private double ToneDbvFor(double dbFs) => session.Capabilities.OutputRangesDbv.Max() + dbFs;

    /// <summary>
    /// Refuses a step whose own half of the bench is not a sound card.
    ///
    /// <para><b>Per direction, because a split has two halves and only one of them has a
    /// calibration to measure.</b> This was one whole-bench check, so every step refused on every
    /// split — including the bench the split exists for, a QA40x driving a capture-only converter,
    /// where the card is the <i>only</i> thing that needs calibrating and the QA40x is the known
    /// source that can do it.</para>
    ///
    /// <para>The output steps want the card to be what plays; both input steps want it to be what
    /// captures. Nothing is required of the <i>other</i> half here — what the loopback step needs of
    /// it is a known voltage, which it checks for itself.</para>
    /// </summary>
    private void RequireCardOn(bool capturing)
    {
        DeviceKind half = capturing ? session.InputDeviceKind : session.OutputDeviceKind;
        if (half != DeviceKind.SoundCard)
        {
            throw new InvalidOperationException(capturing
                ? "This bench does not capture with a sound card, so there is no input calibration to measure."
                : "This bench does not play through a sound card, so there is no output calibration to measure.");
        }

        if (!session.Connected)
            throw new InvalidOperationException("Connect the sound card before calibrating it.");
    }
}
