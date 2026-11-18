namespace Tonebench.Dsp;

/// <summary>
/// What an oscilloscope does with the records after the first one.
/// </summary>
public enum RecordAccumulation
{
    /// <summary>Each record replaces the one before it. What a scope does by default.</summary>
    Normal,

    /// <summary>
    /// The mean of the last N records, sample position by sample position.
    ///
    /// <para><b>It needs a stable trigger and it is not a filter.</b> Averaging records that are
    /// aligned on the same point of the same repeating signal divides the uncorrelated part by
    /// sqrt(N) and leaves the signal alone — 20 dB of noise floor for 100 records. Averaging records
    /// that are <i>not</i> aligned low-passes the signal itself, so a jittery trigger buys a
    /// smaller, rounder waveform and calls it quieter. Nothing here can tell those apart, which is
    /// why the manual says to get the trigger standing still first.</para>
    /// </summary>
    Average,

    /// <summary>
    /// The lowest and highest each sample position has reached over the last N records.
    ///
    /// <para>The opposite question to <see cref="Average"/>: not "what does this signal look like
    /// with the noise taken out" but "how much does it move". Jitter, drift, modulation and a fault
    /// that happens once in a hundred cycles all show as a band, and a signal that repeats exactly
    /// shows as a line. It is not a waveform, which is why the readings taken from one are its own
    /// extremes and nothing else.</para>
    /// </summary>
    Envelope,
}

/// <summary>
/// Accumulates successive records of the same shape, sample position by sample position.
///
/// <para><b>Position by position, which is the whole of what makes it an accumulation rather than a
/// filter.</b> Sample i of every record is the same point on the waveform — as long as the trigger
/// put them there — so combining across records at fixed i leaves the signal untouched and works on
/// whatever is different between them. That is also the one assumption it cannot check: see
/// <see cref="RecordAccumulation.Average"/>.</para>
///
/// <para>In <c>Tonebench.Dsp</c>, pure and unit-tested, because every figure it changes is one a
/// reader acts on — an averaged noise floor is quoted, and an envelope's width is read as jitter.</para>
/// </summary>
public sealed class RecordAccumulator
{
    /// <summary>Fewer than two records is not an accumulation of anything.</summary>
    public const int MinCount = 2;

    /// <summary>
    /// The most records that may be combined.
    ///
    /// <para>256, which is 24 dB of noise floor under averaging — past the analyzer's own
    /// resolution on anything it can capture — and about four seconds of 20 ms records on the
    /// clock. The ceiling is on the <i>wait</i> rather than on the arithmetic: at the one-second
    /// record this app allows, 256 of them is four minutes before the trace is finished, and a
    /// display that takes four minutes to settle is one nobody will believe.</para>
    /// </summary>
    public const int MaxCount = 256;

    private readonly int _count;
    private double[][]? _sum;
    private double[][]? _low;
    private double[][]? _high;

    /// <summary>A ring of the last <see cref="Count"/> records, so the mean is of those and not of every record ever.</summary>
    private double[][][]? _history;
    private int _next;

    /// <param name="mode">What to do with each record after the first.</param>
    /// <param name="count">How many to combine, clamped to <see cref="MinCount"/>..<see cref="MaxCount"/>.</param>
    public RecordAccumulator(RecordAccumulation mode, int count)
    {
        Mode = mode;
        _count = Math.Clamp(count, MinCount, MaxCount);
    }

    public RecordAccumulation Mode { get; }

    /// <summary>How many records are being combined, once enough have arrived.</summary>
    public int Count => _count;

    /// <summary>How many have gone in so far, which is what a screen says while it fills.</summary>
    public int Taken { get; private set; }

    /// <summary>True once <see cref="Taken"/> has reached <see cref="Count"/>.</summary>
    public bool Complete => Taken >= _count;

    /// <summary>
    /// Starts again. Called whenever the thing being accumulated stops being the same thing: a
    /// retune, a mode change, a cleared screen, a break in the stream.
    /// </summary>
    public void Reset()
    {
        _sum = null;
        _low = null;
        _high = null;
        _history = null;
        _next = 0;
        Taken = 0;
    }

    /// <summary>
    /// Folds one record in and returns what should be drawn: the accumulated channels, and for
    /// <see cref="RecordAccumulation.Envelope"/> the lower outline beside them.
    ///
    /// <para><b>A record of a different shape starts again rather than being combined</b> — a
    /// different length, a different channel count, or a rate change under it all mean position i
    /// is no longer the same point on the waveform. Silently combining them would produce a trace
    /// that is a average of two different measurements, which is the plausible wrong answer this
    /// whole file exists to avoid.</para>
    ///
    /// <para><b>Under <see cref="RecordAccumulation.Normal"/> the record is handed straight back</b>,
    /// so the ordinary case allocates nothing and costs nothing.</para>
    /// </summary>
    public (double[][] Channels, double[][]? Lower) Fold(double[][] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        if (Mode == RecordAccumulation.Normal || channels.Length == 0 || channels[0].Length == 0)
        {
            Taken = 1;
            return (channels, null);
        }

        if (!SameShape(channels))
            Reset();

        return Mode == RecordAccumulation.Average ? FoldAverage(channels) : FoldEnvelope(channels);
    }

    private bool SameShape(double[][] channels)
    {
        var reference = _sum ?? _high;
        if (reference is null)
            return true;
        if (reference.Length != channels.Length)
            return false;
        for (int c = 0; c < channels.Length; c++)
            if (reference[c].Length != channels[c].Length)
                return false;
        return true;
    }

    /// <summary>
    /// The mean of the last <see cref="Count"/> records.
    ///
    /// <para><b>A ring of the records themselves, not a running sum</b>, and that is a deliberate
    /// 2 MB-a-record cost rather than a few kilobytes. A running sum with the oldest subtracted
    /// needs the oldest, so it needs the ring anyway; and an <i>exponential</i> average, which
    /// needs neither, is a different thing wearing the same name — it never settles, it weights the
    /// newest record most, and the sqrt(N) figure a reader quotes off an averaged noise floor does
    /// not hold for it. A plain mean of a known number of records is the only one whose answer can
    /// be stated.</para>
    ///
    /// <para>While it is filling, the mean is of what has arrived: the trace quietens as it goes
    /// rather than appearing all at once after N records.</para>
    /// </summary>
    private (double[][], double[][]?) FoldAverage(double[][] channels)
    {
        int frames = channels[0].Length;
        _history ??= new double[_count][][];

        var copy = new double[channels.Length][];
        for (int c = 0; c < channels.Length; c++)
            copy[c] = (double[])channels[c].Clone();

        _history[_next] = copy;
        _next = (_next + 1) % _count;
        Taken = Math.Min(_count, Taken + 1);

        var mean = new double[channels.Length][];
        for (int c = 0; c < channels.Length; c++)
            mean[c] = new double[frames];

        int folded = 0;
        for (int h = 0; h < _count; h++)
        {
            var record = _history[h];
            if (record is null)
                continue;

            folded++;
            for (int c = 0; c < channels.Length; c++)
            {
                var into = mean[c];
                var from = record[c];
                for (int i = 0; i < frames; i++)
                    into[i] += from[i];
            }
        }

        if (folded > 1)
        {
            double scale = 1.0 / folded;
            foreach (var channel in mean)
                for (int i = 0; i < channel.Length; i++)
                    channel[i] *= scale;
        }

        // The sum array is the shape reference the next fold is checked against.
        _sum = mean;
        return (mean, null);
    }

    /// <summary>
    /// The lowest and highest each position has reached.
    ///
    /// <para><b>Over every record since the reset, not over a ring of the last N.</b> An envelope
    /// is read as "how far does this ever move", and a bound that quietly forgot the worst case
    /// three records ago would answer a different question each time it was looked at. The count is
    /// therefore how many it has <i>seen</i> rather than how many it holds — which is how a bench
    /// scope's envelope works too. The way to start again is <see cref="Reset"/>, which the runner
    /// calls on a Clear, on a new run, and on any retune that changes what is being combined.</para>
    /// </summary>
    private (double[][], double[][]?) FoldEnvelope(double[][] channels)
    {
        int frames = channels[0].Length;

        if (_high is null || _low is null)
        {
            _high = new double[channels.Length][];
            _low = new double[channels.Length][];
            for (int c = 0; c < channels.Length; c++)
            {
                _high[c] = (double[])channels[c].Clone();
                _low[c] = (double[])channels[c].Clone();
            }

            Taken = 1;
            return (Copy(_high), Copy(_low));
        }

        for (int c = 0; c < channels.Length; c++)
        {
            var high = _high[c];
            var low = _low[c];
            var from = channels[c];
            for (int i = 0; i < frames; i++)
            {
                double v = from[i];
                if (v > high[i]) high[i] = v;
                if (v < low[i]) low[i] = v;
            }
        }

        Taken = Math.Min(_count, Taken + 1);

        // Copies, because the accumulator goes on mutating its own pair and a record handed out is
        // read by every fetch until the next one replaces it.
        return (Copy(_high), Copy(_low));
    }

    private static double[][] Copy(double[][] source)
    {
        var copy = new double[source.Length][];
        for (int c = 0; c < source.Length; c++)
            copy[c] = (double[])source[c].Clone();
        return copy;
    }
}
