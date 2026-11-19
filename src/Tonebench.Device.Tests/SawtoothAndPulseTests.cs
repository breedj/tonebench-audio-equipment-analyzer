using System.Numerics;
using Tonebench.Device.Hardware;
using Tonebench.Dsp.Fft;

namespace Tonebench.Device.Tests;

/// <summary>
/// The two shapes added to <see cref="PeriodicWave"/>'s family after the square and the triangle:
/// <see cref="GeneratorKind.Sawtooth"/> and <see cref="GeneratorKind.Pulse"/>.
///
/// <para>As with the first two, the spectral assertions are the ones that matter, and they are
/// against <b>arithmetic</b> rather than against a previous run: the sampled series of each shape
/// has an exact closed form (see <see cref="PeriodicWave.FundamentalDbv"/>), so every expectation
/// below is a formula and not a figure somebody recorded.</para>
///
/// <para>Most of them correlate the record against the harmonic directly rather than transforming
/// it. A DFT over a whole number of periods is exact either way, but the FFT wants a power-of-two
/// record and the periods here are not powers of two — which would mean choosing the frequency to
/// suit the transform rather than to suit the arithmetic being checked. The one test that does need
/// the whole spectrum is the one looking for content <i>between</i> the harmonics, and it uses
/// 750 Hz, whose 64-frame period divides a 4096-frame record exactly.</para>
/// </summary>
public class SawtoothAndPulseTests
{
    private const double SampleRateHz = 48_000.0;

    /// <summary>750 Hz at 48 kHz is 64 frames a cycle — a power of two, so a whole number of cycles fills an FFT record.</summary>
    private const double FreqHz = 750.0;

    private const int PeriodFrames = 64;

    private static ChannelGeneratorConfig Channel(
        GeneratorKind kind, double levelDbv, double dutyPercent = 50.0, double freqHz = FreqHz) =>
        new(Enabled: true, Tones: [])
        {
            Kind = kind,
            Wave = new PeriodicWaveSettings
            {
                FrequencyHz = freqHz,
                LevelDbv = levelDbv,
                DutyPercent = dutyPercent,
            },
        };

    private static double[] Record(GeneratorKind kind, double levelDbv, int frames, double dutyPercent = 50.0)
    {
        var waveform = new Qa40xGeneratorWaveform(new Random(1), SampleRateHz);
        var channel = Channel(kind, levelDbv, dutyPercent);
        return [.. Enumerable.Range(0, frames).Select(i => waveform.ComputeSample(channel, i))];
    }

    /// <summary>
    /// Peak amplitude of harmonic <paramref name="order"/>, by correlating the record against it.
    /// Exact when the record holds a whole number of periods, which every caller arranges.
    /// </summary>
    private static double HarmonicAmplitude(double[] record, int order)
    {
        double re = 0.0, im = 0.0;
        for (int n = 0; n < record.Length; n++)
        {
            double angle = 2.0 * Math.PI * order * n / PeriodFrames;
            re += record[n] * Math.Cos(angle);
            im -= record[n] * Math.Sin(angle);
        }

        return 2.0 * Math.Sqrt(re * re + im * im) / record.Length;
    }

    // ---------------------------------------------------------------- sawtooth

    /// <summary>
    /// The one thing the half-sample offset exists for. An ideal ramp sampled at n/M has a mean of
    /// -1/M — a quarter of peak at the four-frame floor, and a real DC offset on a stimulus that may
    /// be driving a voice coil. Sampled at (n + 1/2)/M the M values sit symmetrically about zero and
    /// sum to zero exactly, for <b>any</b> period, which is what this asserts.
    /// </summary>
    [Theory]
    [InlineData(1000.0)]
    [InlineData(700.0)]
    [InlineData(40_000.0)]
    public void ASawtoothHasNoDcAtAnyPeriod(double freqHz)
    {
        int period = PeriodicWave.PeriodFrames(freqHz, SampleRateHz);
        var settings = new PeriodicWaveSettings { FrequencyHz = freqHz, LevelDbv = 0.0 };

        double sum = 0.0;
        for (int i = 0; i < period; i++)
            sum += PeriodicWave.Sample(GeneratorKind.Sawtooth, settings, i, SampleRateHz);

        Assert.Equal(0.0, sum, precision: 12);
    }

    /// <summary>
    /// A sawtooth is the one shape in this family that is not half-wave symmetric, which is exactly
    /// the condition that kills even harmonics — so H2 is present, and that is the whole reason the
    /// shape is worth having. The levels are the sampled series' own,
    /// <c>sin(pi/M)/sin(k.pi/M)</c>, which at M = 64 is 6.010 dB down at H2 and 9.514 at H3, both a
    /// hair above the textbook 6.021 and 9.542.
    /// </summary>
    [Fact]
    public void ASawtoothCarriesEveryHarmonic_AtTheSampledSeriesOwnLevels()
    {
        var record = Record(GeneratorKind.Sawtooth, levelDbv: -20.0, frames: PeriodFrames * 64);
        double fundamental = HarmonicAmplitude(record, 1);

        for (int order = 2; order <= 6; order++)
        {
            double expected = Math.Sin(Math.PI / PeriodFrames) / Math.Sin(order * Math.PI / PeriodFrames);
            Assert.Equal(expected, HarmonicAmplitude(record, order) / fundamental, precision: 9);
        }

        // And the even ones really are there rather than merely being asserted: a square of the same
        // frequency has nothing at H2 at all.
        var square = Record(GeneratorKind.Square, levelDbv: -20.0, frames: PeriodFrames * 64);
        Assert.True(HarmonicAmplitude(square, 2) < HarmonicAmplitude(square, 1) * 1e-12);
        Assert.True(HarmonicAmplitude(record, 2) > fundamental * 0.4);
    }

    /// <summary>
    /// The fundamental the app <i>reports</i> is the line the analyzer reads: 2.158 dB below the
    /// waveform's rms for a sawtooth at this period, against the continuous-time 2.162.
    /// </summary>
    [Fact]
    public void ASawtoothsReportedFundamental_IsTheOneInTheRecord()
    {
        const double levelDbv = -20.0;
        var record = Record(GeneratorKind.Sawtooth, levelDbv, frames: PeriodFrames * 64);

        double measuredDbv = 20.0 * Math.Log10(HarmonicAmplitude(record, 1) / Math.Sqrt(2.0));
        double reported = PeriodicWave.FundamentalDbv(GeneratorKind.Sawtooth, levelDbv, PeriodFrames);

        Assert.Equal(reported, measuredDbv, precision: 9);
        Assert.Equal(levelDbv - 2.1580, reported, precision: 3);
    }

    /// <summary>
    /// A sawtooth's peak is sqrt(3) times its rms, like a triangle's — so the level means the same
    /// power into the same load whichever shape is selected, which is what makes the dropdown a
    /// comparison.
    /// </summary>
    [Fact]
    public void ASawtoothsLevelIsItsRms_AndItsPeakIsSqrt3TimesIt()
    {
        var record = Record(GeneratorKind.Sawtooth, levelDbv: 0.0, frames: PeriodFrames * 64);

        double rms = Math.Sqrt(record.Sum(v => v * v) / record.Length);
        double peak = record.Max(Math.Abs);

        // The sampled rms is sqrt(1 - 1/M^2) of the ideal, which at 64 frames is 0.001 dB -- the
        // documented cost of the half-sample offset, and well inside what a bench could see.
        Assert.Equal(Math.Sqrt(1.0 - 1.0 / (PeriodFrames * (double)PeriodFrames)), rms, precision: 9);

        // And the peak the range selector is given is the nominal one, which is 1/M above the
        // sampled extreme -- conservative, which is the right direction for a range.
        Assert.Equal(Math.Sqrt(3.0) * (1.0 - 1.0 / PeriodFrames), peak, precision: 9);
        Assert.Equal(Math.Sqrt(3.0), PeriodicWave.PeakVolts(GeneratorKind.Sawtooth, 0.0), precision: 12);
    }

    // ------------------------------------------------------------------- pulse

    /// <summary>
    /// A pulse at 50 % is a square, sample for sample. That is what licenses the whole shape sharing
    /// the square's level convention, its sampling factor and its range arithmetic — and it is the
    /// case a reader will check first.
    /// </summary>
    [Fact]
    public void APulseAtFiftyPercent_IsExactlyASquare()
    {
        var pulse = Record(GeneratorKind.Pulse, levelDbv: -6.0, frames: PeriodFrames * 4, dutyPercent: 50.0);
        var square = Record(GeneratorKind.Square, levelDbv: -6.0, frames: PeriodFrames * 4);

        Assert.Equal(square, pulse);
    }

    /// <summary>
    /// The width is a whole number of samples and never 0 or the whole period: a pulse that is never
    /// high and one that is always high are both silence wearing a frequency, and the control that
    /// produced them would read as broken rather than as refused.
    /// </summary>
    [Theory]
    [InlineData(50.0, 32)]
    [InlineData(25.0, 16)]
    [InlineData(1.0, 1)]
    // Below the floor, so it is held there -- and one frame is what that rounds to anyway.
    [InlineData(0.5, 1)]
    [InlineData(99.0, 63)]
    [InlineData(200.0, 63)]
    public void AWidthIsAWholeNumberOfSamples_AndNeverNoneOrAllOfThem(double dutyPercent, int expected)
    {
        Assert.Equal(expected, PeriodicWave.WidthFrames(dutyPercent, PeriodFrames));
    }

    /// <summary>
    /// A bipolar pulse's DC is <c>peak.(2d - 1)</c>, which at a 10 % duty is -0.8 of peak — most of
    /// the waveform rather than a trim. It is reported rather than removed, so the figure reported
    /// has to be the one in the record.
    /// </summary>
    [Theory]
    [InlineData(50.0, 0.0)]
    [InlineData(25.0, -0.5)]
    [InlineData(75.0, 0.5)]
    public void APulsesDc_IsWhatTheRecordHolds(double dutyPercent, double expectedFraction)
    {
        var settings = new PeriodicWaveSettings
        {
            FrequencyHz = FreqHz,
            LevelDbv = 0.0,
            DutyPercent = dutyPercent,
        };
        var record = Record(GeneratorKind.Pulse, levelDbv: 0.0, frames: PeriodFrames * 8, dutyPercent: dutyPercent);

        double mean = record.Average();
        Assert.Equal(expectedFraction, mean, precision: 9);
        Assert.Equal(mean, PeriodicWave.DcVolts(GeneratorKind.Pulse, settings, SampleRateHz), precision: 9);
    }

    /// <summary>
    /// Narrowing a pulse moves its energy out of the fundamental and into the harmonics, by exactly
    /// <c>sin(pi.d)</c> — 10.2 dB at a 10 % duty. The reported fundamental has to follow, or the Gen
    /// tile and the scope trigger would name a line 10 dB above the one in the record.
    /// </summary>
    [Theory]
    [InlineData(50.0)]
    [InlineData(25.0)]
    [InlineData(12.5)]
    public void ANarrowPulsesFundamental_FallsAsSinOfItsDuty(double dutyPercent)
    {
        const double levelDbv = -20.0;
        var record = Record(GeneratorKind.Pulse, levelDbv, PeriodFrames * 64, dutyPercent);

        int width = PeriodicWave.WidthFrames(dutyPercent, PeriodFrames);
        double measuredDbv = 20.0 * Math.Log10(HarmonicAmplitude(record, 1) / Math.Sqrt(2.0));
        double reported = PeriodicWave.FundamentalDbv(GeneratorKind.Pulse, levelDbv, PeriodFrames, width);

        Assert.Equal(reported, measuredDbv, precision: 9);

        double expectedDrop = 20.0 * Math.Log10(Math.Sin(Math.PI * width / PeriodFrames));
        double squareFundamental = PeriodicWave.FundamentalDbv(GeneratorKind.Square, levelDbv, PeriodFrames);
        Assert.Equal(squareFundamental + expectedDrop, reported, precision: 9);
    }

    /// <summary>
    /// A quarter-duty pulse has no fourth harmonic, and no eighth or twelfth: <c>sin(k.pi/4)</c> is
    /// zero at every multiple of four. An exact null is the strongest statement a spectral test can
    /// make about a synthesis, because nothing but the right arithmetic produces one.
    /// </summary>
    [Fact]
    public void AQuarterDutyPulse_HasNoFourthHarmonicAtAll()
    {
        var record = Record(GeneratorKind.Pulse, -20.0, PeriodFrames * 64, dutyPercent: 25.0);
        Assert.Equal(16, PeriodicWave.WidthFrames(25.0, PeriodFrames));

        double fundamental = HarmonicAmplitude(record, 1);

        foreach (int order in new[] { 4, 8, 12 })
            Assert.True(HarmonicAmplitude(record, order) < fundamental * 1e-12, $"H{order} is not a null");

        foreach (int order in new[] { 2, 3, 5 })
            Assert.True(HarmonicAmplitude(record, order) > fundamental * 0.1, $"H{order} is missing");
    }

    /// <summary>
    /// Every sample of a pulse is +-peak whatever the duty, so its rms does not move as the width
    /// does — which is the property that makes the duty control safe to turn. A unipolar pulse would
    /// not have it: its rms falls as the square root of the duty, so sweeping to 1 % would drive the
    /// DUT 20 dB harder at the same setting.
    /// </summary>
    [Theory]
    [InlineData(50.0)]
    [InlineData(10.0)]
    [InlineData(90.0)]
    public void APulsesRmsAndPeak_DoNotMoveWithItsDuty(double dutyPercent)
    {
        var record = Record(GeneratorKind.Pulse, levelDbv: 0.0, frames: PeriodFrames * 8, dutyPercent: dutyPercent);

        Assert.Equal(1.0, Math.Sqrt(record.Sum(v => v * v) / record.Length), precision: 12);
        Assert.Equal(1.0, record.Max(Math.Abs), precision: 12);
        Assert.Equal(1.0, PeriodicWave.PeakVolts(GeneratorKind.Pulse, 0.0), precision: 12);
    }

    /// <summary>
    /// Neither new shape puts anything between the harmonics — the property the whole hard-switched,
    /// integer-period construction exists for, and the one a free-running phase accumulator breaks.
    /// The record is 64 cycles of a 64-frame period, so the transform is exact and every harmonic
    /// lands on a bin.
    /// </summary>
    [Theory]
    [InlineData(GeneratorKind.Sawtooth, 50.0)]
    [InlineData(GeneratorKind.Pulse, 30.0)]
    public void NothingLandsBetweenTheHarmonics(GeneratorKind kind, double dutyPercent)
    {
        var record = Record(kind, -20.0, PeriodFrames * 64, dutyPercent);

        var spectrum = new Complex[record.Length / 2 + 1];
        new FftFlatBackend().Forward(record, spectrum);
        var bins = spectrum.Select(c => 2.0 * c.Magnitude / record.Length).ToArray();

        for (int bin = 1; bin < bins.Length; bin++)
        {
            if (bin % 64 == 0)
                continue;

            Assert.True(bins[bin] < bins[64] * 1e-9, $"bin {bin} holds {bins[bin]:0.###e+00}");
        }
    }

    /// <summary>
    /// Both shapes are in sine phase like the two before them, so a scope's zero-crossing trigger
    /// lands at the same place on all four. A pulse's first sample is its own +peak, which is the
    /// rising edge; a sawtooth's is one step above zero and climbing.
    /// </summary>
    [Fact]
    public void BothStartWhereTheRestOfTheFamilyDoes()
    {
        var saw = Record(GeneratorKind.Sawtooth, levelDbv: 0.0, frames: 4);
        Assert.True(saw[0] > 0.0 && saw[0] < Math.Sqrt(3.0) / 10.0);
        Assert.True(saw[1] > saw[0]);

        var pulse = Record(GeneratorKind.Pulse, levelDbv: 0.0, frames: 4, dutyPercent: 25.0);
        Assert.Equal(1.0, pulse[0], precision: 12);
    }

    /// <summary>
    /// The harmonic count the panel quotes follows the shape rather than the period alone: a
    /// sawtooth and a pulse hold every harmonic, a square and a triangle the odd ones only.
    /// </summary>
    [Fact]
    public void TheHarmonicCountFollowsWhetherTheShapeHasEvenHarmonics()
    {
        // 64 frames a cycle: harmonics 1..31 fit below Nyquist, of which 16 are odd.
        Assert.Equal(31, PeriodicWave.HarmonicsBelowNyquist(GeneratorKind.Sawtooth, FreqHz, SampleRateHz));
        Assert.Equal(31, PeriodicWave.HarmonicsBelowNyquist(GeneratorKind.Pulse, FreqHz, SampleRateHz));
        Assert.Equal(16, PeriodicWave.HarmonicsBelowNyquist(GeneratorKind.Square, FreqHz, SampleRateHz));
        Assert.Equal(16, PeriodicWave.HarmonicsBelowNyquist(GeneratorKind.Triangle, FreqHz, SampleRateHz));
    }

    /// <summary>
    /// The range selector reads <see cref="PeriodicWave.PeakVolts"/> rather than converting the
    /// level as if it were a sine, so a sawtooth at 0 dBV asks for 4.77 dB more headroom than a
    /// square at the same setting — get that wrong and it parks a range low and clips every edge.
    /// </summary>
    [Fact]
    public void TheRangeSelectorAsksForEachShapesOwnPeak()
    {
        Assert.Equal(1.0, OutputRangeSelector.RequiredPeakVolts(Channel(GeneratorKind.Square, 0.0)), precision: 9);
        Assert.Equal(1.0, OutputRangeSelector.RequiredPeakVolts(Channel(GeneratorKind.Pulse, 0.0, 10.0)), precision: 9);
        Assert.Equal(
            Math.Sqrt(3.0), OutputRangeSelector.RequiredPeakVolts(Channel(GeneratorKind.Sawtooth, 0.0)), precision: 9);
    }
}
