// Most tests in this assembly drive a real, wall-clock acquisition: SimulatedDevice streams at the
// requested sample rate, SpectrumPipeline ticks on a PeriodicTimer, and assertions are about what
// arrives within a bounded time. xUnit runs test *classes* in parallel by default, so several of
// those were competing for the same cores — and the timing-sensitive ones (the flush test's
// in-flight-discard check, the sweep runner's per-point capture budget) then failed intermittently
// depending on what else happened to be running.
//
// The fix for that was `DisableTestParallelization = true`, with a comment saying it "costs about a
// second of wall clock". Measured 2026-09-30, that was wrong by two orders of magnitude: the sum of
// this assembly's 666 per-test durations is 545 s and its wall clock was 545 s — exactly no overlap
// — against 2 s for Tonebench.Dsp.Tests' 5,280 tests. This one assembly was 96% of a 9 m 28 s suite.
//
// The cost is not CPU. A test here costs the number of *simulated seconds of audio* it consumes,
// because SimulatedDevice.StreamAsync paces its blocks on a PeriodicTimer at real-time cadence on
// purpose (so that it exercises the pipeline's backpressure like a real device would, pinned by
// SimulatedDeviceTests.StreamAsync_EmitsAtRealTimeCadence_NotAsFastAsPossible). Those tests spend
// their time *waiting*, not computing — which is why Tonebench.Device.Tests compresses 131.7 s of summed
// duration into 57 s of wall clock while this assembly compressed nothing at all.
//
// So the answer is bounded parallelism rather than none: enough width to overlap the waiting, few
// enough threads that the FFT analysis at the end of each record still has cores to spare on this
// 12-core machine, since contending for all of them is what made the timing assertions flaky. Eight
// measured 1 m 55 s against 2 m 49 s at four and 9 m 05 s serial, with three consecutive clean runs.
// xUnit parallelises by *class*, so the floor is the longest class -- SteppedResponseRunnerTests at
// 107 s, with FrequencyResponseRunnerTests just behind it at 105 s. Going below that means splitting
// those two, not raising this number.
//
// Raising MaxParallelThreads is the first thing to try if this gets slow again, and re-running the
// suite three times is how to tell whether it is still honest: the failures this guards against are
// probabilistic, so one green run proves nothing.
[assembly: CollectionBehavior(MaxParallelThreads = 8)]
