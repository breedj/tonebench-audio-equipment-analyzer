using Tonebench.Dsp;

namespace Tonebench.App.Streaming;

/// <summary>How the engine re-arms itself after a capture, or whether it arms at all.</summary>
public enum ScopeRunMode
{
    /// <summary>One record, then hold. The engine does not re-arm.</summary>
    Single,

    /// <summary>Re-arm after every record and wait as long as it takes. A screen that stays blank means nothing qualified.</summary>
    Normal,

    /// <summary>
    /// Like <see cref="Normal"/>, but if nothing qualifies within the timeout a record is taken
    /// anyway and flagged <see cref="ScopeRecord.Forced"/>. What stops a mis-set level looking like
    /// a dead input -- at the price of a trace that is not aligned to anything, which is why the
    /// flag exists rather than the forced record passing for a triggered one.
    /// </summary>
    Auto,

    /// <summary>No trigger at all; the newest window is drawn as it arrives. The engine never arms.</summary>
    Roll,
}

/// <summary>Everything the engine needs to know to take one record.</summary>
/// <param name="Trigger">What qualifies.</param>
/// <param name="TriggerChannel">Which channel the scanner watches.</param>
/// <param name="RecordFrames">The whole record, pre- and post-trigger together.</param>
/// <param name="PreTriggerFrames">How much of it sits before the trigger. Equal to <paramref name="RecordFrames"/> is 100 % pre-trigger.</param>
/// <param name="Mode">How to re-arm.</param>
/// <param name="SampleRateHz">Carried onto the record so a consumer never has to ask twice.</param>
/// <param name="AutoTimeoutFrames">For <see cref="ScopeRunMode.Auto"/>: how long to wait before forcing one.</param>
/// <param name="DiscontinuityHoldoffFrames">
/// How long to refuse to trigger after the stream breaks. Not the same thing as the buffer's own
/// contiguity floor and not redundant with it: after a 24 dB attenuator release the samples read
/// exactly 24 dB low for about 1.05 s, and <b>only the first of those ~10 blocks carries the
/// discontinuity flag</b> -- so the flag bounds the splice but not the wrong levels that follow it.
/// </param>
public readonly record struct ScopeCaptureSpec(
    TriggerSpec Trigger,
    int TriggerChannel,
    int RecordFrames,
    int PreTriggerFrames,
    ScopeRunMode Mode,
    double SampleRateHz,
    long AutoTimeoutFrames = 0,
    long DiscontinuityHoldoffFrames = 0,
    long SettleFrames = 0,
    long RollStepFrames = 0);

/// <summary>
/// One captured record: the samples, and everything needed to say what they are.
///
/// <para>The samples have been <b>quantised to f32</b> through <c>PcmCodec.QuantizeInPlace</c>
/// before anything measured them. The stored waveform is the durable truth, so the readings on
/// screen have to be readings of exactly that -- otherwise re-opening a saved result shows a
/// different Vpp from the one it was saved with, for no reason a user could ever see. It also makes
/// a parked record and a saved record bit-identical.</para>
///
/// <para>The mean is deliberately <i>not</i> removed, unlike the simulation import path that shares
/// this codec: DC is one of the readings a scope exists to give.</para>
/// </summary>
/// <param name="Channels">One array per channel, <paramref name="FrameCount"/> long, in DUT volts.</param>
/// <param name="SampleRateHz">The rate the samples are on.</param>
/// <param name="FirstIndex">Absolute index of <c>Channels[*][0]</c>.</param>
/// <param name="TriggerPosition">
/// Absolute, fractional index of the crossing -- the sub-sample interpolation carried through, so
/// t = 0 lands where the signal actually crossed rather than on the nearest sample.
/// </param>
/// <param name="Epoch">The buffer epoch this was taken in.</param>
/// <param name="Spliced">
/// A chunk or block was dropped while this record was being filled, so somewhere in it two
/// non-adjacent pieces of the stream sit side by side. Flagged rather than hidden: a hole and a DUT
/// fault are indistinguishable in a waveform drawn without it.
/// </param>
/// <param name="Forced">Taken by <see cref="ScopeRunMode.Auto"/>'s timeout; nothing qualified.</param>
/// <param name="Rolled">
/// Taken by <see cref="ScopeRunMode.Roll"/>: the newest window, with no trigger involved at all.
/// <paramref name="TriggerPosition"/> is its right-hand edge, so t = 0 is <i>now</i> and the whole
/// record sits to the left of it — the axis going negative rather than re-zeroing, which is this
/// app's convention everywhere a time is measured from an event.
///
/// <para>Flagged rather than left to be inferred from the span, because what a reader must not do
/// is read the trigger controls against it: nothing triggered, so the level, the slope and the
/// hysteresis had no part in where this window starts. The chart draws no trigger line on one.</para>
/// </param>
/// <param name="Lower">
/// The lower outline, under <see cref="RecordAccumulation.Envelope"/> only — null everywhere else.
///
/// <para>With it set, <see cref="Channels"/> is the <b>upper</b> outline and the pair is the band
/// every sample position has swept over the records since the accumulator was reset. That is not a
/// waveform, and the readings taken from one are its own extremes and nothing else: see
/// <c>ScopeSliceMeasurements.OfEnvelope</c> for which figures survive and why the rest are absent
/// rather than computed from an outline.</para>
/// </param>
/// <param name="AveragesTaken">
/// How many records went into this one — 1 for an ordinary capture, and what a screen counts up
/// while an average or an envelope fills.
/// </param>
public sealed record ScopeRecord(
    double[][] Channels,
    double SampleRateHz,
    long FirstIndex,
    double TriggerPosition,
    int Epoch,
    bool Spliced,
    bool Forced,
    bool Rolled = false,
    double[][]? Lower = null,
    int AveragesTaken = 1)
{
    public int ChannelCount => Channels.Length;

    /// <summary>
    /// True where <see cref="Lower"/> is set: this is a band rather than a waveform.
    ///
    /// <para>What reads it is everything that assumes a waveform and would otherwise answer
    /// plausibly about an outline — the math channel, the X–Y figure and the eye all refuse on one,
    /// and the slice's readings are the band's own extremes.</para>
    /// </summary>
    public bool IsEnvelope => Lower is not null;

    public int FrameCount => Channels.Length == 0 ? 0 : Channels[0].Length;

    /// <summary>Seconds from the start of the record to the trigger. Positive for any pre-trigger at all.</summary>
    public double TriggerOffsetS => (TriggerPosition - FirstIndex) / SampleRateHz;

    /// <summary>
    /// The time of frame <paramref name="frame"/> relative to the trigger, which is t = 0.
    /// Negative to the left of it -- the axis goes negative rather than re-zeroing, the convention
    /// the decay surfaces' head start already uses.
    /// </summary>
    public double TimeAt(int frame) => (FirstIndex + frame - TriggerPosition) / SampleRateHz;

    /// <summary>The record's own span, as (first, last) seconds relative to the trigger.</summary>
    public (double FromS, double ToS) Span => (TimeAt(0), TimeAt(Math.Max(0, FrameCount - 1)));
}
