using Qa403.Dsp;

namespace Qa403.Dsp.Tests;

/// <summary>
/// Group and phase delay against closed forms: a pure delay is a constant, a one-pole low pass is
/// <c>1/(2π·fc·(1 + (f/fc)²))</c>, and the whole point of the regression is that it beats a bin
/// difference on a noisy phase.
/// </summary>
public class GroupDelayTests
{
    private const double BinHz = 48000.0 / 131072.0; // 0.366 Hz, the chirp's finest grid
    private const int BinCount = 65537;
    private static readonly int FirstBin = (int)Math.Ceiling(4.0 / BinHz);
    private static readonly int LastBin = (int)Math.Floor(22000.0 / BinHz);

    private static double[] LinearPhase(double delayS, double offsetDeg = 0.0)
    {
        var phase = new double[BinCount];
        for (int k = 0; k < BinCount; k++)
            phase[k] = offsetDeg - (360.0 * k * BinHz * delayS);
        return phase;
    }

    private static IEnumerable<int> MidBandBins(int margin = 2000)
    {
        for (int k = FirstBin + margin; k <= LastBin - margin; k += 97)
            yield return k;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(69)]
    [InlineData(48)]
    [InlineData(24)]
    [InlineData(12)]
    [InlineData(6)]
    [InlineData(3)]
    public void PureDelay_ReadsAConstantGroupDelay_ToANanosecond(int denominator)
    {
        const double delayS = 1.234e-3;
        var tau = GroupDelay.FromUniformPhase(LinearPhase(delayS), BinHz, denominator, FirstBin, LastBin);

        for (int k = FirstBin; k <= LastBin; k++)
            Assert.True(Math.Abs(tau[k] - delayS) < 1e-9, $"bin {k}: {tau[k]} vs {delayS} at 1/{denominator}");

        Assert.Equal(0.0, tau[0]);
        Assert.Equal(0.0, tau[LastBin + 1]);
    }

    [Fact]
    public void OnePoleLowPass_MatchesTheAnalyticGroupDelay_Within0Point1Percent()
    {
        const double fc = 1000.0;
        var phase = new double[BinCount];
        for (int k = 0; k < BinCount; k++)
            phase[k] = -Math.Atan(k * BinHz / fc) * 180.0 / Math.PI;

        var tau = GroupDelay.FromUniformPhase(phase, BinHz, denominator: 24, FirstBin, LastBin);

        foreach (int k in MidBandBins())
        {
            double f = k * BinHz;
            double expected = 1.0 / (2.0 * Math.PI * fc * (1.0 + (f / fc) * (f / fc)));
            Assert.True(Math.Abs(tau[k] - expected) <= 1e-3 * expected,
                $"{f:F1} Hz: {tau[k] * 1e6:F3} µs vs {expected * 1e6:F3} µs");
        }
    }

    [Fact]
    public void RegressionAndCentralDifference_AgreeOnALinearPhase()
    {
        var phase = LinearPhase(250e-6, offsetDeg: 720.0);
        var regression = GroupDelay.FromUniformPhase(phase, BinHz, 12, FirstBin, LastBin);
        var difference = GroupDelay.FromUniformPhase(phase, BinHz, 0, FirstBin, LastBin);

        for (int k = FirstBin; k <= LastBin; k++)
            Assert.Equal(difference[k], regression[k], 12);
    }

    [Fact]
    public void NoisyPhase_RegressionBeatsFiniteDifference()
    {
        const double delayS = 20e-6;
        var random = new Random(4242);
        var phase = LinearPhase(delayS);
        for (int k = 0; k < BinCount; k++)
            phase[k] += (random.NextDouble() - 0.5) * 4.0; // ±2° uniform

        var regression = GroupDelay.FromUniformPhase(phase, BinHz, 12, FirstBin, LastBin);
        var difference = GroupDelay.FromUniformPhase(phase, BinHz, 0, FirstBin, LastBin);

        double Rms(double[] tau)
        {
            double sum = 0.0;
            int n = 0;
            foreach (int k in MidBandBins())
            {
                sum += (tau[k] - delayS) * (tau[k] - delayS);
                n++;
            }
            return Math.Sqrt(sum / n);
        }

        double regressionRms = Rms(regression);
        double differenceRms = Rms(difference);

        // ±2° on 0.37 Hz bins is milliseconds of group-delay noise per bin; the fit has to bring
        // that down by orders of magnitude, not just a little.
        Assert.True(differenceRms > 1e-3, $"finite difference rms {differenceRms}");
        Assert.True(regressionRms < differenceRms / 20.0, $"regression {regressionRms} vs difference {differenceRms}");
    }

    [Fact]
    public void Window_ClampsToTheAnalysedBand_SoOutOfBandTurnsDoNotLeakIn()
    {
        const double delayS = 100e-6;
        var phase = LinearPhase(delayS);
        var random = new Random(7);
        for (int k = 0; k < FirstBin; k++)
            phase[k] = (random.NextDouble() - 0.5) * 1e5;
        for (int k = LastBin + 1; k < BinCount; k++)
            phase[k] = (random.NextDouble() - 0.5) * 1e5;

        var tau = GroupDelay.FromUniformPhase(phase, BinHz, 3, FirstBin, LastBin);

        Assert.True(Math.Abs(tau[FirstBin] - delayS) < 1e-9, $"first bin {tau[FirstBin]}");
        Assert.True(Math.Abs(tau[FirstBin + 1] - delayS) < 1e-9, $"second bin {tau[FirstBin + 1]}");
        Assert.True(Math.Abs(tau[LastBin] - delayS) < 1e-9, $"last bin {tau[LastBin]}");
        Assert.Equal(0.0, tau[FirstBin - 1]);
        Assert.Equal(0.0, tau[LastBin + 1]);
    }

    [Fact]
    public void OnGrid_CentralDifference_IsOneSidedAtTheEnds()
    {
        const double delayS = 3.5e-3;
        var freqHz = new double[40];
        var phase = new double[40];
        for (int i = 0; i < freqHz.Length; i++)
        {
            freqHz[i] = 20.0 * Math.Pow(1000.0, i / 39.0);
            phase[i] = 45.0 - (360.0 * freqHz[i] * delayS);
        }

        var tau = GroupDelay.FromPhaseOnGrid(phase, freqHz);

        Assert.Equal(freqHz.Length, tau.Length);
        foreach (double value in tau)
            Assert.Equal(delayS, value, 12);
    }

    [Fact]
    public void OnGrid_WithFewerThanTwoPoints_ReadsZero()
    {
        Assert.Empty(GroupDelay.FromPhaseOnGrid([], []));
        Assert.Equal([0.0], GroupDelay.FromPhaseOnGrid([90.0], [1000.0]));
    }

    [Fact]
    public void PhaseDelay_OfAPureDelay_IsTheDelay()
    {
        const double delayS = 2.0e-3;
        var tau = GroupDelay.PhaseDelayUniform(LinearPhase(delayS), BinHz, FirstBin, LastBin, anchorBin: null);

        for (int k = FirstBin; k <= LastBin; k++)
            Assert.Equal(delayS, tau[k], 12);
        Assert.Equal(0.0, tau[0]);
    }

    [Fact]
    public void PhaseDelay_AnchorRemovesWholeTurns()
    {
        // A one-pole's phase, then the same curve carrying two spurious turns from an unwrapper
        // that started in the sub-band junk. Anchored at a passband bin, both read the same.
        const double fc = 1000.0;
        var clean = new double[BinCount];
        for (int k = 0; k < BinCount; k++)
            clean[k] = -Math.Atan(k * BinHz / fc) * 180.0 / Math.PI;
        var wound = clean.Select(p => p + 720.0).ToArray();

        int anchor = (int)Math.Round(632.0 / BinHz);
        var expected = GroupDelay.PhaseDelayUniform(clean, BinHz, FirstBin, LastBin, anchor);
        var actual = GroupDelay.PhaseDelayUniform(wound, BinHz, FirstBin, LastBin, anchor);

        for (int k = FirstBin; k <= LastBin; k++)
            Assert.Equal(expected[k], actual[k], 12);

        // And the analytic figure at the anchor itself.
        double f = anchor * BinHz;
        Assert.Equal(Math.Atan(f / fc) / (2.0 * Math.PI * f), actual[anchor], 12);
    }

    [Fact]
    public void PhaseDelay_WithoutAnAnchor_KeepsTheTurns()
    {
        var clean = LinearPhase(1e-3);
        var wound = clean.Select(p => p + 360.0).ToArray();

        var unanchored = GroupDelay.PhaseDelayUniform(wound, BinHz, FirstBin, LastBin, anchorBin: null);
        var reference = GroupDelay.PhaseDelayUniform(clean, BinHz, FirstBin, LastBin, anchorBin: null);

        int k = FirstBin + 100;
        Assert.Equal(reference[k] - 1.0 / (k * BinHz), unanchored[k], 12);
    }

    [Fact]
    public void PhaseDelay_InvertingDut_ReadsHalfAPeriod()
    {
        var inverted = new double[BinCount];
        Array.Fill(inverted, 180.0);

        var tau = GroupDelay.PhaseDelayUniform(inverted, BinHz, FirstBin, LastBin, anchorBin: FirstBin + 50);

        foreach (int k in MidBandBins())
            Assert.Equal(-1.0 / (2.0 * k * BinHz), tau[k], 12);

        // The other branch of the same inversion: −180° anchors to +180°, so the sign is the same.
        Array.Fill(inverted, -180.0);
        var other = GroupDelay.PhaseDelayUniform(inverted, BinHz, FirstBin, LastBin, anchorBin: FirstBin + 50);
        Assert.Equal(tau[FirstBin + 500], other[FirstBin + 500], 12);
    }

    [Fact]
    public void PhaseDelay_OnGrid_MatchesTheDefinition()
    {
        double[] freqHz = [100.0, 1000.0, 10000.0];
        double[] phase = [-36.0, -360.0, -3600.0]; // 1 ms of delay
        var tau = GroupDelay.PhaseDelayOnGrid(phase, freqHz, anchorIndex: null);
        Assert.All(tau, v => Assert.Equal(1e-3, v, 12));
    }

    [Fact]
    public void PhaseDelay_RefusesAZeroFrequency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroupDelay.PhaseDelayUniform(new double[16], 1.0, firstBin: 0, lastBin: 8, anchorBin: null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroupDelay.PhaseDelayOnGrid([0.0, 10.0], [0.0, 100.0], anchorIndex: null));
    }
}
