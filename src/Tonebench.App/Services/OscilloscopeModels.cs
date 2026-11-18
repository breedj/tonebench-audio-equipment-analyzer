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
/// <param name="TriggerKind">Which scanner to build. Only <see cref="Tonebench.Dsp.TriggerKind.Edge"/> is implemented so far.</param>
/// <param name="TriggerSource">Which channel the scanner watches.</param>
/// <param name="TriggerSlope">Which direction qualifies.</param>
/// <param name="TriggerLevelV">The threshold, in DUT volts — the same volts the trace is drawn in.</param>
/// <param name="TriggerHysteresisV">The noise-reject band. Zero is a plain crossing test.</param>
/// <param name="HoldoffSeconds">How long after a hit the trigger refuses to fire again.</param>
/// <param name="AutoTimeoutSeconds">For <see cref="ScopeRunMode.Auto"/>: how long to wait before forcing a record.</param>
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
    double AutoTimeoutSeconds = 0.5,
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

    public void Validate()
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

        if (TriggerKind != TriggerKind.Edge)
            throw new ArgumentOutOfRangeException(nameof(TriggerKind), TriggerKind, "Only the edge trigger is implemented so far.");

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
        if (TriggerSource is TriggerSource.Math or TriggerSource.Stimulus)
            throw new ArgumentOutOfRangeException(nameof(TriggerSource), TriggerSource, "That trigger source is not implemented yet.");
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
/// <param name="RecordsTaken">How many records this run has committed — the count a Normal run's "still alive" reads from.</param>
/// <param name="HasRecord">Whether a record is on hand to be drawn or saved.</param>
/// <param name="Spliced">The record on hand spans a dropped chunk or block.</param>
/// <param name="Forced">The record on hand was taken by Auto's timeout; nothing qualified.</param>
/// <param name="Rolled">
/// The run is <see cref="ScopeRunMode.Roll"/>, so the record on hand is the newest window and
/// nothing triggered. What the client does with it is suppress the trigger line: the level, the
/// slope and the hysteresis had no part in where this window starts.
/// </param>
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
    bool Rolled = false);

/// <summary>The acknowledgement a start returns, so the client knows what it actually got.</summary>
/// <param name="SampleRateHz">The rate the run is streaming at.</param>
/// <param name="RecordFrames">The record length the seconds asked for came to.</param>
/// <param name="PreTriggerFrames">And how many of those are pre-trigger.</param>
public sealed record OscilloscopeStarted(double SampleRateHz, int RecordFrames, int PreTriggerFrames);
