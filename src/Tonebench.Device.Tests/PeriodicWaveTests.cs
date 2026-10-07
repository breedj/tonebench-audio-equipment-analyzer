using System.Numerics;
using Tonebench.Device.Hardware;
using Tonebench.Dsp.Fft;

namespace Tonebench.Device.Tests;

/// <summary>
/// The square and triangle stimuli. The spectral tests are the ones that matter: the whole reason
/// these are hard-switched at an exact integer sample period rather than built from a free-running
/// phase accumulator is that the result has to be free of inharmonic content, and that is a claim
/// about a transform rather than about a waveform.
/// </summary>
public class PeriodicWaveTests
{
    private const double SampleRateHz = 48_000.0;

    private static ChannelGeneratorConfig Channel(GeneratorKind kind, double freqHz, double levelDbv) =>
        new(Enabled: true, Tones: [])
        {
            Kind = kind,
            Wave = new PeriodicWaveSettings { FrequencyHz = freqHz, LevelDbv = levelDbv },
        };

    private static double[] Record(GeneratorKind kind, double freqHz, double levelDbv, int frames)
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = Channel(kind, freqHz, levelDbv);
        return [.. Enumerable.Range(0, frames).Select(i => waveform.ComputeSample(channel, i))];
    }

    /// <summary>
    /// Amplitude per bin of a rectangular-windowed record, which is exact here because every record
    /// in these tests holds a whole number of cycles.
    /// </summary>
    private static double[] BinAmplitudes(double[] record)
    {
        var spectrum = new Complex[record.Length / 2 + 1];
        new FftFlatBackend().Forward(record, spectrum);
        return [.. spectrum.Select(c => 2.0 * c.Magnitude / record.Length)];
    }

    [Theory]
    // Exact: 48 kHz divides by 1 kHz to 48 frames, already even.
    [InlineData(1000.0, 48, 1000.0)]
    // 10 kHz wants 4.8 frames; the nearest even period is 4 (not 6 — the half period rounds to 2).
    [InlineData(10_000.0, 4, 12_000.0)]
    // 700 Hz wants 68.57; half rounds to 34, so 68 frames and 705.88 Hz.
    [InlineData(700.0, 68, 48_000.0 / 68)]
    // Below the floor: a period can never be shorter than four frames, so fs/4 is the ceiling.
    [InlineData(40_000.0, 4, 12_000.0)]
    public void PeriodFrames_IsTheNearestWholeEvenPeriod_AndTheFrequencyFollowsIt(
        double requestedHz, int expectedFrames, double expectedHz)
    {
        Assert.Equal(expectedFrames, PeriodicWave.PeriodFrames(requestedHz, SampleRateHz));
        Assert.Equal(expectedHz, PeriodicWave.EffectiveFrequencyHz(requestedHz, SampleRateHz), precision: 6);
    }

    /// <summary>
    /// An odd period would put the extra sample on one half of a square and leave a DC offset and a
    /// full set of even harmonics behind it — the reason the quantisation is to an even number
    /// rather than to the nearer whole one.
    /// </summary>
    [Theory]
    [InlineData(1000.0)]
    [InlineData(700.0)]
    [InlineData(333.0)]
    [InlineData(10_000.0)]
    public void PeriodFrames_IsAlwaysEven(double requestedHz)
    {
        Assert.Equal(0, PeriodicWave.PeriodFrames(requestedHz, SampleRateHz) % 2);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    public void PeriodFrames_WithNoFrequencyToPlay_IsZero(double requestedHz)
    {
        Assert.Equal(0, PeriodicWave.PeriodFrames(requestedHz, SampleRateHz));
        Assert.Equal(0.0, PeriodicWave.Sample(GeneratorKind.Square, new PeriodicWaveSettings { FrequencyHz = requestedHz }, 17, SampleRateHz));
    }

    /// <summary>
    /// The level is rms for every kind here, so one level is three different peaks. Getting this
    /// wrong is not cosmetic: <see cref="OutputRangeSelector"/> derives the DAC's attenuator from
    /// it before a single sample is played, so a triangle treated as a sine clips every edge.
    /// </summary>
    [Fact]
    public void PeakVolts_FollowsTheShapesOwnCrestFactor_NotASines()
    {
        // 0 dBV = 1 Vrms.
        Assert.Equal(1.0, PeriodicWave.PeakVolts(GeneratorKind.Square, 0.0), precision: 12);
        Assert.Equal(Math.Sqrt(3.0), PeriodicWave.PeakVolts(GeneratorKind.Triangle, 0.0), precision: 12);
    }

    [Fact]
    public void ARecordHoldsExactlyTheRmsItWasAskedFor()
    {
        // 750 Hz is 64 frames at 48 kHz, so 4096 samples hold 64 whole cycles.
        double[] square = Record(GeneratorKind.Square, 750.0, -6.0, 4096);
        double expectedRms = Math.Pow(10.0, -6.0 / 20.0);

        Assert.Equal(expectedRms, Math.Sqrt(square.Average(v => v * v)), precision: 9);
        Assert.Equal(0.0, square.Average(), precision: 12);
    }

    /// <summary>
    /// A triangle's sampled rms is its continuous-time 1/sqrt(3) only in the limit — a very short
    /// period reads a few tenths of a decibel high, because the samples miss the corners. At any
    /// ordinary audio frequency that is far below the DAC's own calibration error.
    /// </summary>
    [Fact]
    public void ATrianglesRms_IsTheContinuousTimeFigure_ToWithinTheSamplingItself()
    {
        double[] triangle = Record(GeneratorKind.Triangle, 750.0, -6.0, 4096);
        double expectedRms = Math.Pow(10.0, -6.0 / 20.0);

        Assert.Equal(expectedRms, Math.Sqrt(triangle.Average(v => v * v)), tolerance: expectedRms * 0.002);
        Assert.Equal(0.0, triangle.Average(), precision: 12);
    }

    /// <summary>
    /// The claim the whole design rests on: a waveform whose period is a whole number of samples is
    /// periodic in the sampled domain, so its spectrum lives *only* on multiples of its own
    /// fundamental. Nothing folds down to an inharmonic frequency, however many harmonics the ideal
    /// shape has above Nyquist.
    /// </summary>
    [Theory]
    [InlineData(GeneratorKind.Square)]
    [InlineData(GeneratorKind.Triangle)]
    public void EveryLineLandsOnAHarmonic_AndEveryEvenHarmonicIsEmpty(GeneratorKind kind)
    {
        // 750 Hz = 64 frames; 4096 samples = 64 whole cycles, so the fundamental is bin 64.
        const int fundamentalBin = 64;
        double[] amplitudes = BinAmplitudes(Record(kind, 750.0, 0.0, 4096));

        for (int bin = 0; bin < amplitudes.Length; bin++)
        {
            bool odd = bin % fundamentalBin == 0 && (bin / fundamentalBin) % 2 == 1;
            if (odd)
                continue;

            // Everything else — the even harmonics and every bin between harmonics alike.
            Assert.True(
                amplitudes[bin] < 1e-12,
                $"bin {bin} ({bin * SampleRateHz / 4096:0.#} Hz) holds {amplitudes[bin]:E3}, which is neither a harmonic nor silence");
        }
    }

    /// <summary>
    /// The harmonics are <b>not</b> quite the textbook 1/n and 1/n^2 — and this is the test that
    /// says what they are instead, because the difference is on screen in the harmonics table.
    ///
    /// <para>Every ideal harmonic above Nyquist folds back onto an odd harmonic below it and adds,
    /// and the sum has a closed form: a square of period M samples carries <c>4/(M.sin(pi.k/M))</c>
    /// at odd harmonic k, so the ratios are <c>(sin(pi/M)/sin(pi.k/M))^p</c> with p = 1 for a square
    /// and 2 for a triangle. At 64 samples a cycle that is 9.51 dBc at H3 where the textbook says
    /// 9.54, and 0.17 dB out by H7 — small, growing towards Nyquist, and real: it is what the DAC
    /// will put out and therefore what a correct analyzer reads back.</para>
    ///
    /// <para>Asserted against the formula rather than against measured constants so a change to the
    /// synthesis has something to be wrong about, and asserted tightly (1e-6) because an exactly
    /// periodic record transformed with no window is exact arithmetic, not an estimate.</para>
    /// </summary>
    [Theory]
    [InlineData(GeneratorKind.Square, 3)]
    [InlineData(GeneratorKind.Square, 5)]
    [InlineData(GeneratorKind.Square, 7)]
    [InlineData(GeneratorKind.Square, 31)]
    [InlineData(GeneratorKind.Triangle, 3)]
    [InlineData(GeneratorKind.Triangle, 5)]
    [InlineData(GeneratorKind.Triangle, 7)]
    [InlineData(GeneratorKind.Triangle, 31)]
    public void AHarmonicSitsAtTheSampledSeriesRatio_NotTheTextbookOne(GeneratorKind kind, int order)
    {
        // 750 Hz is 64 frames at 48 kHz, so the fundamental is bin 64 of a 4096-point record and
        // harmonic 31 (bin 1984) is the last one under Nyquist.
        const int periodFrames = 64;
        const int fundamentalBin = 64;
        double[] amplitudes = BinAmplitudes(Record(kind, 750.0, 0.0, 4096));

        int power = kind == GeneratorKind.Square ? 1 : 2;
        double sampled = Math.Pow(
            Math.Sin(Math.PI / periodFrames) / Math.Sin(Math.PI * order / periodFrames), power);
        double expected = amplitudes[fundamentalBin] * sampled;

        Assert.Equal(expected, amplitudes[fundamentalBin * order], tolerance: expected * 1e-6);

        // ... and the textbook ratio really is a different number, i.e. this test would pass for the
        // wrong reason if the two agreed.
        double textbook = amplitudes[fundamentalBin] / Math.Pow(order, power);
        if (order >= 7)
            Assert.NotEqual(textbook, amplitudes[fundamentalBin * order], tolerance: expected * 1e-3);
    }

    /// <summary>
    /// What goes on the effective tone list, so the Gen tile, the scope trigger and the gain
    /// reading all name the line the analyzer measures rather than the waveform's total rms. A
    /// square's fundamental is 0.91 dB under its level, a triangle's 0.06 dB.
    /// </summary>
    [Fact]
    public void FundamentalDbv_IsTheLineTheAnalyzerWillRead()
    {
        const int periodFrames = 64;
        const int fundamentalBin = 64;
        foreach (var (kind, continuousDb) in new[]
                 {
                     (GeneratorKind.Square, -0.91210),
                     (GeneratorKind.Triangle, -0.06328),
                 })
        {
            // With no period given, the continuous-time figure.
            Assert.Equal(continuousDb, PeriodicWave.FundamentalDbv(kind, 0.0), tolerance: 0.0005);

            // With one, the sampled series' own — which is what the synthesized record carries, to
            // the last digit the transform can express.
            double[] amplitudes = BinAmplitudes(Record(kind, 750.0, 0.0, 4096));
            double measuredDbv = 20.0 * Math.Log10(amplitudes[fundamentalBin] / Math.Sqrt(2.0));
            Assert.Equal(PeriodicWave.FundamentalDbv(kind, 0.0, periodFrames), measuredDbv, tolerance: 0.0001);

            // The correction is small here and must still be in the right direction: folding adds.
            Assert.True(PeriodicWave.FundamentalDbv(kind, 0.0, periodFrames) > continuousDb);
        }
    }

    /// <summary>
    /// At the four-sample floor the correction is no longer a rounding: a square there is a sine
    /// pair and its "fundamental" holds 0.9 dB more than the textbook series says.
    /// </summary>
    [Fact]
    public void FundamentalDbv_AtTheShortestPeriod_IsNearlyADecibelOffTheTextbookFigure()
    {
        double continuousDb = PeriodicWave.FundamentalDbv(GeneratorKind.Square, 0.0);
        double sampledDb = PeriodicWave.FundamentalDbv(GeneratorKind.Square, 0.0, PeriodicWave.MinPeriodFrames);

        Assert.Equal(0.9120, sampledDb - continuousDb, tolerance: 0.001);
    }

    [Theory]
    // 1 kHz at 48 kHz: 48 frames, odd harmonics 1..23 = 12 of them.
    [InlineData(1000.0, 12)]
    // 750 Hz: 64 frames, odd harmonics 1..31 = 16.
    [InlineData(750.0, 16)]
    // The ceiling: four frames a cycle leaves only the fundamental under Nyquist.
    [InlineData(40_000.0, 1)]
    public void HarmonicsBelowNyquist_CountsTheOddOnesThatFit(double freqHz, int expected)
    {
        Assert.Equal(expected, PeriodicWave.HarmonicsBelowNyquist(freqHz, SampleRateHz));
    }

    /// <summary>
    /// And the spectrum agrees with that count: the last harmonic it names is the last line there
    /// is. Only at 750 Hz, whose 64-frame period divides the record — a frequency whose period does
    /// not is smeared by the rectangular window and says nothing about where its lines are.
    /// </summary>
    [Fact]
    public void NothingSitsAboveTheLastHarmonicCounted()
    {
        double[] amplitudes = BinAmplitudes(Record(GeneratorKind.Square, 750.0, 0.0, 4096));
        const int lastHarmonicBin = 64 * 31;

        Assert.True(amplitudes[lastHarmonicBin] > 0.01);
        Assert.All(amplitudes.Skip(lastHarmonicBin + 1), amplitude => Assert.True(amplitude < 1e-12));
    }

    /// <summary>
    /// The waveform has to be a function of the frame index and nothing else, like the chirp's
    /// wrap: a phase that accumulated per block would drift off its own grid, and the exactness
    /// every test above depends on would decay over the length of a run.
    /// </summary>
    [Fact]
    public void TheWaveformIsPeriodicInTheFrameIndexHoweverLongTheStreamHasRun()
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = Channel(GeneratorKind.Triangle, 1000.0, -6.0);
        const int period = 48;

        for (int i = 0; i < period; i++)
        {
            double first = waveform.ComputeSample(channel, i);
            double muchLater = waveform.ComputeSample(channel, 9_000_000_000L * period + i);
            Assert.Equal(first, muchLater, precision: 12);
        }
    }

    /// <summary>
    /// The simulator must produce bit-identical audio to the hardware path for these kinds — they
    /// share <see cref="PeriodicWave"/> rather than each holding a copy, and this is what says so.
    /// A drift between them is a DUT that behaves differently under --simulate.
    /// </summary>
    [Theory]
    [InlineData(GeneratorKind.Square)]
    [InlineData(GeneratorKind.Triangle)]
    public void TheSimulatorPlaysTheSameWaveformAsTheHardware(GeneratorKind kind)
    {
        var channel = Channel(kind, 1234.0, -12.0);
        var hardware = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var simulator = new Simulation.SignalSynthesizer(new Random(2), SampleRateHz);
        var profile = new Simulation.SimulatedSignalProfile
        {
            GeneratorLeft = channel,
            HarmonicsEnabled = false,
            HumEnabled = false,
            NoiseEnabled = false,
        };

        for (long frame = 0; frame < 512; frame++)
        {
            Assert.Equal(
                hardware.ComputeSample(channel, frame),
                simulator.ComputeChannelSample(profile, frame, channelIndex: 0),
                precision: 12);
        }
    }

    /// <summary>
    /// The output attenuator is programmed from the configured level before anything is played, so
    /// it has to know that these two shapes are not sines: a square needs 3.01 dB *less* headroom
    /// than a sine at the same dBV and a triangle 4.77 dB more.
    /// </summary>
    [Fact]
    public void OutputRangeSelector_ReadsEachShapesOwnPeak()
    {
        Assert.Equal(
            1.0,
            OutputRangeSelector.RequiredPeakVolts(Channel(GeneratorKind.Square, 1000.0, 0.0)),
            precision: 12);
        Assert.Equal(
            Math.Sqrt(3.0),
            OutputRangeSelector.RequiredPeakVolts(Channel(GeneratorKind.Triangle, 1000.0, 0.0)),
            precision: 12);

        // A disabled channel is silent whatever its settings say, like every other kind.
        Assert.Equal(
            0.0,
            OutputRangeSelector.RequiredPeakVolts(Channel(GeneratorKind.Square, 1000.0, 0.0) with { Enabled = false }));
    }
}
