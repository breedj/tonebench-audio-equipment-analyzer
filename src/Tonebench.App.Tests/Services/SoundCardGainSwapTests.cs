using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.Simulation;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// Which front-end gain the pipeline divides by, when the analyzer changes.
///
/// <para>There is exactly one place that undoes an input gain — <c>SpectrumPipeline.Enqueue</c>, via
/// <see cref="ExternalGainSetting"/> — with the three ranging seams and the <c>params_json</c>
/// recording already worked out against it. A sound card's front-end is remembered separately from
/// the QA403's, because they are different benches, so the choice is which document feeds that one
/// setting rather than a second gain concept anywhere downstream.</para>
/// </summary>
public class SoundCardGainSwapTests
{
    /// <summary>A factory that builds a simulator whatever the kind — what is under test is the session's bookkeeping, not any device.</summary>
    private sealed class AnyKindFactory(DeviceKind initialKind, SoundCardSetting soundCard)
        : AnalyzerDeviceFactory(NullLoggerFactory.Instance, initialKind, soundCard)
    {
        public override IAnalyzerDevice Create(DeviceKind kind) => new SimulatedDevice();
    }

    private static (DeviceSessionService Session, ExternalGainSetting Gain, SoundCardSetting Card) Build(
        DeviceKind initialKind, double cardLeftDb, double cardRightDb, string cardUnit = ExternalGainConfig.DefaultUnit)
    {
        var card = new SoundCardSetting
        {
            Config = SoundCardConfig.Default with
            {
                InputHostApi = "ASIO",
                InputDeviceName = "Card",
                OutputHostApi = "ASIO",
                OutputDeviceName = "Card",
                FrontEndGainLeftDb = cardLeftDb,
                FrontEndGainRightDb = cardRightDb,
                GainDisplayUnit = cardUnit,
            },
        };

        var gain = new ExternalGainSetting();
        var session = new DeviceSessionService(
            new AnyKindFactory(initialKind, card),
            new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance),
            new CaptureDelaySetting(),
            NullLogger<DeviceSessionService>.Instance,
            gain,
            card);

        return (session, gain, card);
    }

    /// <summary>
    /// The whole point: each analyzer keeps its own bench wiring, and switching between them swaps
    /// which one the pipeline applies.
    /// </summary>
    [Fact]
    public async Task SwitchingToTheSoundCardAndBack_SwapsWhichFrontEndIsApplied()
    {
        var (session, gain, _) = Build(DeviceKind.Hardware, cardLeftDb: -20.0, cardRightDb: -6.0);

        // The QA403's own divider, as the external-gain document would have set it at startup.
        session.SetSharedExternalGain(-10.0, -10.0);
        Assert.Equal(-10.0, gain.LeftDb);

        await session.SwitchDeviceAsync(DeviceKind.SoundCard, CancellationToken.None);
        Assert.Equal(-20.0, gain.LeftDb);
        Assert.Equal(-6.0, gain.RightDb);

        await session.SwitchDeviceAsync(DeviceKind.Hardware, CancellationToken.None);
        Assert.Equal(-10.0, gain.LeftDb);
        Assert.Equal(-10.0, gain.RightDb);
    }

    /// <summary>
    /// The ordering bug this nearly had: the shared value is captured the first time the session
    /// leaves a non-sound-card analyzer, not in a field initializer. <c>Program</c> loads the
    /// external-gain document into the setting <i>after</i> the container builds the session, so
    /// seeding it at construction would bank a zero and lose the QA403's divider on the way back.
    /// </summary>
    [Fact]
    public async Task AGainLoadedAfterTheSessionWasBuilt_SurvivesARoundTrip()
    {
        var (session, gain, _) = Build(DeviceKind.Hardware, cardLeftDb: -20.0, cardRightDb: -20.0);

        // Exactly what Program does at startup, straight into the setting.
        gain.LeftDb = -14.0;
        gain.RightDb = -14.0;

        await session.SwitchDeviceAsync(DeviceKind.SoundCard, CancellationToken.None);
        Assert.Equal(-20.0, gain.LeftDb);

        await session.SwitchDeviceAsync(DeviceKind.Hardware, CancellationToken.None);
        Assert.Equal(-14.0, gain.LeftDb);
    }

    /// <summary>
    /// Editing the external gain while the sound card is selected must not swap the card's divider
    /// for the QA403's — the document describes a different bench.
    /// </summary>
    [Fact]
    public async Task SettingTheSharedGain_WhileTheSoundCardIsSelected_LeavesTheCardsOwnInPlace()
    {
        var (session, gain, _) = Build(DeviceKind.SoundCard, cardLeftDb: -20.0, cardRightDb: -20.0);
        await session.SwitchDeviceAsync(DeviceKind.Hardware, CancellationToken.None);
        await session.SwitchDeviceAsync(DeviceKind.SoundCard, CancellationToken.None);

        session.SetSharedExternalGain(-3.0, -3.0);

        Assert.Equal(-20.0, gain.LeftDb);

        // ...and it is what comes back on the QA403.
        await session.SwitchDeviceAsync(DeviceKind.Hardware, CancellationToken.None);
        Assert.Equal(-3.0, gain.LeftDb);
    }

    /// <summary>A card's gain edited in its own dialog reaches the pipeline without a reconnect.</summary>
    [Fact]
    public async Task RefreshSoundCardGain_PicksUpAnEditedFrontEnd()
    {
        var (session, gain, card) = Build(DeviceKind.Hardware, cardLeftDb: 0.0, cardRightDb: 0.0);
        await session.SwitchDeviceAsync(DeviceKind.SoundCard, CancellationToken.None);
        Assert.Equal(0.0, gain.LeftDb);

        card.Config = card.Config with { FrontEndGainLeftDb = -26.0 };
        session.RefreshSoundCardGain();

        Assert.Equal(-26.0, gain.LeftDb);
    }

    /// <summary>A card's gain edit must do nothing while a different analyzer is in use.</summary>
    [Fact]
    public void RefreshSoundCardGain_OnAnotherAnalyzer_ChangesNothing()
    {
        var (session, gain, card) = Build(DeviceKind.Hardware, cardLeftDb: 0.0, cardRightDb: 0.0);
        session.SetSharedExternalGain(-8.0, -8.0);

        card.Config = card.Config with { FrontEndGainLeftDb = -26.0 };
        session.RefreshSoundCardGain();

        Assert.Equal(-8.0, gain.LeftDb);
    }

    /// <summary>
    /// The status reports the card's own fields only while it is the analyzer in use, the same null
    /// convention the Simulated* fields use — so the UI gates on the field rather than on a
    /// device-kind string.
    /// </summary>
    [Fact]
    public async Task Status_ReportsTheCardsFields_OnlyWhileItIsTheAnalyzerInUse()
    {
        var (session, _, _) = Build(DeviceKind.Hardware, cardLeftDb: 0.0, cardRightDb: 0.0);
        await session.ConnectAsync(CancellationToken.None);

        Assert.Null(session.GetStatus().SoundCardInputDevice);
        Assert.Null(session.GetStatus().SoundCardCalibrated);

        await session.SwitchDeviceAsync(DeviceKind.SoundCard, CancellationToken.None);

        Assert.Equal("Card", session.GetStatus().SoundCardInputDevice);
        Assert.Equal("SoundCard", session.GetStatus().DeviceKind);

        // Nothing measured yet, so it says so rather than claiming volts.
        Assert.False(session.GetStatus().SoundCardCalibrated);
    }

    /// <summary>
    /// The unit is stored beside the gain it describes, so it has to swap with it. A unit left
    /// behind on a switch is worse than a wrong gain, because it is not wrong by a visible amount:
    /// a card's ×0.1 read against the QA403's bench prints "0.1" where the stored figure is
    /// −20 dB, which is a plausible-looking small gain rather than an obvious fault.
    /// </summary>
    [Fact]
    public async Task SwitchingAnalyzers_SwapsTheUnitWithTheGain()
    {
        var (session, _, _) = Build(
            DeviceKind.Hardware, cardLeftDb: -20.0, cardRightDb: -20.0, cardUnit: ExternalGainConfig.TimesUnit);

        session.SetSharedExternalGain(-10.0, -10.0, ExternalGainConfig.DefaultUnit);
        Assert.Equal(ExternalGainConfig.DefaultUnit, session.ExternalGainUnit);
        Assert.Equal(ExternalGainConfig.DefaultUnit, session.GetStatus().ExternalGainUnit);

        await session.SwitchDeviceAsync(DeviceKind.SoundCard, CancellationToken.None);
        Assert.Equal(ExternalGainConfig.TimesUnit, session.ExternalGainUnit);
        Assert.Equal(ExternalGainConfig.TimesUnit, session.GetStatus().ExternalGainUnit);

        await session.SwitchDeviceAsync(DeviceKind.Hardware, CancellationToken.None);
        Assert.Equal(ExternalGainConfig.DefaultUnit, session.ExternalGainUnit);
    }

    /// <summary>
    /// And it is never null, on either branch of <c>GetStatus</c> — the disconnected one is the
    /// easy one to forget, and the client reads the field without a fallback.
    /// </summary>
    [Fact]
    public void TheUnit_IsReportedEvenBeforeAnythingIsConnected()
    {
        var (session, _, _) = Build(DeviceKind.Hardware, cardLeftDb: 0.0, cardRightDb: 0.0);

        Assert.False(session.GetStatus().Connected);
        Assert.Equal(ExternalGainConfig.DefaultUnit, session.GetStatus().ExternalGainUnit);
    }

    /// <summary>
    /// A hand-edited document naming a unit this app does not know must not reach the status bar,
    /// where it would select neither dropdown option.
    /// </summary>
    [Fact]
    public async Task AnUnknownUnitOnTheCard_ReadsAsDb()
    {
        var (session, _, _) = Build(
            DeviceKind.SoundCard, cardLeftDb: 0.0, cardRightDb: 0.0, cardUnit: "ratio");

        await session.ConnectAsync(CancellationToken.None);

        Assert.Equal(ExternalGainConfig.DefaultUnit, session.ExternalGainUnit);
    }
}
