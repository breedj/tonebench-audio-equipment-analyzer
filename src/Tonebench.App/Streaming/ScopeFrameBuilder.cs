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
/// The math channel over one window — a waveform derived from the two captured ones, on the
/// slice's own geometry.
///
/// <para><b>It rides on the slice for the residual's reason</b>: the two traces are drawn against
/// one x array, so the math trace is raw exactly where the waveform is raw and has the same column
/// count where it is not. What it carries beyond the picture is the whole reading set — Vpp of
/// A − B is precisely what a differential probe is for — and the <b>unit</b> those readings are in,
/// which for everything but the additive operations is not volts.</para>
/// </summary>
/// <param name="Operation">What was computed.</param>
/// <param name="Unit">What its numbers are in. See <see cref="WaveformMathUnit"/>.</param>
/// <param name="Columns">Min/max per column, or null when the window is served raw.</param>
/// <param name="Samples">The derived waveform itself, or null when it is decimated.</param>
/// <param name="Measurements">
/// Every reading of it, measured on the derived samples at full resolution — not on the columns,
/// and not combined from the two channels' own readings, which would be a different quantity
/// altogether (the difference of two maxima is not the maximum of the difference).
/// </param>
/// <param name="ClampedSamples">
/// How many samples <see cref="WaveformOperation.ADivB"/> railed on. Zero for every other
/// operation, and reported rather than hidden: a ratio trace that spends most of its time at the
/// limit is one whose reader has to be told.
/// </param>
public sealed record ScopeMath(
    WaveformOperation Operation,
    WaveformMathUnit Unit,
    MinMax[]? Columns,
    float[]? Samples,
    ScopeSliceMeasurements Measurements,
    int ClampedSamples);

/// <summary>
/// The X–Y figure — one channel against the other, with time thrown away. A goniometer, a
/// vectorscope, a Lissajous figure; the same picture under three names.
///
/// <para><b>It is pairs of samples, and it cannot be anything else.</b> Every other slice this
/// builder cuts may be summarised as a min and a max per column, because a waveform is a function
/// of time and a column is a stretch of it. An X–Y figure is not a function of anything: what it
/// draws is which <i>pairs</i> (A[i], B[i]) occurred, and a min/max per column throws the pairing
/// away — four numbers that say two channels each reached these extremes somewhere in this
/// stretch, from which no figure can be recovered. So this carries the samples themselves, and the
/// cap below is what keeps that affordable.</para>
/// </summary>
/// <param name="X">Channel A's samples — the horizontal axis.</param>
/// <param name="Y">Channel B's — the vertical one.</param>
/// <param name="FirstFrame">Index into the record of the first pair.</param>
/// <param name="FrameCount">How many pairs are here.</param>
/// <param name="WindowFrameCount">How many the window held, which is more when it was truncated.</param>
/// <param name="Correlation">Pearson's r over the pairs drawn. The stereo meter's own reading.</param>
/// <param name="PhaseDeg">How far B lags A, signed, or null where no single number is the figure's phase.</param>
/// <param name="AmplitudeRatio">B's AC RMS over A's; 1 for a balanced pair.</param>
public sealed record ScopeXy(
    float[] X,
    float[] Y,
    int FirstFrame,
    int FrameCount,
    int WindowFrameCount,
    double Correlation,
    double? PhaseDeg,
    double? AmplitudeRatio)
{
    /// <summary>The window held more pairs than the cap, so this is the first stretch of it.</summary>
    public bool Truncated => FrameCount < WindowFrameCount;
}

/// <summary>
/// The record folded on its own period: every cycle of the waveform drawn on top of every other.
///
/// <para><b>It is traces of samples, and like an X–Y figure it cannot be columns.</b> What this
/// picture shows is how much one cycle differs from the next, and a min/max per column has already
/// collapsed the folds into one band — which is the very thing being measured. So this carries the
/// folds themselves, and the two caps below are what keep that affordable.</para>
///
/// <para><b>The fold is the signal’s own measured period, not a symbol clock.</b> There is no
/// clock here to recover: a comms eye folds on the symbol rate and reads the opening a receiver has
/// to decide in, where this folds on the frequency the slice measured and reads repeatability. A
/// clean tone folds into one line; amplitude modulation, rail sag under a burst, a drifting
/// generator or a noisy supply spread it into a band, and a crossing that wanders spreads it
/// sideways.</para>
/// </summary>
/// <param name="Folds">One array per cycle drawn, all of <see cref="FoldFrameCount"/> samples.</param>
/// <param name="FoldFrameCount">Samples in one fold — the period, rounded to a whole sample.</param>
/// <param name="FoldSeconds">That fold in seconds, which is the picture’s own x span.</param>
/// <param name="PeriodHz">The frequency it was folded on, from the slice’s own measurement.</param>
/// <param name="WindowFoldCount">How many whole folds the window held, which is more when truncated.</param>
/// <param name="MaxSpreadV">The widest the band gets anywhere across the fold, peak to peak.</param>
/// <param name="SpreadAtS">Where in the fold that worst spread sits — at the peaks it is amplitude, at a crossing it is jitter.</param>
/// <param name="MeanSpreadV">The band’s average width over the fold.</param>
/// <param name="JitterS">How much the folds’ own zero crossings move, as a standard deviation, or null where too few cross.</param>
public sealed record ScopeEye(
    float[][] Folds,
    int FoldFrameCount,
    double FoldSeconds,
    double PeriodHz,
    int WindowFoldCount,
    double MaxSpreadV,
    double SpreadAtS,
    double MeanSpreadV,
    double? JitterS)
{
    /// <summary>The window held more cycles than the cap, so these are the first of them.</summary>
    public bool Truncated => Folds.Length < WindowFoldCount;
}

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

    /// <summary>
    /// The math channel on the geometry of <paramref name="slice"/>.
    ///
    /// <para><b>Computed from the record's samples, never from the slice's columns</b>, and that is
    /// arithmetic rather than tidiness: a column carries the min and the max of what it covers, and
    /// the difference of two envelopes is not the envelope of a difference — the two extremes need
    /// not even fall at the same sample. On a differential pair, which is what A − B is for, the two
    /// channels are nearly equal and the small difference between them is the entire reading, so an
    /// envelope subtraction would draw zero where the signal is.</para>
    ///
    /// <para><b>Null when the operation has no channel to read.</b> A measurement that captures one
    /// input has no second waveform to combine with, and an absent trace with a reason beside it is
    /// the honest answer — computing against an array of zeros would draw A itself and label it
    /// A − B.</para>
    /// </summary>
    public static ScopeMath? MathTrace(
        ScopeRecord record, ScopeFrameSlice slice, WaveformOperation operation, int maxColumns = DefaultMaxColumns)
    {
        var (values, clamped) = Combine(record, operation, slice.FirstFrame, slice.FrameCount);
        if (values is null)
            return null;

        var measurements = ScopeSliceMeasurements.Of(values, record.SampleRateHz);
        var unit = WaveformMath.UnitOf(operation);

        // The same shape as the slice by construction rather than by repeating its own test: two
        // traces on one x array cannot be one raw and the other decimated.
        if (slice.IsRaw)
        {
            var samples = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
                samples[i] = (float)values[i];
            return new ScopeMath(operation, unit, Columns: null, samples, measurements, clamped);
        }

        var columns = new MinMax[slice.Columns!.Length];
        SpectrumDecimator.DecimateLinear(values, columns);
        return new ScopeMath(operation, unit, columns, Samples: null, measurements, clamped);
    }

    /// <summary>
    /// The math channel's readings over a gate and nothing else — <see cref="Measure"/>'s
    /// counterpart, and absent on a window that names nothing for the same reason.
    /// </summary>
    public static ScopeSliceMeasurements? MathGate(
        ScopeRecord record, WaveformOperation operation, double fromS, double toS)
    {
        if (FrameRange(record, fromS, toS) is not { } range)
            return null;

        var (values, _) = Combine(record, operation, range.First, range.Count);
        if (values is null)
            return null;

        return ScopeSliceMeasurements.Of(values, record.SampleRateHz);
    }

    /// <summary>
    /// Runs the operation over one range of the record. Shared by the slice and the gate so the two
    /// cannot disagree about what a math trace is — a gated figure computed a second way is the
    /// plausible disagreement nothing on screen would flag.
    /// </summary>
    private static (double[]? Values, int Clamped) Combine(
        ScopeRecord record, WaveformOperation operation, int firstFrame, int frameCount)
    {
        if (frameCount <= 0)
            return (null, 0);
        // Refused on an envelope: combining two upper outlines is arithmetic on two things that
        // were never signals, and A - B of them would read as a differential measurement.
        if (record.IsEnvelope)
            return (null, 0);
        if (WaveformMath.NeedsA(operation) && record.ChannelCount < 1)
            return (null, 0);
        if (WaveformMath.NeedsB(operation) && record.ChannelCount < 2)
            return (null, 0);

        var a = WaveformMath.NeedsA(operation)
            ? record.Channels[0].AsSpan(firstFrame, frameCount)
            : ReadOnlySpan<double>.Empty;
        var b = WaveformMath.NeedsB(operation)
            ? record.Channels[1].AsSpan(firstFrame, frameCount)
            : ReadOnlySpan<double>.Empty;

        var values = new double[frameCount];
        int clamped = WaveformMath.Apply(operation, a, b, record.SampleRateHz, values);
        return (values, clamped);
    }

    /// <summary>
    /// How many pairs of samples an X–Y figure is drawn from, at most.
    ///
    /// <para>8192, which is 43 ms at 192 kS/s and 171 ms at 48 — dozens of periods of anything in
    /// the audio band, and 64 kB on the wire. The cap exists because the alternative at the top of
    /// this app's range is a megabyte and a half: the rule the frame builder is built around.</para>
    /// </summary>
    public const int MaxXyPoints = 8192;

    /// <summary>
    /// The X–Y figure over the window <paramref name="slice"/> names.
    ///
    /// <para><b>Taken from the record's own samples, never from the slice's columns</b>, for the
    /// reason <see cref="ScopeXy"/> gives: a min/max pair per column throws away which value of A
    /// went with which value of B, which is the whole of what this picture is.</para>
    ///
    /// <para><b>A contiguous run from the start of the window, not every n-th pair.</b> Striding is
    /// the obvious way to fit a long window under the cap and it is the one that produces a
    /// plausible wrong picture: a stride near a whole number of periods collapses a figure onto a
    /// handful of points, so an ellipse becomes a line and a reader concludes the channels are in
    /// phase. A contiguous run is always a true figure of a real stretch of the record, and the
    /// reader can narrow the window to move it. <see cref="ScopeXy.Truncated"/> says when the
    /// window held more, and the readings are of the stretch drawn rather than of the window, so
    /// the figures under the picture are figures of the picture.</para>
    ///
    /// <para>Null on a record with one channel — there is no second axis to plot against, and a
    /// figure against an array of zeros would be a horizontal line that looks like a measurement.</para>
    /// </summary>
    public static ScopeXy? XyFigure(ScopeRecord record, ScopeFrameSlice slice, int maxPoints = MaxXyPoints)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 1);

        // Refused on an envelope: pairing the upper outlines of two channels is not pairing two
        // signals, and the figure it would draw is a shape nothing was ever at.
        if (record.ChannelCount < 2 || slice.FrameCount <= 0 || record.IsEnvelope)
            return null;

        int count = Math.Min(slice.FrameCount, maxPoints);
        var a = record.Channels[0].AsSpan(slice.FirstFrame, count);
        var b = record.Channels[1].AsSpan(slice.FirstFrame, count);

        var x = new float[count];
        var y = new float[count];
        for (int i = 0; i < count; i++)
        {
            x[i] = (float)a[i];
            y[i] = (float)b[i];
        }

        // Measured on the same stretch that is drawn, and with each channel's own frequency from
        // the same measurement set the panel shows -- so a phase quoted here is a phase of this
        // figure rather than of some other window of the record.
        var readings = ChannelCorrelation.Measure(
            a,
            b,
            record.SampleRateHz,
            ScopeSliceMeasurements.Of(a, record.SampleRateHz).FrequencyHz,
            ScopeSliceMeasurements.Of(b, record.SampleRateHz).FrequencyHz);

        return new ScopeXy(
            x,
            y,
            slice.FirstFrame,
            count,
            slice.FrameCount,
            readings.Correlation,
            readings.PhaseDeg,
            readings.AmplitudeRatio);
    }

    /// <summary>
    /// How many folds an eye diagram overlays, at most.
    ///
    /// <para>256, which at 1 kHz is a quarter of a second of signal and at 20 kHz is 13 ms — far
    /// more cycles than a reader can tell apart in the picture, and enough that an intermittent one
    /// in a hundred shows. The cap is on the wire as much as on the drawing: 256 folds of a 1 kHz
    /// period at 192 kS/s is 192 samples each, 196 kB, which is already the largest thing this
    /// builder sends.</para>
    /// </summary>
    public const int MaxEyeFolds = 256;

    /// <summary>
    /// How many samples one fold may carry, at most.
    ///
    /// <para>2048 is 10.7 ms at 192 kS/s, i.e. every period down to 94 Hz drawn sample for sample.
    /// Below that the fold is <b>strided</b> rather than refused — see the remarks on the method —
    /// which is the one place in this builder where striding is the right answer.</para>
    /// </summary>
    public const int MaxEyeFoldFrames = 2048;

    /// <summary>
    /// The eye diagram over the window <paramref name="slice"/> names, folded on the frequency that
    /// slice measured.
    ///
    /// <para><b>Null rather than a guess where there is no measured frequency</b>, where the window
    /// holds fewer than two whole periods, and where the period is not a usable fold: a picture
    /// folded on an invented period is a band of noise that looks exactly like a jittery signal,
    /// which is the plausible wrong answer this whole file is written against. The panel says which
    /// of those it is rather than drawing nothing silently.</para>
    ///
    /// <para><b>A contiguous run of folds from the start of the window</b>, like the X–Y figure
    /// and for the same reason: striding <i>folds</i> would skip cycles, and an intermittent fault
    /// one cycle in three is exactly what someone turns this picture on to find.</para>
    ///
    /// <para><b>Within a fold, a long period is strided</b>, which is the opposite of the rule
    /// everywhere else here — and right, because the x axis of this picture is one period rather
    /// than a stretch of the record: a min/max per column would collapse the folds into the band
    /// the picture exists to resolve, so there is no decimation available that keeps what is being
    /// measured. The stride is a whole number and the <i>same</i> for every fold, so the folds stay
    /// aligned with each other; what it costs is the detail between its samples, and the readings
    /// are taken on the strided folds so the figures are figures of the picture.</para>
    /// </summary>
    public static ScopeEye? EyeFigure(
        ScopeRecord record,
        int channel,
        ScopeFrameSlice slice,
        int maxFolds = MaxEyeFolds,
        int maxFoldFrames = MaxEyeFoldFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFolds, WaveformEye.MinFolds);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFoldFrames, 2);

        // An envelope has no measured frequency (its readings are extremes only), so this is
        // already refused -- but stated here as well, since what makes it wrong is the outlines
        // rather than the missing figure.
        if (record.IsEnvelope || slice.FrameCount <= 0 || slice.Measurements.FrequencyHz is not { } seedHz || !(seedHz > 0.0))
            return null;

        // Refined by a sine fit, seeded from the crossing estimate -- and this is the difference
        // between a feature that works and one that cannot.
        //
        // <para>A fold is a whole number of samples, so the period has to be right to within
        // roughly half a sample over the whole overlay or successive cycles walk sideways and a
        // clean tone draws as a band. The crossing estimate is not that good: measured against the
        // simulator's snapped 1000.49 Hz tone it read 996.09 Hz, 0.44 % low, which rounds the fold
        // to 193 samples where the period is 191.9 -- one sample of walk per cycle, nineteen
        // samples over the window, and a 0.10 V "spread" on a 0.71 V peak signal that is entirely
        // this and nothing to do with the DUT. The fit reads the same tone to about a part in
        // 100,000, which rounds to the right fold and folds it to one line.</para>
        //
        // <para>The same bounded fit the residual uses, and bounded for its reason: SineFit is O(n)
        // per iteration and 2.65 s over a 1,048,576-point record, which is not a thing to put on a
        // fetch that happens several times a second. A fit that will not converge falls back to the
        // seed rather than refusing -- a picture folded a little loosely is still a picture, where
        // none at all is not.</para>
        double frequencyHz = RefineFoldFrequency(record, channel, slice, seedHz);

        int foldFrames = WaveformEye.FoldFrames(1.0 / frequencyHz, record.SampleRateHz);
        if (foldFrames == 0)
            return null;

        int windowFolds = WaveformEye.FoldCount(slice.FrameCount, foldFrames);
        if (windowFolds == 0)
            return null;

        int folds = Math.Min(windowFolds, maxFolds);
        int stride = Math.Max(1, (foldFrames + maxFoldFrames - 1) / maxFoldFrames);
        int drawn = (foldFrames + stride - 1) / stride;

        // Each fold placed at its EXACT fractional period and read between samples, not at a whole
        // number of them -- and this is what makes the picture mean what it says.
        //
        // <para>A period is almost never a whole number of samples: 1000.49 Hz at 48 kS/s is
        // 47.9765. Starting fold k at k whole folds leaves every cycle 0.0235 samples further
        // along than the last, which ACCUMULATES -- 0.45 samples by the twentieth fold and twelve
        // by the two hundred and fifty sixth. At the steepest part of a sine that is 6 % of the
        // amplitude at twenty folds and the whole of it at the cap, so a signal that repeats
        // perfectly would draw as a band that grows with the length of the overlay. Measured: a
        // 1000.49 Hz tone over twenty folds read a 0.0585 V spread on a 1.0 V amplitude, which is
        // exactly omega times the walk and nothing to do with any DUT.</para>
        //
        // <para>Linear between the two samples either side, which is affordable precisely because
        // the offset is under one sample: for a band-limited signal the error goes as
        // (omega*delta)^2/8, i.e. 0.05 % of amplitude at 48 samples a period. It is the same trade
        // the waveform view's Lines drawing makes, and the same place it stops being free -- near
        // Nyquist a straight line between samples is not the signal, which is what sin(x)/x
        // drawing exists for and what makes a 2-sample-a-period eye a coarse picture whatever is
        // done here.</para>
        double periodSamples = record.SampleRateHz / frequencyHz;
        double step = periodSamples / drawn;
        int available = slice.FirstFrame + slice.FrameCount;

        var source = record.Channels[channel];
        var samples = new double[folds][];
        var wire = new float[folds][];
        for (int f = 0; f < folds; f++)
        {
            samples[f] = new double[drawn];
            wire[f] = new float[drawn];
            double start = slice.FirstFrame + (f * periodSamples);
            for (int i = 0; i < drawn; i++)
            {
                double at = start + (i * step);
                int lower = (int)Math.Floor(at);
                double fraction = at - lower;
                // The last fold can reach one sample past the window's end by a rounding, where
                // holding the final sample is right and reading past the record is not.
                int upper = Math.Min(lower + 1, available - 1);
                lower = Math.Clamp(lower, slice.FirstFrame, available - 1);
                upper = Math.Clamp(upper, slice.FirstFrame, available - 1);

                double v = lower == upper
                    ? source[lower]
                    : source[lower] + ((source[upper] - source[lower]) * fraction);
                samples[f][i] = v;
                wire[f][i] = (float)v;
            }
        }

        // Measured on the folds that are drawn, strided and all, so the figures under the picture
        // are figures of the picture -- the X-Y figure's own rule.
        // The exact period rather than the rounded sample count, since that is what the folds are
        // placed on -- an x axis a fraction of a sample longer than the picture would put the
        // cursor readout and the spread's own phase slightly off.
        double foldSeconds = 1.0 / frequencyHz;
        // The readings are taken on the folds that are DRAWN, so the figures are figures of the
        // picture -- the X-Y figure's own rule. Their time base is one drawn point rather than one
        // sample, since the points are spaced `step` samples apart.
        double drawnRateHz = record.SampleRateHz / step;
        var spread = WaveformEye.Measure(samples, drawnRateHz) ?? new EyeSpread(0.0, 0.0, 0.0);

        return new ScopeEye(
            wire,
            drawn,
            foldSeconds,
            frequencyHz,
            windowFolds,
            spread.MaxSpreadV,
            spread.AtTimeS,
            spread.MeanSpreadV,
            // Against the folds' own mean rather than against zero: an offset waveform has no zero
            // crossing at all, and this hardware's captures carry the front end's own offset.
            WaveformEye.CrossingJitterS(samples, drawnRateHz, slice.Measurements.MeanV));
    }

    /// <summary>
    /// The frequency an eye is folded on: the window's own measured one, refined by a sine fit.
    ///
    /// <para>Shares <see cref="Residual"/>'s bounds — at least <see cref="ResidualFitPeriods"/>
    /// whole periods of the seed, clamped to
    /// <see cref="MinResidualFitFrames"/>..<see cref="MaxResidualFitFrames"/> and to the window —
    /// and its centred placement, so neither pays for the other's choices. Falls back to the seed
    /// wherever the fit cannot run or comes back implausible: a <b>tenth of an octave</b> either
    /// side is the bound, because a fit that has locked onto a harmonic or onto noise is worse than
    /// the estimate it was seeded from, while a genuine refinement is a fraction of a per cent.</para>
    /// </summary>
    private static double RefineFoldFrequency(ScopeRecord record, int channel, ScopeFrameSlice slice, double seedHz)
    {
        double fs = record.SampleRateHz;
        int wanted = (int)Math.Ceiling(ResidualFitPeriods * fs / seedHz);
        int fitLength = Math.Clamp(Math.Max(wanted, MinResidualFitFrames), 1, MaxResidualFitFrames);
        fitLength = Math.Min(fitLength, slice.FrameCount);
        if (fitLength < 4)
            return seedHz;

        int fitStart = slice.FirstFrame + ((slice.FrameCount - fitLength) / 2);

        try
        {
            var fit = SineFit.Fit(record.Channels[channel].AsSpan(fitStart, fitLength), fs, seedHz);
            double ratio = fit.FreqHz / seedHz;
            return fit.FreqHz > 0.0 && ratio > 0.933 && ratio < 1.072 ? fit.FreqHz : seedHz;
        }
        catch (ArgumentOutOfRangeException)
        {
            return seedHz;
        }
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

        // An envelope's gated figures are the band's own extremes over that stretch, the same three
        // the slice reads -- measuring the upper outline on its own would report the band's top as
        // both the maximum and the minimum.
        if (record.Lower is { } lower)
        {
            return ScopeSliceMeasurements.OfEnvelope(
                lower[channel].AsSpan(range.First, range.Count),
                record.Channels[channel].AsSpan(range.First, range.Count));
        }

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

        // An envelope is two outlines rather than a waveform, so it is drawn as the band it is and
        // its readings are the band's own extremes. Always columns, never raw samples: the pair of
        // outlines IS the min/max per column the chart draws, and a raw slice has nowhere to put
        // the second one -- while a single outline served as a waveform would be read as one.
        if (record.Lower is { } lower)
        {
            var low = lower[channel].AsSpan(firstFrame, frameCount);
            int envelopeColumns = Math.Min(maxColumns, frameCount);
            var band = new MinMax[envelopeColumns];
            ReduceEnvelope(low, source, band);

            return new ScopeFrameSlice(
                x0,
                frameCount * samplePeriod / envelopeColumns,
                band,
                Samples: null,
                firstFrame,
                frameCount,
                ScopeSliceMeasurements.OfEnvelope(low, source));
        }

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

    /// <summary>
    /// Reduces a pair of outlines onto <paramref name="columns"/>: the lowest the lower one reaches
    /// in each column, and the highest the upper one does.
    ///
    /// <para>Its own loop rather than two passes of <c>SpectrumDecimator.DecimateLinear</c>, and
    /// the reason is which extreme comes from which array: a column's minimum must come from the
    /// <b>lower</b> outline and its maximum from the <b>upper</b> one. Decimating each on its own
    /// and taking one half of each answer would work, and decimating the upper outline alone --
    /// the obvious shortcut, since it is what sits in <c>Channels</c> -- would draw the band's top
    /// edge twice and lose the bottom one entirely.</para>
    /// </summary>
    private static void ReduceEnvelope(ReadOnlySpan<double> lower, ReadOnlySpan<double> upper, Span<MinMax> columns)
    {
        int count = columns.Length;
        int frames = Math.Min(lower.Length, upper.Length);

        for (int c = 0; c < count; c++)
        {
            // The same column bounds DecimateLinear uses, so an envelope and a waveform of the
            // same record land on the same x positions.
            int from = (int)((long)c * frames / count);
            int to = (int)((long)(c + 1) * frames / count);
            if (to <= from)
                to = Math.Min(frames, from + 1);

            double low = double.PositiveInfinity;
            double high = double.NegativeInfinity;
            for (int i = from; i < to; i++)
            {
                if (lower[i] < low) low = lower[i];
                if (upper[i] > high) high = upper[i];
            }

            columns[c] = double.IsFinite(low) && double.IsFinite(high) ? new MinMax(low, high) : new MinMax(0.0, 0.0);
        }
    }

    /// <summary>The frame index a time relative to the trigger falls on.</summary>
    private static int FrameAt(ScopeRecord record, double seconds, bool roundUp)
    {
        double exact = (record.TriggerPosition + (seconds * record.SampleRateHz)) - record.FirstIndex;
        return (int)(roundUp ? Math.Ceiling(exact) : Math.Floor(exact));
    }
}
