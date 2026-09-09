using System.Reflection;
using System.Runtime.CompilerServices;
using Qa403.App.Services;

namespace Qa403.App.Tests.Services;

/// <summary>
/// Every runner's <c>Running</c> against every <see cref="FrequencyResponsePhase"/>.
///
/// <para><b>Why this is worth a mechanical test.</b> <c>Running</c> is what rejects a concurrent
/// start, keeps STOP enabled and tells a caller the run is over — so a phase a runner can enter but
/// which is missing from its own <c>Running</c> reads as "finished" the moment it begins. That is
/// not a subtle failure: it made every frequency-response runner test fail at once when the
/// input-range probe was added. The risk is entirely in <i>adding</i> a phase, which is why the
/// table below has to name every enum member: a new one fails
/// <see cref="EveryPhaseIsAccountedForByEveryRunner"/> until somebody decides, for each runner,
/// whether it counts as running.</para>
///
/// <para>The runners are built with <see cref="RuntimeHelpers.GetUninitializedObject"/> rather than
/// constructed, because a real one wants a device session, a pipeline and a broadcast hub, and none
/// of that is involved here: <c>Running</c> is a pure function of the phase field in all four.</para>
/// </summary>
public class RunnerPhaseCoverageTests
{
    /// <summary>Which phases each runner considers itself busy in. Everything not listed must read as not running.</summary>
    private static readonly Dictionary<Type, FrequencyResponsePhase[]> BusyPhases = new()
    {
        [typeof(FrequencyResponseRunner)] =
        [
            FrequencyResponsePhase.DeterminingLevel,
            FrequencyResponsePhase.DeterminingRange,
            FrequencyResponsePhase.Measuring,
        ],
        [typeof(SteppedResponseRunner)] =
        [
            FrequencyResponsePhase.DeterminingLevel,
            FrequencyResponsePhase.DeterminingRange,
            FrequencyResponsePhase.Sweeping,
        ],
        [typeof(ImpulseResponseRunner)] =
        [
            FrequencyResponsePhase.DeterminingLevel,
            FrequencyResponsePhase.DeterminingRange,
            FrequencyResponsePhase.Measuring,
            FrequencyResponsePhase.Analyzing,
        ],
        [typeof(ImpedanceRunner)] =
        [
            // No DeterminingLevel: an impedance run is given its generator level rather than solving
            // one, since a one-port has no output level to aim at.
            FrequencyResponsePhase.DeterminingRange,
            FrequencyResponsePhase.Measuring,
            FrequencyResponsePhase.Calibrating,
        ],
    };

    [Fact]
    public void EveryRunnerReportsRunningForExactlyItsOwnBusyPhases()
    {
        foreach (var (runnerType, busy) in BusyPhases)
        {
            foreach (var phase in Enum.GetValues<FrequencyResponsePhase>())
            {
                bool expected = busy.Contains(phase);
                bool actual = RunningAt(runnerType, phase);

                Assert.True(
                    expected == actual,
                    $"{runnerType.Name}.Running in phase {phase} was {actual}, expected {expected}.");
            }
        }
    }

    /// <summary>
    /// The terminal phases must read as not running for every runner, which is what makes a caller's
    /// "wait until it stops" loop terminate. Stated separately from the table so that adding a runner
    /// with a typo in its list cannot quietly agree with itself.
    /// </summary>
    [Fact]
    public void NoRunnerIsEverRunningInATerminalPhase()
    {
        FrequencyResponsePhase[] terminal =
        [
            FrequencyResponsePhase.Idle,
            FrequencyResponsePhase.Finished,
            FrequencyResponsePhase.Cancelled,
            FrequencyResponsePhase.Failed,
        ];

        foreach (var runnerType in BusyPhases.Keys)
            foreach (var phase in terminal)
                Assert.False(RunningAt(runnerType, phase), $"{runnerType.Name} claimed to be running in {phase}.");
    }

    /// <summary>
    /// Fails when a phase is added to the enum and not classified for every runner. Blunt on purpose:
    /// the alternative is a new phase that silently reads as finished on whichever runner introduced
    /// it.
    /// </summary>
    [Fact]
    public void EveryPhaseIsAccountedForByEveryRunner()
    {
        var allPhases = Enum.GetValues<FrequencyResponsePhase>();

        foreach (var (runnerType, busy) in BusyPhases)
        {
            foreach (var phase in busy)
                Assert.Contains(phase, allPhases);

            // Every member is either busy or deliberately not, and the loop in
            // EveryRunnerReportsRunningForExactlyItsOwnBusyPhases already checks each one. What this
            // adds is the count: a phase appended to the enum widens allPhases, and if the runner
            // that introduced it did not add it here, that test fails rather than this one -- which
            // is the louder of the two failures.
            Assert.True(busy.Length < allPhases.Length, $"{runnerType.Name} cannot be busy in every phase.");
        }

        Assert.Equal(allPhases.Length, allPhases.Distinct().Count());
    }

    private static bool RunningAt(Type runnerType, FrequencyResponsePhase phase)
    {
        object runner = RuntimeHelpers.GetUninitializedObject(runnerType);

        var field = runnerType.GetField("_phase", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{runnerType.Name} has no _phase field.");
        field.SetValue(runner, phase);

        var property = runnerType.GetProperty("Running", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{runnerType.Name} has no Running property.");

        return (bool)property.GetValue(runner)!;
    }
}
