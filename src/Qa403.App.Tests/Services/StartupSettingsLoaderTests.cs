using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qa403.App.Diagnostics;
using Qa403.App.Json;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Services;

/// <summary>
/// The startup load order.
///
/// <para><see cref="AnalyzerKindMemoryTests"/> already pins the <i>reading</i> of
/// <c>device.analyzer</c>, and it passed throughout the bug this file exists for: the document was
/// written correctly and read correctly, just too late to matter. Resolving
/// <see cref="DeviceSessionService"/> builds the first device, and
/// <see cref="AnalyzerDeviceFactory.InitialKind"/> is fixed at that moment — so a settings load
/// sequence that touches the session before it has read the stored kind comes up on the QA403
/// however carefully the kind was saved. Nothing downstream can tell, which is why this is pinned
/// here rather than left to the comments that already said it.</para>
/// </summary>
public sealed class StartupSettingsLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qa403-startup-{Guid.NewGuid():N}");
    private readonly string _configPath;

    public StartupSettingsLoaderTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.db");
        using var connection = SqliteConnectionFactory
            .OpenAtAsync(_configPath, create: true, CancellationToken.None).GetAwaiter().GetResult();
        new MigrationRunner(MigrationRunner.ConfigResourcePrefix)
            .RunAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// A factory shaped exactly like the one <c>Program</c> registers — <see cref="InitialKind"/>
    /// comes from <see cref="AnalyzerKindSetting"/> at construction — but building a simulator
    /// whatever the kind says. The real one would construct a <c>SoundCardDevice</c>, whose
    /// constructor reaches <c>PortAudioNative.Shared</c> and starts a native audio stack; what is
    /// under test is which kind the factory captured, not what it then built.
    /// </summary>
    private sealed class TestFactory(AnalyzerKindSetting kind, SoundCardSetting soundCard)
        : AnalyzerDeviceFactory(NullLoggerFactory.Instance, kind.Kind, soundCard)
    {
        private readonly SoundCardSetting _soundCard = soundCard;

        /// <summary>
        /// What <see cref="SoundCardSetting"/> held at the instant the device was built — which is
        /// the question, since the real factory reads it right there to construct the card. The
        /// setting's value <i>afterwards</i> proves nothing: it is loaded either way, and a test
        /// reading it at the end passes against the very bug this file is about.
        /// </summary>
        public SoundCardConfig? ConfigWhenBuilt { get; private set; }

        public override IAnalyzerDevice Create(DeviceKind _)
        {
            ConfigWhenBuilt = _soundCard.Config;
            return new SimulatedDevice();
        }
    }

    private ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new Qa403ConfigDatabaseOptions { Path = _configPath });
        services.AddSingleton<ConfigConnectionFactory>();
        services.AddSingleton<ConfigRepository>();

        services.AddSingleton<CaptureDelaySetting>();
        services.AddSingleton<ExternalGainSetting>();
        services.AddSingleton<SoundCardSetting>();
        services.AddSingleton<AnalyzerKindSetting>();
        services.AddSingleton<UserWeightingSetting>();
        services.AddSingleton<UserWeightingService>();
        services.AddSingleton<DiagnosticRecorder>();

        // The loader reads the stream recorder's setting into it, before the session resolves --
        // pointed at this test's own folder, since the real one is the developer's data folder.
        services.AddSingleton(sp => new StreamRecorder(
            NullLogger<StreamRecorder>.Instance, Path.Combine(_dir, "recordings")));
        services.AddSingleton<SpectrumBroadcastHub>();
        services.AddSingleton<SpectrumPipeline>();

        // The registration whose laziness the whole arrangement depends on: it reads
        // AnalyzerKindSetting when the factory is first resolved, not when it is registered.
        services.AddSingleton<AnalyzerDeviceFactory>(sp => new TestFactory(
            sp.GetRequiredService<AnalyzerKindSetting>(),
            sp.GetRequiredService<SoundCardSetting>()));
        services.AddSingleton<DeviceSessionService>();

        return services.BuildServiceProvider();
    }

    private async Task StoreKindAsync(ServiceProvider provider, DeviceKind kind)
    {
        await provider.GetRequiredService<ConfigRepository>().SetAsync(
            ConfigRepository.AnalyzerKindKey,
            System.Text.Json.JsonSerializer.Serialize(
                new AnalyzerKindConfig(kind.ToString()), AppJsonContext.Default.AnalyzerKindConfig),
            CancellationToken.None);
    }

    [Theory]
    [InlineData(DeviceKind.SoundCard)]
    [InlineData(DeviceKind.Simulated)]
    public async Task TheRememberedAnalyzer_IsTheOneTheFirstDeviceIsBuiltFor(DeviceKind stored)
    {
        await using ServiceProvider provider = BuildContainer();
        await StoreKindAsync(provider, stored);

        await StartupSettingsLoader.LoadAsync(provider, CancellationToken.None);

        // InitialKind is captured when the factory is constructed, which the loader's last step
        // forces by resolving the session. Reading it afterwards is reading what the app came up on.
        Assert.Equal(stored, provider.GetRequiredService<AnalyzerDeviceFactory>().InitialKind);
        Assert.Equal(stored, provider.GetRequiredService<DeviceSessionService>().Kind);
    }

    [Fact]
    public async Task TheSoundCardsSettings_AreLoadedBeforeTheFirstDeviceIsBuilt()
    {
        // The second half of the same rule: a remembered sound card whose settings arrive after the
        // device was built is a card with no device chosen and no calibration -- which reads on the
        // bench as the card having forgotten everything, not as a load order.
        await using ServiceProvider provider = BuildContainer();
        await StoreKindAsync(provider, DeviceKind.SoundCard);
        await provider.GetRequiredService<ConfigRepository>().SetAsync(
            ConfigRepository.SoundCardKey,
            System.Text.Json.JsonSerializer.Serialize(
                SoundCardConfig.Default with
                {
                    InputHostApi = "ASIO",
                    InputDeviceName = "Test Interface",
                    OutputHostApi = "ASIO",
                    OutputDeviceName = "Test Interface",
                },
                AppJsonContext.Default.SoundCardConfig),
            CancellationToken.None);

        await StartupSettingsLoader.LoadAsync(provider, CancellationToken.None);

        var factory = (TestFactory)provider.GetRequiredService<AnalyzerDeviceFactory>();
        Assert.Equal("Test Interface", factory.ConfigWhenBuilt?.InputDeviceName);
    }

    [Fact]
    public async Task WithNothingStored_TheAppStillComesUpOnTheQa403()
    {
        await using ServiceProvider provider = BuildContainer();

        await StartupSettingsLoader.LoadAsync(provider, CancellationToken.None);

        Assert.Equal(DeviceKind.Hardware, provider.GetRequiredService<AnalyzerDeviceFactory>().InitialKind);
    }

    [Fact]
    public async Task TheOtherSettings_AreStillLoaded()
    {
        // The loader took over a block that did several unrelated things; a reorder that quietly
        // dropped one of them would leave no trace until a sweep ran with the wrong settle time.
        await using ServiceProvider provider = BuildContainer();
        var config = provider.GetRequiredService<ConfigRepository>();
        await config.SetAsync(
            ConfigRepository.CaptureDelayKey,
            System.Text.Json.JsonSerializer.Serialize(
                new CaptureDelayConfig(250.0), AppJsonContext.Default.CaptureDelayConfig),
            CancellationToken.None);
        await config.SetAsync(
            ConfigRepository.ExternalGainKey,
            System.Text.Json.JsonSerializer.Serialize(
                new ExternalGainConfig(-20.0, -10.0), AppJsonContext.Default.ExternalGainConfig),
            CancellationToken.None);

        await StartupSettingsLoader.LoadAsync(provider, CancellationToken.None);

        Assert.Equal(250.0, provider.GetRequiredService<CaptureDelaySetting>().Ms);
        Assert.Equal(-20.0, provider.GetRequiredService<ExternalGainSetting>().LeftDb);
        Assert.Equal(-10.0, provider.GetRequiredService<ExternalGainSetting>().RightDb);
    }
}
