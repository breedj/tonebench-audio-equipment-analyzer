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
/// is here is the App layer's half: the stored document, the three refusals, and the one rule that
/// is invisible from the outside — a sound-card half must be built for <i>one direction</i>.</para>
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
    public void ASoundCardOnBothSides_IsRefused_AndNamesTheAlternative()
    {
        // Everything PortAudio does runs on one thread, which SoundCardDevice occupies for a
        // stream's whole life, so the second card's pump would never be scheduled: no exception, no
        // log line, just an acquisition that never produces a block. That bench already has a home.
        Assert.Null(SplitPairing.Resolve(new SplitDeviceConfig("SoundCard", "SoundCard"), out string? problem));

        Assert.NotNull(problem);
        Assert.Contains("one thread", problem);
        Assert.Contains("Sound card", problem);
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
            Config = new SplitDeviceConfig("SoundCard", "SoundCard", 96_000.0),
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

        protected override IAnalyzerDevice CreateSoundCard(SoundCardSettings settings)
        {
            CardsBuilt.Add(settings);
            return new SimulatedDevice();
        }
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
