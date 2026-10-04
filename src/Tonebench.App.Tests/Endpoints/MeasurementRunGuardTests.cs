using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.App.Tests.Services;
using Tonebench.Device;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// Two measurements cannot share the pipeline (see <see cref="IMeasurementRun"/>): a start is
/// refused while any runner is running, the session's own acquisition excepted, and the cancel-all
/// a measurement switch makes unwinds every runner and none of the session.
/// </summary>
public class MeasurementRunGuardTests
{
    private sealed class FakeRun(bool running) : IMeasurementRun
    {
        public bool Running { get; private set; } = running;
        public int Cancelled { get; private set; }

        public Task CancelAsync()
        {
            Cancelled++;
            Running = false;
            return Task.CompletedTask;
        }
    }

    private static async Task<DeviceSessionService> SessionAsync()
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(new SimulatedDevice(randomSeed: 1), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);
        return session;
    }

    [Fact]
    public async Task AStart_IsRefusedWhileAnyRunnerRuns_ButNotForThePlainAcquisition()
    {
        var session = await SessionAsync();
        var idle = new FakeRun(running: false);
        var busy = new FakeRun(running: true);

        Assert.False(MeasurementRunGuard.AnotherRunIsActive(session, [session, idle]));
        Assert.True(MeasurementRunGuard.AnotherRunIsActive(session, [session, idle, busy]));

        // The FFT screen's own acquisition is what a runner-free session is for: never a blocker.
        await session.StartAcquisitionAsync(new AcquisitionConfig(48_000, 2, 1024), CancellationToken.None);
        try
        {
            Assert.True(((IMeasurementRun)session).Running);
            Assert.False(MeasurementRunGuard.AnotherRunIsActive(session, [session, idle]));
        }
        finally
        {
            await session.StopAcquisitionAsync();
        }
    }

    [Fact]
    public async Task EveryRealRunner_IsAMeasurementRunWithACancel()
    {
        // The DI registrations in Program.cs list the runners by hand; this pins that each of them
        // implements the interface the guard and the cancel-all rely on, so a new runner that is
        // registered but not cancellable cannot slip through as "never running".
        var session = await SessionAsync();
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var hub = new SpectrumBroadcastHub();
        IMeasurementRun[] runners =
        [
            new ThdSweepRunner(session, pipeline, hub, NullLogger<ThdSweepRunner>.Instance),
            new ImdSweepRunner(session, pipeline, hub, NullLogger<ImdSweepRunner>.Instance),
            new FrequencyResponseRunner(session, pipeline, hub, NullLogger<FrequencyResponseRunner>.Instance),
            new SteppedResponseRunner(session, pipeline, hub, NullLogger<SteppedResponseRunner>.Instance),
            new CrosstalkRunner(session, pipeline, hub, NullLogger<CrosstalkRunner>.Instance),
            new OutputImpedanceRunner(session, pipeline, hub, NullLogger<OutputImpedanceRunner>.Instance),
            new ImpulseResponseRunner(session, pipeline, new InMemoryLoopbackReferenceStore(), NullLogger<ImpulseResponseRunner>.Instance),
            new ImpedanceRunner(session, pipeline, hub, NullLogger<ImpedanceRunner>.Instance),
        ];

        foreach (var runner in runners)
        {
            Assert.False(runner.Running, runner.GetType().Name);
            // A cancel with nothing running is a no-op that must not throw.
            await runner.CancelAsync();
        }
    }
}
