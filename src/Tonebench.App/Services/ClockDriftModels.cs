namespace Tonebench.App.Services;

/// <summary>What to play, for how long, and how often to take a reading.</summary>
/// <param name="ToneHz">
/// The tone to measure. Snapped to a bin by the session like any other, so what is actually played
/// is read back rather than assumed — see <see cref="ClockDriftRunner"/>.
/// </param>
/// <param name="LevelDbv">
/// The generator level. Loud enough that the tone is well clear of the input's own hum, which is the
/// one thing that silently ruins this measurement; the status reports what the tone actually reached
/// so a bench that needs more can be told rather than left to produce a confident wrong answer.
/// </param>
/// <param name="DurationSeconds">
/// How long to measure for. Zero runs until stopped. Two clocks separate at a constant rate, so the
/// figure converges quickly and the length is really about <i>stability</i> — a reading that wanders
/// for the first half-minute is a clock still settling, which is a real and documented behaviour of
/// the QA403 after a sample-rate change.
/// </param>
public sealed record ClockDriftRequest(
    double ToneHz = 1000.0,
    double LevelDbv = -20.0,
    double DurationSeconds = 60.0);

/// <summary>One reading: how far apart the two clocks were, this many seconds into the run.</summary>
/// <param name="DominanceDb">
/// The tone against everything else in that record. Carried per point rather than once for the run
/// because it is what says whether the point is worth believing, and a cable knocked half out part
/// way through is exactly the case a single up-front check would miss.
/// </param>
public sealed record ClockDriftPoint(
    double AtSeconds, double Ppm, double ToneDbv, double DominanceDb);

/// <summary>
/// What the measurement means, in the terms the user's own bench is in.
///
/// <para>Separate from the status so it can be tested as the pure function it is: every field below
/// follows from the mean drift, the sample rate and the record length by arithmetic, and the
/// wording is the part worth pinning.</para>
/// </summary>
/// <param name="Headline">One line: the figure and what kind of bench it describes.</param>
/// <param name="Detail">What it does to measurements, in sentences.</param>
/// <param name="Quality">Good / Fair / Poor / Unknown — what the dialog colours on.</param>
/// <param name="SlipSamplesPerRecord">
/// How far the two clocks separate across one analysis record. The figure the existing two-clocks
/// warning is really about: it is read as a delay that grows with time, i.e. as a frequency-dependent
/// phase error on anything measured from a chirp.
/// </param>
/// <param name="SlipMicroseconds">The same slip as a time.</param>
/// <param name="PhaseErrorDegAt20k">
/// That slip as degrees at 20 kHz, which is where it is worst and where it is read as DUT phase.
/// </param>
public sealed record ClockDriftVerdict(
    string Headline,
    string Detail,
    string Quality,
    double SlipSamplesPerRecord,
    double SlipMicroseconds,
    double PhaseErrorDegAt20k);

/// <summary>
/// Turning a drift figure into something a reader can act on.
///
/// <para><b>The thresholds are not taste.</b> An ordinary consumer audio crystal is specified at
/// ±50 to ±100 ppm, and the quantity here is the <i>difference</i> of two of them, so anything up to
/// about ±200 ppm is two perfectly healthy cards and says nothing bad about either. Measured on this
/// project's own bench, a MOTU M4 against a Sound BlasterX G6 reads −104.37 ppm and is stable to a
/// quarter of a ppm. Below about 1 ppm the two are not independent at all — the same card both ways,
/// or two locked to one word clock. Far above 200 ppm is the interesting failure: not a bad crystal,
/// but a card that is not running at the rate it was asked for, or a resampler in the path.</para>
/// </summary>
public static class ClockDriftReading
{
    /// <summary>Below this the two sides are one clock, or locked to one.</summary>
    public const double LockedPpm = 1.0;

    /// <summary>Two independent crystals, each within its own ±100 ppm, cannot exceed this.</summary>
    public const double TwoCrystalsPpm = 200.0;

    /// <summary>
    /// A reading whose spread is this much of its own size has not settled, whatever its mean says.
    /// </summary>
    public const double UnstableFraction = 0.25;

    public static ClockDriftVerdict Describe(
        double meanPpm, double? spreadPpm, double sampleRateHz, int fftSize)
    {
        double magnitude = Math.Abs(meanPpm);
        double recordSeconds = sampleRateHz > 0 ? fftSize / sampleRateHz : 0;
        double slipSeconds = magnitude * 1e-6 * recordSeconds;
        double slipSamples = slipSeconds * sampleRateHz;
        double degAt20k = slipSeconds * 20000.0 * 360.0;

        bool unstable = spreadPpm is { } spread
            && magnitude > LockedPpm
            && spread > Math.Max(UnstableFraction * magnitude, LockedPpm);

        string headline;
        string quality;
        string detail;

        if (magnitude < LockedPpm)
        {
            headline = $"{meanPpm:+0.00;-0.00;0.00} ppm — one clock.";
            quality = "Good";
            detail =
                "The two sides are not running independently: this is one card used both ways, or " +
                "two locked to the same word clock. Nothing here is affected by clock drift, and " +
                "the two-clocks warning does not apply to this bench.";
        }
        else if (unstable)
        {
            headline = $"{meanPpm:+0.0;-0.0;0.0} ppm, and still moving.";
            quality = "Fair";
            detail =
                "The mean is plausible but the reading has not settled, so take it as provisional. " +
                "A clock that has just been retuned does this: a QA403 re-locks over 15 to 30 seconds " +
                "after a sample-rate change, and a sound card can do the same after its stream is " +
                "reopened. Leave it running, or start the test again once the bench has been playing " +
                "for a minute. If it never settles, something in the path is resampling.";
        }
        else if (magnitude <= TwoCrystalsPpm)
        {
            headline = $"{meanPpm:+0.0;-0.0;0.0} ppm — two ordinary crystals.";
            quality = "Good";
            detail =
                $"That is normal and says nothing bad about either card: a consumer audio crystal is " +
                $"specified at ±50 to ±100 ppm and this is the difference between two of them. " +
                $"Tone measurements — THD, IMD, stepped sweeps — are unaffected, because each reads " +
                $"one frequency at a time and a {magnitude:0} ppm error on 1 kHz is {magnitude / 1000:0.000} Hz. " +
                $"What it does reach is anything measured from a chirp.";
        }
        else
        {
            headline = $"{meanPpm:+0.0;-0.0;0.0} ppm — larger than two crystals explain.";
            quality = "Poor";
            detail =
                "Two independent crystals differ by at most about 200 ppm, so this is something " +
                "else: most likely one of the two is not running at the rate it was asked for, or " +
                "there is a resampler in the path. Check that neither card is set to a different " +
                "rate in its own control panel, and that the host API is one that retunes the " +
                "device rather than converting for it — DirectSound and MME always resample, and " +
                "WASAPI does unless it is given the device exclusively.";
        }

        return new ClockDriftVerdict(
            headline, detail, quality, slipSamples, slipSeconds * 1e6, degAt20k);
    }

    /// <summary>
    /// What the drift costs a chirp measurement, as its own sentence — kept apart from
    /// <see cref="Describe"/> because it is the same whatever the verdict, and because it is the
    /// half of the answer the existing two-clocks warning states without a number.
    /// </summary>
    public static string DescribeChirpCost(ClockDriftVerdict verdict, double recordSeconds)
    {
        if (verdict.SlipSamplesPerRecord < 0.01)
            return "Over one analysis record the two clocks separate by less than a hundredth of a sample, which nothing can see.";

        return
            $"Over one {recordSeconds:0.00} s analysis record the two clocks separate by " +
            $"{verdict.SlipSamplesPerRecord:0.0} samples ({verdict.SlipMicroseconds:0} µs). A chirp " +
            $"measurement reads that as a delay that grows across the sweep, i.e. as " +
            $"{verdict.PhaseErrorDegAt20k:0} ° of phase error at 20 kHz, reported as the DUT's own " +
            $"phase and group delay. Use the right input as a reference, or the stepped sine, where " +
            $"phase matters.";
    }
}

/// <summary>Where a clock-drift run has got to.</summary>
public sealed record ClockDriftStatus(
    bool Running,
    string Phase,
    double ElapsedSeconds,
    double DurationSeconds,
    double ToneHz,
    double PlayedToneHz,
    double SampleRateHz,
    double BinHz,
    int FftSize,
    string MeasuredChannel,
    IReadOnlyList<ClockDriftPoint> Points,
    double? MeanPpm,
    double? SpreadPpm,
    double? SigmaPpm,
    ClockDriftVerdict? Verdict,
    string? ChirpCost,
    string? Warning,
    string? Error)
{
    public static ClockDriftStatus Idle { get; } = new(
        false, "Idle", 0, 0, 0, 0, 0, 0, 0, "None", [], null, null, null, null, null, null, null);
}
