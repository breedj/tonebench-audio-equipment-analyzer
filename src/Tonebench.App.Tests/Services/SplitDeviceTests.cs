using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Device.SoundCard;
using Tonebench.Device.Split;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The fourth analyzer kind: which two devices a composite is made of, which pairings are refused
/// and why, and that the factory builds each half the way it has to be built.
///
/// <para>The composite's own routing is <c>SplitAnalyzerDeviceTests</c> in the device project. What
/// is here is the App layer's half: the stored document, the two refusals, and the two rules that
/// are invisible from the outside — a sound-card half must be built for <i>one direction</i>, and
/// a card against another card must be given a transfer thread of its own.</para>
/// </summary>
public class SplitDeviceTests
{
    [Fact]
    public void TheDefaultPairing_IsAQa40xCapturingWhileACardPlays()
    {
        // The bench this feature was asked for: a Topping D50 III playing, a QA403 measuring.
        SplitPairing pairing = Assert.IsType<SplitPairing>(
            SplitPairing.Resolve(SplitDeviceConfig.Default, out string? problem));

        Assert.Null(problem);
        Assert.Equal(DeviceKind.Hardware, pairing.Input);
        Assert.Equal(DeviceKind.SoundCard, pairing.Output);

        // And it follows the analysis rate, so a split behaves like one device until told otherwise.
        Assert.Equal(0.0, SplitDeviceConfig.Default.GeneratorSampleRateHz);
    }

    [Fact]
    public void ASoundCardOnBothSides_IsAllowed_BecauseEachHalfTakesATransferThreadOfItsOwn()
    {
        // This was refused until a card half could be given a thread of its own: a transfer loop
        // occupies the thread it is posted to, so two cards on the library's one thread meant the
        // second pump never ran -- no exception and no log line, just an acquisition producing
        // nothing. It is now the pairing the whole feature exists for.
        SplitPairing pairing = Assert.IsType<SplitPairing>(
            SplitPairing.Resolve(new SplitDeviceConfig("SoundCard", "SoundCard"), out string? problem));

        Assert.Null(problem);
        Assert.Equal(DeviceKind.SoundCard, pairing.Input);
        Assert.Equal(DeviceKind.SoundCard, pairing.Output);
    }

    /// <summary>
    /// Every pairing of every kind, so a fifth <see cref="DeviceKind"/> cannot be added without a
    /// verdict for it. A matrix rather than a case each because the interesting property is that
    /// the refusals are <i>exactly</i> these: a kind that quietly became unbuildable would
    /// otherwise show up as a bench that falls back to the default with nothing said.
    /// </summary>
    [Theory]
    [InlineData(DeviceKind.Hardware, DeviceKind.Hardware, false)]
    [InlineData(DeviceKind.Hardware, DeviceKind.Simulated, true)]
    [InlineData(DeviceKind.Hardware, DeviceKind.SoundCard, true)]
    [InlineData(DeviceKind.Hardware, DeviceKind.Split, false)]
    [InlineData(DeviceKind.Simulated, DeviceKind.Hardware, true)]
    [InlineData(DeviceKind.Simulated, DeviceKind.Simulated, true)]
    [InlineData(DeviceKind.Simulated, DeviceKind.SoundCard, true)]
    [InlineData(DeviceKind.Simulated, DeviceKind.Split, false)]
    [InlineData(DeviceKind.SoundCard, DeviceKind.Hardware, true)]
    [InlineData(DeviceKind.SoundCard, DeviceKind.Simulated, true)]
    [InlineData(DeviceKind.SoundCard, DeviceKind.SoundCard, true)]
    [InlineData(DeviceKind.SoundCard, DeviceKind.Split, false)]
    [InlineData(DeviceKind.Split, DeviceKind.Hardware, false)]
    [InlineData(DeviceKind.Split, DeviceKind.Simulated, false)]
    [InlineData(DeviceKind.Split, DeviceKind.SoundCard, false)]
    [InlineData(DeviceKind.Split, DeviceKind.Split, false)]
    public void ThePairingMatrix_IsExactlyTheseRefusals(DeviceKind input, DeviceKind output, bool allowed)
    {
        SplitPairing? pairing = SplitPairing.Resolve(
            new SplitDeviceConfig(input.ToString(), output.ToString()), out string? problem);

        Assert.Equal(allowed, pairing is not null);
        Assert.Equal(allowed, problem is null);
    }

    [Fact]
    public void EveryKind_HasARowInThePairingMatrix()
    {
        // The matrix above is written out, so it goes stale silently. This is what fails instead.
        Assert.Equal(4, Enum.GetValues<DeviceKind>().Length);
    }

    [Fact]
    public void AQa40xOnBothSides_IsRefused_BecauseThatIsOneDeviceOpenedTwice()
    {
        Assert.Null(SplitPairing.Resolve(new SplitDeviceConfig("Hardware", "Hardware"), out string? problem));

        Assert.NotNull(problem);
        Assert.Contains("opened twice", problem);
    }

    [Fact]
    public void ACompositeInsideAComposite_IsRefused()
    {
        Assert.Null(SplitPairing.Resolve(new SplitDeviceConfig("Split", "SoundCard"), out string? problem));
        Assert.Contains("have to be a QA40x", problem);

        Assert.Null(SplitPairing.Resolve(new SplitDeviceConfig("Hardware", "Split"), out _));
    }

    [Fact]
    public void TwoSimulators_AreAllowed_BecauseThatIsTheOnlyBenchWithNothingPluggedIn()
    {
        // It exercises the whole composite -- both stream lifecycles, the rate conversion, the
        // withheld settle -- at a desk, which is what makes the rest of this testable at all.
        SplitPairing pairing = Assert.IsType<SplitPairing>(
            SplitPairing.Resolve(new SplitDeviceConfig("Simulated", "Simulated"), out _));

        Assert.Equal(DeviceKind.Simulated, pairing.Input);
        Assert.Equal(DeviceKind.Simulated, pairing.Output);
    }

    [Fact]
    public void AKindStoredAsANumber_IsRefused()
    {
        // Enum.TryParse also accepts a number: "1" parses happily as Simulated. Storing names
        // rather than numbers is the whole point, so a document holding a number is not one this
        // build wrote and its number means nothing -- the same rule device.analyzer follows.
        Assert.Null(SplitPairing.Resolve(new SplitDeviceConfig("0", "2"), out string? problem));
        Assert.Contains("not an analyzer this build knows", problem);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-48_000.0)]
    [InlineData(1_000_000.0)]
    public void AnImpossibleGeneratorRate_IsRefused(double rateHz)
    {
        Assert.Null(SplitPairing.Resolve(new SplitDeviceConfig("Hardware", "SoundCard", rateHz), out string? problem));
        Assert.Contains("out of range", problem);
    }

    [Fact]
    public void TheSetting_HandedAnUnbuildablePairing_FallsBackToTheDefault()
    {
        // Load bearing twice over: the session builds its first device in a field initializer, so a
        // refusal here would be a crash at startup rather than a message on the status bar.
        var setting = new SplitDeviceSetting
        {
            Config = new SplitDeviceConfig("Hardware", "Hardware", 96_000.0),
        };

        Assert.Equal(SplitDeviceConfig.Default, setting.Config);
        Assert.Equal(DeviceKind.Hardware, setting.InputKind);
        Assert.Equal(DeviceKind.SoundCard, setting.OutputKind);
        Assert.Equal(0.0, setting.GeneratorSampleRateHz);
    }

    [Fact]
    public void TheSetting_HandedAUsablePairing_KeepsIt()
    {
        var setting = new SplitDeviceSetting
        {
            Config = new SplitDeviceConfig("Simulated", "SoundCard", 48_000.0),
        };

        Assert.Equal(DeviceKind.Simulated, setting.InputKind);
        Assert.Equal(DeviceKind.SoundCard, setting.OutputKind);
        Assert.Equal(48_000.0, setting.GeneratorSampleRateHz);
    }

    // ---- the factory ----

    /// <summary>
    /// The real factory with its one native constructor replaced: a <c>SoundCardDevice</c> reaches
    /// <c>PortAudioNative.Shared</c>, so a unit test must not build one. What is under test is which
    /// <i>settings</i> each half was built from.
    /// </summary>
    private sealed class RecordingFactory(SoundCardSetting soundCard, SplitDeviceSetting split)
        : AnalyzerDeviceFactory(NullLoggerFactory.Instance, DeviceKind.Split, soundCard, split: split)
    {
        public List<SoundCardSettings> CardsBuilt { get; } = [];

        /// <summary>Whether each card, in the order built, was given a transfer thread of its own.</summary>
        public List<bool> OwnStreamThreads { get; } = [];

        protected override IAnalyzerDevice CreateSoundCard(SoundCardSettings settings, bool ownStreamThread = false)
        {
            CardsBuilt.Add(settings);
            OwnStreamThreads.Add(ownStreamThread);
            return new SimulatedDevice();
        }
    }

    private static SoundCardSetting TwoCards(string inputHostApi = "WASAPI", string outputHostApi = "WASAPI") => new()
    {
        Config = SoundCardConfig.Default with
        {
            InputHostApi = inputHostApi,
            InputDeviceName = "In 1-2 (MOTU M Series)",
            OutputHostApi = outputHostApi,
            OutputDeviceName = "Luidsprekers (TOPPING USB DAC)",
        },
    };

    /// <summary>
    /// <b>Migration 006's open risk, closed.</b> A bench migrated from a two-card sound card to a
    /// split has to keep finding the clock difference it measured, rather than silently reading
    /// zero and correcting nothing — and the figure is stored per <i>pairing</i>, so what matters
    /// is that both routes resolve to the same two keys.
    ///
    /// <para>Asserted as the two being equal rather than against a spelled-out key, which would
    /// pin the format instead of the property: the format may change, and this must not care.</para>
    /// </summary>
    [Fact]
    public void AMigratedTwoCardBench_KeepsTheClockPairingItHadAsASoundCard()
    {
        SoundCardConfig cards = TwoCards().Config;

        BenchClocks? asACard = BenchClocks.For(DeviceKind.SoundCard, cards, new SplitDeviceSetting());
        BenchClocks? asASplit = BenchClocks.For(
            DeviceKind.Split,
            cards,
            new SplitDeviceSetting { Config = new SplitDeviceConfig("SoundCard", "SoundCard") });

        Assert.NotNull(asACard);
        Assert.NotNull(asASplit);
        Assert.Equal(asACard.OutputKey, asASplit.OutputKey);
        Assert.Equal(asACard.InputKey, asASplit.InputKey);
    }

    private static SoundCardSetting DuplexCard() => new()
    {
        Config = SoundCardConfig.Default with
        {
            InputHostApi = "WASAPI",
            InputDeviceName = "In 1-2 (MOTU M Series)",
            OutputHostApi = "WASAPI",
            OutputDeviceName = "Out 1-2 (Topping D50 III)",
        },
    };

    [Fact]
    public void TheFactory_BuildsASplitFromTheNamedKinds()
    {
        var factory = new RecordingFactory(
            DuplexCard(),
            new SplitDeviceSetting { Config = new SplitDeviceConfig("Simulated", "Simulated", 48_000.0) });

        var split = Assert.IsType<SplitAnalyzerDevice>(factory.Create(DeviceKind.Split));

        Assert.IsType<SimulatedDevice>(split.Input);
        Assert.IsType<SimulatedDevice>(split.Output);
        Assert.Equal(48_000.0, split.GeneratorSampleRateHz);
        Assert.Empty(factory.CardsBuilt);
    }

    [Fact]
    public void TheFactory_BuildsASoundCardHalfForOneDirectionOnly()
    {
        // Without the masking a fully-configured duplex card used as one half opens *duplex* and
        // claims an input the split is not using -- which under WASAPI exclusive takes an endpoint
        // away from whatever else wanted it, and on the generator side is a capture nobody reads.
        var factory = new RecordingFactory(
            DuplexCard(),
            new SplitDeviceSetting { Config = new SplitDeviceConfig("Hardware", "SoundCard") });

        factory.Create(DeviceKind.Split);

        SoundCardSettings card = Assert.Single(factory.CardsBuilt);
        Assert.Equal(SoundCardStreamDirection.OutputOnly, card.Direction);
        Assert.Equal("Out 1-2 (Topping D50 III)", card.OutputDeviceName);
        Assert.Equal(string.Empty, card.InputDeviceName);
    }

    [Fact]
    public void TwoCardHalves_AreEachGivenATransferThreadOfTheirOwn()
    {
        // The join to the device layer, and the thing that makes a two-card split work at all: a
        // transfer loop occupies the thread it is posted to for its stream's whole life, so two
        // cards sharing the library's thread means the second pump never runs.
        var factory = new RecordingFactory(
            TwoCards(),
            new SplitDeviceSetting { Config = new SplitDeviceConfig("SoundCard", "SoundCard") });

        factory.Create(DeviceKind.Split);

        Assert.Equal(2, factory.CardsBuilt.Count);
        Assert.All(factory.OwnStreamThreads, own => Assert.True(own));
    }

    [Fact]
    public void ALoneCardHalf_KeepsTheLibrarysThread()
    {
        // One PortAudio stream is the arrangement every measurement this app has taken was
        // measured with, and a card against a QA40x or the simulator keeps it. Giving a thread of
        // its own to every card half would move that path too, for nothing.
        var factory = new RecordingFactory(
            TwoCards(),
            new SplitDeviceSetting { Config = new SplitDeviceConfig("Hardware", "SoundCard") });

        factory.Create(DeviceKind.Split);

        Assert.False(Assert.Single(factory.OwnStreamThreads));
    }

    [Fact]
    public void APlainSoundCardBench_KeepsTheLibrarysThread()
    {
        var factory = new RecordingFactory(TwoCards(), new SplitDeviceSetting());

        factory.Create(DeviceKind.SoundCard);

        Assert.False(Assert.Single(factory.OwnStreamThreads));
    }

    [Fact]
    public async Task TwoAsioCards_AreRefusedAtConnect_RatherThanAtConstruction()
    {
        // Construction happens in the session's field initializer, where a throw is a crash at
        // startup instead of a message on the status bar -- so the refusal rides along as a
        // problem the composite reports when something tries to open it.
        var factory = new RecordingFactory(
            TwoCards(inputHostApi: "ASIO", outputHostApi: "ASIO"),
            new SplitDeviceSetting { Config = new SplitDeviceConfig("SoundCard", "SoundCard") });

        var split = Assert.IsType<SplitAnalyzerDevice>(factory.Create(DeviceKind.Split));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => split.ConnectAsync(CancellationToken.None));

        Assert.Contains("one driver open per process", ex.Message);
        Assert.Contains("WASAPI", ex.Message);
    }

    [Fact]
    public void OneAsioCardAgainstAWasapiOne_IsBuiltWithNothingToReport()
    {
        // The pairing a single duplex stream cannot express at all, and the reason two streams are
        // worth having: the better converter keeps the better driver.
        var factory = new RecordingFactory(
            TwoCards(inputHostApi: "ASIO", outputHostApi: "WASAPI"),
            new SplitDeviceSetting { Config = new SplitDeviceConfig("SoundCard", "SoundCard") });

        var split = Assert.IsType<SplitAnalyzerDevice>(factory.Create(DeviceKind.Split));

        Assert.Null(split.CombinationProblem);
    }

    [Fact]
    public void APinnedComposite_ReportsItselfAsASplit()
    {
        // A four-way type test, and this is the arm a two-card bench depends on: a composite is
        // neither a SimulatedDevice nor a SoundCardDevice, so without its own arm it falls through
        // to Hardware and the status bar describes a two-card bench as one QA403.
        var split = new SplitAnalyzerDevice(
            new SimulatedDevice(), new SimulatedDevice(),
            SplitDeviceSettings.Default, NullLogger<SplitAnalyzerDevice>.Instance);

        Assert.Equal(DeviceKind.Split, AnalyzerDeviceFactory.Pinned(split).InitialKind);
    }

    [Fact]
    public void TheFactory_BuildingACardAsTheCapturingHalf_MasksTheOtherDirection()
    {
        var factory = new RecordingFactory(
            DuplexCard(),
            new SplitDeviceSetting { Config = new SplitDeviceConfig("SoundCard", "Simulated") });

        factory.Create(DeviceKind.Split);

        SoundCardSettings card = Assert.Single(factory.CardsBuilt);
        Assert.Equal(SoundCardStreamDirection.InputOnly, card.Direction);
        Assert.Equal("In 1-2 (MOTU M Series)", card.InputDeviceName);
        Assert.Equal(string.Empty, card.OutputDeviceName);
    }

    [Fact]
    public void TheFactory_BuildingTheSoundCardKindOnItsOwn_StillOpensBothDirections()
    {
        // The seam above must not have narrowed the ordinary single-card bench.
        var factory = new RecordingFactory(DuplexCard(), new SplitDeviceSetting());

        factory.Create(DeviceKind.SoundCard);

        SoundCardSettings card = Assert.Single(factory.CardsBuilt);
        Assert.Equal(SoundCardStreamDirection.Duplex, card.Direction);
    }

    [Fact]
    public void APinnedSplitDevice_ReportsSplit_NotHardware()
    {
        // The pinned-constructor kind inference is a type test, and a composite is neither a
        // SimulatedDevice nor a SoundCardDevice -- so without a fourth arm a two-device bench
        // reports itself as one QA403, which is what the status bar would then show.
        var split = new SplitAnalyzerDevice(
            new SimulatedDevice(), new SimulatedDevice(),
            SplitDeviceSettings.Default, NullLogger<SplitAnalyzerDevice>.Instance);

        Assert.Equal(DeviceKind.Split, AnalyzerDeviceFactory.Pinned(split).InitialKind);
    }

    [Fact]
    public void TheFactory_PushesTheStepDownDelayIntoBothHalves()
    {
        var delay = new AutoRangeStepDownDelaySetting { Ms = 750.0 };
        var factory = new AnalyzerDeviceFactory(
            NullLoggerFactory.Instance, DeviceKind.Split,
            stepDownDelay: delay,
            split: new SplitDeviceSetting { Config = new SplitDeviceConfig("Simulated", "Simulated") });

        var split = Assert.IsType<SplitAnalyzerDevice>(factory.Create(DeviceKind.Split));

        Assert.Equal(750.0, split.Input.AutoRangeStepDownDelayMs);
        Assert.Equal(750.0, split.Output.AutoRangeStepDownDelayMs);
    }
}
