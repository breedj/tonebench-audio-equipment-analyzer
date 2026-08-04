namespace Qa403.Device.Simulation;

public enum IntermodulationMode
{
    None,

    /// <summary>Two-tone SMPTE: 60 Hz + 7 kHz at a 4:1 amplitude ratio. Replaces the fundamental/harmonic series.</summary>
    Smpte,

    /// <summary>Two-tone CCIF: 19 kHz + 20 kHz at equal amplitude. Replaces the fundamental/harmonic series.</summary>
    Ccif,
}
