namespace Tonebench.Dsp;

/// <summary>
/// A generated window's coefficients plus its precomputed <see cref="WindowSums"/>.
/// Instances are cached per (type, length) by <see cref="Window"/> — building the
/// coefficient array and summing it is the only allocation; every later call for the
/// same (type, length) reuses this instance.
/// </summary>
public sealed class WindowCoefficients
{
    private readonly double[] _values;

    internal WindowCoefficients(WindowType type, double[] values, WindowSums sums)
    {
        Type = type;
        _values = values;
        Sums = sums;
    }

    public WindowType Type { get; }

    public int Length => _values.Length;

    public WindowSums Sums { get; }

    public ReadOnlySpan<double> Values => _values;
}
