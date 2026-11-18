using Tonebench.Dsp;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// Reading a record between its samples: the primitive that puts every averaged record on one
/// fractional grid.
///
/// <para>Two of these matter more than the rest. <b>The misalignment figure</b> — how much a
/// sinusoid loses to records that are aligned only to the nearest sample — is asserted against
/// <c>sinc(f·Δt)</c> rather than against "smaller", because it is the whole reason the shift
/// exists. And <b>the negative result</b>: a sinc reconstruction from one record already carries
/// everything interleaving records at other offsets could add, which is why equivalent-time
/// sampling is not built here and must not be added later on the assumption that it would help.</para>
/// </summary>
public class FractionalDelayTests
{
    private static double[] Tone(int frames, double cyclesPerSample, double phase = 0.0)
    {
        var samples = new double[frames];
        for (int i = 0; i < frames; i++)
            samples[i] = Math.Sin((2.0 * Math.PI * cyclesPerSample * i) + phase);
        return samples;
    }

    [Fact]
    public void AShiftOfNothing_LeavesTheSamplesExactlyAsTheyWere()
    {
        // Bit for bit, not nearly: a shift of zero and no shift at all must not differ, or an
        // aligned record and an unaligned one are two different measurements.
        var source = Tone(64, 1.0 / 7.0);
        var destination = new double[64];

        FractionalDelay.Shift(source, destination, 0.0);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void AShiftBelowTheNegligibleThreshold_IsSkipped()
    {
        // A thousandth of a sample is 5 ns at 192 kS/s -- resampling for it would spend the
        // kernel's own residue to correct nothing.
        var source = Tone(64, 1.0 / 7.0);
        var destination = new double[64];

        Assert.False(FractionalDelay.Matters(0.0005));
        FractionalDelay.Shift(source, destination, 0.0005);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void AShiftOfAWholeSample_MovesTheWaveformByOneSample()
    {
        // The sign convention, which is the thing a caller gets wrong once: a positive shift reads
        // LATER in the source, so the feature arrives EARLIER in the result.
        var source = Tone(96, 1.0 / 11.0);
        var destination = new double[96];

        FractionalDelay.Shift(source, destination, 1.0);

        // Away from the ends, where the held edges are.
        for (int i = 20; i < 70; i++)
            Assert.Equal(source[i + 1], destination[i], 9);
    }

    [Fact]
    public void AHalfSampleShift_ReadsTheAnalyticValueBetweenTwoSamples()
    {
        // Against the closed form rather than against another interpolator: the whole claim of a
        // windowed sinc is that it reads the one band-limited signal the samples came from.
        const double cycles = 1.0 / 9.0;
        var source = Tone(200, cycles);
        var destination = new double[200];

        FractionalDelay.Shift(source, destination, 0.5);

        double worst = 0.0;
        for (int i = FractionalDelay.Taps; i < 200 - FractionalDelay.Taps; i++)
        {
            double expected = Math.Sin(2.0 * Math.PI * cycles * (i + 0.5));
            worst = Math.Max(worst, Math.Abs(destination[i] - expected));
        }

        // A thousandth of full scale at nine samples a period.
        Assert.True(worst < 1e-3, $"worst {worst:E2}");
    }

    [Fact]
    public void NearNyquist_TheKernelsOwnResidueIsStillUnderAPerCent()
    {
        // 2.5 samples a period, which is where the chart's own reconstruction measures 0.9 % -- the
        // finite kernel's residue that close to Nyquist, not an error of method. Stated so a later
        // reader does not take a figure of this size for a fault.
        const double cycles = 1.0 / 2.5;
        var source = Tone(400, cycles);
        var destination = new double[400];

        FractionalDelay.Shift(source, destination, 0.37);

        double worst = 0.0;
        for (int i = FractionalDelay.Taps; i < 400 - FractionalDelay.Taps; i++)
        {
            double expected = Math.Sin(2.0 * Math.PI * cycles * (i + 0.37));
            worst = Math.Max(worst, Math.Abs(destination[i] - expected));
        }

        Assert.True(worst < 0.02, $"worst {worst:E2}");
    }

    [Fact]
    public void AConstantStaysAtItsOwnLevel()
    {
        // What a reader would notice at a glance, and what normalising the kernel's weights buys.
        var source = new double[64];
        Array.Fill(source, 0.37);
        var destination = new double[64];

        FractionalDelay.Shift(source, destination, 0.42);

        foreach (double v in destination)
            Assert.Equal(0.37, v, 12);
    }

    [Fact]
    public void TheEndsAreHeldRatherThanZeroed()
    {
        // A record has no outside. Zeros would be a step at each edge and wrapping would put the
        // end of the capture before its start; holding invents no feature.
        var source = new double[64];
        Array.Fill(source, 1.0);
        var destination = new double[64];

        FractionalDelay.Shift(source, destination, 0.5);

        Assert.Equal(1.0, destination[0], 9);
        Assert.Equal(1.0, destination[^1], 9);
    }

    [Fact]
    public void AlignedAveraging_KeepsAmplitudeWhereNearestSampleAveragingLosesIt()
    {
        // THE figure this primitive exists for. Records aligned only to the nearest sample carry a
        // residual offset uniform over +/- half a sample, which attenuates a sinusoid by
        // sinc(f*dt). At 0.4 cycles a sample that is sin(pi*0.4)/(pi*0.4) = 0.757, i.e. 2.4 dB --
        // and it reads as a smaller waveform rather than as a misaligned one.
        const double cycles = 0.4;
        const int frames = 512;
        const int records = 64;

        var alignedSum = new double[frames];
        var nearestSum = new double[frames];
        var shifted = new double[frames];

        for (int r = 0; r < records; r++)
        {
            // Each record's trigger lands a different fraction of a sample from its first frame,
            // which is what a real interpolated trigger does on a period that is not a whole
            // number of samples.
            double fraction = (double)r / records;
            var record = Tone(frames, cycles, phase: -2.0 * Math.PI * cycles * fraction);

            for (int i = 0; i < frames; i++)
                nearestSum[i] += record[i];

            // The record's waveform is delayed by `fraction`, so reading it `fraction` LATER undoes
            // that -- the sign convention Shift documents, and the one a caller gets wrong once.
            FractionalDelay.Shift(record, shifted, fraction);
            for (int i = 0; i < frames; i++)
                alignedSum[i] += shifted[i];
        }

        double aligned = Rms(alignedSum, records);
        double nearest = Rms(nearestSum, records);
        double expected = Sinc(cycles);

        // The aligned average keeps the tone's own RMS; the unaligned one loses the sinc factor.
        Assert.Equal(1.0 / Math.Sqrt(2.0), aligned, 2);
        Assert.Equal(expected / Math.Sqrt(2.0), nearest, 2);
        Assert.True(aligned > nearest * 1.25, $"aligned {aligned:F4} against nearest {nearest:F4}");
    }

    [Fact]
    public void ASincReconstructionFromOneRecord_AlreadyHasEverythingInterleavingCouldAdd()
    {
        // The negative result, measured rather than asserted -- and the reason equivalent-time
        // sampling is NOT built here.
        //
        // A bench scope interleaves records taken at different fractional offsets to synthesise a
        // rate above its own sampler's. That works because its sampler is slower than its front
        // end. Here the band limit is the ADC's own decimation filter, applied before the samples
        // exist: so a band-limited capture's samples already determine the waveform everywhere
        // between them, and reading it at an offset gives the same answer as a record actually
        // taken at that offset. There is nothing left for interleaving to add.
        const double cycles = 1.0 / 3.2;
        const int frames = 400;
        var baseline = Tone(frames, cycles);

        double worst = 0.0;
        for (int step = 1; step < 8; step++)
        {
            double offset = step / 8.0;

            // What a record genuinely taken 'offset' samples later would hold...
            var taken = Tone(frames, cycles, phase: 2.0 * Math.PI * cycles * offset);

            // ...against reading the first record at that offset.
            for (int i = FractionalDelay.Taps; i < frames - FractionalDelay.Taps; i++)
                worst = Math.Max(worst, Math.Abs(FractionalDelay.At(baseline, i + offset) - taken[i]));
        }

        // Within the kernel's own residue at 3.2 samples a period -- so an interleaved record
        // carries no information the one record did not already have.
        Assert.True(worst < 0.01, $"worst {worst:E2}");
    }

    private static double Rms(double[] sum, int records)
    {
        double total = 0.0;
        for (int i = FractionalDelay.Taps; i < sum.Length - FractionalDelay.Taps; i++)
        {
            double v = sum[i] / records;
            total += v * v;
        }

        return Math.Sqrt(total / (sum.Length - (2 * FractionalDelay.Taps)));
    }

    /// <summary>The attenuation a uniform ±½-sample misalignment costs a sinusoid at <paramref name="cyclesPerSample"/>.</summary>
    private static double Sinc(double cyclesPerSample)
    {
        double x = Math.PI * cyclesPerSample;
        return Math.Sin(x) / x;
    }
}
