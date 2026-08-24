// Most tests in this assembly drive a real, wall-clock acquisition: SimulatedDevice streams at the
// requested sample rate, SpectrumPipeline ticks on a PeriodicTimer, and assertions are about what
// arrives within a bounded time. xUnit runs test *classes* in parallel by default, so several of
// those were competing for the same cores — and the timing-sensitive ones (the flush test's
// in-flight-discard check, the sweep runner's per-point capture budget) then failed intermittently
// depending on what else happened to be running.
//
// Serialising the assembly costs about a second of wall clock and removes that whole class of
// flakiness. The pure-maths assemblies (Qa403.Dsp.Tests especially) are untouched and still
// parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
