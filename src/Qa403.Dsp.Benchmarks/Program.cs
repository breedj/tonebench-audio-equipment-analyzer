using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Qa403.Dsp.Benchmarks;

var config = ManualConfig.CreateMinimumViable()
    .AddJob(Job.Default
        .WithWarmupCount(2)
        .WithIterationCount(5));

BenchmarkRunner.Run<FftBackendBenchmarks>(config);
