using System.Numerics;
using Tonebench.Dsp.Fft;

namespace Tonebench.Dsp.Tests;

/// <summary>
/// Regression tests for the two defects found by comparing this app against QuantAsylum's QA40x
/// app on the same QA403 and the same loopback (192 kHz, 128k FFT, Hann, 1.0 Vrms at 1000.488 Hz):
///
/// <list type="number">
/// <item>THD+N/SNR integrated from bin 0, so a DC-coupled input's few-mV offset dominated them.
/// The bench read THD+N = -46.79 dB where QA40x read -79.70 dB.</item>
/// <item>Each harmonic was the raw sum of its ±3 skirt bins, so with no harmonic content at all
/// THD still read about -106 dB — pure integrated noise floor. The bench read -107.40 dB.</item>
/// </list>
/// </summary>
public class MeasurementBandAndNoiseFloorTests
{
    private const int N = 131072;
    private const double Fs = 192000.0;
    private const int FundBin = 683;               // 1000.48828125 Hz — exactly coherent
    private const double FundVrms = 1.0;

    /// <summary>sigma that puts the white-noise floor at roughly -125 dBV/bin, as seen on the bench.</summary>
    private const double NoiseSigma = 1.44e-4;

    private const double BenchDcOffsetV = 0.00458;

    private static double[] Build(double dcOffsetV, double noiseStdDev, params (double Dbc, int Order)[] harmonics)
    {
        // CoherentTone takes peak amplitude; 1.0 Vrms => sqrt(2) Vpk.
        var comps = new List<(double AmplitudeV, int Bin)> { (FundVrms * Math.Sqrt(2.0), FundBin) };
        foreach (var (dbc, order) in harmonics)
            comps.Add((FundVrms * Math.Pow(10.0, dbc / 20.0) * Math.Sqrt(2.0), FundBin * order));

        var samples = TestSignals.CoherentTone(N, comps.ToArray());

        if (noiseStdDev > 0)
        {
            var noise = TestSignals.GaussianWhiteNoise(N, noiseStdDev, seed: 1234);
            for (int i = 0; i < N; i++) samples[i] += noise[i];
        }

        if (dcOffsetV != 0)
            for (int i = 0; i < N; i++) samples[i] += dcOffsetV;

        return samples;
    }

    private static void Analyze(double[] samples, out double thdDb, out double thdPlusNAudioBandDb, out double thdPlusNFullBandDb)
    {
        var w = Window.Get(WindowType.Hann, N);
        var windowed = new double[N];
        Window.Apply(samples, w, windowed);

        var spectrum = new Complex[(N / 2) + 1];
        new FftSharpBackend().Forward(windowed, spectrum);

        var psdBuf = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, Fs, w.Sums, psdBuf);

        var harmonics = new Harmonic[12];
        int written = HarmonicAnalyzer.Analyze(psd, FundBin, 12, harmonics);
        var h = harmonics.AsSpan(0, written);

        thdDb = SpectrumMetrics.ThdDb(h);
        thdPlusNAudioBandDb = SpectrumMetrics.ThdPlusNDb(psd, h[0], psd.BandOf(20.0, 20000.0));
        thdPlusNFullBandDb = SpectrumMetrics.ThdPlusNDb(psd, h[0], psd.FullBand);
    }

    [Fact]
    public void ThdPlusN_AudioBand_IsUnaffectedByADcOffset()
    {
        Analyze(Build(0.0, NoiseSigma), out _, out double cleanAudioBand, out _);
        Analyze(Build(BenchDcOffsetV, NoiseSigma), out _, out double withDcAudioBand, out double withDcFullBand);

        // The whole point: a DC offset must not move THD+N at all once the band excludes bin 0.
        Assert.Equal(cleanAudioBand, withDcAudioBand, 3);

        // And it very much does when the band does not — this is the defect, reproduced. 4.58 mV
        // against a 1 V fundamental is about -46.8 dB, which is what the bench showed.
        Assert.True(
            withDcFullBand > withDcAudioBand + 25.0,
            $"expected DC to wreck a full-band THD+N; audio band {withDcAudioBand:F2} dB, full band {withDcFullBand:F2} dB");
        Assert.InRange(withDcFullBand, -48.0, -45.0);
    }

    /// <summary>
    /// The same defect at a short record, which is how it came back. The band's lower edge is a
    /// rounded bin, so "20 Hz" lands on bin 2 at 11.72 Hz bins but on bin <b>1</b> -- right beside
    /// DC, inside the window's own main lobe -- at 23.44 Hz bins and coarser. An adaptive sweep
    /// shortens its record as it climbs, crosses that at 1500 Hz, and every point above it read a
    /// DC offset as noise: measured on a QA403, THD+N 0.0022 % -> 2.56 % and SNR 93.4 -> 31.8 dB
    /// with nothing changing but the FFT size.
    ///
    /// <para>Parameterised over the sizes an adaptive sweep actually uses at 192 kHz. The guard is
    /// <see cref="WindowSums.DcGuardBins"/>; before it, the 4096 and 8192 cases failed by ~60 dB.</para>
    /// </summary>
    [Theory]
    [InlineData(131072)]
    [InlineData(16384)]
    [InlineData(8192)]
    [InlineData(4096)]
    public void ThdPlusN_AtEveryAdaptiveRecordLength_IsUnaffectedByADcOffset(int fftSize)
    {
        double clean = ThdPlusNAt(fftSize, dcOffsetV: 0.0);
        double withDc = ThdPlusNAt(fftSize, dcOffsetV: BenchDcOffsetV);

        Assert.True(
            Math.Abs(withDc - clean) < 0.5,
            $"a {BenchDcOffsetV * 1000:F2} mV offset moved THD+N at {fftSize} points: "
                + $"{clean:F2} dB clean, {withDc:F2} dB with DC");
    }

    /// <summary>THD+N over 20 Hz - 20 kHz of one coherent tone at a given record length, in dB.</summary>
    private static double ThdPlusNAt(int fftSize, double dcOffsetV)
    {
        // A tone on a bin that exists at every size under test, so the fundamental is coherent and
        // the comparison is of the band alone.
        int fundBin = fftSize / 64;
        var samples = TestSignals.CoherentTone(fftSize, [(Math.Sqrt(2.0), fundBin)]);
        var noise = TestSignals.GaussianWhiteNoise(fftSize, NoiseSigma, seed: 99);
        for (int i = 0; i < fftSize; i++) samples[i] += noise[i] + dcOffsetV;

        var w = Window.Get(WindowType.Hann, fftSize);
        var windowed = new double[fftSize];
        Window.Apply(samples, w, windowed);

        var spectrum = new Complex[(fftSize / 2) + 1];
        new FftSharpBackend().Forward(windowed, spectrum);

        var psdBuf = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, Fs, w.Sums, psdBuf);

        var harmonics = new Harmonic[12];
        int written = HarmonicAnalyzer.Analyze(psd, fundBin, 12, harmonics);
        return SpectrumMetrics.ThdPlusNDb(psd, harmonics[0], psd.BandOf(20.0, 20000.0));
    }

    /// <summary>
    /// The guard is the window's, not a constant: Flat Top spreads DC over five bins where Hann
    /// spreads it over two, and a band that cleared Hann's lobe would sit inside Flat Top's.
    /// </summary>
    [Theory]
    [InlineData(WindowType.Rectangular, 4)]
    [InlineData(WindowType.Hann, 5)]
    [InlineData(WindowType.BlackmanHarris, 6)]
    [InlineData(WindowType.Kaiser7, 7)]
    [InlineData(WindowType.FlatTop, 10)]
    public void DcGuard_WidensWithTheWindowsOwnLobe(WindowType window, int expectedBins)
    {
        Assert.Equal(expectedBins, Window.Get(window, 8192).Sums.DcGuardBins);
    }

    [Fact]
    public void Thd_WithNoHarmonicContent_ReadsWellBelowTheIntegratedNoiseFloor()
    {
        Analyze(Build(0.0, NoiseSigma), out double thdDb, out _, out _);

        // Before noise subtraction this signal — which contains no harmonics whatsoever — read
        // -106.2 dB, purely from summing 12 harmonics x 7 skirt bins of noise floor. It now reads
        // about -113.
        //
        // -113 rather than -infinity because subtracting the *mean* noise leaves the noise's own
        // fluctuation: a 7-bin skirt of exponentially-distributed power has standard deviation
        // sqrt(7)·mean, so a single un-averaged FFT simply cannot resolve a harmonic below roughly
        // -111 dBc at this noise floor. That is a property of the measurement, not of this code —
        // the way past it is spectrum averaging (SpectrumAverager), which shrinks the fluctuation
        // by sqrt(count) while leaving any real harmonic where it is.
        Assert.True(thdDb < -111.0, $"THD should not report the noise floor as distortion, got {thdDb:F2} dB");
    }

    [Fact]
    public void Thd_WithARealHarmonic_MeasuresItAccuratelyThroughTheNoise()
    {
        Analyze(Build(BenchDcOffsetV, NoiseSigma, (-95.0, 2)), out double thdDb, out _, out _);

        // A genuine -95 dBc second harmonic must still come back as -95 dBc: noise subtraction
        // must not eat real distortion, and DC must not inflate it either.
        Assert.InRange(thdDb, -96.0, -94.0);
    }

    /// <summary>
    /// A tone deliberately placed *between* bins, which is what happens with "Snap to bin" off.
    /// Its window skirts then spread far past the main lobe, and a ±3-bin notch leaves enough
    /// fundamental behind to dominate the residual — the reason qa40x-rs excludes ±24 bins and
    /// PyQa40x half an octave.
    /// </summary>
    [Fact]
    public void ThdPlusN_OffBinFundamental_IsNotInflatedByItsOwnLeakage()
    {
        var w = Window.Get(WindowType.Hann, N);

        // Half a bin off — worst case for leakage.
        double offBinHz = (FundBin + 0.5) * (Fs / N);
        var samples = TestSignals.Tone(N, FundVrms * Math.Sqrt(2.0), offBinHz, Fs);
        var noise = TestSignals.GaussianWhiteNoise(N, NoiseSigma, seed: 4321);
        for (int i = 0; i < N; i++) samples[i] += noise[i];

        var windowed = new double[N];
        Window.Apply(samples, w, windowed);
        var spectrum = new Complex[(N / 2) + 1];
        new FftSharpBackend().Forward(windowed, spectrum);
        var psdBuf = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, Fs, w.Sums, psdBuf);

        var harmonics = new Harmonic[1];
        HarmonicAnalyzer.Analyze(psd, FundBin, 1, harmonics);
        var audioBand = psd.BandOf(20.0, 20000.0);

        double octaveNotchDb = SpectrumMetrics.ThdPlusNDb(psd, harmonics[0], audioBand);
        double narrowNotchDb = 20.0 * Math.Log10(
            psd.IntegrateVrmsExcluding(
                [new BinRange(harmonics[0].Bin - 3, harmonics[0].Bin + 3)], audioBand) / harmonics[0].AmplitudeVrms);

        // The narrow notch is the old behaviour and is dominated by leaked fundamental; the
        // octave notch has to be far better than it, and close to the true noise-only floor.
        Assert.True(
            octaveNotchDb < narrowNotchDb - 15.0,
            $"octave notch {octaveNotchDb:F2} dB should be well below a ±3-bin notch {narrowNotchDb:F2} dB");
        Assert.InRange(octaveNotchDb, -90.0, -75.0);
    }

    [Fact]
    public void Analyze_StopsCountingHarmonicsAboveTheMeasurementBand()
    {
        // 5 kHz fundamental: H2=10k and H3=15k are in band, H4=20k is the edge, H5=25k is not.
        const int Fund5kBin = 3413;   // 5000.24 Hz
        var w = Window.Get(WindowType.Hann, N);
        var samples = TestSignals.CoherentTone(N, (Math.Sqrt(2.0), Fund5kBin));
        var windowed = new double[N];
        Window.Apply(samples, w, windowed);
        var spectrum = new Complex[(N / 2) + 1];
        new FftSharpBackend().Forward(windowed, spectrum);
        var psdBuf = new double[spectrum.Length];
        var psd = PsdSpectrum.Compute(spectrum, Fs, w.Sums, psdBuf);

        var bounded = new Harmonic[12];
        int boundedCount = HarmonicAnalyzer.Analyze(
            psd, Fund5kBin, 12, bounded, maxBinInclusive: psd.BandOf(20.0, 20000.0).HiBinInclusive);

        var unbounded = new Harmonic[12];
        int unboundedCount = HarmonicAnalyzer.Analyze(psd, Fund5kBin, 12, unbounded);

        // Fundamental + H2..H4 — H5 at 25 kHz is out of band even though it is under Nyquist.
        Assert.Equal(4, boundedCount);
        Assert.True(unboundedCount > boundedCount, "without a band the harmonics should run on up to Nyquist");
    }
}
