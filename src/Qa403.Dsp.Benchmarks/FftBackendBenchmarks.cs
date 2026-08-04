using System.Numerics;
using BenchmarkDotNet.Attributes;
using Qa403.Dsp.Fft;

namespace Qa403.Dsp.Benchmarks;

/// <summary>
/// FftSharpBackend vs MathNetBackend at the FFT sizes the app actually uses, per Phase 2:
/// 32k, 128k, 512k, 1M, f64 real input. No default backend is picked until these are reviewed.
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

        // Warm each backend's internal scratch buffer up to N once, outside the timed run.
        _fftSharp.Forward(_timeIn, _specOut);
        _mathNet.Forward(_timeIn, _specOut);
    }

    [Benchmark(Baseline = true)]
    public void FftSharp() => _fftSharp.Forward(_timeIn, _specOut);

    [Benchmark]
    public void MathNet() => _mathNet.Forward(_timeIn, _specOut);
}
