namespace Qa403.Dsp.Tests;

public class BandwidthAnalyzerTests
{
    private const double BinHz = 1.0;

    [Fact]
    public void FindPeak_SkipsDc()
    {
        // DC carries the DUT's offset, not its response, and is routinely the largest value there.
        double[] trace = [100.0, 0.0, 5.0, 2.0];

        var peak = BandwidthAnalyzer.FindPeak(trace, BinHz);

        Assert.Equal(2, peak.Bin);
        Assert.Equal(5.0, peak.LevelDb);
        Assert.Equal(2.0, peak.FreqHz);
    }

    [Fact]
    public void Find_OnAnIdealBandpass_RecoversBothCorners()
    {
        // A flat plateau from bin 100 to 900 with 1 dB/bin skirts either side, so the -3 dB points
        // sit exactly 3 bins outside the plateau.
        var trace = new double[1024];
        for (int k = 1; k < trace.Length; k++)
        {
            if (k < 100)
                trace[k] = -(100 - k);
            else if (k > 900)
                trace[k] = -(k - 900);
            else
                trace[k] = 0.0;
        }

        var result = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0);

        Assert.Equal(97.0, result.LowerHz, 6);
        Assert.Equal(903.0, result.UpperHz, 6);
        Assert.Equal(806.0, result.BandwidthHz, 6);
        Assert.False(result.LowerClamped);
        Assert.False(result.UpperClamped);
    }

    [Fact]
    public void Find_InterpolatesBetweenBins()
    {
        // The threshold falls halfway between two bins in level; the reported frequency must land
        // between them rather than snapping to either. Interpolation is in log-f, so the answer is
        // the geometric mean of the two bin frequencies, not the arithmetic one.
        var trace = new double[64];
        for (int k = 1; k < trace.Length; k++)
            trace[k] = 0.0;
        trace[40] = -2.0;
        trace[41] = -4.0;
        for (int k = 42; k < trace.Length; k++)
            trace[k] = -20.0;

        var result = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0, firstBin: 1, lastBin: 63);

        Assert.Equal(Math.Sqrt(40.0 * 41.0), result.UpperHz, 6);
    }

    [Fact]
    public void Find_StopsAtTheFirstCrossing_NotTheNearestValueAnywhere()
    {
        // The reference app searched every bin for whichever came closest in level to the threshold.
        // Here a deep notch well past the roll-off sits at exactly -3 dB; that must not be mistaken
        // for the corner, which is at bin 20.
        var trace = new double[256];
        for (int k = 1; k < trace.Length; k++)
            trace[k] = k <= 20 ? 0.0 : -30.0;
        trace[200] = -3.0;

        var result = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0);

        Assert.InRange(result.UpperHz, 20.0, 21.0);
    }

    [Fact]
    public void Find_WhenTheResponseNeverFalls_ClampsAndSaysSo()
    {
        var trace = new double[128];
        for (int k = 1; k < trace.Length; k++)
            trace[k] = 0.0;

        var result = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0, firstBin: 1, lastBin: 127);

        Assert.True(result.LowerClamped);
        Assert.True(result.UpperClamped);
        Assert.Equal(1.0, result.LowerHz);
        Assert.Equal(127.0, result.UpperHz);
    }

    [Fact]
    public void Find_RespectsTheSearchRange()
    {
        // Outside the swept band the estimate is meaningless, so a caller restricts the search. The
        // roll-off at bin 10 must be invisible when the search starts at bin 40.
        var trace = new double[256];
        for (int k = 1; k < trace.Length; k++)
            trace[k] = k < 10 ? -40.0 : 0.0;
        for (int k = 150; k < trace.Length; k++)
            trace[k] = -40.0;

        var result = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0, firstBin: 40, lastBin: 200);

        Assert.True(result.LowerClamped);
        Assert.Equal(40.0, result.LowerHz);
        Assert.InRange(result.UpperHz, 149.0, 150.0);
    }

    [Fact]
    public void Find_OneDbIsNarrowerThanThreeDb()
    {
        var trace = new double[512];
        for (int k = 1; k < trace.Length; k++)
        {
            double octaves = Math.Log2(k / 256.0);
            // A smooth hump peaking at bin 256, steep enough that both -3 dB corners (about bins
            // 141 and 466) stay inside the array -- a corner that runs off the end clamps, and two
            // clamped corners would compare equal and prove nothing.
            trace[k] = -4.0 * octaves * octaves;
        }

        var oneDb = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 1.0);
        var threeDb = BandwidthAnalyzer.Find(trace, BinHz, dropDb: 3.0);

        Assert.True(oneDb.BandwidthHz < threeDb.BandwidthHz);
        Assert.True(oneDb.LowerHz > threeDb.LowerHz);
        Assert.True(oneDb.UpperHz < threeDb.UpperHz);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    public void Find_RejectsANonPositiveDrop(double dropDb)
    {
        var trace = new double[64];
        Assert.Throws<ArgumentOutOfRangeException>(() => BandwidthAnalyzer.Find(trace, BinHz, dropDb));
    }

    /// <summary>
    /// A flat passband from 20 Hz to 20 kHz with a tall narrow resonance above it — an output
    /// transformer's leakage inductance against its winding capacitance, which every real amplifier
    /// has and which the simulator's tube-amp model puts at 38 kHz.
    /// </summary>
    private static (double[] TraceDb, double[] FreqHz) TubeAmpLikeTrace(double resonanceDb = 4.5)
    {
        const int points = 600;
        var freqHz = new double[points];
        var traceDb = new double[points];

        for (int i = 0; i < points; i++)
        {
            // 2 Hz to 200 kHz, log spaced, so the shape is the one the chart draws.
            freqHz[i] = 2.0 * Math.Pow(10.0, 5.0 * i / (points - 1.0));

            double f = freqHz[i];
            // One-pole corners at 20 Hz and 46 kHz for the passband. The top corner is above the
            // resonance on purpose -- that is what the tube-amp model does, and it is the whole
            // reason the resonance ends up taller than the passband rather than buried under the
            // roll-off. A 20 kHz corner would put the peak back inside the passband and the failure
            // this file pins would not reproduce.
            double lowRolloff = -10.0 * Math.Log10(1.0 + Math.Pow(20.0 / f, 2.0));
            double highRolloff = -10.0 * Math.Log10(1.0 + Math.Pow(f / 46_000.0, 2.0));
            // ...plus a Q 3.5 resonance at 38 kHz sitting on top of it.
            double detune = (f / 38_000.0) - (38_000.0 / f);
            double resonance = resonanceDb / (1.0 + Math.Pow(3.5 * detune, 2.0));

            traceDb[i] = lowRolloff + highRolloff + resonance;
        }

        return (traceDb, freqHz);
    }

    [Fact]
    public void Find_AgainstAPeakReference_ReportsTheResonanceInsteadOfTheAmplifier()
    {
        // The bug this reference mode exists for, pinned so it cannot be reintroduced by "simplifying"
        // the reference back to the peak: the -1 dB threshold sits above the whole passband, so both
        // corners land on the resonance's own skirts and the answer is a couple of kHz wide.
        (double[] traceDb, double[] freqHz) = TubeAmpLikeTrace();

        var result = BandwidthAnalyzer.Find(traceDb, freqHz, dropDb: 1.0);

        Assert.InRange(result.LowerHz, 30_000.0, 38_000.0);
        Assert.InRange(result.UpperHz, 38_000.0, 50_000.0);
        Assert.True(result.BandwidthHz < 20_000.0);
    }

    [Fact]
    public void Find_AgainstAPassbandReference_ReportsTheAmplifier()
    {
        // The same trace, referenced to the median over 200 Hz - 2 kHz: the corners are now the
        // amplifier's own, near the 20 Hz and 20 kHz poles that put them there.
        (double[] traceDb, double[] freqHz) = TubeAmpLikeTrace();

        var reference = BandwidthAnalyzer.FindReference(traceDb, freqHz, BandwidthReferenceSpec.DefaultBand);
        var result = BandwidthAnalyzer.Find(traceDb, freqHz, dropDb: 3.0, reference, symmetric: false);

        Assert.False(reference.FellBackToPeak);
        Assert.Equal(BandwidthReferenceMode.BandMedian, reference.Mode);
        Assert.InRange(result.LowerHz, 18.0, 23.0);
        // The one-sided -3 dB search walks straight through the resonance, so the upper corner is on
        // its far side rather than at the 20 kHz pole -- see the analyzer's remarks on why that is
        // the right answer to this particular question and the wrong one to the flatness question.
        Assert.True(result.UpperHz > 38_000.0);
    }

    [Fact]
    public void Find_Symmetric_StopsWhereTheResponseRisesOutOfTolerance()
    {
        // The flatness question: +-1 dB of the passband. The resonance's rising edge ends the band,
        // which the one-sided search above cannot see at all.
        (double[] traceDb, double[] freqHz) = TubeAmpLikeTrace();

        var reference = BandwidthAnalyzer.FindReference(traceDb, freqHz, BandwidthReferenceSpec.DefaultBand);
        var symmetric = BandwidthAnalyzer.Find(traceDb, freqHz, dropDb: 1.0, reference, symmetric: true);
        var oneSided = BandwidthAnalyzer.Find(traceDb, freqHz, dropDb: 1.0, reference, symmetric: false);

        Assert.True(symmetric.Symmetric);
        Assert.True(symmetric.UpperHz < oneSided.UpperHz);
        Assert.True(symmetric.UpperHz < 38_000.0);
        // The rise into the resonance is what stopped it, so the level there is above the reference.
        Assert.True(symmetric.UpperHz > 20_000.0);
    }

    [Fact]
    public void Find_SymmetricUnderAPeakReference_MatchesTheOneSidedSearch()
    {
        // Nothing in a trace exceeds its own maximum, so the upper threshold is unreachable and the
        // two searches cannot disagree. That equivalence is what lets the UI label one row "+-1 dB"
        // unconditionally without changing what a peak-referenced run has always reported.
        (double[] traceDb, double[] freqHz) = TubeAmpLikeTrace();

        var reference = BandwidthAnalyzer.FindReference(traceDb, freqHz, BandwidthReferenceSpec.Peak);
        var symmetric = BandwidthAnalyzer.Find(traceDb, freqHz, dropDb: 1.0, reference, symmetric: true);
        var oneSided = BandwidthAnalyzer.Find(traceDb, freqHz, dropDb: 1.0, reference, symmetric: false);

        Assert.Equal(oneSided.LowerHz, symmetric.LowerHz, 9);
        Assert.Equal(oneSided.UpperHz, symmetric.UpperHz, 9);
    }

    [Fact]
    public void FindReference_TakesTheMedian_NotTheMeanAndNotThePeak()
    {
        // One bin of the reference band is 40 dB up -- a resonance, or a mains harmonic that survived
        // smoothing. The median ignores it; a mean would be dragged 4 dB by it and the peak 40 dB.
        var freqHz = new double[100];
        var traceDb = new double[100];
        for (int i = 0; i < 100; i++)
        {
            freqHz[i] = 10.0 * Math.Pow(10.0, 3.0 * i / 99.0);
            traceDb[i] = -6.0;
        }

        int inBand = Array.FindIndex(freqHz, f => f >= 200.0);
        traceDb[inBand] = 34.0;

        var reference = BandwidthAnalyzer.FindReference(traceDb, freqHz, BandwidthReferenceSpec.DefaultBand);

        Assert.Equal(-6.0, reference.LevelDb, 9);
    }

    [Fact]
    public void FindReference_AnchorsOnTheBandsGeometricCentre()
    {
        // 200-2000 Hz: the geometric centre is 632 Hz, not the arithmetic 1100 Hz. The distinction
        // decides where the outward walk starts, which on a rippled trace decides which ripple ends
        // it.
        var freqHz = new double[300];
        var traceDb = new double[300];
        for (int i = 0; i < 300; i++)
        {
            freqHz[i] = 10.0 * Math.Pow(10.0, 4.0 * i / 299.0);
            traceDb[i] = 0.0;
        }

        var reference = BandwidthAnalyzer.FindReference(traceDb, freqHz, BandwidthReferenceSpec.DefaultBand);

        // Within one step of this grid (3.1 % apart) of 632 Hz -- and nowhere near the 1100 Hz an
        // arithmetic centre would pick.
        Assert.InRange(reference.FreqHz, 613.0, 653.0);
    }

    [Fact]
    public void FindReference_WhenTheBandIsOutsideTheSearchedRange_FallsBackToThePeakAndSaysSo()
    {
        // A 20-24 kHz measurement asked to reference 200 Hz - 2 kHz has not measured the band it is
        // quoting against. Substituting the peak is the only thing left to do; doing it silently is
        // what the flag prevents.
        var freqHz = new double[64];
        var traceDb = new double[64];
        for (int i = 0; i < 64; i++)
        {
            freqHz[i] = 20_000.0 + (i * 62.5);
            traceDb[i] = i == 30 ? 3.0 : 0.0;
        }

        var reference = BandwidthAnalyzer.FindReference(traceDb, freqHz, BandwidthReferenceSpec.DefaultBand);

        Assert.True(reference.FellBackToPeak);
        Assert.Equal(BandwidthReferenceMode.Peak, reference.Mode);
        Assert.Equal(3.0, reference.LevelDb, 9);
        Assert.Equal(30, reference.Index);
    }

    [Fact]
    public void FindReference_Peak_NeverReportsAFallback()
    {
        // A caller that asked for the peak got the peak; flagging that as a substitution would put a
        // "reference band not measured" warning on every bandpass measurement in the app.
        (double[] traceDb, double[] freqHz) = TubeAmpLikeTrace();

        var reference = BandwidthAnalyzer.FindReference(traceDb, freqHz, BandwidthReferenceSpec.Peak);

        Assert.False(reference.FellBackToPeak);
    }

    [Fact]
    public void Find_CarriesTheReferenceItMeasuredFrom()
    {
        // The chart draws the reference line and both thresholds from these, so a reading that did
        // not carry its reference would leave the client re-deriving it from the decimated columns --
        // which is exactly the trace the corners were deliberately not searched on.
        (double[] traceDb, double[] freqHz) = TubeAmpLikeTrace();

        var reference = BandwidthAnalyzer.FindReference(traceDb, freqHz, BandwidthReferenceSpec.DefaultBand);
        var result = BandwidthAnalyzer.Find(traceDb, freqHz, dropDb: 3.0, reference, symmetric: false);

        Assert.Equal(reference.LevelDb, result.ReferenceDb, 9);
        Assert.Equal(3.0, result.DropDb, 9);
    }

    [Fact]
    public void Find_WithAReferenceBandThatStraddlesTheRolloff_StillFindsBothCorners()
    {
        // The reference band need not sit in the middle of the response: a bandpass measured with the
        // default band still gets a sane answer, because the median is taken over whatever part of
        // the band was searched rather than assumed to be flat.
        var freqHz = new double[400];
        var traceDb = new double[400];
        for (int i = 0; i < 400; i++)
        {
            freqHz[i] = 10.0 * Math.Pow(10.0, 4.0 * i / 399.0);
            double detune = (freqHz[i] / 1000.0) - (1000.0 / freqHz[i]);
            traceDb[i] = -10.0 * Math.Log10(1.0 + Math.Pow(2.0 * detune, 2.0));
        }

        var reference = BandwidthAnalyzer.FindReference(traceDb, freqHz, BandwidthReferenceSpec.DefaultBand);
        var result = BandwidthAnalyzer.Find(traceDb, freqHz, dropDb: 3.0, reference, symmetric: false);

        Assert.False(result.LowerClamped);
        Assert.False(result.UpperClamped);
        Assert.True(result.LowerHz < 1000.0);
        Assert.True(result.UpperHz > 1000.0);
    }
}
