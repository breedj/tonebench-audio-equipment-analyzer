using System.Numerics;
using BenchmarkDotNet.Attributes;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Benchmarks;

/// <summary>
/// All three <see cref="IFftBackend"/> implementations at the FFT sizes the app actually uses,
/// f64 real input. <see cref="MathNetBackend"/> is the baseline because it is the one
/// <c>SpectrumPipeline</c> and every runner construct, so the ratio column reads directly as
/// "what switching would buy".
///
/// <para>Forward only, deliberately: it is the transform on the live path -- profiled at ~53 % of
/// per-record cost at 1,048,576 points -- while the inverse is reached only by the frequency
/// response, impedance and impulse measurements, once per pass rather than per tick. All three
/// route their inverse through the same <c>HermitianInverse</c> identity anyway, so an inverse
/// benchmark would be measuring the same shared code three times over.</para>
/// </summary>
[MemoryDiagnoser]
public class FftBackendBenchmarks
{
    [Params(32 * 1024, 128 * 1024, 512 * 1024, 1024 * 1024)]
    public int N;

    private double[] _timeIn = [];
    private Complex[] _specOut = [];
    private FftSharpBackend _fftSharp = new();
    private MathNetBackend _mathNet = new();
    private FftFlatBackend _fftFlat = new();

    [GlobalSetup]
    public void Setup()
    {
        _timeIn = new double[N];
        var random = new Random(42);
        for (int i = 0; i < N; i++)
            _timeIn[i] = random.NextDouble() * 2.0 - 1.0;

        _specOut = new Complex[N / 2 + 1];
        _fftSharp = new FftSharpBackend();
        _mathNet = new MathNetBackend();
        _fftFlat = new FftFlatBackend();

        // Warm each backend's internal scratch (and FftFlat's twiddle tables) up to N once,
        // outside the timed run -- all three allocate lazily on first use at a given length.
        _fftSharp.Forward(_timeIn, _specOut);
        _mathNet.Forward(_timeIn, _specOut);
        _fftFlat.Forward(_timeIn, _specOut);
    }

    [Benchmark(Baseline = true)]
    public void MathNet() => _mathNet.Forward(_timeIn, _specOut);

    [Benchmark]
    public void FftSharp() => _fftSharp.Forward(_timeIn, _specOut);

    [Benchmark]
    public void FftFlat() => _fftFlat.Forward(_timeIn, _specOut);
}
