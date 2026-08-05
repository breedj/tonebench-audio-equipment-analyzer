namespace Qa403.Dsp;

/// <summary>How <see cref="SpectrumAverager"/> combines successive spectra.</summary>
public enum AveragingMode
{
    /// <summary>Each tick's spectrum is used as-is.</summary>
    Off,

    /// <summary>Boxcar mean of the last <c>count</c> spectra.</summary>
    Linear,

    /// <summary>Exponential moving average, alpha = 2/(count+1).</summary>
    Exponential,
}
