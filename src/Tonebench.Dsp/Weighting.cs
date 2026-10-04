namespace Tonebench.Dsp;

/// <summary>
/// The built-in noise weighting curves. <see cref="None"/> is flat. Every curve is 0 dB at 1 kHz
/// except <see cref="CcirArm"/>, which is ITU-R 468 re-zeroed at 2 kHz (the Dolby "average
/// responding meter" convention, +5.6 dB at 1 kHz).
/// </summary>
public enum WeightingStandard
{
    None,
    A,
    C,
    ItuR468,
    CcirArm,
    RiaaPlayback,
    RiaaRecord,
}

/// <summary>
/// A frequency weighting: the gain in dB applied to a bin at <c>freqHz</c>. Positive is gain,
/// negative is attenuation, and a curve that goes to nothing at DC (A, C, ITU-R 468) returns
/// <see cref="double.NegativeInfinity"/> at 0 Hz, which is a power gain of exactly zero.
/// </summary>
public interface IWeightingCurve
{
    double GainDb(double freqHz);
}

/// <summary>0 dB everywhere — what a "None" selection and an empty user slot resolve to.</summary>
public sealed class FlatWeighting : IWeightingCurve
{
    public static readonly FlatWeighting Instance = new();

    private FlatWeighting() { }

    public double GainDb(double freqHz) => 0.0;
}

/// <summary>The sum, in dB, of several curves — how a standard curve and the user tables combine.</summary>
public sealed class CompositeWeighting : IWeightingCurve
{
    private readonly IWeightingCurve[] _parts;

    public CompositeWeighting(params IWeightingCurve[] parts)
    {
        _parts = parts ?? throw new ArgumentNullException(nameof(parts));
    }

    public double GainDb(double freqHz)
    {
        double sum = 0.0;
        foreach (var part in _parts)
            sum += part.GainDb(freqHz);
        return sum;
    }
}

/// <summary>
/// The analytic standard curves. All are magnitude responses of rational functions evaluated
/// directly (no filter design, no sample-rate dependence), normalised at construction so the
/// reference frequency reads exactly 0 dB rather than relying on a rounded published constant.
/// </summary>
public static class StandardWeighting
{
    public static IWeightingCurve For(WeightingStandard standard) => standard switch
    {
        WeightingStandard.None => FlatWeighting.Instance,
        WeightingStandard.A => AWeighting.Instance,
        WeightingStandard.C => CWeighting.Instance,
        WeightingStandard.ItuR468 => ItuR468Weighting.Instance,
        WeightingStandard.CcirArm => CcirArmWeighting.Instance,
        WeightingStandard.RiaaPlayback => RiaaWeighting.Playback,
        WeightingStandard.RiaaRecord => RiaaWeighting.Record,
        _ => throw new ArgumentOutOfRangeException(nameof(standard), standard, null),
    };

    internal static double ToDb(double magnitude) =>
        magnitude <= 0.0 ? double.NegativeInfinity : 20.0 * Math.Log10(magnitude);
}

/// <summary>IEC 61672-1 A-weighting: 12194² f⁴ / ((f²+20.6²)·√((f²+107.7²)(f²+737.9²))·(f²+12194²)), 0 dB at 1 kHz.</summary>
public sealed class AWeighting : IWeightingCurve
{
    public static readonly AWeighting Instance = new();

    private readonly double _normDb;

    private AWeighting()
    {
        _normDb = -StandardWeighting.ToDb(Magnitude(1000.0));
    }

    private static double Magnitude(double f)
    {
        double f2 = f * f;
        const double c1 = 20.6 * 20.6, c2 = 107.7 * 107.7, c3 = 737.9 * 737.9, c4 = 12194.0 * 12194.0;
        return c4 * f2 * f2 / ((f2 + c1) * Math.Sqrt((f2 + c2) * (f2 + c3)) * (f2 + c4));
    }

    public double GainDb(double freqHz) => StandardWeighting.ToDb(Magnitude(freqHz)) + _normDb;
}

/// <summary>IEC 61672-1 C-weighting: 12194² f² / ((f²+20.6²)(f²+12194²)), 0 dB at 1 kHz.</summary>
public sealed class CWeighting : IWeightingCurve
{
    public static readonly CWeighting Instance = new();

    private readonly double _normDb;

    private CWeighting()
    {
        _normDb = -StandardWeighting.ToDb(Magnitude(1000.0));
    }

    private static double Magnitude(double f)
    {
        double f2 = f * f;
        const double c1 = 20.6 * 20.6, c4 = 12194.0 * 12194.0;
        return c4 * f2 / ((f2 + c1) * (f2 + c4));
    }

    public double GainDb(double freqHz) => StandardWeighting.ToDb(Magnitude(freqHz)) + _normDb;
}

/// <summary>
/// ITU-R BS.468-4 (CCIR 468) weighting, from the recommendation's own rational form
/// R(f) = 1.246332637532143e-4·f / √(h1(f)² + h2(f)²), 0 dB at 1 kHz (the published +18.2 dB
/// constant, computed here rather than quoted). +12.2 dB at 6.3 kHz, −22.2 dB at 20 kHz.
/// </summary>
public sealed class ItuR468Weighting : IWeightingCurve
{
    public static readonly ItuR468Weighting Instance = new();

    private readonly double _normDb;

    private ItuR468Weighting()
    {
        _normDb = -StandardWeighting.ToDb(Magnitude(1000.0));
    }

    internal static double Magnitude(double f)
    {
        double f2 = f * f;
        double f3 = f2 * f;
        double f4 = f2 * f2;
        double f5 = f4 * f;
        double f6 = f4 * f2;
        double h1 = -4.737338981378384e-24 * f6 + 2.043828333606125e-15 * f4 - 1.363894795463638e-7 * f2 + 1.0;
        double h2 = 1.306612257412824e-19 * f5 - 2.118150887518656e-11 * f3 + 5.559488023498642e-4 * f;
        return 1.246332637532143e-4 * f / Math.Sqrt(h1 * h1 + h2 * h2);
    }

    public double GainDb(double freqHz) => StandardWeighting.ToDb(Magnitude(freqHz)) + _normDb;
}

/// <summary>ITU-R 468 re-zeroed at 2 kHz — the CCIR/ARM (Dolby) convention. Same shape, +5.6 dB at 1 kHz.</summary>
public sealed class CcirArmWeighting : IWeightingCurve
{
    public static readonly CcirArmWeighting Instance = new();

    private readonly double _normDb;

    private CcirArmWeighting()
    {
        _normDb = -StandardWeighting.ToDb(ItuR468Weighting.Magnitude(2000.0));
    }

    public double GainDb(double freqHz) => StandardWeighting.ToDb(ItuR468Weighting.Magnitude(freqHz)) + _normDb;
}

/// <summary>
/// The RIAA curve, time constants 3180 µs (pole), 318 µs (zero) and 75 µs (pole), 0 dB at 1 kHz.
/// Playback is the de-emphasis (+19.3 dB at 20 Hz, −19.6 dB at 20 kHz); record is its exact
/// negation. Weighting a preamp's output by the record curve is what makes its noise read as it
/// would through a flat chain.
/// </summary>
public sealed class RiaaWeighting : IWeightingCurve
{
    public static readonly RiaaWeighting Playback = new(inverse: false);
    public static readonly RiaaWeighting Record = new(inverse: true);

    private const double T1 = 3180e-6, T2 = 318e-6, T3 = 75e-6;

    private readonly double _sign;
    private readonly double _normDb;

    private RiaaWeighting(bool inverse)
    {
        _sign = inverse ? -1.0 : 1.0;
        _normDb = -PlaybackDb(1000.0);
    }

    private static double PlaybackDb(double f)
    {
        double w = 2.0 * Math.PI * f;
        double num = Math.Sqrt(1.0 + w * w * T2 * T2);
        double den = Math.Sqrt(1.0 + w * w * T1 * T1) * Math.Sqrt(1.0 + w * w * T3 * T3);
        return 20.0 * Math.Log10(num / den);
    }

    public double GainDb(double freqHz) => _sign * (PlaybackDb(freqHz) + _normDb);
}

/// <summary>
/// One row of a user weighting table, in the QA40x file convention: <paramref name="AttenuationDb"/>
/// is <b>positive for attenuation</b>, negative for gain. The tutorial's ITU-R 468 file says
/// <c>6300, -12.2</c> for the curve's +12.2 dB peak.
/// </summary>
public readonly record struct WeightingPoint(double FreqHz, double AttenuationDb);

/// <summary>
/// A user weighting table: gain is the negated attenuation, interpolated linearly in dB over log
/// frequency between the rows, and held at the end values outside the table's range.
/// </summary>
public sealed class TabulatedWeighting : IWeightingCurve
{
    private readonly double[] _logFreq;
    private readonly double[] _gainDb;

    public TabulatedWeighting(IReadOnlyList<WeightingPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            throw new ArgumentException("A weighting table needs at least two points.", nameof(points));

        var sorted = points.OrderBy(p => p.FreqHz).ToArray();
        _logFreq = new double[sorted.Length];
        _gainDb = new double[sorted.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            if (!(sorted[i].FreqHz > 0.0) || double.IsInfinity(sorted[i].FreqHz))
                throw new ArgumentException($"Weighting table frequency must be positive, got {sorted[i].FreqHz}.", nameof(points));
            if (double.IsNaN(sorted[i].AttenuationDb) || double.IsInfinity(sorted[i].AttenuationDb))
                throw new ArgumentException($"Weighting table value at {sorted[i].FreqHz} Hz is not finite.", nameof(points));
            if (i > 0 && sorted[i].FreqHz == sorted[i - 1].FreqHz)
                throw new ArgumentException($"Weighting table lists {sorted[i].FreqHz} Hz twice.", nameof(points));
            _logFreq[i] = Math.Log10(sorted[i].FreqHz);
            _gainDb[i] = -sorted[i].AttenuationDb;
        }
    }

    public int Count => _logFreq.Length;

    public double GainDb(double freqHz)
    {
        if (!(freqHz > 0.0))
            return _gainDb[0];
        double x = Math.Log10(freqHz);
        if (x <= _logFreq[0])
            return _gainDb[0];
        int last = _logFreq.Length - 1;
        if (x >= _logFreq[last])
            return _gainDb[last];

        int hi = Array.BinarySearch(_logFreq, x);
        if (hi >= 0)
            return _gainDb[hi];
        hi = ~hi;
        int lo = hi - 1;
        double t = (x - _logFreq[lo]) / (_logFreq[hi] - _logFreq[lo]);
        return _gainDb[lo] + t * (_gainDb[hi] - _gainDb[lo]);
    }
}

/// <summary>
/// Parses a QA40x-style weighting file: one <c>frequency, value</c> pair per line, comma or
/// whitespace separated, value in dB with <b>positive meaning attenuation</b>. Blank lines and
/// lines starting with <c>#</c>, <c>;</c> or <c>//</c> are skipped, as is a first line that is not
/// numeric (a column header). Any other line that does not parse fails the whole file, naming
/// the line — a table with a silently dropped row is a different curve.
/// </summary>
public static class WeightingTableParser
{
    private static readonly char[] Separators = [',', ';', '\t', ' '];

    public static IReadOnlyList<WeightingPoint> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var points = new List<WeightingPoint>();
        var lines = text.Split('\n');
        bool sawData = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';') || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            var fields = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            double freqHz = 0.0, valueDb = 0.0;
            bool ok = fields.Length >= 2
                && double.TryParse(fields[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out freqHz)
                && double.TryParse(fields[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out valueDb);

            if (!ok)
            {
                // A header row is allowed once, before any data.
                if (!sawData)
                {
                    sawData = true;
                    continue;
                }
                throw new FormatException($"Line {i + 1} is not a \"frequency, dB\" pair: \"{lines[i].Trim()}\".");
            }

            sawData = true;
            points.Add(new WeightingPoint(freqHz, valueDb));
        }

        if (points.Count < 2)
            throw new FormatException("A weighting file needs at least two \"frequency, dB\" lines.");

        return points;
    }
}

/// <summary>The per-bin power gain a pipeline multiplies a spectrum by: 10^(GainDb(k·Δf)/10).</summary>
public static class WeightingTable
{
    public static void FillPowerGains(IWeightingCurve curve, double binSizeHz, Span<double> powerGain)
    {
        ArgumentNullException.ThrowIfNull(curve);
        for (int k = 0; k < powerGain.Length; k++)
        {
            double db = curve.GainDb(k * binSizeHz);
            powerGain[k] = double.IsNegativeInfinity(db) ? 0.0 : Math.Pow(10.0, db / 10.0);
        }
    }
}
