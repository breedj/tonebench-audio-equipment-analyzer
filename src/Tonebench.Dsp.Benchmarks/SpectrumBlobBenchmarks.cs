using BenchmarkDotNet.Attributes;
using Tonebench.Data;

namespace Tonebench.Dsp.Benchmarks;

/// <summary>
/// Encoded <c>spectrum.mag_db</c> BLOB size and <see cref="SpectrumCodec"/> throughput at the FFT
/// sizes the app can produce -- see docs/tonebench-stack-spec.md §6.2: "keep individual BLOBs
/// under ~1-2 MB... measure at 1M-point FFT before deciding" whether in-row storage still holds.
/// BLOB size itself is exact arithmetic (<see cref="SpectrumCodec.ByteLength"/>), not something
/// that needs a benchmark to discover; this exists to also measure encode/decode cost, and to
/// report both numbers together in <c>BenchmarkDotNet.Artifacts</c>.
/// </summary>
[MemoryDiagnoser]
public class SpectrumBlobBenchmarks
{
    [Params(65536, 262144, 1048576)] // 2^16, 2^18, 2^20 -- the app's smallest, a middle, and the spec's 1M-point case
    public int FftSize;

    private double[] _dbv = [];
    private byte[] _encoded = [];

    [GlobalSetup]
    public void Setup()
    {
        int binCount = FftSize / 2 + 1;
        _dbv = new double[binCount];
        var random = new Random(42);
        for (int i = 0; i < binCount; i++)
            _dbv[i] = random.NextDouble() * 200.0 - 180.0; // realistic dBV range

        _encoded = new byte[SpectrumCodec.ByteLength(binCount)];
    }

    [Benchmark]
    public void Encode() => SpectrumCodec.EncodeTo(_dbv, _encoded);

    [Benchmark]
    public void Decode() => SpectrumCodec.DecodeTo(_encoded, _dbv);
}
