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
        if (!double.IsFinite(fromS) || !double.IsFinite(toS) || toS <= fromS)
            return Overview(record, channel, maxColumns);

        int first = FrameAt(record, fromS, roundUp: false);
        int last = FrameAt(record, toS, roundUp: true);

        first = Math.Clamp(first, 0, Math.Max(0, record.FrameCount - 1));
        last = Math.Clamp(last, first + 1, record.FrameCount);

        return Build(record, channel, first, last - first, maxColumns);
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
