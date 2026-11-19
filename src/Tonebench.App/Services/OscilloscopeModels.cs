using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// One oscilloscope run, as the client asks for it.
///
/// <para>The timebase is given in <b>seconds</b> and the pre-trigger as a <b>fraction</b>, not as
/// frame counts: both have to mean the same thing at every sample rate, which is the lesson the
/// transfer queue's depth and the decay surfaces' span each had to learn separately. The runner
/// converts them once, against the rate it is actually going to stream at.</para>
/// </summary>
/// <param name="SampleRateHz">Checked against the analyzer's own list before anything starts.</param>
/// <param name="RecordSeconds">The whole record, pre- and post-trigger together.</param>
/// <param name="PreTriggerFraction">How much of it sits before the trigger, 0 to 1. One is 100 % pre-trigger.</param>
/// <param name="Mode">Single, Normal, Auto or Roll.</param>
/// <param name="TriggerKind">Which scanner to build.</param>
/// <param name="TriggerSource">Which channel the scanner watches.</param>
/// <param name="TriggerSlope">Which direction qualifies.</param>
/// <param name="TriggerLevelV">The threshold, in DUT volts — the same volts the trace is drawn in.</param>
/// <param name="TriggerHysteresisV">The noise-reject band. Zero is a plain crossing test.</param>
/// <param name="HoldoffSeconds">How long after a hit the trigger refuses to fire again.</param>
/// <param name="TriggerLevelLowV">
/// The second level, read only by <see cref="Tonebench.Dsp.TriggerKind.Window"/>: the band's other
/// bound, in either order with <paramref name="TriggerLevelV"/>.
/// </param>
/// <param name="PulseWidthLoSeconds">
/// For <see cref="Tonebench.Dsp.TriggerKind.PulseWidth"/>: the shortest width that qualifies. Zero
/// means nothing bounds it below.
/// </param>
/// <param name="DropoutSeconds">
/// For <see cref="Tonebench.Dsp.TriggerKind.Dropout"/>: how long an absence of qualifying edges has
/// to last. Its own field rather than the pulse widths' because it is its own control with its own
/// label, and a stored measurement has to say which one the capture was set by.
/// </param>
/// <param name="PulseWidthHiSeconds">
/// For <see cref="Tonebench.Dsp.TriggerKind.PulseWidth"/>: the longest width that qualifies.
///
/// <para><b>Null means unbounded, and it is nullable for that reason rather than carrying
/// <c>double.PositiveInfinity</c></b>: an infinity is not a JSON number, and
/// <c>System.Text.Json</c> finds that out part way through writing the response -- the rule that
/// truncates a 200 and takes the whole status with it.</para>
/// </param>
/// <param name="AutoTimeoutSeconds">For <see cref="ScopeRunMode.Auto"/>: how long to wait before forcing a record.</param>
/// <param name="Accumulation">What to do with the records after the first: replace, average or envelope.</param>
/// <param name="AverageCount">How many records an average combines. Ignored under Normal.</param>
/// <param name="ProcessLeft">Whether the left channel is captured.</param>
/// <param name="ProcessRight">Whether the right channel is captured.</param>
/// <param name="Generator">
/// The scope screen's own staged stimulus, applied before arming. Null leaves whatever is playing
/// alone, which is what a run against an external source wants.
/// </param>
public sealed record OscilloscopeRequest(
    double SampleRateHz,
    double RecordSeconds,
    double PreTriggerFraction,
    ScopeRunMode Mode,
    TriggerKind TriggerKind,
    TriggerSource TriggerSource,
    TriggerSlope TriggerSlope,
    double TriggerLevelV,
    double TriggerHysteresisV = 0.0,
    double HoldoffSeconds = 0.0,
    double TriggerLevelLowV = 0.0,
    double PulseWidthLoSeconds = 0.0,
    double? PulseWidthHiSeconds = null,
    double DropoutSeconds = 0.0,
    double AutoTimeoutSeconds = 0.5,
    RecordAccumulation Accumulation = RecordAccumulation.Normal,
    int AverageCount = 16,
    bool ProcessLeft = true,
    bool ProcessRight = true,
    GeneratorConfig? Generator = null)
{
    /// <summary>
    /// The longest record the scope will take, in seconds.
    ///
    /// <para>One second of 192 kS/s stereo is 1.5 MB as f32 and about 1.5 MB again in the ring
    /// behind it, which is what a saved result costs and what a <c>.tbench</c> has to stay
    /// mailable with. Longer captures are the stream recorder's job, which already writes WAVs by
    /// the minute.</para>
    /// </summary>
    public const double MaxRecordSeconds = 1.0;

    /// <summary>The shortest, so that a record is always something a chart can draw.</summary>
    public const double MinRecordSeconds = 20e-6;

    /// <summary>
    /// How often <see cref="ScopeRunMode.Roll"/> offers a newer window, in seconds.
    ///
    /// <para>50 ms, which is a smooth scroll for the slow signals Roll exists for, and a
    /// <i>time</i> rather than a frame count so it means the same at every sample rate and at every
    /// record length — the lesson the transfer queue's depth and the decay span each had to learn
    /// on their own. It paces a <b>counter</b> and not a copy: nothing is built until a client asks
    /// for the window, so this decides how often one may usefully ask rather than how much work the
    /// acquisition thread does. See <c>ScopeTriggerEngine.NewestWindow</c>.</para>
    /// </summary>
    public const double RollStepSeconds = 0.05;

    /// <summary>
    /// The record length the pipeline analyses at while the scope runs.
    ///
    /// <para>The scope does not want an FFT, but the pipeline must not be switched off either:
    /// <c>MaybeStepInputRangeAsync</c> reads <c>ProcessLeft</c>/<c>ProcessRight</c> to pick the
    /// peak it ranges on, so with both false the session ranger scans nothing and walks the range
    /// down for ever. Pinning a small size keeps the ranger fed and the status bar's levels alive
    /// for 85 ms of analysis at 192 kHz — the same reasoning, and the same handing-back on the way
    /// out, as <c>SoundCardCalibrationRunner</c>'s own record length.</para>
    /// </summary>
    public const int OscilloscopeFftSize = 16384;

    /// <summary>
    /// How many frames the staged stimulus repeats in, or null where it does not repeat in a way
    /// this can know.
    ///
    /// <para><b>The trigger source that no bench scope has needs exactly one number, and this is
    /// it.</b> Every periodic stimulus here is built to a whole frame count — the chirp's own rule,
    /// because a period that is not an exact number of samples is not periodic in the sampled
    /// domain at all — so the figure is read off the settings rather than measured.</para>
    ///
    /// <para><b>A plain tone is deliberately absent.</b> Its period is <c>fs / f</c>, which is a
    /// whole number of frames only for frequencies nobody types, and rounding it would walk the
    /// trace sideways a fraction of a sample per cycle. A tone has a clean crossing anyway, which
    /// is what the edge trigger is for; the stimuli listed here are the ones that have no feature
    /// to lock to.</para>
    ///
    /// <para>The first enabled channel decides, since that is the one driving the DUT; with both
    /// on they are normally the same stimulus, and where they are not the left is what the trace
    /// is read against.</para>
    ///
    /// <para><b>A periodic noise sequence is handled and is not reachable from a screen</b>, which
    /// is why the refusal names three stimuli and this names four: <c>PeriodFrames</c> is set by
    /// the runners that build their own configuration — the impedance measurement's pink noise —
    /// and the generator endpoint's noise block carries a level and no period, so noise staged from
    /// a panel is the continuous kind and genuinely has no repeat. The arm is kept because the
    /// rule is about the <i>stimulus</i> rather than about which screen composed it.</para>
    /// </summary>
    /// <param name="projectFilePath">
    /// The open project, for an arbitrary stimulus whose stored path is relative to it. Null is
    /// "no project", which leaves a relative path unresolvable and so answers null — the refusal
    /// <see cref="Validate"/> then gives names the file.
    /// </param>
    public long? StimulusPeriodFrames(string? projectFilePath = null)
    {
        if (Generator is null)
            return null;

        var channel = Generator.Left is { Enabled: true } left ? left
            : Generator.Right is { Enabled: true } right ? right
            : null;

        if (channel is null)
            return null;

        if (PeriodicWave.IsWaveKind(channel.Kind))
        {
            int frames = PeriodicWave.PeriodFrames(channel.Wave?.FrequencyHz ?? 0.0, SampleRateHz);
            return frames > 0 ? frames : null;
        }

        if (channel.Kind == GeneratorKind.Arbitrary)
            return ArbitraryPeriodFrames(channel.Arbitrary, projectFilePath);

        return channel.Kind switch
        {
            GeneratorKind.EchoChirp when channel.Chirp is { RepeatFrames: > 1 } chirp => chirp.RepeatFrames,
            GeneratorKind.PinkNoise or GeneratorKind.WhiteNoise when channel.Noise is { PeriodFrames: > 1 } noise =>
                noise.PeriodFrames,
            GeneratorKind.ToneBurst when channel.Burst is not null =>
                ToneBurst.BurstPeriodFrames(channel.Burst, SampleRateHz) is > 1 and var burst ? burst : null,
            _ => null,
        };
    }

    /// <summary>
    /// An arbitrary waveform's loop, in frames — which is the one period here that cannot be
    /// derived from the request alone, because it is the resampled table's own length.
    ///
    /// <para>So the file is read. That is <b>cheap in every case that matters</b>: the loader
    /// caches by the file's identity and the rate, so the read this does is the same one the
    /// generator's own configure is about to do, and every later call is a dictionary lookup. It is
    /// also the pairing this stimulus most wants — a recorded waveform may have no clean crossing
    /// to trigger on at all, while its repeat is exact by construction.</para>
    ///
    /// <para>A file that cannot be read answers <c>null</c> rather than throwing, because this runs
    /// inside <see cref="Validate"/>: the refusal a user needs there is about the file, and
    /// <see cref="Validate"/> says so by name rather than blaming the trigger.</para>
    /// </summary>
    private int? ArbitraryPeriodFrames(ArbitrarySettings? arbitrary, string? projectFilePath)
    {
        if (arbitrary is null || string.IsNullOrWhiteSpace(arbitrary.Path))
            return null;

        if (arbitrary.Table is { Frames: > 1 } loaded)
            return loaded.Frames;

        try
        {
            // The project rather than nothing: a stored path is relative to it, and a trigger that
            // could not find the file would refuse a run the generator is about to start happily.
            int frames = ArbitraryWaveformLoader.Load(arbitrary, SampleRateHz, projectFilePath).Frames;
            return frames > 1 ? frames : null;
        }
        catch (WaveFileException)
        {
            return null;
        }
    }

    /// <param name="projectFilePath">
    /// The open project, passed through to <see cref="StimulusPeriodFrames"/> so an arbitrary
    /// stimulus stored relative to it can be found. Defaults to null, which every caller that has
    /// no project — and every test — is right to leave alone.
    /// </param>
    public void Validate(string? projectFilePath = null)
    {
        if (!(SampleRateHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(SampleRateHz), SampleRateHz, "The sample rate must be above zero.");
        if (!Enum.IsDefined(Mode))
            throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "Mode must be Single, Normal, Auto or Roll.");
        if (!Enum.IsDefined(TriggerKind))
            throw new ArgumentOutOfRangeException(nameof(TriggerKind), TriggerKind, "Unknown trigger kind.");
        if (!Enum.IsDefined(TriggerSource))
            throw new ArgumentOutOfRangeException(nameof(TriggerSource), TriggerSource, "Unknown trigger source.");
        if (!Enum.IsDefined(TriggerSlope))
            throw new ArgumentOutOfRangeException(nameof(TriggerSlope), TriggerSlope, "Slope must be Rising, Falling or Either.");
        if (!Enum.IsDefined(Accumulation))
            throw new ArgumentOutOfRangeException(nameof(Accumulation), Accumulation, "Accumulation must be Normal, Average or Envelope.");

        if (Accumulation != RecordAccumulation.Normal
            && (AverageCount < RecordAccumulator.MinCount || AverageCount > RecordAccumulator.MaxCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(AverageCount), AverageCount,
                $"The number of records to combine must be between {RecordAccumulator.MinCount} and {RecordAccumulator.MaxCount}.");
        }

        // Single takes one record and holds it, so there is never a second one to combine with --
        // and a screen that sat at "1 of 16" for ever, with no way to advance it, is worse than
        // the refusal. Roll has no record boundary at all: its windows overlap by all but one roll
        // step, so averaging them would low-pass the signal and an envelope of them would be the
        // same band drawn N times.
        if (Accumulation != RecordAccumulation.Normal && Mode is ScopeRunMode.Single or ScopeRunMode.Roll)
        {
            throw new ArgumentException(
                $"{Accumulation} needs repeated records to combine, which {Mode} does not produce. Use Auto or Normal.",
                nameof(Accumulation));
        }

        if (TriggerKind == TriggerKind.Dropout && (!(DropoutSeconds > 0.0) || !double.IsFinite(DropoutSeconds)))
        {
            // Zero would fire on every sample on which no edge happened, which is almost all of
            // them -- a trigger that fires constantly while claiming to report an absence.
            throw new ArgumentOutOfRangeException(
                nameof(DropoutSeconds), DropoutSeconds,
                "A dropout trigger needs a timeout above zero: how long an absence has to last.");
        }

        // The window's band and the runt's pair of thresholds are the same two numbers read two
        // ways, so they are checked together -- and a single threshold is no condition in either.
        if (TriggerKind is TriggerKind.Window or TriggerKind.Runt)
        {
            if (!double.IsFinite(TriggerLevelLowV))
                throw new ArgumentOutOfRangeException(nameof(TriggerLevelLowV), TriggerLevelLowV, "The second trigger level must be a finite voltage.");

            // The band is what the trigger is about, so two equal bounds are not a narrow window --
            // they are no window, and nothing can ever be inside one. Named by the control rather
            // than by the field, since the two boxes are what a reader has in front of them.
            if (TriggerLevelLowV == TriggerLevelV)
            {
                throw new ArgumentException(
                    TriggerKind == TriggerKind.Window
                        ? "The window trigger's two levels must differ. Set one above the other to make a band."
                        : "A runt is a pulse that reaches one threshold and not the other, so the two must differ.",
                    nameof(TriggerLevelLowV));
            }
        }

        if (TriggerKind == TriggerKind.PulseWidth)
        {
            if (!(PulseWidthLoSeconds >= 0.0) || !double.IsFinite(PulseWidthLoSeconds))
                throw new ArgumentOutOfRangeException(nameof(PulseWidthLoSeconds), PulseWidthLoSeconds, "The shorter width must be zero or a positive time.");
            if (PulseWidthHiSeconds is { } hi && (!(hi > 0.0) || !double.IsFinite(hi)))
                throw new ArgumentOutOfRangeException(nameof(PulseWidthHiSeconds), PulseWidthHiSeconds, "The longer width must be a positive time, or absent for no upper bound.");
            if (PulseWidthHiSeconds is { } bound && bound < PulseWidthLoSeconds)
            {
                throw new ArgumentException(
                    "The longer width cannot be below the shorter one.",
                    nameof(PulseWidthHiSeconds));
            }

            // Both bounds absent means every pulse qualifies, which is an edge trigger that fires on
            // trailing edges instead -- not what anybody reaches for this kind to get, and silent if
            // it were allowed. The message names the two controls rather than the condition, because
            // the panel offers "shorter than", "longer than" and "between" over this one pair.
            if (PulseWidthLoSeconds <= 0.0 && PulseWidthHiSeconds is null)
            {
                throw new ArgumentException(
                    "A pulse-width trigger needs a width condition: set the shorter bound, the longer bound, or both.",
                    nameof(PulseWidthLoSeconds));
            }
        }

        if (!(RecordSeconds >= MinRecordSeconds) || RecordSeconds > MaxRecordSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RecordSeconds), RecordSeconds,
                $"The record must be between {MinRecordSeconds * 1e6:0} us and {MaxRecordSeconds:0.###} s.");
        }

        if (!(PreTriggerFraction >= 0.0) || PreTriggerFraction > 1.0)
            throw new ArgumentOutOfRangeException(nameof(PreTriggerFraction), PreTriggerFraction, "The pre-trigger fraction must be between 0 and 1.");

        if (!double.IsFinite(TriggerLevelV))
            throw new ArgumentOutOfRangeException(nameof(TriggerLevelV), TriggerLevelV, "The trigger level must be a finite voltage.");
        if (!(TriggerHysteresisV >= 0.0) || !double.IsFinite(TriggerHysteresisV))
            throw new ArgumentOutOfRangeException(nameof(TriggerHysteresisV), TriggerHysteresisV, "The hysteresis must be zero or a positive voltage.");
        if (!(HoldoffSeconds >= 0.0) || !double.IsFinite(HoldoffSeconds))
            throw new ArgumentOutOfRangeException(nameof(HoldoffSeconds), HoldoffSeconds, "The holdoff must be zero or a positive time.");
        if (Mode == ScopeRunMode.Auto && !(AutoTimeoutSeconds > 0.0))
            throw new ArgumentOutOfRangeException(nameof(AutoTimeoutSeconds), AutoTimeoutSeconds, "Auto mode needs a timeout above zero.");

        if (!ProcessLeft && !ProcessRight)
            throw new ArgumentException("At least one channel must be captured.", nameof(ProcessLeft));

        if (TriggerSource == TriggerSource.ChannelA && !ProcessLeft)
            throw new ArgumentException("The trigger watches the left channel, which is not being captured.", nameof(TriggerSource));
        if (TriggerSource == TriggerSource.ChannelB && !ProcessRight)
            throw new ArgumentException("The trigger watches the right channel, which is not being captured.", nameof(TriggerSource));
        if (TriggerSource == TriggerSource.Math)
            throw new ArgumentOutOfRangeException(nameof(TriggerSource), TriggerSource, "That trigger source is not implemented yet.");

        if (TriggerSource == TriggerSource.Stimulus)
        {
            // The source replaces the kind rather than qualifying it: there is no level, no slope
            // and no width in a trigger that fires on the generator's own repeat, so a kind beside
            // it would be four controls doing nothing. Refused by name rather than ignored.
            if (TriggerKind != TriggerKind.Edge)
            {
                throw new ArgumentException(
                    "The stimulus trigger fires on the generator's own repeat, so it cannot also be a "
                        + $"{TriggerKind} trigger. Set the kind to Edge, or pick a channel to trigger on.",
                    nameof(TriggerSource));
            }

            if (StimulusPeriodFrames(projectFilePath) is null)
            {
                // A file names itself: "no repeat" would send the reader looking at the trigger
                // when what is wrong is the path, and the file is the thing they can fix.
                bool arbitrary = Generator is not null
                    && (Generator.Left is { Enabled: true, Kind: GeneratorKind.Arbitrary }
                        || Generator.Right is { Enabled: true, Kind: GeneratorKind.Arbitrary });

                throw new ArgumentException(
                    arbitrary
                        ? "The stimulus trigger needs the .wav file's loop, and the file could not be read. "
                            + "Choose another trigger source and press Run to see why."
                        : "The stimulus trigger needs a stimulus whose repeat is exact: a chirp, a periodic "
                            + "noise sequence, one of the hard-switched shapes, a tone burst or a .wav on a "
                            + "loop. A plain tone has a crossing to trigger on instead.",
                    nameof(TriggerSource));
            }
        }
    }

    /// <summary>The record length in frames at <paramref name="sampleRateHz"/>, at least two.</summary>
    public int RecordFramesAt(double sampleRateHz) =>
        Math.Max(2, (int)Math.Round(RecordSeconds * sampleRateHz));

    /// <summary>
    /// The pre-trigger length in frames, never longer than the record itself.
    ///
    /// <para><b>The whole record in Roll</b>, whatever the fraction says. There t = 0 is <i>now</i>
    /// and every sample in the record is older than it, so the pre-trigger <em>is</em> the record —
    /// which is also what makes the client's own window arithmetic come out at
    /// <c>[-record, 0]</c> without a second rule. The staged fraction is left alone rather than
    /// rewritten, so switching back to a triggered mode restores it.</para>
    /// </summary>
    public int PreTriggerFramesAt(double sampleRateHz) =>
        Mode == ScopeRunMode.Roll
            ? RecordFramesAt(sampleRateHz)
            : Math.Clamp((int)Math.Round(PreTriggerFraction * RecordFramesAt(sampleRateHz)), 0, RecordFramesAt(sampleRateHz));

    /// <summary>The roll step in frames at <paramref name="sampleRateHz"/>, at least one.</summary>
    public long RollStepFramesAt(double sampleRateHz) =>
        Math.Max(1, (long)Math.Round(RollStepSeconds * sampleRateHz));

    /// <summary>Which channel index the trigger watches.</summary>
    public int TriggerChannelIndex => TriggerSource == TriggerSource.ChannelB ? 1 : 0;
}

/// <summary>
/// What the oscilloscope is doing, and what it last caught.
///
/// <para><b>Every absent reading is null, never NaN.</b> A non-finite double cannot be written as
/// JSON, and <c>System.Text.Json</c> finds that out part way through — after the status line and
/// everything before it has already been flushed — so the client sees a 200 with a truncated body
/// and discards the whole status. On this record most of the readings are absent most of the time
/// (nothing captured yet, no trigger in Roll mode), which makes it the likeliest place in the app
/// for that fault to arrive. <c>OscilloscopeStatusJsonTests</c> is what holds the line.</para>
/// </summary>
/// <param name="Phase">Where the run is. <see cref="FrequencyResponsePhase.WaitingForTrigger"/> is armed and watching.</param>
/// <param name="Error">Why it failed, or null.</param>
/// <param name="SampleRateHz">The rate it is streaming at, or 0 before a run.</param>
/// <param name="RecordFrames">The record length in frames, or 0 before a run.</param>
/// <param name="PreTriggerFrames">How many of those sit before the trigger.</param>
/// <param name="RecordsTaken">
/// How many records this run has <b>collected</b> — the count a Normal run's "still alive" reads
/// from, and what the client watches to know there is a newer one to fetch. Not what the trigger
/// found: see <paramref name="RecordsCommitted"/>.
/// </param>
/// <param name="RecordsCommitted">
/// How many the trigger engine committed over the same acquisition, which is the capture rate the
/// hardware is managing.
///
/// <para>The engine holds <b>one</b> record and overwrites it, so the difference between the two is
/// captures that existed and were replaced before anything collected them — invisible without this,
/// and indistinguishable from a device that simply did not trigger. Zero in Roll, which commits
/// nothing at all and whose <paramref name="RecordsTaken"/> counts roll steps instead.</para>
/// </param>
/// <param name="HasRecord">Whether a record is on hand to be drawn or saved.</param>
/// <param name="Spliced">The record on hand spans a dropped chunk or block.</param>
/// <param name="Forced">The record on hand was taken by Auto's timeout; nothing qualified.</param>
/// <param name="Rolled">
/// The run is <see cref="ScopeRunMode.Roll"/>, so the record on hand is the newest window and
/// nothing triggered. What the client does with it is suppress the trigger line: the level, the
/// slope and the hysteresis had no part in where this window starts.
/// </param>
/// <param name="Accumulation">Which mode the records are being combined under, as its own name.</param>
/// <param name="AveragesTaken">How many have gone into the record on hand — what a screen counts up while it fills.</param>
/// <param name="AverageCount">How many it is working toward. One under Normal.</param>
/// <param name="TriggerOffsetS">Seconds from the start of the record to the trigger, or null with no record.</param>
/// <param name="RecordFromS">The record's first sample, relative to the trigger. Negative for any pre-trigger at all.</param>
/// <param name="RecordToS">Its last sample, same reference.</param>
public sealed record OscilloscopeStatus(
    FrequencyResponsePhase Phase,
    string? Error,
    double SampleRateHz,
    int RecordFrames,
    int PreTriggerFrames,
    int RecordsTaken,
    bool HasRecord,
    bool Spliced,
    bool Forced,
    double? TriggerOffsetS,
    double? RecordFromS,
    double? RecordToS,
    bool Rolled = false,
    string Accumulation = nameof(RecordAccumulation.Normal),
    int AveragesTaken = 0,
    int AverageCount = 1,
    int RecordsCommitted = 0);

/// <summary>The acknowledgement a start returns, so the client knows what it actually got.</summary>
/// <param name="SampleRateHz">The rate the run is streaming at.</param>
/// <param name="RecordFrames">The record length the seconds asked for came to.</param>
/// <param name="PreTriggerFrames">And how many of those are pre-trigger.</param>
public sealed record OscilloscopeStarted(double SampleRateHz, int RecordFrames, int PreTriggerFrames);
