namespace Tonebench.Dsp;

/// <summary>
/// Puts a simulator's waveform onto a sample grid an FFT can analyse: a non-uniform time axis onto
/// a uniform one (<see cref="ToUniform"/>), and a uniform grid onto a lower rate without folding
/// what sits above the new Nyquist back into the band (<see cref="Decimate"/>).
///
/// <para><b>The anti-alias filter is the whole point of this type, not a refinement of it.</b> A
/// circuit simulator runs at whatever timestep it needs — 1 µs, i.e. a 1 MHz grid, for the
/// amplifier this was developed against — while the record an FFT wants spans the simulation's
/// whole duration at a few hundred kilohertz. Resampling that down by picking or interpolating
/// samples folds every megahertz-region component the simulator produced into the audio band,
/// where it reads as distortion the circuit does not have. That failure is invisible: the spectrum
/// looks entirely plausible, and the only thing wrong with it is the answer.</para>
///
/// <para>Pure, allocation-light and I/O-free, like everything else in this assembly — volts in,
/// volts out, no gain and no calibration (see CLAUDE.md).</para>
/// </summary>
public static class Resampler
{
    /// <summary>
    /// Stopband attenuation the decimation filter is designed for, in dB — and what it really buys
    /// is not the stopband but the kernel's <i>sidelobe floor</i>, which is where the image of the
    /// source grid ends up.
    ///
    /// <para>It was 100, on the reasoning that the quietest harmonic worth reading is −110 dBc. The
    /// number that matters is a different one: decimating replicates the input about the source
    /// <i>grid</i> rate, so a tone at <c>f</c> leaves a line at <c>|m·f_source ± k·f|</c> folded
    /// into the new band — a frequency that is a harmonic of nothing, and therefore the one thing
    /// on a spectrum that cannot be explained away. Reported 2026-10-19 from a real LTspice run
    /// (1 MHz grid, 245 ms, a 16384-point record, so the grid folds to 3102.04 Hz): a line at
    /// 4102.04 Hz at <b>−163 dBc</b> beside a 1 kHz tone, with the rest of the family under it,
    /// and the same file analysed at its native rate putting those frequencies at −193 dBV, i.e.
    /// the simulator's own floor.</para>
    ///
    /// <para>160 dB puts them at <b>−216 dBc</b> — below anything a simulation can show, so the
    /// imported spectrum and the simulator's own agree line for line — and costs about a third
    /// more time in the one place this runs (779 → 1030 ms for 245 k samples, Debug; the 276 MB
    /// file it came from takes several seconds to read). Going further is invisible, and going
    /// back is caught by
    /// <c>Decimate_LeavesTheSourceGridsImage_BelowWhatASimulationCanShow</c>.</para>
    /// </summary>
    public const double StopbandDb = 160.0;

    /// <summary>
    /// Where the filter's passband ends, as a fraction of the new Nyquist. The gap between this and
    /// 1.0 is the transition band, and it is what the tap count is bought with — 0.9 leaves 10 % of
    /// the new Nyquist (about 8.6 kHz of a 172.5 kHz rate) rolling off, which is above any band
    /// this app measures in and keeps the kernel to a few hundred taps.
    /// </summary>
    public const double PassbandFraction = 0.9;

    /// <summary>
    /// Resamples a non-uniform series onto a uniform grid by linear interpolation:
    /// <paramref name="destination"/>[i] is the value at <c>startTimeS + i / sampleRateHz</c>.
    /// </summary>
    /// <remarks>
    /// <para>Linear, and that is a decision rather than laziness: the only input this is allowed to
    /// see is one already dense against the band being measured. A simulator run with
    /// <c>.options plotwinsize=0</c> and a 1 µs maximum timestep is 50× oversampled against a
    /// 20 kHz band, where linear interpolation's error is far below the measurement floor. It is
    /// emphatically <i>not</i> adequate for a compressed waveform, whose steps reach tens of
    /// microseconds — but interpolating one of those is inventing samples, and the importing
    /// service refuses such a file outright rather than handing it here.</para>
    ///
    /// <para><paramref name="timeS"/> must be strictly increasing. The caller checks that; it is
    /// cheap there and this runs per sample.</para>
    /// </remarks>
    public static void ToUniform(
        ReadOnlySpan<double> timeS,
        ReadOnlySpan<double> values,
        double startTimeS,
        double sampleRateHz,
        Span<double> destination)
    {
        if (timeS.Length != values.Length)
            throw new ArgumentException("timeS and values must be the same length.", nameof(values));
        if (timeS.Length < 2)
            throw new ArgumentException("At least two points are needed to interpolate.", nameof(timeS));
        if (!(sampleRateHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        double step = 1.0 / sampleRateHz;
        int j = 0;

        for (int i = 0; i < destination.Length; i++)
        {
            double t = startTimeS + (i * step);

            // The output grid is monotone, so the source index only ever moves forward: this is one
            // pass over both series, not a search per sample.
            while (j + 1 < timeS.Length - 1 && timeS[j + 1] <= t)
                j++;

            if (t <= timeS[0])
            {
                destination[i] = values[0];
                continue;
            }
            if (t >= timeS[^1])
            {
                destination[i] = values[^1];
                continue;
            }

            double span = timeS[j + 1] - timeS[j];
            double w = span > 0.0 ? (t - timeS[j]) / span : 0.0;
            destination[i] = values[j] + (w * (values[j + 1] - values[j]));
        }
    }

    /// <summary>
    /// Resamples a uniform series from <paramref name="sourceRateHz"/> to
    /// <paramref name="targetRateHz"/> through a Kaiser-windowed sinc low-pass, writing
    /// <paramref name="destination"/>.Length samples. An arbitrary (non-integer) ratio is fine,
    /// which it has to be: the target rate is derived from the simulation's duration and the FFT
    /// size, so it is never a neat fraction of the simulator's own grid.
    /// </summary>
    /// <remarks>
    /// <para>When <paramref name="targetRateHz"/> is at or above the source rate nothing needs
    /// filtering — there is no new Nyquist to fold anything under — so the kernel's cutoff is
    /// referred to whichever rate is lower.</para>
    ///
    /// <para>Each output sample is <c>Σ x[k]·h(k − p)</c> over the taps that reach it, divided by
    /// <c>Σ h(k − p)</c>. The normalisation is not cosmetic: it makes the DC gain exactly one
    /// whatever the fractional phase and however the kernel is truncated, and it is what keeps the
    /// first and last few output samples sane where the kernel hangs off the end of the input.
    /// Those two ends are also the ones an analysis window attenuates to nothing, so what is left
    /// of the edge effect never reaches a reading.</para>
    ///
    /// <para>No transcendental functions run in the inner loop. The taps of one output sample are
    /// <c>sin(2π·fc·d)/(π·d)</c> at <c>d</c> spaced exactly one source sample apart, so the sine
    /// advances by a fixed angle per tap and is carried by rotating a unit phasor; the Kaiser
    /// window, which is smooth over the kernel's whole half-length, comes from a table with linear
    /// interpolation. Cost works out at roughly <c>128 × sourceSamples</c> multiply-accumulates
    /// regardless of the ratio, because the tap count grows exactly as fast as the output shrinks.</para>
    /// </remarks>
    public static void Decimate(
        ReadOnlySpan<double> source,
        double sourceRateHz,
        double targetRateHz,
        Span<double> destination)
    {
        if (!(sourceRateHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(sourceRateHz));
        if (!(targetRateHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(targetRateHz));
        if (source.Length < 2)
            throw new ArgumentException("At least two samples are needed to resample.", nameof(source));

        double ratio = sourceRateHz / targetRateHz;

        // Cutoff in cycles per *source* sample, referred to the lower of the two Nyquists.
        double cutoff = PassbandFraction * 0.5 / Math.Max(1.0, ratio);

        // Kaiser design: beta from the stopband, tap count from the transition width. Both are the
        // standard formulas (Kaiser 1974) -- beta = 0.1102(A - 8.7), and N ≈ (A - 8)/(2.285·Δω).
        double beta = 0.1102 * (StopbandDb - 8.7);
        double transition = (1.0 - PassbandFraction) * 0.5 / Math.Max(1.0, ratio);
        int halfLength = Math.Max(
            4, (int)Math.Ceiling((StopbandDb - 8.0) / (2.0 * 2.285 * 2.0 * Math.PI * transition)));

        double[] windowTable = BuildKaiserTable(halfLength, beta, out int density);

        double phaseStep = 2.0 * Math.PI * cutoff;
        double sinStep = Math.Sin(phaseStep);
        double cosStep = Math.Cos(phaseStep);

        for (int i = 0; i < destination.Length; i++)
        {
            double centre = i * ratio;
            int first = (int)Math.Ceiling(centre) - halfLength;
            int last = (int)Math.Floor(centre) + halfLength;

            // Seed the phasor at the first tap's angle, then rotate by one source sample per tap.
            double d0 = first - centre;
            double angle = 2.0 * Math.PI * cutoff * d0;
            double sin = Math.Sin(angle);
            double cos = Math.Cos(angle);

            double sum = 0.0;
            double weightSum = 0.0;

            for (int k = first; k <= last; k++)
            {
                double d = k - centre;
                double absD = Math.Abs(d);

                double h;
                if (absD < 1e-12)
                {
                    h = 2.0 * cutoff;
                }
                else
                {
                    double window = WindowAt(windowTable, density, absD, halfLength);
                    h = window * sin / (Math.PI * d);
                }

                if (k >= 0 && k < source.Length)
                {
                    sum += source[k] * h;
                    weightSum += h;
                }

                (sin, cos) = ((sin * cosStep) + (cos * sinStep), (cos * cosStep) - (sin * sinStep));
            }

            destination[i] = weightSum != 0.0 ? sum / weightSum : 0.0;
        }
    }

    /// <summary>
    /// The number of samples the uniform grid at <paramref name="sampleRateHz"/> holds over
    /// <paramref name="durationS"/>, which is what the caller needs to size a buffer.
    /// </summary>
    public static int SampleCount(double durationS, double sampleRateHz) =>
        (int)Math.Floor(durationS * sampleRateHz);

    /// <summary>
    /// Half of the Kaiser window, sampled <paramref name="density"/> times per source sample over
    /// <c>[0, halfLength]</c>. A bell with no oscillation in it, so linear interpolation off this
    /// table is accurate to far better than the stopband it has to support — unlike the sinc
    /// beside it, which is why only the window is tabulated.
    /// </summary>
    private static double[] BuildKaiserTable(int halfLength, double beta, out int density)
    {
        density = 64;
        int count = (halfLength * density) + 2;
        var table = new double[count];
        double normalisation = Window.BesselI0(beta);

        for (int i = 0; i < count; i++)
        {
            double r = (double)i / (halfLength * density);
            double shape = Math.Sqrt(Math.Max(0.0, 1.0 - (r * r)));
            table[i] = Window.BesselI0(beta * shape) / normalisation;
        }

        return table;
    }

    private static double WindowAt(double[] table, int density, double absD, int halfLength)
    {
        if (absD >= halfLength)
            return 0.0;

        double position = absD * density;
        int index = (int)position;
        double fraction = position - index;

        // index + 1 is always inside the table: absD < halfLength, so position < halfLength*density
        // and the table is two entries longer than that.
        return table[index] + (fraction * (table[index + 1] - table[index]));
    }
}
