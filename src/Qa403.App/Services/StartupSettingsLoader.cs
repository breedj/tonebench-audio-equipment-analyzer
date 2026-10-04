using Qa403.App.Diagnostics;
using Qa403.App.Endpoints;
using Qa403.Data;

namespace Qa403.App.Services;

/// <summary>
/// Copies the configuration documents the app reads on hot paths into the in-memory settings that
/// hold them, once, at startup.
///
/// <para><b>The order here is the whole point, and it is why this is a function rather than a block
/// in <c>Program</c>.</b> Resolving <see cref="DeviceSessionService"/> builds the first device —
/// its <c>_device</c> and <c>_kind</c> are field initializers calling
/// <c>deviceFactory.Create(deviceFactory.InitialKind)</c> — and
/// <see cref="AnalyzerDeviceFactory.InitialKind"/> is fixed when the factory is constructed, from
/// whatever <see cref="AnalyzerKindSetting.Kind"/> says at that moment. So every load that the
/// first device depends on has to happen <i>before</i> anything touches the session, and the one
/// call that does touch it is deliberately last.</para>
///
/// <para>It was not, and the symptom was exactly what you would expect of a preference that is
/// written correctly and read too late: selecting the sound card, restarting, and finding the
/// QA403 selected again — with <c>device.analyzer</c> holding <c>{"kind":"SoundCard"}</c> in the
/// configuration database the whole time. The seeding of the shared external gain sat two steps
/// above the analyzer-kind load, resolved the session, and pinned <c>InitialKind</c> to the
/// <see cref="DeviceKind.Hardware"/> default before the stored kind had been read. Both of the
/// loads below it carried comments saying they had to precede anything that resolved the session;
/// nothing enforced it, and nothing could fail when it stopped being true.</para>
///
/// <para>The sound card's own settings are in the same position and were equally too late — had
/// the kind been right, <see cref="AnalyzerDeviceFactory.Create"/> would have built a
/// <c>SoundCardDevice</c> from <see cref="SoundCardConfig.Default"/>: no card chosen, no
/// calibration, on a bench where one had been set up. That is why the fix is an order and not a
/// single moved line.</para>
///
/// <para>Guarded by <c>StartupSettingsLoaderTests</c>.</para>
/// </summary>
public static class StartupSettingsLoader
{
    /// <summary>
    /// Loads every startup setting, in the one order that works. <paramref name="services"/> may be
    /// the root provider or a scope's — all of these are singletons.
    /// </summary>
    public static async Task LoadAsync(IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<ConfigRepository>();

        // The capture delay is read on every stimulus change, under the session lock, so it is
        // loaded into memory once here rather than fetched from the database each time -- see
        // CaptureDelaySetting. The endpoint keeps the two in step from then on.
        services.GetRequiredService<CaptureDelaySetting>().Ms =
            (await ConfigEndpoints.ReadCaptureDelayAsync(config, ct)).CaptureDelayMs;

        // The external input gain, for the same reason: the pipeline reads it per block.
        var externalGain = await ConfigEndpoints.ReadExternalGainAsync(config, ct);
        var externalGainSetting = services.GetRequiredService<ExternalGainSetting>();
        externalGainSetting.LeftDb = externalGain.LeftDb;
        externalGainSetting.RightDb = externalGain.RightDb;

        // The sound card's settings and calibration. AnalyzerDeviceFactory reads them to build a
        // device, which happens the moment anything resolves the session -- and a card that has
        // been calibrated must not come up claiming dBFS.
        services.GetRequiredService<SoundCardSetting>().Config =
            await ConfigEndpoints.ReadSoundCardAsync(config, ct);

        // Which analyzer to come up on. --simulate overrides it, at the factory's registration.
        services.GetRequiredService<AnalyzerKindSetting>().Kind =
            await ConfigEndpoints.ReadAnalyzerKindAsync(config, ct);

        // The experimental 384 kHz opt-in. The device reads this through a Func rather than
        // capturing it (Qa40xUsbDevice.Allow384kHz), so unlike the two settings above it would in
        // fact survive being loaded after the first device was built. It is loaded here anyway: the
        // rule about this method is an order, not a case-by-case judgement, and a later change that
        // turned that Func into a captured bool would otherwise re-arm the trap silently -- which is
        // exactly how the analyzer-kind preference came to be written correctly and read too late.
        services.GetRequiredService<Experimental384kSetting>().Enabled =
            (await ConfigEndpoints.ReadExperimental384kAsync(config, ct)).Enabled;

        // The stream recorder's switch and cap. Before the session resolves, because the session
        // takes the recorder as a dependency and arms the device's tap from it -- a setting read
        // afterwards would leave the first acquisition of the session unrecorded.
        var streamRecording = await ConfigEndpoints.ReadStreamRecordingAsync(config, ct);
        var recorder = services.GetRequiredService<StreamRecorder>();
        recorder.Enabled = streamRecording.Enabled;
        recorder.MaxFileMegabytes = streamRecording.MaxFileMegabytes;

        // The user weighting tables, for the same reason the capture delay is here: the pipeline
        // reads them on its worker thread. A damaged document leaves its slot empty and is logged.
        await services.GetRequiredService<UserWeightingService>().LoadAllAsync(ct);

        // LAST, and the comment above says why: this resolves the session, which builds the first
        // device from the two settings loaded above. Nothing that reads a document may come after
        // it. Handing the session the whole external-gain document also seeds its shared-bench copy
        // outright rather than leaving it to the lazy capture in ApplyEffectiveExternalGain, which
        // exists only because this runs after the container has built the service.
        services.GetRequiredService<DeviceSessionService>()
            .SetSharedExternalGain(externalGain.LeftDb, externalGain.RightDb, externalGain.Unit);
    }
}
