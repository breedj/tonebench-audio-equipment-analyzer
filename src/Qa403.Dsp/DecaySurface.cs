namespace Qa403.Dsp;

/// <summary>What a decay surface's second axis measures.</summary>
public enum DecayAxis
{
    /// <summary>Milliseconds after the impulse — the cumulative spectral decay's axis.</summary>
    Milliseconds = 0,

    /// <summary>
    /// Periods of the frequency being looked at — the burst decay's axis, and the whole reason that
    /// measurement exists separately. One millisecond is a fifth of a period at 200 Hz and twenty of
    /// them at 20 kHz, so a decay read off a millisecond axis is being judged by a different
    /// standard at every frequency.
    /// </summary>
    Periods = 1,
}

/// <summary>
/// One row of a decay surface: the level across the whole frequency grid at one point on the
/// surface's second axis.
/// </summary>
/// <param name="Value">Where this slice sits on that axis — milliseconds or periods.</param>
/// <param name="FirstValidColumn">
/// The first frequency column this slice has data for; <see cref="LevelDb"/> starts there rather
/// than at column 0.
///
/// <para>Not a decoration. A burst decay's deep slices run off the end of the impulse response at
/// the low-frequency end — thirty periods of 40 Hz is 750 ms, which a 0.68 s record simply does not
/// contain — and the alternative to saying so is reading the zeros the gate left behind as though
/// they were the DUT going quiet. Which is to say: as a perfect decay, exactly where the DUT is
/// least likely to have one. Every slice of a cumulative spectral decay is valid across the whole
/// grid, so this is 0 throughout there.</para>
/// </param>
public sealed record DecaySlice(double Value, int FirstValidColumn, float[] LevelDb);

/// <summary>
/// A time-frequency decay surface: level in dB over (frequency, time), as both the cumulative
/// spectral decay and the burst decay produce it.
///
/// <para><b>0 dB is not an absolute level and the two measurements do not even put it in the same
/// place.</b> A cumulative spectral decay is normalised once, to the loudest point of the whole
/// surface, so the front slice still carries the DUT's frequency response and a resonance reads as
/// a ridge that is both tall and slow. A burst decay is normalised <i>per frequency</i>, to that
/// frequency's own maximum, which deletes the frequency response from the picture on purpose:
/// what is left is how long each frequency rings relative to how loud it was, which is the question
/// a burst decay is asked. Neither is convertible into the other after the fact.</para>
/// </summary>
/// <param name="FreqHz">
/// The frequency columns, log-spaced and shared by every slice. Log because the axis is drawn log,
/// and a linear grid resampled onto it wastes almost every column above 2 kHz.
/// </param>
/// <param name="ResolutionLimitHz">
/// Below this the surface describes the analysis rather than the DUT — the transform length's own
/// resolution for a cumulative spectral decay. Zero when the analysis has no single such limit,
/// which is the case for a burst decay: its resolution is proportional to frequency by construction.
/// </param>
public sealed record DecaySurface(
    DecayAxis SliceAxis,
    float[] FreqHz,
    IReadOnlyList<DecaySlice> Slices,
    double ResolutionLimitHz);
