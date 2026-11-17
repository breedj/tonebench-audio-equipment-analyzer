using Tonebench.Dsp;

namespace Tonebench.App.Streaming;

/// <summary>
/// One channel of a record, cut to the window the client is looking at and reduced to what will fit
/// on the wire. Either <see cref="Columns"/> or <see cref="Samples"/> is set, never both.
/// </summary>
/// <param name="X0">Time of the first column or sample, relative to the trigger.</param>
/// <param name="Dx">Column width, or the sample period when raw.</param>
/// <param name="Columns">Min/max per column, or null when the window is served raw.</param>
/// <param name="Samples">The samples themselves, or null when the window is decimated.</param>
/// <param name="FirstFrame">Index into the record of the first frame in this slice.</param>
/// <param name="FrameCount">How many frames of the record the slice covers.</param>
/// <param name="Measurements">
/// Every reading of the window, measured on the samples at <b>full resolution</b> whether or not
/// the slice itself was decimated — which is the whole reason they are taken here rather than on
/// the client. Vmax, Vmin and Vpp would survive the decimation, because both extremes of every
/// column are kept; nothing else about the samples does. A mean of column maxima is the mean of an
/// envelope, a period read off a column pair is a reading of the column width, and an RMS over
/// maxima is an RMS of nothing — each of them confidently wrong rather than merely coarse.
/// </param>
public sealed record ScopeFrameSlice(
    double X0,
    double Dx,
    MinMax[]? Columns,
    float[]? Samples,
    int FirstFrame,
    int FrameCount,
    ScopeSliceMeasurements Measurements)
{
    /// <summary>True when the window was tight enough to send the waveform itself.</summary>
    public bool IsRaw => Samples is not null;
}


/// <summary>
/// One channel's window with its fundamental removed — the THD residual, drawn as a second trace.
///
/// <para><b>It shares the slice's own <c>X0</c>, <c>Dx</c> and raw-or-columns shape</b>, and that is
/// why it rides on the slice rather than arriving as a slice of its own: the two traces are drawn
/// against one x array, and a residual on an x array a fraction of a sample different from the
/// waveform it was subtracted from would put the error where the signal is not.</para>
/// </summary>
/// <param name="Columns">Min/max per column, or null when the window is served raw.</param>
/// <param name="Samples">The residual itself, or null when it is decimated.</param>
/// <param name="FitFrequencyHz">The fitted fundamental — refined, so a tone off a bin cancels too.</param>
/// <param name="FitAmplitudeVpk">Its peak amplitude.</param>
/// <param name="FitOffsetV">Its DC term, which is subtracted with it.</param>
/// <param name="ResidualRmsV">RMS of what is left, over the whole window.</param>
/// <param name="ResidualPercent">
/// That against the fundamental's own RMS, which is the figure a THD+N reading is quoted as. Null
/// where the fit found no amplitude to divide by.
/// </param>
public sealed record ScopeResidual(
    MinMax[]? Columns,
    float[]? Samples,
    double FitFrequencyHz,
    double FitAmplitudeVpk,
    double FitOffsetV,
    double ResidualRmsV,
    double? ResidualPercent);

/// <summary>
/// Cuts a <see cref="ScopeRecord"/> down to what a chart needs.
///
/// <para><b>The whole record always goes out as columns</b>, because the zoom bar's overview strip
/// cannot be drawn from anything less — the client has to hold the shape of the entire capture to
/// show where the zoom box sits in it. What it must never be is the raw record: one second of
/// 192 kS/s stereo is 1.5 MB as f32, and the broadcast hub's per-client channel is 64 frames deep,
/// so a wedged tab would buffer tens of megabytes. The answer is not a deeper channel but never
/// putting a megabyte in it.</para>
///
/// <para><b>A tight enough zoom is served raw</b>, which is the floor that makes edge and timing
/// work believable: once a window holds no more samples than there are columns to draw them in,
/// decimating it would be a reading of the decimation rather than of the waveform. The same rule
/// the spectrum follows when a zoom reaches one bin per column.</para>
/// </summary>
public static class ScopeFrameBuilder
{
    /// <summary>
    /// The default column budget, matching the spectrum's. A chart is at most a couple of thousand
    /// pixels wide, and min/max per column means nothing narrower than a column can hide.
    /// </summary>
    public const int DefaultMaxColumns = 1600;

    /// <summary>
    /// Below this many samples per column the window is sent raw instead. Two, so that the step
    /// happens exactly when decimation stops being able to say anything a sample could not.
    /// </summary>
    public const int RawSamplesPerColumn = 2;


    /// <summary>
    /// How many whole periods of the fundamental the fit is taken over, at least. Four, which is
    /// what <c>SpectrumPipeline.ResidualFitCount</c> settled on: below about three the fit starts
    /// eating the harmonics, and from there every window agrees within 0.2 dB.
    /// </summary>
    public const int ResidualFitPeriods = 4;

    /// <summary>The bounds on that window, in samples. See <see cref="Residual"/> for why it is bounded at all.</summary>
    public const int MinResidualFitFrames = 8192;
    public const int MaxResidualFitFrames = 32768;

    /// <summary>
    /// The window with its fundamental removed, on the same geometry as the slice it is given.
    ///
    /// <para><b>The fit is bounded, and it is <i>centred</i> rather than taken from the tail</b> —
    /// which is the one place this differs from the FFT screen's residual and the difference is the
    /// subtraction, not the fit. There the fit is subtracted from a 2048-sample tail, so a window at
    /// the tail is where the error is smallest; here it is subtracted across the <i>whole</i> window,
    /// so the worst case sits at both ends and a centred window halves it. Bounded because
    /// <see cref="SineFit"/> is O(n) per iteration and takes 2.65 s over a 1,048,576-point record,
    /// which is not a thing to put on a fetch that happens several times a second.</para>
    ///
    /// <para><b>Null when there is no fundamental to remove.</b> The seed comes from the window's own
    /// measured frequency, which is absent on noise, on DC and on anything with one settled level —
    /// and a fit seeded with a guess is a sine subtracted from a record that has none, i.e. a
    /// "residual" that is the record plus an invented tone.</para>
    /// </summary>
    public static ScopeResidual? Residual(ScopeRecord record, int channel, ScopeFrameSlice slice, int maxColumns = DefaultMaxColumns)
    {
        if (slice.Measurements.FrequencyHz is not { } seedHz || !(seedHz > 0.0))
            return null;
        if (slice.FrameCount < 4)
            return null;

        var window = record.Channels[channel].AsSpan(slice.FirstFrame, slice.FrameCount);
        double fs = record.SampleRateHz;

        // At least four whole periods of the seed, then the bounds, then the window itself.
        int wanted = (int)Math.Ceiling(ResidualFitPeriods * fs / seedHz);
        int fitLength = Math.Clamp(Math.Max(wanted, MinResidualFitFrames), 1, MaxResidualFitFrames);
        fitLength = Math.Min(fitLength, slice.FrameCount);
        if (fitLength < 4)
            return null;

        int fitStart = (slice.FrameCount - fitLength) / 2;

        SineFitResult fit;
        try
        {
            fit = SineFit.Fit(window.Slice(fitStart, fitLength), fs, seedHz);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        // The fit's phase is referenced to sample 0 of the span it was fitted on, which is
        // window[fitStart]. Re-referencing it to window[0] is what lets one fit be subtracted
        // across the whole window -- the same transform the solver itself does at the end.
        double omega = 2.0 * Math.PI * fit.FreqHz / fs;
        var shifted = fit with { PhaseRad = fit.PhaseRad - (omega * fitStart) };

        var residual = new double[slice.FrameCount];
        double sumSquares = 0.0;
        for (int i = 0; i < residual.Length; i++)
        {
            double r = window[i] - ((shifted.AmplitudeVpk * Math.Cos(omega * i + shifted.PhaseRad)) + shifted.OffsetV);
            residual[i] = r;
            sumSquares += r * r;
        }

        double rms = Math.Sqrt(sumSquares / residual.Length);
        double? percent = fit.Vrms > 0.0 ? rms / fit.Vrms * 100.0 : null;

        // The same shape as the slice, by construction rather than by repeating its own test: two
        // traces on one x array cannot be one raw and the other decimated.
        if (slice.IsRaw)
        {
            var samples = new float[residual.Length];
            for (int i = 0; i < residual.Length; i++)
                samples[i] = (float)residual[i];
            return new ScopeResidual(Columns: null, samples, fit.FreqHz, fit.AmplitudeVpk, fit.OffsetV, rms, percent);
        }

        var columns = new MinMax[slice.Columns!.Length];
        SpectrumDecimator.DecimateLinear(residual, columns);
        return new ScopeResidual(columns, Samples: null, fit.FreqHz, fit.AmplitudeVpk, fit.OffsetV, rms, percent);
    }

    /// <summary>Cuts the whole record, for the overview a chart and the zoom strip both draw.</summary>
    public static ScopeFrameSlice Overview(ScopeRecord record, int channel, int maxColumns = DefaultMaxColumns) =>
        Build(record, channel, 0, record.FrameCount, maxColumns);

    /// <summary>
    /// Cuts the window <paramref name="fromS"/>..<paramref name="toS"/>, measured against the
    /// trigger at t = 0. A window reaching outside the record is clamped to it; one that selects
    /// nothing comes back as the whole record, since an empty chart is never the useful answer.
    /// </summary>
    public static ScopeFrameSlice Window(
        ScopeRecord record, int channel, double fromS, double toS, int maxColumns = DefaultMaxColumns)
    {
        if (FrameRange(record, fromS, toS) is not { } range)
            return Overview(record, channel, maxColumns);

        return Build(record, channel, range.First, range.Count, maxColumns);
    }

    /// <summary>
    /// The readings of one window and nothing else — no columns, no samples. What a measurement
    /// gated to a cursor pair is.
    ///
    /// <para><b>A window that selects nothing answers null, where <see cref="Window"/> answers with
    /// the whole record.</b> That difference is the point of this being its own method rather than a
    /// flag on that one: a picture is always better drawn than blank, so falling back to the whole
    /// record is the right answer for a slice — and it is the <i>wrong</i> answer for a gate, where
    /// it would quote the whole record's figures under a heading saying they are the cursors'. A
    /// reading nothing was measured for is absent, like every other reading here.</para>
    /// </summary>
    public static ScopeSliceMeasurements? Measure(ScopeRecord record, int channel, double fromS, double toS)
    {
        if (FrameRange(record, fromS, toS) is not { } range)
            return null;

        return ScopeSliceMeasurements.Of(
            record.Channels[channel].AsSpan(range.First, range.Count), record.SampleRateHz);
    }

    /// <summary>
    /// The frames a window names, clamped to the record, or null when it names none.
    ///
    /// <para>Shared by the slice and the gate so the two cannot disagree about which samples a pair
    /// of times selects — a gated reading of a window half a sample different from the one drawn is
    /// exactly the kind of plausible disagreement nothing on screen would flag.</para>
    /// </summary>
    private static (int First, int Count)? FrameRange(ScopeRecord record, double fromS, double toS)
    {
        if (!double.IsFinite(fromS) || !double.IsFinite(toS) || toS <= fromS || record.FrameCount <= 0)
            return null;

        int first = FrameAt(record, fromS, roundUp: false);
        int last = FrameAt(record, toS, roundUp: true);

        // Outside the record altogether, in either direction: clamping would silently measure the
        // first or last sample instead of saying the window holds nothing.
        if (last <= 0 || first >= record.FrameCount)
            return null;

        first = Math.Clamp(first, 0, record.FrameCount - 1);
        last = Math.Clamp(last, first + 1, record.FrameCount);
        return (first, last - first);
    }

    private static ScopeFrameSlice Build(ScopeRecord record, int channel, int firstFrame, int frameCount, int maxColumns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColumns, 1);

        var source = record.Channels[channel].AsSpan(firstFrame, frameCount);
        double samplePeriod = 1.0 / record.SampleRateHz;
        double x0 = record.TimeAt(firstFrame);

        var measurements = ScopeSliceMeasurements.Of(source, record.SampleRateHz);

        if (frameCount <= maxColumns * RawSamplesPerColumn)
        {
            var samples = new float[frameCount];
            for (int i = 0; i < frameCount; i++)
                samples[i] = (float)source[i];

            return new ScopeFrameSlice(x0, samplePeriod, Columns: null, samples, firstFrame, frameCount, measurements);
        }

        int columnCount = Math.Min(maxColumns, frameCount);
        var columns = new MinMax[columnCount];
        SpectrumDecimator.DecimateLinear(source, columns);

        // The column's own width, so column i sits at x0 + i*dx. Measured from the centre of the
        // first column to the centre of the last would be a different convention and a different
        // axis; this one matches what the encoder documents.
        double dx = frameCount * samplePeriod / columnCount;
        return new ScopeFrameSlice(x0, dx, columns, Samples: null, firstFrame, frameCount, measurements);
    }

    /// <summary>The frame index a time relative to the trigger falls on.</summary>
    private static int FrameAt(ScopeRecord record, double seconds, bool roundUp)
    {
        double exact = (record.TriggerPosition + (seconds * record.SampleRateHz)) - record.FirstIndex;
        return (int)(roundUp ? Math.Ceiling(exact) : Math.Floor(exact));
    }
}
