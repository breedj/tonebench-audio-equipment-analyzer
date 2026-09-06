namespace Qa403.Dsp.Tests;

public class ImpulseWindowTests
{
    private const double SampleRateHz = 48000.0;
    private const int RecordLength = 4096;

    /// <summary>
    /// The impulse itself must come through at full amplitude. Both half-windows peak where they
    /// meet, so the sample the gate is centred on is multiplied by exactly one — a whole window
    /// stretched across the span would instead attenuate it by however far off-centre it sits, which
    /// is the reason the halves exist.
    /// </summary>
    [Fact]
    public void WindowIr_LeavesTheImpulseItselfUntouched()
    {
        var ir = Ramp();

        var gated = ImpulseWindow.WindowIr(ir, 1000, 1.0, 5.0, SampleRateHz);

        Assert.Equal(ir[1000], gated.Samples[gated.PreSamples], 12);
        Assert.Equal(48, gated.PreSamples);
        Assert.Equal(240, gated.PostSamples);
    }

    /// <summary>
    /// The impulse response of a periodic sweep is a circular record, so a peak near index 0 has its
    /// pre-samples at the far end of the array. Reading it linearly would silently gate in a stretch
    /// of zeros — and would do so only for the records that happened to land near the origin, which
    /// is precisely the case latency removal produces.
    /// </summary>
    [Fact]
    public void WindowIr_ReadsTheRecordCircularly()
    {
        var ir = Ramp();

        var gated = ImpulseWindow.WindowIr(ir, 2, 1.0, 1.0, SampleRateHz, WindowType.Rectangular);

        // Index 0 of the gate is 48 samples before index 2, i.e. index 4050 of a 4096-long record.
        Assert.Equal(ir[RecordLength - 46], gated.Samples[0], 12);
        Assert.Equal(ir[0], gated.Samples[gated.PreSamples - 2], 12);
    }

    /// <summary>
    /// Rectangular is a legitimate choice, not a degenerate one: it is what isolates a reflection
    /// exactly. It must therefore be a bare cut with no taper anywhere.
    /// </summary>
    [Fact]
    public void WindowIr_WithARectangularWindow_IsABareCut()
    {
        var ir = Ramp();

        var gated = ImpulseWindow.WindowIr(ir, 1000, 1.0, 2.0, SampleRateHz, WindowType.Rectangular);

        for (int j = 0; j < gated.Samples.Length; j++)
            Assert.Equal(ir[1000 - gated.PreSamples + j], gated.Samples[j], 12);
    }

    /// <summary>A taper that is not monotone away from the impulse is not a taper.</summary>
    [Fact]
    public void WindowIr_TapersMonotonicallyAwayFromTheImpulse()
    {
        var ir = new double[RecordLength];
        Array.Fill(ir, 1.0);

        var gated = ImpulseWindow.WindowIr(ir, 1000, 2.0, 4.0, SampleRateHz);

        for (int j = 1; j <= gated.PreSamples; j++)
            Assert.True(gated.Samples[j] > gated.Samples[j - 1], $"rise stalled at {j}");

        for (int j = gated.PreSamples + 1; j < gated.Samples.Length; j++)
            Assert.True(gated.Samples[j] < gated.Samples[j - 1], $"fall stalled at {j}");

        Assert.Equal(1.0, gated.Samples[gated.PreSamples], 12);
    }

    /// <summary>
    /// The readout the whole panel hangs on: a gate of T seconds cannot resolve anything narrower
    /// than 1/T, so a 5 ms gate says nothing below 200 Hz.
    /// </summary>
    [Fact]
    public void WindowIr_ReportsTheResolutionLimitOfTheGateItApplied()
    {
        var gated = ImpulseWindow.WindowIr(Ramp(), 1000, 1.0, 4.0, SampleRateHz);

        double lengthSeconds = gated.Samples.Length / SampleRateHz;
        Assert.Equal(1.0 / lengthSeconds, gated.ResolutionLimitHz, 9);
        Assert.Equal(-gated.PreSamples / SampleRateHz, gated.FirstSampleTimeS, 12);

        // ~5 ms of gate, so a shade under 200 Hz.
        Assert.InRange(gated.ResolutionLimitHz, 195.0, 200.0);
    }

    [Fact]
    public void WindowIr_WithAGateLongerThanTheRecord_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ImpulseWindow.WindowIr(Ramp(), 1000, 50.0, 50.0, SampleRateHz));
    }

    [Fact]
    public void RotateToOrigin_PutsThePeakAtZeroAndLosesNothing()
    {
        var ir = Ramp();
        var rotated = new double[RecordLength];

        ImpulseWindow.RotateToOrigin(ir, 300, rotated);

        Assert.Equal(ir[300], rotated[0], 12);
        Assert.Equal(ir[299], rotated[RecordLength - 1], 12);
        Assert.Equal(ir.Sum(), rotated.Sum(), 9);
    }

    /// <summary>
    /// The user-facing gate must stay inside the anti-distortion one, or it keeps the harmonic
    /// packets that gate exists to delete. Derived from the same function rather than re-derived, so
    /// the two cannot drift apart.
    /// </summary>
    [Theory]
    [InlineData(32768, 48000.0, 20.0, 20000.0)]
    [InlineData(524288, 192000.0, 2.0, 94080.0)]
    public void Bounds_StayInsideTheSweepGate(int fftSize, double sampleRateHz, double startHz, double endHz)
    {
        var (maxPreMs, maxPostMs) = ImpulseWindow.Bounds(fftSize, sampleRateHz, startHz, endHz);
        var sweepGate = ImpulseGate.ForSweep(fftSize, startHz, endHz);

        Assert.Equal(sweepGate.PreSamples, (int)Math.Round(maxPreMs * sampleRateHz / 1000.0));
        Assert.Equal(sweepGate.PostSamples, (int)Math.Round(maxPostMs * sampleRateHz / 1000.0));

        // A gate at the bounds still fits the record it gates.
        var gated = ImpulseWindow.WindowIr(new double[fftSize], fftSize / 4, maxPreMs, maxPostMs, sampleRateHz);
        Assert.True(gated.Samples.Length <= fftSize);
    }

    /// <summary>Distinct values everywhere, so a misindexed read cannot coincidentally match.</summary>
    private static double[] Ramp()
    {
        var ir = new double[RecordLength];
        for (int i = 0; i < RecordLength; i++)
            ir[i] = (i + 1) * 0.001;
        return ir;
    }
}
