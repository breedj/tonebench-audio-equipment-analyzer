namespace Qa403.Dsp;

public enum WindowType
{
    Rectangular,
    Hann,
    Hamming,
    BlackmanHarris,
    FlatTop,

    /// <summary>Kaiser-Bessel with α = 5, i.e. β = 5π. See <see cref="Window"/> for the convention.</summary>
    Kaiser5,

    /// <summary>Kaiser-Bessel with α = 7, i.e. β = 7π. See <see cref="Window"/> for the convention.</summary>
    Kaiser7,
}
