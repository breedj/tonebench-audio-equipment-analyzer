using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The output-referred level probe's correction step, which decides how hard the DUT gets driven.
///
/// <para>Pinned as a pure function because the fault it guards against is not reproducible on
/// demand: it appeared five times across a day of use and never once when looked for. What the log
/// did show was the shape — a single iteration jumping straight past the generator's ceiling and
/// ending the probe there, so one capture that read as silence set the drive level for the whole
/// measurement.</para>
/// </summary>
public class ProbeCorrectionTests
{
    private const double MaxDbv = 18.0;
    private const double MinDbv = -60.0;

    private static (double Amplitude, ResponseCaptureSupport.ProbeCorrection Outcome) Correct(
        double amplitude, double shortfall, bool ceilingAlreadyAsked = false) =>
        ResponseCaptureSupport.CorrectProbeAmplitude(amplitude, shortfall, ceilingAlreadyAsked, MaxDbv, MinDbv);

    /// <summary>
    /// The ordinary case, and the one a naive step limit would have broken: the probe starts at
    /// −40 dBV, so a first correction of 40 dB toward a 0 dBV target is completely normal. Step
    /// <i>size</i> is therefore not what distinguishes a bad reading — asking for more than the
    /// hardware has is.
    /// </summary>
    [Fact]
    public void ALargeButReachableCorrectionIsTakenAtFaceValue()
    {
        var (amplitude, outcome) = Correct(-40.0, 40.0);

        Assert.Equal(0.0, amplitude, 9);
        Assert.Equal(ResponseCaptureSupport.ProbeCorrection.Continue, outcome);
    }

    /// <summary>
    /// Even a correction that would legitimately reach the ceiling from the probe's start point —
    /// 58 dB, for an 18 dBV target against a unity DUT — stays inside it and is taken as read.
    /// </summary>
    [Fact]
    public void ACorrectionThatLandsExactlyOnTheCeilingIsNotTreatedAsSuspect()
    {
        var (amplitude, outcome) = Correct(-40.0, 58.0);

        Assert.Equal(MaxDbv, amplitude, 9);
        Assert.Equal(ResponseCaptureSupport.ProbeCorrection.Continue, outcome);
    }

    /// <summary>The failure this exists for: one capture reading as silence asks for far more level than exists.</summary>
    [Fact]
    public void AFirstDemandBeyondTheCeilingIsClampedButNotBelieved()
    {
        var (amplitude, outcome) = Correct(-40.0, 100.0);

        Assert.Equal(MaxDbv, amplitude, 9);
        Assert.Equal(ResponseCaptureSupport.ProbeCorrection.ConfirmCeiling, outcome);
    }

    /// <summary>A DUT that really is that quiet asks again from the ceiling, and is believed.</summary>
    [Fact]
    public void ASecondDemandBeyondTheCeilingIsAccepted()
    {
        var (amplitude, outcome) = Correct(MaxDbv, 30.0, ceilingAlreadyAsked: true);

        Assert.Equal(MaxDbv, amplitude, 9);
        Assert.Equal(ResponseCaptureSupport.ProbeCorrection.AcceptCeiling, outcome);
    }

    /// <summary>
    /// The recovery path, which is the whole point. After a spurious demand for the ceiling the
    /// probe re-measures at maximum, finds the DUT is in fact at unity, and corrects back down —
    /// landing on the level that was asked for instead of running the measurement at full output.
    /// </summary>
    [Fact]
    public void AfterASpuriousCeilingTheNextReadingCorrectsBackDown()
    {
        var (clamped, first) = Correct(-40.0, 100.0);
        Assert.Equal(ResponseCaptureSupport.ProbeCorrection.ConfirmCeiling, first);

        // Re-measured at +18 dBV, the DUT reads unity: measured 18 dBV against a 0 dBV target.
        var (recovered, second) = Correct(clamped, -18.0, ceilingAlreadyAsked: true);

        Assert.Equal(0.0, recovered, 9);
        Assert.Equal(ResponseCaptureSupport.ProbeCorrection.Continue, second);
    }

    /// <summary>
    /// A reading that comes back in range clears the suspicion, so a genuine ceiling reached later
    /// in the same probe still gets its own confirmation rather than inheriting an earlier one.
    /// </summary>
    [Fact]
    public void AnInRangeCorrectionDoesNotCarryTheCeilingFlagForward()
    {
        var (_, outcome) = Correct(-40.0, 20.0, ceilingAlreadyAsked: true);

        Assert.Equal(ResponseCaptureSupport.ProbeCorrection.Continue, outcome);
    }

    /// <summary>
    /// The floor is reported rather than confirmed, and does not need confirming: the caller aborts
    /// the run with a message instead of driving the DUT, so a spurious reading here fails safe.
    /// </summary>
    [Fact]
    public void ADemandBelowTheFloorIsReportedImmediately()
    {
        var (amplitude, outcome) = Correct(-40.0, -40.0);

        Assert.Equal(-80.0, amplitude, 9);
        Assert.Equal(ResponseCaptureSupport.ProbeCorrection.BelowFloor, outcome);
    }
}
