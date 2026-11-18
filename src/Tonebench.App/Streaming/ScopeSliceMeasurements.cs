using Tonebench.Dsp;

namespace Tonebench.App.Streaming;

/// <summary>
/// Every reading of one window of one channel, measured on the samples at full resolution.
///
/// <para><b>Measured on the analyzer and sent beside the picture, never worked out from it.</b>
/// Vmax, Vmin and Vpp would survive the decimation — a min/max pair per column keeps both extremes
/// of every column exactly — and nothing else here would: a mean of column maxima is the mean of an
/// envelope, an RMS over them is an RMS of nothing, and a rise time read off a column pair is a
/// reading of the column width. That last one is the measurement a scope is most often pointed at
/// and the one it would be worst to get plausibly wrong.</para>
///
/// <para><b>Absent is null, never NaN.</b> Most of these are undefined on most waveforms — a period
/// on noise, a rise time with no edge, a duty cycle on DC — and
/// <c>System.Text.Json</c> will not write a NaN: it finds out part way through the response, after
/// the 200 and everything before it have gone out, so the body stops mid-object and the browser
/// discards the whole status. A scope carries more optional readings than anything else in this
/// app, which is why this rule is stated on the type that carries them. See
/// <c>ScopeStatusJsonTests</c>.</para>
/// </summary>
/// <param name="MaxV">The largest sample in the window.</param>
/// <param name="MinV">The smallest.</param>
/// <param name="PeakToPeakV">Max less min.</param>
/// <param name="MeanV">
/// The arithmetic mean — the DC component, reported rather than removed. Note that both the QA403
/// and a sound card are AC-coupled, so on a quiet input this is the input's own residual offset
/// rather than the DUT's DC.
/// </param>
/// <param name="RmsV">True RMS, DC included.</param>
/// <param name="AcRmsV">RMS about the mean. The figure to quote for a signal riding on an offset.</param>
/// <param name="CrestFactor">Peak over true RMS; null on digital silence, where it is infinite.</param>
/// <param name="TopV">The settled high level, from a histogram rather than from the maximum — see <see cref="WaveformHistogram"/>. Null where the record has one level.</param>
/// <param name="BaseV">The settled low level.</param>
/// <param name="AmplitudeV">Top less base, which is what an overshoot and the edge thresholds are quoted against.</param>
/// <param name="PeriodS">One period, between successive rising crossings of the middle.</param>
/// <param name="FrequencyHz">Its reciprocal.</param>
/// <param name="PositiveWidthS">How long the waveform spends above the middle in one cycle.</param>
/// <param name="NegativeWidthS">And below it.</param>
/// <param name="DutyPercent">The positive width as a percentage of the period.</param>
/// <param name="RiseTimeS">10 % to 90 % of the first clean rising edge.</param>
/// <param name="FallTimeS">90 % to 10 % of the first clean falling one.</param>
/// <param name="OvershootPercent">How far past the top the record goes, against the amplitude.</param>
/// <param name="PreshootPercent">The same below the base.</param>
public sealed record ScopeSliceMeasurements(
    double MaxV,
    double MinV,
    double PeakToPeakV,
    double MeanV,
    double RmsV,
    double AcRmsV,
    double? CrestFactor,
    double? TopV,
    double? BaseV,
    double? AmplitudeV,
    double? PeriodS,
    double? FrequencyHz,
    double? PositiveWidthS,
    double? NegativeWidthS,
    double? DutyPercent,
    double? RiseTimeS,
    double? FallTimeS,
    double? OvershootPercent,
    double? PreshootPercent)
{
    /// <summary>What an empty window reads: nothing at all, rather than zeros that look measured.</summary>
    public static ScopeSliceMeasurements Empty { get; } =
        new(0, 0, 0, 0, 0, 0, null, null, null, null, null, null, null, null, null, null, null, null, null);

    /// <summary>
    /// Measures one window.
    ///
    /// <para>Three passes over the samples: the levels, the histogram, and the timing. At the
    /// worst case this app allows — one second at 192 kS/s — that is about 600 000 reads of a
    /// contiguous array per channel per fetch, which is microseconds and is paid only when a
    /// client asks for a slice rather than on every captured record.</para>
    /// </summary>
    public static ScopeSliceMeasurements Of(ReadOnlySpan<double> samples, double sampleRateHz)
    {
        if (samples.IsEmpty)
            return Empty;

        var levels = WaveformMeasure.Measure(samples);
        var pair = WaveformHistogram.TopBase(samples);

        // The timing needs two levels to measure between, and a record with only one has no edges
        // to time: everything below it would be a reading of the noise around that level.
        var timing = pair is { } p
            ? WaveformTimingMeasure.Measure(samples, sampleRateHz, p.TopV, p.BaseV)
            : default;

        return new ScopeSliceMeasurements(
            MaxV: levels.MaxV,
            MinV: levels.MinV,
            PeakToPeakV: levels.PeakToPeakV,
            MeanV: levels.MeanV,
            RmsV: levels.RmsV,
            AcRmsV: levels.AcRmsV,
            CrestFactor: Finite(levels.CrestFactor),
            TopV: pair?.TopV,
            BaseV: pair?.BaseV,
            AmplitudeV: pair is { } amp ? amp.TopV - amp.BaseV : null,
            PeriodS: Finite(timing.PeriodS),
            FrequencyHz: Finite(timing.FrequencyHz),
            PositiveWidthS: Finite(timing.PositiveWidthS),
            NegativeWidthS: Finite(timing.NegativeWidthS),
            DutyPercent: Finite(timing.DutyPercent),
            RiseTimeS: Finite(timing.RiseTimeS),
            FallTimeS: Finite(timing.FallTimeS),
            OvershootPercent: Finite(timing.OvershootPercent),
            PreshootPercent: Finite(timing.PreshootPercent));
    }

    /// <summary>
    /// Measures an <b>envelope</b>: the band between a lower and an upper outline.
    ///
    /// <para><b>Three figures, and everything else absent.</b> An envelope is not a waveform, so
    /// only the questions that are about a band have answers: how high it ever reached, how low,
    /// and how far apart those are. A mean, an RMS or a crest factor of an outline is arithmetic
    /// on something that was never a signal; a period or a duty cycle is a reading of the
    /// accumulation rather than of the DUT; and a rise time between two outlines is the jitter and
    /// the edge added together, which is neither. Absent rather than wrong is this screen's rule
    /// everywhere, and this is the place with the most opportunities to break it.</para>
    /// </summary>
    public static ScopeSliceMeasurements OfEnvelope(ReadOnlySpan<double> lower, ReadOnlySpan<double> upper)
    {
        if (lower.IsEmpty || upper.IsEmpty)
            return Empty;

        double low = double.PositiveInfinity;
        double high = double.NegativeInfinity;

        foreach (double v in lower)
            if (v < low) low = v;
        foreach (double v in upper)
            if (v > high) high = v;

        // An outline of non-finite samples cannot happen on a quantised record, but Empty is the
        // right answer rather than a pair of infinities on the wire.
        if (!double.IsFinite(low) || !double.IsFinite(high))
            return Empty;

        return Empty with { MaxV = high, MinV = low, PeakToPeakV = high - low };
    }

    /// <summary>The last gate before the wire: anything that is not a number this app may write becomes absent.</summary>
    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;
}
