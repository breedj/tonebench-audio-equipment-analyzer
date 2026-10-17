using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The bookkeeping behind "a measurement switch does not throw a sweep away" — see
/// <see cref="RunParking{TState}"/>. Pure, so every rule it states is pinned here rather than only
/// through a runner that takes seconds per case.
/// </summary>
public class RunParkingTests
{
    private sealed record Run(string Name);

    [Fact]
    public void SwitchingToTheSameOwner_DoesNothing()
    {
        var parking = new RunParking<Run>(capacity: 4);
        parking.Switch("p:1", new Run("a"));

        var outcome = parking.Switch("p:1", new Run("a"));

        Assert.False(outcome.Changed);
        Assert.False(outcome.Cleared);
        Assert.Null(outcome.Adopted);
        Assert.Equal("p:1", parking.Owner);
    }

    [Fact]
    public void AMeasurementSwitchedAwayFromAndBackTo_GetsItsOwnRunBack()
    {
        // The reported bug, as a property: run a sweep on one measurement, open another, come back.
        var parking = new RunParking<Run>(capacity: 4);
        var sweep = new Run("amp sweep");

        parking.Switch("p:1", null);
        var away = parking.Switch("p:2", sweep);
        var back = parking.Switch("p:1", null);

        // Leaving p:1 empties the live slot -- p:2 must not see p:1's points.
        Assert.True(away.Changed);
        Assert.True(away.Cleared);
        Assert.Null(away.Adopted);

        Assert.Same(sweep, back.Adopted);
    }

    [Fact]
    public void SwitchingAwayFromNobody_AdoptsWhatIsOnHandRatherThanClearingIt()
    {
        // A fresh process, or a sweep started from a second tab before any measurement claimed it:
        // the state belongs to nobody, so the first measurement to claim it keeps it.
        var parking = new RunParking<Run>(capacity: 4);

        var outcome = parking.Switch("p:1", new Run("unowned"));

        Assert.True(outcome.Changed);
        Assert.False(outcome.Cleared);
        Assert.Null(outcome.Adopted);
    }

    [Fact]
    public void AnOwnerWithNothingToPark_LosesWhateverItHadParked()
    {
        // Clear is a deliberate "throw this away", and switching measurement and back must not
        // undo it by handing the earlier run back.
        var parking = new RunParking<Run>(capacity: 4);
        parking.Switch("p:1", null);
        parking.Switch("p:2", new Run("first"));
        parking.Switch("p:1", null);

        // p:1 is live again and empty; now leave it with nothing to park, and come back.
        parking.Switch("p:2", null);
        var back = parking.Switch("p:1", null);

        Assert.Null(back.Adopted);
        Assert.True(back.Cleared);
    }

    [Fact]
    public void PastTheCapacity_TheLeastRecentlyParkedIsEvicted()
    {
        var parking = new RunParking<Run>(capacity: 2);
        parking.Switch("a", null);
        parking.Switch("b", new Run("a's run"));
        parking.Switch("c", new Run("b's run"));
        parking.Switch("d", new Run("c's run"));

        // a was parked first, so a's run is the one that goes. The two switches below park nothing
        // (current is null), so neither of them can be what evicted anything.
        Assert.Null(parking.Switch("a", null).Adopted);
        Assert.Equal("c's run", parking.Switch("c", null).Adopted?.Name);
    }

    [Fact]
    public void AtCapacity_TheOwnerBeingSwitchedToIsNotEvictedByItsOwnSwitch()
    {
        // Its entry is taken out before the outgoing one is put in. The other way round, the park
        // overflows first and the oldest entry goes -- which here is the one being collected.
        var parking = new RunParking<Run>(capacity: 1);
        parking.Switch("a", null);
        parking.Switch("b", new Run("a's run"));

        Assert.Equal("a's run", parking.Switch("a", new Run("b's run")).Adopted?.Name);
    }

    [Fact]
    public void ReParkingAnOwner_RefreshesItsRunWithoutTakingASecondSlot()
    {
        var parking = new RunParking<Run>(capacity: 2);
        parking.Switch("a", null);
        parking.Switch("b", new Run("first"));
        parking.Switch("a", null);
        parking.Switch("b", new Run("second"));

        // a has been parked twice and still holds one slot, so c's arrival evicts nothing yet.
        Assert.Equal("second", parking.Switch("a", null).Adopted?.Name);
    }

    [Fact]
    public void SwitchingToNobody_ParksAndLeavesTheSlotEmpty()
    {
        var parking = new RunParking<Run>(capacity: 4);
        parking.Switch("p:1", null);

        var outcome = parking.Switch(null, new Run("a"));

        Assert.True(outcome.Cleared);
        Assert.Null(outcome.Adopted);
        Assert.Null(parking.Owner);
        Assert.Equal("a", parking.Switch("p:1", null).Adopted?.Name);
    }
}
