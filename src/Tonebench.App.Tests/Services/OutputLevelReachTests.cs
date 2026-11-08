using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The 80 % rule a level probe stops on — see <see cref="OutputLevelReach"/>. The property worth
/// pinning is that "80 %" is read in the <i>target's own quantity</i>: 80 % of a power is 0.97 dB
/// down and 80 % of a voltage 1.94 dB, and a run judged in the wrong one is judged against a limit
/// twice or half the size of the one the user was promised.
/// </summary>
public class OutputLevelReachTests
{
    [Theory]
    [InlineData(ThdSweepLevelUnit.Watt, 0.9691)]
    [InlineData(ThdSweepLevelUnit.MilliWatt, 0.9691)]
    [InlineData(ThdSweepLevelUnit.Volt, 1.9382)]
    [InlineData(ThdSweepLevelUnit.MilliVolt, 1.9382)]
    [InlineData(ThdSweepLevelUnit.Dbv, 1.9382)]
    public void TheLimitIsEightyPercentOfWhateverTheUnitMeasures(ThdSweepLevelUnit unit, double expectedDb)
    {
        Assert.Equal(expectedDb, OutputLevelReach.ShortfallLimitDb(unit), 3);
    }

    [Fact]
    public void AReadingAtExactlyEightyPercentCounts_AndOneBelowItDoesNot()
    {
        // 1 W into 8 Ω is 9.03 dBV; 0.8 W is 8.06 dBV.
        double target = ThdSweepLevelUnit.Watt.ToDbv(1.0, 8.0);
        double eightyPercent = ThdSweepLevelUnit.Watt.ToDbv(0.8, 8.0);

        Assert.True(OutputLevelReach.Reached(target, eightyPercent + 0.001, ThdSweepLevelUnit.Watt));
        Assert.False(OutputLevelReach.Reached(target, eightyPercent - 0.01, ThdSweepLevelUnit.Watt));

        // The same figures as a voltage target are comfortably inside it — which is the point of
        // asking the unit rather than picking one limit for both.
        Assert.True(OutputLevelReach.Reached(target, eightyPercent - 0.01, ThdSweepLevelUnit.Volt));
    }

    [Fact]
    public void TheProbesOwnConvergenceToleranceIsNeverEnoughToFail()
    {
        // Every probe stops correcting at 0.5 dB, so that reading must count as reached in both
        // quantities or a run that converged normally would be stopped.
        foreach (var unit in new[] { ThdSweepLevelUnit.Watt, ThdSweepLevelUnit.Volt, ThdSweepLevelUnit.Dbv })
            Assert.True(OutputLevelReach.Reached(9.03, 9.03 - 0.5, unit));
    }

    [Fact]
    public void ADutThatIsNotThereIsNowhereNear()
    {
        Assert.False(OutputLevelReach.Reached(9.03, -60.0, ThdSweepLevelUnit.Watt));
        Assert.False(OutputLevelReach.Reached(9.03, double.NaN, ThdSweepLevelUnit.Watt));
    }

    [Fact]
    public void TheMessageQuotesTheUnitTheLevelWasTypedIn()
    {
        // Asked for 1 W into 8 Ω, got 6 dB less — a quarter of the power.
        double target = ThdSweepLevelUnit.Watt.ToDbv(1.0, 8.0);
        string message = OutputLevelReach.Message(target, target - 6.0206, ThdSweepLevelUnit.Watt, 8.0, 18.0);

        Assert.Contains("0.25 W", message);
        Assert.Contains("1 W", message);
        Assert.Contains("25 %", message);
        Assert.Contains("18 dBV ceiling", message);
        Assert.Contains("powered on", message);
    }

    [Fact]
    public void TheMessageSpeaksVoltsForAVoltageTarget()
    {
        double target = ThdSweepLevelUnit.Volt.ToDbv(2.0, 8.0);
        string message = OutputLevelReach.Message(target, target - 6.0206, ThdSweepLevelUnit.Volt, 8.0, 18.0);

        Assert.Contains("1 V of the 2 V", message);
        Assert.Contains("50 %", message);
    }

    [Theory]
    [InlineData(ThdSweepLevelUnit.Watt, 1.0)]
    [InlineData(ThdSweepLevelUnit.MilliWatt, 250.0)]
    [InlineData(ThdSweepLevelUnit.Volt, 2.83)]
    [InlineData(ThdSweepLevelUnit.MilliVolt, 775.0)]
    [InlineData(ThdSweepLevelUnit.Dbv, -3.5)]
    public void FromDbvInvertsToDbv(ThdSweepLevelUnit unit, double value)
    {
        double dbv = unit.ToDbv(value, 8.0);
        Assert.Equal(value, OutputLevelReach.FromDbv(dbv, unit, 8.0), 6);
    }

    /// <summary>
    /// The report this was written for, as a case: 5 mV of DUT output asked of a tape head preamp,
    /// against the chirp's -60 dBV floor. The figure that explains it is the 14 dB of gain the
    /// target still allows -- without it the message says only that the gain is too high, which is
    /// what left the user turning his preamp down in search of a limit he could not see.
    /// </summary>
    [Fact]
    public void TheFloorMessage_NamesTheGainThatTargetStillAllows()
    {
        double target = ThdSweepLevelUnit.MilliVolt.ToDbv(5.0, 8.0);
        string message = OutputLevelReach.FloorMessage(
            target, ThdSweepLevelUnit.MilliVolt, 8.0, -60.0, OutputLevelReach.ResponseGeneratorModeLabel);

        // -46.02 dBV asked for, -60 dBV floor: 14 dB.
        Assert.Contains("less than 14 dB of gain", message);

        // Quoted in the unit it was typed in, not as the dBV it is stored as -- the ceiling
        // message's own rule, and here it is the difference between "5 mV" and "-46.021 dBV".
        Assert.Contains("5 mV", message);
        Assert.Contains("-60 dBV", message);
    }

    /// <summary>
    /// The bound tightens as the target falls, which is the whole mechanism: the same DUT is
    /// measurable at one target and not at another, so the limit has to be quoted per run rather
    /// than stated once in the manual.
    /// </summary>
    [Theory]
    [InlineData(0.0, -60.0, "less than 60 dB of gain")]
    [InlineData(-20.0, -60.0, "less than 40 dB of gain")]
    [InlineData(0.0, -40.0, "less than 40 dB of gain")]
    public void TheFloorMessagesGainLimit_IsTheTargetMinusTheFloor(double targetDbv, double floorDbv, string expected)
    {
        Assert.Contains(expected, OutputLevelReach.FloorMessage(
            targetDbv, ThdSweepLevelUnit.Dbv, 8.0, floorDbv, OutputLevelReach.SweepGeneratorModeLabel));
    }

    /// <summary>
    /// A target below the floor itself leaves a negative limit, which the stepped family reaches
    /// easily: its floor is -40 dBV, so 5 mV of output can only be measured on a DUT that
    /// attenuates. Stated in that direction rather than as "less than -6 dB of gain", which is
    /// true and reads as a typo.
    /// </summary>
    [Fact]
    public void TheFloorMessage_BelowTheFloor_SpeaksOfAttenuationRatherThanNegativeGain()
    {
        double target = ThdSweepLevelUnit.MilliVolt.ToDbv(5.0, 8.0);
        string message = OutputLevelReach.FloorMessage(
            target, ThdSweepLevelUnit.MilliVolt, 8.0, -40.0, OutputLevelReach.SweepGeneratorModeLabel);

        Assert.Contains("attenuates by more than 6 dB", message);
        Assert.DoesNotContain("-6 dB of gain", message);
    }

    /// <summary>
    /// The lever, which is the half the four hand-written copies all missed: every one of them
    /// described the fault and none named the control that resolves it.
    /// </summary>
    [Fact]
    public void TheFloorMessage_NamesTheModeToSwitchTo_InThatScreensOwnSpelling()
    {
        Assert.Contains("Input voltage", OutputLevelReach.FloorMessage(
            -46.02, ThdSweepLevelUnit.Dbv, 8.0, -60.0, OutputLevelReach.ResponseGeneratorModeLabel));

        Assert.Contains("Generator voltage", OutputLevelReach.FloorMessage(
            -46.02, ThdSweepLevelUnit.Dbv, 8.0, -40.0, OutputLevelReach.SweepGeneratorModeLabel));
    }

    /// <summary>
    /// An audit rather than a case, on <c>UdevRulesTests</c>'s terms: it reads the six
    /// settings panels as text and fails if one of them offers the generator-driven level mode
    /// under a label this class does not know.
    ///
    /// <para>This is the check that would have caught the mistake that produced it. Two panels --
    /// frequency response and impulse response -- carry their own <c>LEVEL_MODES</c> and spell the
    /// option <i>Input voltage</i>, where the other four import the shared list and spell it
    /// <i>Generator voltage</i>; the first answer given to the user named the wrong one, which
    /// would have sent him looking for a control that is not on his screen. Nothing else fails when
    /// a panel's label is edited, because the message is a string in another language's project.</para>
    /// </summary>
    [Fact]
    public void EveryPanelsGeneratorModeLabel_IsOneTheFloorMessageKnows()
    {
        (string Panel, string Expected)[] panels =
        [
            ("FrequencyResponseConfigPanel.vue", OutputLevelReach.ResponseGeneratorModeLabel),
            ("ImpulseResponseConfigPanel.vue", OutputLevelReach.ResponseGeneratorModeLabel),
            ("SweepConfigPanel.vue", OutputLevelReach.SweepGeneratorModeLabel),
            ("SteppedResponseConfigPanel.vue", OutputLevelReach.SweepGeneratorModeLabel),
            ("CrosstalkConfigPanel.vue", OutputLevelReach.SweepGeneratorModeLabel),
            ("OutputImpedanceConfigPanel.vue", OutputLevelReach.SweepGeneratorModeLabel),
        ];

        foreach (var (panel, expected) in panels)
        {
            string source = File.ReadAllText(ComponentPath(panel));

            // The panels that import the shared list name no label of their own, so the label is
            // asserted against measurements/levelMode.ts for those and against the panel for the
            // two that still carry a copy.
            string where = source.Contains("LEVEL_MODES: {", StringComparison.Ordinal)
                ? source
                : File.ReadAllText(LevelModeModulePath());

            Assert.Contains($"label: '{expected}'", where);
        }
    }

    private static string ComponentPath(string fileName) =>
        Path.Combine(WebSrc(), "components", fileName);

    private static string LevelModeModulePath() =>
        Path.Combine(WebSrc(), "measurements", "levelMode.ts");

    private static string WebSrc()
    {
        // Walk up from the test binary to the repository root, as UdevRulesTests does.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "web", "src", "components")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "web", "src");
    }
}
