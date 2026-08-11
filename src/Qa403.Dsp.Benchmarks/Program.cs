using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Qa403.Dsp.Benchmarks;

var config = ManualConfig.CreateMinimumViable()
    .AddJob(Job.Default
        .WithWarmupCount(2)
        .WithIterationCount(5));

// Two benchmark classes now live in this project (FftBackendBenchmarks, SpectrumBlobBenchmarks).
// BenchmarkSwitcher picks one via `--filter *ClassName*`; with no args it prompts interactively.
BenchmarkSwitcher.FromAssembly(typeof(FftBackendBenchmarks).Assembly).Run(args, config);
