using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// When a bench has a clock-drift correction, and when offering one would be worse than offering
/// none: the correction <em>changes</em> a measurement's phase and group delay, so a wrong figure
/// replaces an error the user knows about with one they do not.
///
/// <para>Keyed on a <see cref="BenchClocks"/> pairing since 2026-11-13, where it used to be one
/// figure on the sound card's document behind a "is the analyzer a sound card" gate. The quantity
/// is a property of two crystals and neither has to be a card — which is the bench the old shape
/// could not express at all, and the first case below.</para>
/// </summary>
public class ClockDriftCorrectionTests
{
    private static readonly DateTimeOffset Measured = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static SoundCardConfig Cards(string input = "Line In (G6)", string output = "Main Out (M4)") =>
        new(InputHostApi: "ASIO", InputDeviceName: input, OutputHostApi: "ASIO", OutputDeviceName: output);

    private static SplitDeviceSetting Split(string inputKind, string outputKind) =>
        new() { Config = new SplitDeviceConfig(inputKind, outputKind) };

    private static SplitDeviceSetting NoSplit() => new();

    private static ClockDriftConfig Store(BenchClocks clocks, double ppm = -104.038) =>
        ClockDriftConfig.Default.With(new ClockDriftEntry(
            clocks.OutputKey, clocks.InputKey, clocks.OutputLabel, clocks.InputLabel,
            ppm, SpreadPpm: 0.33, SigmaPpm: null, ToneMarginDb: null, MeasuredUtc: Measured));

    // ---- which benches have two clocks ----

    /// <summary>
    /// The bench this whole change exists for: a QA40x capturing while a USB DAC plays. Two
    /// crystals exactly as much as two cards are, and until this was keyed on a pairing it was
    /// offered no correction at all.
    /// </summary>
    [Fact]
    public void AQa40xCapturingWhileACardPlays_IsTwoClocks()
    {
        BenchClocks? clocks = BenchClocks.For(DeviceKind.Split, Cards(), Split("Hardware", "SoundCard"));

        Assert.NotNull(clocks);
        Assert.Equal("qa40x", clocks!.InputKey);
        Assert.StartsWith("card:", clocks.OutputKey);

        var found = ClockDriftCorrection.ForBench(clocks, Store(clocks));
        Assert.Equal(-104.038, found?.Ppm);
        Assert.Equal(Measured, found?.MeasuredUtc);
    }

    [Theory]
    [InlineData(DeviceKind.Hardware)]
    [InlineData(DeviceKind.Simulated)]
    public void AnAnalyzerWithOneClock_HasNoPairingAtAll(DeviceKind kind)
    {
        // One device plays and captures, so the quantity does not exist for it.
        Assert.Null(BenchClocks.For(kind, Cards(), NoSplit()));
        Assert.Null(ClockDriftCorrection.ForBench(null, ClockDriftConfig.Default));
    }

    [Fact]
    public void ASplitWhoseHalvesAreTheSameDevice_IsOneClock()
    {
        // Two simulators are one model twice over: a difference measured between them would be a
        // reading of nothing.
        Assert.Null(BenchClocks.For(DeviceKind.Split, Cards(), Split("Simulated", "Simulated")));
    }

    /// <summary>
    /// The premise of the whole measurement: drift is the <em>difference</em> between two crystals,
    /// and one card has one. A card used both ways reads ~0 ppm however it is driven — a resampler
    /// on it converts between the app's rate and the device's on the same clock in both directions,
    /// so nothing is time-scaled.
    /// </summary>
    [Fact]
    public void OneCardUsedBothWays_IsNeverOfferedACorrection()
    {
        // The endpoint names differ, which is exactly the trap: one interface is several endpoints.
        var oneCard = Cards(input: "In 3-4 (MOTU M Series)", output: "Out 3-4 (MOTU M Series)");

        Assert.False(ClockDriftCorrection.UsesTwoCards(oneCard));
        Assert.Null(BenchClocks.For(DeviceKind.SoundCard, oneCard, NoSplit()));
        Assert.Equal(0.0, ClockDriftCorrection.Resolve(true, null, ClockDriftConfig.Default));
    }

    /// <summary>
    /// An ASIO driver <em>is</em> the card, so both sides carry the same bare name and there is no
    /// stem to find. The fallback has to read that as one card, or every ASIO bench would be
    /// offered a correction for a clock difference it cannot have.
    /// </summary>
    [Fact]
    public void AnAsioDriverUsedBothWays_IsOneCard()
    {
        Assert.False(ClockDriftCorrection.UsesTwoCards(Cards(input: "MOTU M Series", output: "MOTU M Series")));
    }

    [Fact]
    public void TwoCardsAreTwoCards_WhateverTheirEndpointsAreCalled()
    {
        Assert.True(ClockDriftCorrection.UsesTwoCards(Cards()));
        Assert.True(ClockDriftCorrection.UsesTwoCards(
            Cards(input: "Line In (2- Sound BlasterX G6)", output: "Out 3-4 (MOTU M Series)")));
    }

    /// <summary>
    /// The same card under two host APIs is two clocks' worth of <i>path</i> but one crystal, and
    /// the host API is part of the key — so this reads as two cards and the figure applies only to
    /// the pairing it was measured on. Erring this way is deliberate: see
    /// <see cref="ClockDriftCorrection.UsesTwoCards"/>, where the expensive mistake is withholding
    /// a correction rather than offering one the threshold then refuses.
    /// </summary>
    [Fact]
    public void TheSameCardUnderTwoHostApis_ReadsAsTwo()
    {
        Assert.True(ClockDriftCorrection.UsesTwoCards(Cards() with { InputHostApi = "WASAPI" }));
    }

    // ---- which figure applies ----

    [Fact]
    public void ABenchThatHasNeverRunTheTest_HasNothingToApply()
    {
        BenchClocks clocks = BenchClocks.For(DeviceKind.SoundCard, Cards(), NoSplit())!;

        Assert.Null(ClockDriftCorrection.ForBench(clocks, ClockDriftConfig.Default));
        Assert.Null(ClockDriftCorrection.ForBench(clocks, null));
    }

    /// <summary>
    /// Either side is enough: the figure is the difference between two crystals, so replacing
    /// either one of them makes it a measurement of something else. Under a keyed store that is not
    /// so much a refusal as a miss — the entry is still there, under the pairing it belongs to.
    /// </summary>
    [Fact]
    public void AFigureMeasuredBetweenADifferentPair_DoesNotApply()
    {
        BenchClocks measured = BenchClocks.For(DeviceKind.SoundCard, Cards(), NoSplit())!;
        ClockDriftConfig store = Store(measured);

        foreach (SoundCardConfig moved in new[]
        {
            Cards(input: "Mic In (laptop)"),
            Cards(output: "Speakers (laptop)"),
            Cards() with { InputHostApi = "WASAPI" },
        })
        {
            BenchClocks now = BenchClocks.For(DeviceKind.SoundCard, moved, NoSplit())!;
            Assert.Null(ClockDriftCorrection.ForBench(now, store));
        }

        // And the original pairing still finds it, which is what a keyed store buys: a bench that
        // swaps a card for an afternoon does not lose a figure that took thirty readings.
        Assert.NotNull(ClockDriftCorrection.ForBench(measured, store));
    }

    /// <summary>
    /// A crystal is a property of the device and a full scale is a property of one channel pair, so
    /// the two differ by design — moving to another channel pair of the same card leaves the drift
    /// figure true. The key says so by construction: it carries no channel offset at all.
    /// </summary>
    [Fact]
    public void AFigureStillAppliesAfterTheChannelPairMoves()
    {
        BenchClocks before = BenchClocks.For(DeviceKind.SoundCard, Cards(), NoSplit())!;
        BenchClocks after = BenchClocks.For(
            DeviceKind.SoundCard, Cards() with { InputChannelOffset = 2, OutputChannelOffset = 2 }, NoSplit())!;

        Assert.Equal(before.InputKey, after.InputKey);
        Assert.NotNull(ClockDriftCorrection.ForBench(after, Store(before)));
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

        BenchClocks clocks = BenchClocks.For(DeviceKind.SoundCard, Cards(), NoSplit())!;
        Assert.Null(ClockDriftCorrection.ForBench(clocks, Store(clocks, ppm)));
    }

    [Fact]
    public void ANonFiniteStoredFigure_IsNotOffered()
    {
        BenchClocks clocks = BenchClocks.For(DeviceKind.SoundCard, Cards(), NoSplit())!;

        Assert.Null(ClockDriftCorrection.ForBench(clocks, Store(clocks, double.NaN)));
        Assert.False(Store(clocks, double.NaN).IsValid);
    }

    // ---- the store ----

    [Fact]
    public void ASecondReadingOfOnePairing_ReplacesTheFirst()
    {
        BenchClocks clocks = BenchClocks.For(DeviceKind.SoundCard, Cards(), NoSplit())!;

        ClockDriftConfig store = Store(clocks, -104.0);
        store = store.With(new ClockDriftEntry(
            clocks.OutputKey, clocks.InputKey, clocks.OutputLabel, clocks.InputLabel, 83.18,
            MeasuredUtc: Measured));

        Assert.Single(store.Entries);
        Assert.Equal(83.18, ClockDriftCorrection.ForBench(clocks, store)?.Ppm);
    }

    [Fact]
    public void TheStoreKeepsEveryPairing_NewestFirst_UpToItsCap()
    {
        var store = ClockDriftConfig.Default;
        for (int i = 0; i < ClockDriftConfig.MaxEntries + 4; i++)
        {
            store = store.With(new ClockDriftEntry(
                $"card:ASIO/out{i}", $"card:ASIO/in{i}", $"out{i}", $"in{i}", 10.0 + i,
                MeasuredUtc: Measured));
        }

        Assert.Equal(ClockDriftConfig.MaxEntries, store.Entries.Count);

        // Newest first, so the cap drops the pairing nobody has measured for longest.
        Assert.Equal("card:ASIO/out19", store.Entries[0].OutputKey);
        Assert.Null(store.For("card:ASIO/out0", "card:ASIO/in0"));
    }
}
