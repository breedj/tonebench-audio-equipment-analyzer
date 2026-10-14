namespace Tonebench.Dsp;

/// <summary>
/// S1/S2 per Heinzel/Rüdiger/Schilling: S1 = Σw[n], S2 = Σw[n]². Store alongside every
/// trace — a spectrum recorded under an unknown window is uninterpretable later.
/// </summary>
public readonly record struct WindowSums(double S1, double S2, int Length)
{
    public double EnbwBins => Length * S2 / (S1 * S1);

    public double EnbwHz(double sampleRateHz) => sampleRateHz * S2 / (S1 * S1);

    /// <summary>
    /// The first bin a measurement band may start at: how far from DC the window's own leakage
    /// has died away. Bin 0 is DC, and a window spreads whatever sits there across its main lobe,
    /// so a band that starts any closer integrates the input's DC offset as if it were noise.
    ///
    /// <para>Window-aware rather than a constant, because the lobe is: +/-2 bins for Hann, +/-5 for
    /// Flat Top, +/-7.1 for Kaiser7. ENBW is the proxy, and <c>round(2*ENBW) + 2</c> clears every
    /// window here -- Rectangular 4, Hamming 5, Hann 5, Blackman-Harris 6, Kaiser5 7, Kaiser7 7
    /// (its lobe's own first null), Flat Top 10.</para>
    ///
    /// <para><b>Rounded, not ceilinged</b>, and that is not a nicety: Hann's ENBW is exactly 1.5,
    /// so <c>ceil(2*ENBW)</c> sits precisely on an integer and a float crumb in the window sums
    /// flips the guard between 4 and 5 bins. Rounding puts the common windows in the middle of
    /// their interval instead of on its edge.</para>
    ///
    /// <para><b>Measured</b>, 2026-10-14, QA403 serial 1077527945 at 192 kHz, Hann, one 1.7 kHz
    /// tone, band 20 Hz - 20 kHz, nothing changing but the FFT size. At 16384 points the band's
    /// lower edge rounds to bin 2 and THD+N reads 0.0022 %; at 8192 it rounds to bin 1 and THD+N
    /// reads <b>2.56 %</b>, with SNR falling 93.4 -> 31.8 dB. Moving the requested lower edge to
    /// 100 Hz (bin 4) at that same 8192 brings it back to 0.0014 % / 97.5 dB, and 200 Hz (bin 9)
    /// agrees at 0.0015 % -- so by bin 4 the leakage is gone under Hann, and this returns 5 for it,
    /// a bin inside what was measured clean. The content is DC and not a signal: bin 1 is 23.4 Hz at 8192 and 46.9 Hz at
    /// 4096, two different frequencies, and both read 2.57 %.</para>
    ///
    /// <para>It bites only where the bins are coarse. At 131072 points and 192 kHz a 20 Hz edge is
    /// bin 14, far above this, and nothing changes; it is the adaptive sweep's short records at the
    /// top of a sweep that fall foul of it -- where it is also a real limit rather than a fudge, as
    /// a 46.9 Hz bin cannot report a band starting at 20 Hz whatever it does.</para>
    /// </summary>
    public int DcGuardBins => (int)Math.Round(2.0 * EnbwBins, MidpointRounding.AwayFromZero) + 2;
}
