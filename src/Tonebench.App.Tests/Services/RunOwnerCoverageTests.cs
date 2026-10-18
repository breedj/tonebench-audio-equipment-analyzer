using System.Reflection;
using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// Every runner that parks a run per measurement has the whole set of verbs that go with it.
///
/// <para><b>Why this is worth a mechanical test.</b> Eight runners do this, each with its own copy
/// of the bookkeeping, and the failure of a missing one is silent in both directions: a runner
/// without <c>SwitchOwner</c> is the 2026-10-17 report again (two measurements of that kind show
/// each other's run, or clear it), and one without <c>ForgetOwner</c> holds a destroyed
/// measurement's run until the process ends — and hands it to the next measurement that arrives,
/// since an unowned slot is adopted. Neither fails a build, and neither is visible until somebody
/// on a bench loses a run.</para>
///
/// <para>The set is derived rather than listed: every <see cref="IMeasurementRun"/> in the
/// assembly that has a <c>SwitchOwner</c> must have the rest, so a ninth runner joins this test by
/// existing. <see cref="SoundCardCalibrationRunner"/> and the like are not measurement runs and
/// hold nothing per measurement, so they are not in the set and need none of it.</para>
/// </summary>
public class RunOwnerCoverageTests
{
    private static IReadOnlyList<Type> ParkingRunners =>
        [.. typeof(ThdSweepRunner).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IMeasurementRun).IsAssignableFrom(t))
            .Where(t => t.GetMethod("SwitchOwner", BindingFlags.Public | BindingFlags.Instance) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)];

    [Fact]
    public void TheRunnersThatParkARunAreTheEightThisTestExpects()
    {
        // Named rather than counted, so that adding a runner is a decision taken here — the two
        // tests below then hold it to the whole contract.
        Assert.Equal(
            [
                "CrosstalkRunner", "FrequencyResponseRunner", "ImdSweepRunner", "ImpedanceRunner",
                "ImpulseResponseRunner", "OutputImpedanceRunner", "SteppedResponseRunner", "ThdSweepRunner",
            ],
            ParkingRunners.Select(t => t.Name));
    }

    [Fact]
    public void EveryRunnerThatParksARun_CanAlsoBeToldItsMeasurementIsGone()
    {
        var missing = ParkingRunners
            .Where(t => t.GetMethod("ForgetOwner", BindingFlags.Public | BindingFlags.Instance, [typeof(string)]) is null)
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These runners park a run per measurement but cannot be told one was destroyed, so they "
                + "will hold it for ever and hand it to the next measurement that arrives: "
                + string.Join(", ", missing));
    }

    [Fact]
    public void EveryRunnerThatParksARun_ReportsWhoseRunItHolds()
    {
        // The owner is what the tests above and POST .../owner's own callers read back; a runner
        // that parks without reporting it cannot be checked by either.
        var missing = ParkingRunners
            .Where(t => t.GetProperty("Owner", BindingFlags.Public | BindingFlags.Instance)?.PropertyType != typeof(string))
            .Select(t => t.Name)
            .ToList();

        Assert.True(missing.Count == 0, $"These runners have no string Owner: {string.Join(", ", missing)}");
    }
}
