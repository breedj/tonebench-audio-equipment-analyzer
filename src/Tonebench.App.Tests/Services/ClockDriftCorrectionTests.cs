using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// There are four separate ways a bench has no clock-drift correction, and every one of them is a
/// case where offering one would be worse than offering none: the correction <em>changes</em> a
/// measurement's phase and group delay, so a wrong figure replaces an error the user knows about
/// with one they do not.
/// </summary>
public class ClockDriftCorrectionTests
{
    private static SoundCardConfig Pair(
        string input = "Line In (G6)", string output = "Main Out (M4)", double? ppm = -104.038) =>
        new(InputHostApi: "ASIO", InputDeviceName: input, OutputHostApi: "ASIO", OutputDeviceName: output)
        {
            ClockDriftPpm = ppm,
            ClockDriftSpreadPpm = 0.33,
            ClockDriftMeasuredUtc = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero),
            ClockDriftInputHostApi = "ASIO",
            ClockDriftInputDeviceName = input,
            ClockDriftOutputHostApi = "ASIO",
            ClockDriftOutputDeviceName = output,
        };

    [Fact]
    public void AMeasuredFigureOnTheCardItWasMeasuredOn_Applies()
    {
        var found = ClockDriftCorrection.ForBench(DeviceKind.SoundCard, Pair());

        Assert.NotNull(found);
        Assert.Equal(-104.038, found!.Ppm);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero), found.MeasuredUtc);
    }

    /// <summary>
    /// The one reason that is about the analyzer rather than the configuration, and the reason this
    /// lives in the App layer: the stored figure still names whichever card was last selected, so a
    /// QA403 session would otherwise be offered a correction measured between two devices that are
    /// not in its path — and it has one clock, so the quantity does not exist for it at all.
    /// </summary>
    [Theory]
    [InlineData(DeviceKind.Hardware)]
    [InlineData(DeviceKind.Simulated)]
    public void AnAnalyzerWithOneClock_IsNeverOfferedOne(DeviceKind kind)
    {
        Assert.Null(ClockDriftCorrection.ForBench(kind, Pair()));
    }

    [Fact]
    public void ABenchThatHasNeverRunTheTest_HasNothingToApply()
    {
        Assert.Null(ClockDriftCorrection.ForBench(DeviceKind.SoundCard, Pair(ppm: null)));
        Assert.Null(ClockDriftCorrection.ForBench(DeviceKind.SoundCard, SoundCardConfig.Default));
        Assert.Null(ClockDriftCorrection.ForBench(DeviceKind.SoundCard, null));
    }

    /// <summary>
    /// Either side is enough: the figure is the difference between two crystals, so replacing
    /// either one of them makes it a measurement of something else.
    /// </summary>
    [Fact]
    public void AFigureMeasuredBetweenADifferentPair_DoesNotApply()
    {
        var movedInput = Pair() with { InputDeviceName = "Mic In (laptop)" };
        var movedOutput = Pair() with { OutputDeviceName = "Speakers (laptop)" };
        var movedHostApi = Pair() with { InputHostApi = "WASAPI" };

        Assert.Null(ClockDriftCorrection.ForBench(DeviceKind.SoundCard, movedInput));
        Assert.Null(ClockDriftCorrection.ForBench(DeviceKind.SoundCard, movedOutput));
        Assert.Null(ClockDriftCorrection.ForBench(DeviceKind.SoundCard, movedHostApi));
    }

    /// <summary>
    /// A crystal is a property of the device and a full scale is a property of one channel pair, so
    /// the two stamps differ by design — moving to another channel pair of the same card leaves the
    /// drift figure true, and a correction that stood itself down there would be answering a
    /// question nobody asked.
    /// </summary>
    [Fact]
    public void AFigureStillAppliesAfterTheChannelPairMoves()
    {
        var moved = Pair() with { InputChannelOffset = 2, OutputChannelOffset = 2 };

        Assert.NotNull(ClockDriftCorrection.ForBench(DeviceKind.SoundCard, moved));
    }

    /// <summary>
    /// The same threshold <see cref="ChirpClockDrift.For"/> refuses at, read from there rather than
    /// restated: a bench offered a correction the estimator then declines to make is a checkbox
    /// that does nothing.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.04)]
    [InlineData(-0.04)]
    public void AFigureTooSmallToBeWorthCorrecting_IsNotOffered(double ppm)
    {
        Assert.True(Math.Abs(ppm) < ChirpClockDrift.NegligiblePpm);
        Assert.Null(ClockDriftCorrection.ForBench(DeviceKind.SoundCard, Pair(ppm: ppm)));
    }

    [Fact]
    public void ANonFiniteStoredFigure_IsNotOffered()
    {
        Assert.Null(ClockDriftCorrection.ForBench(DeviceKind.SoundCard, Pair(ppm: double.NaN)));
    }

    /// <summary>
    /// A figure stored by hand with no stamp is read as applying — the opposite of what the
    /// calibration notices do with an unstamped full scale, because there is no history of
    /// unstamped drift figures for the leniency to protect.
    /// </summary>
    [Fact]
    public void AnUnstampedFigure_IsReadAsApplying()
    {
        var unstamped = SoundCardConfig.Default with
        {
            InputDeviceName = "Line In (G6)",
            OutputDeviceName = "Main Out (M4)",
            ClockDriftPpm = -104.038,
        };

        Assert.NotNull(ClockDriftCorrection.ForBench(DeviceKind.SoundCard, unstamped));
    }

    [Fact]
    public void ARunThatDidNotAskToBeCorrected_IsNot()
    {
        Assert.Equal(0.0, ClockDriftCorrection.Resolve(false, DeviceKind.SoundCard, Pair()));
    }

    /// <summary>
    /// Asking for a correction a bench cannot make is not an error: the run measures uncorrected,
    /// which is what every build before this did. It is reachable from a browser that was open
    /// across an analyzer switch.
    /// </summary>
    [Fact]
    public void AskingOnABenchWithNoFigure_MeasuresUncorrected()
    {
        Assert.Equal(0.0, ClockDriftCorrection.Resolve(true, DeviceKind.Hardware, Pair()));
        Assert.Equal(0.0, ClockDriftCorrection.Resolve(true, DeviceKind.SoundCard, Pair(ppm: null)));
    }

    [Fact]
    public void AskingOnABenchWithOne_UsesIt()
    {
        Assert.Equal(-104.038, ClockDriftCorrection.Resolve(true, DeviceKind.SoundCard, Pair()));
    }

    /// <summary>
    /// The figure the resolver hands out has to be one the estimator will act on, or the whole
    /// chain is a checkbox and a stored ppm with nothing in between. This is the seam those two
    /// halves meet at, so it is asserted end to end rather than at either end.
    /// </summary>
    [Fact]
    public void TheResolvedFigureIsOneTheEstimatorWillCorrectBy()
    {
        double ppm = ClockDriftCorrection.Resolve(true, DeviceKind.SoundCard, Pair());

        Assert.NotNull(ChirpClockDrift.For(ppm, 48_000.0, 32768, 20.0, 24_000.0));
    }

    /// <summary>
    /// The stamp is what makes a stored figure checkable, so it has to record the pairing as it
    /// stands at the moment the figure is taken rather than whatever a later write leaves behind.
    /// </summary>
    [Fact]
    public void StampingRecordsThePairTheFigureWasMeasuredBetween()
    {
        var measured = new DateTimeOffset(2026, 11, 11, 18, 30, 0, TimeSpan.Zero);
        var stamped = new SoundCardConfig(
                InputHostApi: "ASIO", InputDeviceName: "Line In (G6)",
                OutputHostApi: "ASIO", OutputDeviceName: "Main Out (M4)")
            .StampClockDrift(-104.038, 0.33, measured);

        Assert.Equal(-104.038, stamped.ClockDriftPpm);
        Assert.Equal(0.33, stamped.ClockDriftSpreadPpm);
        Assert.Equal(measured, stamped.ClockDriftMeasuredUtc);
        Assert.Equal("Line In (G6)", stamped.ClockDriftInputDeviceName);
        Assert.Equal("Main Out (M4)", stamped.ClockDriftOutputDeviceName);
        Assert.True(stamped.ClockDriftMatchesPairing);
        Assert.True(stamped.IsValid);
    }

    /// <summary>
    /// A rate error is a real and diagnosable fault rather than a corrupt reading — 44.1 kHz where
    /// 48 kHz was asked for is 81 250 ppm — so it has to be storable, while a figure no clock can
    /// produce must not be.
    /// </summary>
    [Fact]
    public void ARateErrorIsStorable_AndSomethingNoClockCanDoIsNot()
    {
        var pair = new SoundCardConfig(
            InputHostApi: "ASIO", InputDeviceName: "in", OutputHostApi: "ASIO", OutputDeviceName: "out");

        Assert.True(pair.StampClockDrift(-81_250.0, null, DateTimeOffset.UtcNow).IsValid);
        Assert.False(pair.StampClockDrift(5_000_000.0, null, DateTimeOffset.UtcNow).IsValid);
        Assert.False(pair.StampClockDrift(double.NaN, null, DateTimeOffset.UtcNow).IsValid);
    }
}
