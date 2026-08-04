namespace Qa403.Dsp;

/// <summary>One located harmonic: order 1 is the fundamental itself (Dbc == 0 by definition).</summary>
public readonly record struct Harmonic(int Order, int Bin, double AmplitudeVrms, double AmplitudeDbv, double Dbc);
