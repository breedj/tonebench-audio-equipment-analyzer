namespace Qa403.Dsp;

/// <summary>One located harmonic: order 1 is the fundamental itself (Dbc == 0 by definition).</summary>
public readonly record struct Harmonic(int Order, int Bin, double AmplitudeVrms, double AmplitudeDbv, double Dbc);

/// <summary>
/// One tone as <see cref="HarmonicAnalyzer.MeasureTone"/> found it: the bin the peak actually sits
/// on, its noise-subtracted amplitude, and the local noise density that was subtracted (0 when the
/// caller asked for no subtraction).
/// </summary>
public readonly record struct ToneMeasurement(int Bin, double AmplitudeVrms, double NoiseDensityV2PerHz);
