using System.Diagnostics;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// Measures how far the input card's clock runs from the output card's, by playing a tone on one
/// and reading its frequency on the other.
///
/// <para><b>Why it is a measurement and not a lookup.</b> Both devices are told the same nominal
/// rate and neither is obliged to produce it exactly, so what comes back is
/// <c>played × (1 + a) / (1 + b)</c>. Neither card's own error is knowable from one cable and
/// neither needs to be: the ratio is <c>a − b</c>, which is the only figure any consequence follows
/// from. <see cref="ToneFrequency"/> reads the captured frequency to about 0.02 ppm, which is three
/// orders finer than the quantity.</para>
///
/// <para><b>It measures through whatever is connected</b>, which is the point rather than a
/// limitation: a DUT changes a tone's amplitude and phase and not its frequency, so the bench does
/// not have to be re-wired into a loopback to run this. What it does need is the output reaching
/// the input at all, and a tone well clear of the input's own hum — the one way this measurement
/// goes quietly wrong. Measured by hand on this bench, a tone 36 dB under a 50 Hz ladder gave a
/// drift figure wrong by a factor of ten and perfectly steady, so <see cref="ClockDriftPoint.DominanceDb"/>
/// rides on every point and the dialog refuses to draw a verdict without it.</para>
///
/// <para><b>Free-running rather than single-shot.</b> Every other runner flushes and takes one
/// record per point because it is changing the stimulus between them; nothing changes here, so the
/// acquisition is left running and the retained record is read as it comes. That costs no flushes,
/// no settles and no re-ranging, and it is what makes a point-per-second chart affordable.
/// Consecutive records overlap by the display hop, so readings are taken no closer together than
/// one whole record — correlated points would make a drifting clock look steadier than it is.</para>
///
/// <para>It is an <see cref="IMeasurementRun"/> like any other: it owns the generator, the pipeline
/// options and the input range for its length, so nothing else may start over it and a project
/// switch is refused while it runs.</para>
/// </summary>
public sealed class ClockDriftRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    ConfigRepository config,
    SoundCardSetting soundCard,
    ILogger<ClockDriftRunner> logger) : IMeasurementRun
{
    /// <summary>
    /// The analysis record. 32768 at 48 kHz is 0.68 s, which puts a reading a second or so apart —
    /// fine enough to show a clock settling, and long enough that the estimator's own error
    /// (0.02 ppm at 40 dB signal-to-noise) is nowhere near the quantity.
    /// </summary>
    public const int FftSize = 32768;

    /// <summary>How far either side of the played tone to look for it, in bins.</summary>
    private const double SearchHalfWidthBins = 40;

    /// <summary>A tone this far under the rest of its own record is not worth a reading.</summary>
    public const double MinDominanceDb = 6.0;

    /// <summary>
    /// How many readings a run must take before it reports a figure at all, and therefore before
    /// one is stored.
    ///
    /// <para><b>Measured on a 418-reading run on this bench, 2026-11-11.</b> Two of those readings
    /// — half a per cent, both from the records whose tone margin was the lowest of the run — came
    /// back wild, one at +157 ppm against a true −104. Taking every window of N consecutive readings
    /// and asking how far its median lands from the run's own middle: 10 readings is out by up to
    /// 2.1 ppm, <b>30 by 1.5 ppm</b>, 120 by 0.4. (A <i>mean</i> over the same windows is out by
    /// 26.0, 8.6 and 2.1, which is the measurement that made the figure a median — see
    /// <see cref="ClockDriftStatus.DriftPpm"/>.)</para>
    ///
    /// <para>So thirty is where the estimate stops being dominated by whether a bad record happened
    /// to land in the run, and it leaves about 1 µs of this bench's 71 µs slip — a hundredth of what
    /// the correction removes. It is a <i>floor</i> and not a target: the default duration takes
    /// several times as many, and the extra readings buy stability rather than precision, since a
    /// clock still settling is the thing a long run is really there to show.</para>
    ///
    /// <para>It costs about 21 s at 48 kHz and 5 s at 192 kHz, a reading being one whole record.</para>
    /// </summary>
    public const int MinReadings = 30;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _run;
    private SpectrumPipelineOptions? _savedOptions;
    private bool _startedAcquisition;

    private string _phase = "Idle";
    private readonly List<ClockDriftPoint> _points = [];
    private double _elapsed;
    private double _duration;
    private double _toneHz;
    private double _playedToneHz;
    private double _sampleRateHz;
    private double _binHz;
    private StreamChannel _measured = StreamChannel.Left;
    private string? _warning;
    private string? _error;
    private bool _remembered;

    public bool Running => _phase is "Settling" or "Measuring";

    public ClockDriftStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                var points = _points.ToArray();
                double[] ppm = [.. points.Select(p => p.Ppm)];

                double? drift = ppm.Length > 0 ? Median(ppm) : null;
                double? spread = ppm.Length > 1 ? ppm.Max() - ppm.Min() : null;
                double? sigma = ppm.Length > 1 && drift is { } m ? RobustSigma(ppm, m) : null;

                ClockDriftVerdict? verdict = null;
                string? chirpCost = null;

                // No figure at all below the minimum, rather than a provisional one: a verdict is
                // what RememberAsync stores on, and a reading this test has said is not good enough
                // to show is not one to correct every later measurement with.
                if (drift is { } value && points.Length >= MinReadings)
                {
                    verdict = ClockDriftReading.Describe(value, sigma, _sampleRateHz, FftSize);
                    chirpCost = ClockDriftReading.DescribeChirpCost(
                        verdict, _sampleRateHz > 0 ? FftSize / _sampleRateHz : 0);
                }

                return new ClockDriftStatus(
                    Running, _phase, _elapsed, _duration, _toneHz, _playedToneHz, _sampleRateHz,
                    _binHz, FftSize, _measured.ToString(), points, drift, spread, sigma,
                    verdict, chirpCost, _warning, _error, _remembered);
            }
        }
    }

    /// <summary>
    /// The middle reading. See <see cref="ClockDriftStatus.DriftPpm"/> for why this and not a mean
    /// — in short, 0.5 % of records on a real bench come back wild, and one of them is enough to
    /// move a mean by more than the whole of what a longer run buys.
    /// </summary>
    internal static double Median(double[] values)
    {
        double[] sorted = [.. values];
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);
    }

    /// <summary>
    /// The scatter of one reading, from the median absolute deviation scaled by 1.4826 so it reads
    /// as a normal distribution's sigma. Robust for the same reason the middle is: the sample
    /// standard deviation of the run this was measured on was seven times larger, and all of that
    /// was two records out of 418.
    /// </summary>
    internal static double RobustSigma(double[] values, double median)
    {
        double[] deviations = [.. values.Select(v => Math.Abs(v - median))];
        return 1.4826 * Median(deviations);
    }

    public async Task<ClockDriftStatus> StartAsync(ClockDriftRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
            throw new ArgumentException(problem);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running)
                throw new InvalidOperationException("A clock drift test is already running.");

            lock (_stateLock)
            {
                _points.Clear();
                _phase = "Settling";
                _remembered = false;
                _elapsed = 0;
                _duration = request.DurationSeconds;
                _toneHz = request.ToneHz;
                _playedToneHz = 0;
                _warning = null;
                _error = null;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts = cts;
            _run = Task.Run(() => RunAsync(request, cts.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }

        return Status;
    }

    /// <summary>What is wrong with the request, or null.</summary>
    public static string? Validate(ClockDriftRequest request)
    {
        if (!double.IsFinite(request.ToneHz) || request.ToneHz < 20 || request.ToneHz > 20000)
            return "The tone must be between 20 Hz and 20 kHz.";
        if (!double.IsFinite(request.LevelDbv) || request.LevelDbv > 20 || request.LevelDbv < -80)
            return "The level must be between -80 and 20 dBV.";
        if (!double.IsFinite(request.DurationSeconds) || request.DurationSeconds < 0 || request.DurationSeconds > 3600)
            return "The duration must be between 0 (until stopped) and 3600 seconds.";
        return null;
    }

    public async Task CancelAsync()
    {
        var cts = _cts;
        var run = _run;
        if (cts is null) return;

        await cts.CancelAsync().ConfigureAwait(false);
        if (run is not null)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public ClockDriftStatus Clear()
    {
        lock (_stateLock)
        {
            _points.Clear();
            _phase = "Idle";
            _remembered = false;
            _elapsed = 0;
            _warning = null;
            _error = null;
        }

        return Status;
    }

    private async Task RunAsync(ClockDriftRequest request, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            double rate = session.StreamConfig?.SampleRateHz ?? session.Capabilities.SampleRatesHz[0];

            _savedOptions ??= pipeline.Options;
            pipeline.Options = pipeline.Options with
            {
                ProcessLeft = true,
                ProcessRight = true,
                FftSize = FftSize,
                Window = WindowType.Hann,
                AverageCount = 1,
                RetainComplexSpectrum = true,
                Weighting = WeightingSpec.None,
            };

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(rate, 2), ct).ConfigureAwait(false);
                _startedAcquisition = true;
            }

            var tone = new ChannelGeneratorConfig(
                Enabled: true, Tones: [new GeneratorTone(request.ToneHz, request.LevelDbv)])
            {
                Kind = GeneratorKind.Tones,
            };

            // Both channels, so the measurement works whichever input the cable is on.
            await session.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone), ct).ConfigureAwait(false);

            double played = PlayedToneHz(request.ToneHz);
            lock (_stateLock)
            {
                _sampleRateHz = rate;
                _binHz = rate / FftSize;
                _playedToneHz = played;
                _phase = "Measuring";
            }

            logger.LogInformation(
                "Clock drift: playing {Played:0.000} Hz at {Level:0.0} dBV, {Fft} points at {Rate:0} Hz, " +
                "for {Duration:0} s, needing at least {MinReadings} readings",
                played, request.LevelDbv, FftSize, rate, request.DurationSeconds, MinReadings);

            // Said before the run rather than discovered after it. A reading is one whole record, so
            // whether a duration can reach MinReadings depends on the sample rate -- which Validate
            // cannot see, being static and holding only the request. Half again on the arithmetic,
            // because the first reading waits for the stream to fill and the odd record is refused
            // for a weak tone.
            double needSeconds = 1.5 * MinReadings * FftSize / rate;
            if (request.DurationSeconds > 0 && request.DurationSeconds < needSeconds)
            {
                lock (_stateLock)
                {
                    _warning =
                        $"This run is {request.DurationSeconds:0} s long, and at this sample rate " +
                        $"{MinReadings} readings take about {needSeconds:0} s. It will probably stop " +
                        "before there is a figure to report. Give it longer, or leave the duration at " +
                        "0 and stop it by hand.";
                }
            }

            await MeasureAsync(request, played, rate, stopwatch, ct).ConfigureAwait(false);

            lock (_stateLock)
            {
                _phase = "Finished";
            }
        }
        catch (OperationCanceledException)
        {
            lock (_stateLock)
            {
                _phase = "Cancelled";
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Clock drift test failed.");
            lock (_stateLock)
            {
                _phase = "Failed";
                _error = ex.Message;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _elapsed = stopwatch.Elapsed.TotalSeconds;
            }

            await RememberAsync().ConfigureAwait(false);
            await TearDownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stores the figure this run measured with the sound card's settings, so the chirp correction
    /// can use it without the test being run again.
    ///
    /// <para><b>Stored without being asked for, and that is the decision.</b> This test has no
    /// other purpose — nobody runs it out of curiosity — so measuring the drift <i>is</i> asking
    /// for it, and a separate button to keep the answer is one more thing to forget, after which
    /// the correction checkbox on the response screens is mysteriously absent. What stays opt-in is
    /// the <i>correction</i>, per measurement, which is where the decision belongs: a figure that
    /// is remembered changes nothing on its own.</para>
    ///
    /// <para><b>Cancelled counts.</b> Two clocks separate at a constant rate, so a run stopped at
    /// forty seconds has the same answer as one that ran the full minute, and what the dialog is
    /// showing at the moment the user stops it is what gets remembered. A <i>failed</i> run does
    /// not, and neither does one with too few points to draw a verdict from.</para>
    ///
    /// <para><b>Only on a sound card</b>, because the figure is stamped with the two devices it was
    /// measured between and a QA403 session is not measuring between them — it has one clock, which
    /// is what makes its reading ~0, and stamping that onto a card pairing would silently retire a
    /// real figure. Note the whole method never throws: a configuration write that fails must not
    /// turn a completed measurement into a failed one, and the reading is still on screen.</para>
    /// </summary>
    private async Task RememberAsync()
    {
        try
        {
            var status = Status;
            if (status.Phase is not ("Finished" or "Cancelled")) return;
            if (status.DriftPpm is not { } ppm || status.Verdict is null) return;
            if (session.Kind != DeviceKind.SoundCard) return;

            var current = soundCard.Config;
            if (!current.IsConfigured) return;

            var updated = current.StampClockDrift(ppm, status.SpreadPpm, DateTimeOffset.UtcNow);
            if (!updated.IsValid)
            {
                logger.LogWarning("Clock drift: {Ppm:0.0} ppm is outside what can be stored; the figure was not kept.", ppm);
                return;
            }

            await config.SetAsync(
                ConfigRepository.SoundCardKey,
                System.Text.Json.JsonSerializer.Serialize(updated, Json.AppJsonContext.Default.SoundCardConfig),
                CancellationToken.None).ConfigureAwait(false);
            soundCard.Config = updated;

            // "Remembered" means kept *and usable*, which is what the dialog goes on to claim --
            // it tells the reader the two chirp screens now offer this correction. On a bench whose
            // two sides are one card there is no correction to offer, so the figure is still kept
            // (it is a real reading, and "0 ppm, one clock" is worth having) and the claim is not
            // made. Asking ForBench rather than re-deriving the conditions is what keeps the two
            // from drifting apart.
            bool usable = ClockDriftCorrection.ForBench(session.Kind, updated) is not null;
            lock (_stateLock)
            {
                _remembered = usable;
            }

            // Not a reopen, unlike the calibration runner's save: a full scale changes what the
            // device reports as its capabilities, where this changes nothing about the device at
            // all -- it is read by the chirp estimator, through the start request. Reopening would
            // also re-clock the card and spend the settle, which is the very state this figure
            // describes.
            logger.LogInformation(
                "Clock drift: stored {Ppm:0.000} ppm (spread {Spread:0.000} ppm) between {Output} and {Input}; {Usable}",
                ppm, status.SpreadPpm ?? 0.0, updated.OutputDeviceName, updated.InputDeviceName,
                usable ? "the chirp screens will offer it" : "nothing can use it on this pairing");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not store the clock drift figure.");
        }
    }

    private async Task MeasureAsync(
        ClockDriftRequest request, double playedHz, double rate, Stopwatch stopwatch, CancellationToken ct)
    {
        double recordSeconds = FftSize / rate;
        uint lastSeq = 0;
        bool seen = false;
        double lastReadingAt = double.NegativeInfinity;
        int quiet = 0;

        while (!ct.IsCancellationRequested)
        {
            double elapsed = stopwatch.Elapsed.TotalSeconds;
            lock (_stateLock)
            {
                _elapsed = elapsed;
            }

            if (request.DurationSeconds > 0 && elapsed >= request.DurationSeconds)
                return;

            var snapshots = pipeline.LatestSnapshots();
            var snapshot = Choose(snapshots, playedHz);

            // One whole record between readings: consecutive ticks overlap by the display hop, and
            // points sharing three quarters of their samples make a wandering clock look steady.
            if (snapshot is not null
                && (!seen || snapshot.Seq != lastSeq)
                && elapsed - lastReadingAt >= recordSeconds)
            {
                lastSeq = snapshot.Seq;
                seen = true;

                if (Read(snapshot, playedHz, elapsed) is { } point)
                {
                    lastReadingAt = elapsed;
                    quiet = 0;
                    lock (_stateLock)
                    {
                        _points.Add(point);
                    }
                }
                else if (++quiet == 10)
                {
                    lock (_stateLock)
                    {
                        _warning =
                            "The tone is not arriving, or is buried in the input's own noise. Check that the " +
                            "output is connected to the input — through the DUT is fine — and that the level " +
                            "is high enough.";
                    }
                }
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The channel carrying the tone most clearly. Both inputs are analysed, so a cable on either
    /// one works without the user being asked which — and a bench where only one input is connected
    /// is the ordinary case rather than a mistake.
    /// </summary>
    private SpectrumSnapshot? Choose(SpectrumSnapshot[] snapshots, double playedHz)
    {
        SpectrumSnapshot? best = null;
        double bestDominance = double.NegativeInfinity;

        foreach (var snapshot in snapshots)
        {
            if (snapshot.Complex is not { Length: > 2 } spectrum || snapshot.BinSizeHz <= 0) continue;

            var estimate = ToneFrequency.Estimate(
                spectrum, snapshot.BinSizeHz, playedHz, SearchHalfWidthBins * snapshot.BinSizeHz);
            if (estimate is not { } found) continue;

            if (found.DominanceDb > bestDominance)
            {
                bestDominance = found.DominanceDb;
                best = snapshot;
            }
        }

        if (best is not null)
        {
            lock (_stateLock)
            {
                _measured = best.Channel;
            }
        }

        return best;
    }

    private ClockDriftPoint? Read(SpectrumSnapshot snapshot, double playedHz, double atSeconds)
    {
        if (snapshot.Complex is not { Length: > 2 } spectrum || snapshot.BinSizeHz <= 0) return null;

        var estimate = ToneFrequency.Estimate(
            spectrum, snapshot.BinSizeHz, playedHz, SearchHalfWidthBins * snapshot.BinSizeHz);
        if (estimate is not { } found || found.DominanceDb < MinDominanceDb) return null;

        return new ClockDriftPoint(
            atSeconds,
            ToneFrequency.DriftPpm(found.Hz, playedHz),
            snapshot.Meters?.FundamentalDbv ?? found.AmplitudeDb,
            found.DominanceDb);
    }

    /// <summary>
    /// What the generator is really playing: the session snaps a tone to a bin, so the figure asked
    /// for is not the figure to measure against. Reading it back rather than re-deriving the snap is
    /// the same rule the status bar's Gen tile follows, and getting it wrong here would put the
    /// whole snap error — up to half a bin, 0.7 Hz, 700 ppm — straight into the answer.
    /// </summary>
    private double PlayedToneHz(double asked)
    {
        var tones = session.EffectiveGeneratorConfig.Left.Tones;
        return tones is { Count: > 0 } ? tones[0].FrequencyHz : asked;
    }

    private async Task TearDownAsync()
    {
        try
        {
            if (session.Connected)
            {
                await session.ConfigureGeneratorAsync(
                    new GeneratorConfig(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not silence the clock drift tone.");
        }

        try
        {
            if (_startedAcquisition)
            {
                await session.StopAcquisitionAsync().ConfigureAwait(false);
                _startedAcquisition = false;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not stop the acquisition the clock drift test started.");
        }

        if (_savedOptions is { } saved)
        {
            pipeline.Options = saved;
            _savedOptions = null;
        }

        _cts?.Dispose();
        _cts = null;
        _run = null;
    }
}
