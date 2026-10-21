using System.Numerics;
using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// That the two trace builders put the <b>Smoothing</b> setting on the phase column as well as on
/// the magnitude one.
///
/// <para>The smoothers themselves are pinned in <c>PhaseSmoothingTests</c>; what these cover is the
/// wiring, which is what was missing. Until 2026-10-20 the magnitude column of a stored trace was an
/// RMS over a fractional-octave band while the phase column beside it was the <i>centre bin</i> of
/// that band, raw — so one capture drew a readable curve and an unreadable one, and the chart looked
/// broken on any measurement whose phase was not well determined.</para>
/// </summary>
public class SmoothedTracePhaseTests
{
    private const int BinCount = 4097;

    /// <summary>A curve with a steady angle under noise that swings it by tens of degrees per bin.</summary>
    private static Complex[] NoisyImpedance(double trueDeg, double ohms, int seed)
    {
        var random = new Random(seed);
        var values = new Complex[BinCount];
        for (int k = 0; k < BinCount; k++)
        {
            double deg = trueDeg + ((random.NextDouble() - 0.5) * 50.0);
            values[k] = Complex.FromPolarCoordinates(ohms, deg * Math.PI / 180.0);
        }

        return values;
    }

    private static ImpedanceRequest ImpedanceAt(int denominator) => new(
        ImpedanceMethod.ReferenceResistor,
        ImpedanceStimulus.Chirp,
        ReferenceOhms: 100.0,
        RightChannelIsReference: false,
        FrequencySpanHz: 24000.0,
        ResolutionMultiplier: 1,
        LevelValue: -20.0,
        LevelUnit: ThdSweepLevelUnit.Dbv,
        SmoothingDenominator: denominator,
        AverageCount: 8);

    /// <summary>The scatter of a column against the one before it, over the analysed band.</summary>
    private static double RmsStep(IReadOnlyList<float> values, int from, int to)
    {
        double sum = 0.0;
        for (int i = from + 1; i <= to; i++)
        {
            double step = values[i] - values[i - 1];
            sum += step * step;
        }

        return Math.Sqrt(sum / (to - from));
    }

    [Fact]
    public void AnImpedanceTrace_SmoothsItsPhaseColumn_NotJustItsMagnitude()
    {
        var values = NoisyImpedance(trueDeg: 15.0, ohms: 8.0, seed: 4039);
        var coherence = new double[BinCount];
        Array.Fill(coherence, 0.3);

        var raw = ImpedanceTraceBuilder.Build(
            ImpedanceAt(0), ImpedanceTraceKind.Impedance, values, coherence, displayColumns: 1600);
        var smoothed = ImpedanceTraceBuilder.Build(
            ImpedanceAt(48), ImpedanceTraceKind.Impedance, values, coherence, displayColumns: 1600);

        int from = raw.PhaseDeg.Length / 4;
        int to = raw.PhaseDeg.Length - 2;

        double rawStep = RmsStep(raw.PhaseDeg, from, to);
        double smoothedStep = RmsStep(smoothed.PhaseDeg, from, to);

        Assert.True(rawStep > 5.0, $"the unsmoothed phase was not noisy enough to be a test: {rawStep}");
        Assert.True(smoothedStep < rawStep / 3.0, $"the phase was not smoothed: {smoothedStep} against {rawStep}");

        // And it still reports the angle that is actually there, rather than flattening toward zero.
        // Averaged over the columns rather than read off one: a fractional-octave band is only a few
        // bins wide at the bottom of a log-decimated trace, so a single low column is still mostly
        // noise -- the smoothing a setting buys is a property of frequency, not a constant.
        double mean = smoothed.PhaseDeg.Skip(from).Take(to - from).Average();
        Assert.True(Math.Abs(mean - 15.0) < 2.0, $"the smoothed angle averaged {mean}");
    }

    [Fact]
    public void AnImpedanceTrace_AtSmoothingNone_KeepsTheBinsOwnPhase()
    {
        var values = NoisyImpedance(trueDeg: -40.0, ohms: 2.0, seed: 60);
        var coherence = new double[BinCount];

        var trace = ImpedanceTraceBuilder.Build(
            ImpedanceAt(0), ImpedanceTraceKind.Impedance, values, coherence, displayColumns: 1600);

        // Every column is one bin's own angle, unchanged, which is what "None" has always meant.
        for (int c = 0; c < trace.PhaseDeg.Length; c++)
        {
            double deg = trace.PhaseDeg[c];
            Assert.Contains(values, z => Math.Abs((z.Phase * 180.0 / Math.PI) - deg) < 1e-4);
        }
    }

    [Fact]
    public void AResponseTrace_SmoothsItsPhaseColumn_AndKeepsTheTurnsTheAnchorNeeds()
    {
        var request = new FrequencyResponseRequest(
            FrequencySpanHz: 24_000.0, ResolutionMultiplier: 1,
            ThdSweepLevelMode.GeneratorVoltage, LevelValue: -20.0, ThdSweepLevelUnit.Dbv, LoadOhms: 8.0,
            RightChannelIsReference: true, SmoothingDenominator: 48, AverageCount: 8, Continuous: false);

        int binCount = (request.FftSize / 2) + 1;
        var random = new Random(1077);
        var magnitude = new double[binCount];
        var phaseDeg = new double[binCount];
        var coherence = new double[binCount];
        for (int k = 0; k < magnitude.Length; k++)
        {
            magnitude[k] = 1.0;
            // A steady lag plus noise: unwrapped, so it runs well past a turn and must stay there.
            phaseDeg[k] = -400.0 + ((random.NextDouble() - 0.5) * 50.0);
            coherence[k] = 0.4;
        }

        var result = new FrequencyResponseResult(magnitude, phaseDeg, coherence, coherence, LatencySamples: 0.0);

        var raw = ResponseCaptureSupport.BuildTrace(
            request with { SmoothingDenominator = 0 }, 1600, FrequencyResponseTraceKind.Gain, result, 0.0, 0.0);
        var smoothed = ResponseCaptureSupport.BuildTrace(
            request, 1600, FrequencyResponseTraceKind.Gain, result, 0.0, 0.0);

        int from = raw.PhaseDeg.Length / 4;
        int to = raw.PhaseDeg.Length - 2;

        double rawStep = RmsStep(raw.PhaseDeg, from, to);
        double smoothedStep = RmsStep(smoothed.PhaseDeg, from, to);

        Assert.True(rawStep > 5.0, $"the unsmoothed phase was not noisy enough to be a test: {rawStep}");
        Assert.True(smoothedStep < rawStep / 3.0, $"the phase was not smoothed: {smoothedStep} against {rawStep}");

        // The anchor takes the whole turns off both, so the two agree about where the curve sits.
        Assert.True(
            Math.Abs(smoothed.PhaseDeg[smoothed.PhaseDeg.Length / 2] - raw.PhaseDeg[raw.PhaseDeg.Length / 2]) < 30.0,
            "smoothing moved the curve rather than quieting it");
    }

    /// <summary>
    /// That the builders <b>ask</b> for the analysed band when they smooth.
    ///
    /// <para>The smoother's own clamp has been tested since it was written; what was missing was a
    /// test that the impedance builder passes it, and it did not. The spike it is there to stop came
    /// straight back when the stimulus changed, because nothing here was watching: a 1/3-octave
    /// window reaches 12 % past the top analysed bin, and an impedance inverts an out-of-band ratio
    /// to the open-circuit ceiling, so one such bin in the band is the whole RMS.</para>
    /// </summary>
    [Fact]
    public void AnImpedanceTrace_SmoothsOnlyWithinTheAnalysedBand()
    {
        var request = ImpedanceAt(3);
        int binCount = request.BinCount;
        double binHz = request.BinHz;
        int lastBin = (int)Math.Floor(request.AnalysisHighHz / binHz);

        // A flat 1.06 ohm fixture, and the open-circuit ceiling everywhere the stimulus does not go.
        var values = new Complex[binCount];
        for (int k = 0; k < binCount; k++)
            values[k] = new Complex(k <= lastBin ? 1.06 : ImpedanceMath.MaxOhms, 0.0);

        var coherence = new double[binCount];
        Array.Fill(coherence, 0.99);

        var trace = ImpedanceTraceBuilder.Build(
            request, ImpedanceTraceKind.Impedance, values, coherence, displayColumns: 1600);

        for (int c = trace.FreqHz.Length - 20; c < trace.FreqHz.Length; c++)
        {
            double ohms = ImpedanceMath.FromDbOhms(trace.MagnitudeMaxDbOhms[c]);
            Assert.True(
                Math.Abs(ohms - 1.06) < 0.01,
                $"{trace.FreqHz[c]:0} Hz read {ohms:0.000} ohms: the smoothing band reached out of the analysed one");
        }
    }
}
