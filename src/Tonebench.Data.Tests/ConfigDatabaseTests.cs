using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tonebench.Dsp;

namespace Tonebench.Data.Tests;

/// <summary>
/// The configuration database: its own file, its own migrations, its own version history.
///
/// <para>The separation is the point rather than an implementation detail. The impedance fixture
/// stored here describes one particular QA403's inter-channel mismatch and one particular set of
/// test leads, so it must not travel inside a measurement database that gets copied to another
/// machine — there it would not be stale, it would be wrong, silently correcting every reading by
/// numbers that describe different hardware.</para>
/// </summary>
public class ConfigDatabaseTests
{
    private static async Task<(ConfigConnectionFactory Config, SqliteConnectionFactory Measurement)> CreateBothAsync(
        string configPath, string measurementPath)
    {
        var config = new ConfigConnectionFactory(new TonebenchConfigDatabaseOptions { Path = configPath });
        await using (var connection = await config.OpenAsync(CancellationToken.None))
            await new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None);

        var measurement = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = measurementPath });
        await using (var connection = await measurement.OpenAsync(CancellationToken.None))
            await new MigrationRunner().RunAsync(connection, CancellationToken.None);

        return (config, measurement);
    }

    /// <summary>
    /// Migration 003: an install that had turned detailed logging off comes up on the new default
    /// (on) once, by having the stored row deleted. Pinned because it is the one place this app
    /// resets a setting the user chose, and because a default alone would have reached only fresh
    /// installs -- which is every tester and nobody already at a bench.
    ///
    /// <para>A row written <i>after</i> the migration survives, which is what keeps turning it back
    /// off meaningful: migrations run once per database.</para>
    /// </summary>
    [Fact]
    public async Task Migration003_ClearsAStoredDetailedLoggingChoice_Once()
    {
        string path = TestDb.CreateTempPath();
        var factory = new ConfigConnectionFactory(new TonebenchConfigDatabaseOptions { Path = path });

        // An install as it stood before this build: the table exists and the user had turned the
        // switch off. Migration 001 only is applied by hand here, since running them all is the
        // thing under test.
        await using (var connection = await factory.OpenAsync(CancellationToken.None))
        {
            await using var create = connection.CreateCommand();
            create.CommandText =
                "CREATE TABLE app_setting (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_utc TEXT NOT NULL);"
                    + "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_utc TEXT NOT NULL);"
                    + "INSERT INTO schema_version (version, name, applied_utc) "
                    + "VALUES (1, '001_app_setting.sql', '2026-01-01T00:00:00Z');"
                    + "INSERT INTO app_setting (key, value, updated_utc) "
                    + "VALUES ('logging.diagnostic', '{\"Enabled\":false}', '2026-01-01T00:00:00Z'),"
                    + "       ('acquisition.captureDelay', '{\"CaptureDelayMs\":300}', '2026-01-01T00:00:00Z');";
            await create.ExecuteNonQueryAsync();
        }

        await using (var connection = await factory.OpenAsync(CancellationToken.None))
            await new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None);

        var repo = new ConfigRepository(factory);

        // Gone, so the new default applies; and nothing else was touched.
        Assert.Null(await repo.GetAsync(ConfigRepository.DiagnosticLoggingKey, CancellationToken.None));
        Assert.NotNull(await repo.GetAsync(ConfigRepository.CaptureDelayKey, CancellationToken.None));

        // Turning it off again sticks: the migration has already run for this database.
        await repo.SetAsync(
            ConfigRepository.DiagnosticLoggingKey, "{\"Enabled\":false}", CancellationToken.None);
        await using (var connection = await factory.OpenAsync(CancellationToken.None))
            await new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None);

        Assert.NotNull(await repo.GetAsync(ConfigRepository.DiagnosticLoggingKey, CancellationToken.None));
    }

    /// <summary>
    /// Migration 004: the settle carried forward from 3000 ms to 2000, and <b>only</b> from exactly
    /// 3000. The row cannot be deleted the way 003's is — it also holds the card's identity and the
    /// three full-scale figures the calibration measured — so this pins both halves: the one field
    /// moves, everything beside it in the same document survives, and a bench that chose its own
    /// figure is left alone.
    /// </summary>
    [Theory]
    [InlineData(3000, 2000.0)]
    [InlineData(5000, 5000.0)]
    [InlineData(0, 0.0)]
    public async Task Migration004_CarriesTheSoundCardSettleForward_OnlyFromTheOldDefault(
        int storedMs, double expectedMs)
    {
        string path = TestDb.CreateTempPath();
        var factory = new ConfigConnectionFactory(new TonebenchConfigDatabaseOptions { Path = path });

        string stored =
            $"{{\"inputHostApi\":\"ASIO\",\"inputDeviceName\":\"MOTU M Series\",\"outputHostApi\":\"ASIO\","
                + $"\"outputDeviceName\":\"MOTU M Series\",\"inputChannelOffset\":2,\"outputChannelOffset\":2,"
                + $"\"streamStartSettleMs\":{storedMs},\"outputFullScalePeakMv\":3370.552932940348,"
                + $"\"inputFullScalePeakMvLeft\":9437.70457685045,\"inputFullScalePeakMvRight\":9432.590945255923,"
                + $"\"frontEndGainLeftDb\":-20}}";

        await using (var connection = await factory.OpenAsync(CancellationToken.None))
        {
            await using var create = connection.CreateCommand();
            create.CommandText =
                "CREATE TABLE app_setting (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_utc TEXT NOT NULL);"
                    + "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_utc TEXT NOT NULL);"
                    + "INSERT INTO schema_version (version, name, applied_utc) "
                    + "VALUES (1, '001_app_setting.sql', '2026-01-01T00:00:00Z');"
                    + "INSERT INTO app_setting (key, value, updated_utc) VALUES ('soundcard.settings', $v, '2026-01-01T00:00:00Z');";
            create.Parameters.AddWithValue("$v", stored);
            await create.ExecuteNonQueryAsync();
        }

        await using (var connection = await factory.OpenAsync(CancellationToken.None))
            await new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None);

        string? after = await new ConfigRepository(factory).GetAsync("soundcard.settings", CancellationToken.None);
        Assert.NotNull(after);

        SoundCardConfig? config = System.Text.Json.JsonSerializer.Deserialize<SoundCardConfig>(
            after, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(config);
        Assert.Equal(expectedMs, config.StreamStartSettleMs);

        // The calibration, which is the whole reason this is a json_set and not a DELETE.
        Assert.Equal("MOTU M Series", config.InputDeviceName);
        Assert.Equal(2, config.InputChannelOffset);
        Assert.Equal(3370.552932940348, config.OutputFullScalePeakMv);
        Assert.Equal(9437.70457685045, config.InputFullScalePeakMvLeft);
        Assert.Equal(9432.590945255923, config.InputFullScalePeakMvRight);
        Assert.Equal(-20.0, config.FrontEndGainLeftDb);
    }

    /// <summary>
    /// The load-bearing property: configuration written here leaves no trace in the measurement
    /// database. If <c>app_setting</c> ever appeared over there, a copied measurement file would
    /// carry this machine's calibration with it.
    /// </summary>
    [Fact]
    public async Task ConfigurationIsWrittenToItsOwnFile_AndTheMeasurementDatabaseNeverSeesIt()
    {
        string configPath = TestDb.CreateTempPath();
        string measurementPath = TestDb.CreateTempPath();
        try
        {
            var (config, measurement) = await CreateBothAsync(configPath, measurementPath);

            var repo = new ConfigRepository(config);
            await repo.SetAsync("impedance.fixture", """{"referenceOhms":100}""", CancellationToken.None);

            Assert.Equal("""{"referenceOhms":100}""", await repo.GetAsync("impedance.fixture", CancellationToken.None));

            await using var other = await measurement.OpenAsync(CancellationToken.None);
            await using var command = other.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_setting';";
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None)));
        }
        finally
        {
            TestDb.Cleanup(configPath);
            TestDb.Cleanup(measurementPath);
        }
    }

    /// <summary>
    /// And the reverse: the measurement schema does not leak into the configuration file. Both
    /// runners create a <c>schema_version</c> table, so a shared file would have them fighting over
    /// one version number and each skipping the other's migrations.
    /// </summary>
    [Fact]
    public async Task TheTwoDatabasesKeepIndependentSchemaVersions()
    {
        string configPath = TestDb.CreateTempPath();
        string measurementPath = TestDb.CreateTempPath();
        try
        {
            var (config, measurement) = await CreateBothAsync(configPath, measurementPath);

            await using var configConnection = await config.OpenAsync(CancellationToken.None);
            await using var measurementConnection = await measurement.OpenAsync(CancellationToken.None);

            Assert.Equal(0L, await CountTableAsync(configConnection, "measurement_result"));
            Assert.Equal(1L, await CountTableAsync(configConnection, "app_setting"));
            Assert.Equal(1L, await CountTableAsync(measurementConnection, "measurement_result"));

            // The config set has one migration; the measurement set has many. A shared file would
            // make the second runner think its own migrations had already been applied.
            Assert.True(await MaxVersionAsync(measurementConnection) > await MaxVersionAsync(configConnection));
        }
        finally
        {
            TestDb.Cleanup(configPath);
            TestDb.Cleanup(measurementPath);
        }
    }

    /// <summary>
    /// The config set, pinned the way <c>MigrationRunnerTests</c> pins the measurement set: adding a
    /// file here is a deliberate act, and 002 is the loopback calibration table that moved out of
    /// the project file.
    /// </summary>
    [Fact]
    public async Task ConfigMigrations_RecordEachVersionInSchemaVersion()
    {
        string configPath = TestDb.CreateTempPath();
        try
        {
            var config = await TestDb.CreateMigratedConfigFactoryAsync(configPath);
            await using var connection = await config.OpenAsync(CancellationToken.None);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT version, name FROM schema_version ORDER BY version;";
            await using var reader = await command.ExecuteReaderAsync();

            var rows = new List<(long Version, string Name)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetInt64(0), reader.GetString(1)));

            Assert.Equal(
                [(1L, "001_app_setting.sql"), (2L, "002_impulse_reference.sql"),
                 (3L, "003_detailed_logging_default.sql"), (4L, "004_soundcard_settle_default.sql")],
                rows);
        }
        finally
        {
            TestDb.Cleanup(configPath);
        }
    }

    [Fact]
    public async Task RunningTheConfigMigrationsTwice_IsANoOp()
    {
        string configPath = TestDb.CreateTempPath();
        try
        {
            var config = new ConfigConnectionFactory(new TonebenchConfigDatabaseOptions { Path = configPath });
            for (int pass = 0; pass < 2; pass++)
            {
                await using var connection = await config.OpenAsync(CancellationToken.None);
                await new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None);
            }

            var repo = new ConfigRepository(config);
            await repo.SetAsync("k", "v", CancellationToken.None);
            Assert.Equal("v", await repo.GetAsync("k", CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(configPath);
        }
    }

    /// <summary>A second write to one key replaces it rather than failing on the primary key.</summary>
    [Fact]
    public async Task SetAsync_OverwritesAnExistingDocument()
    {
        string configPath = TestDb.CreateTempPath();
        try
        {
            var (config, _) = await CreateBothAsync(configPath, TestDb.CreateTempPath());
            var repo = new ConfigRepository(config);

            await repo.SetAsync("k", "first", CancellationToken.None);
            await repo.SetAsync("k", "second", CancellationToken.None);

            Assert.Equal("second", await repo.GetAsync("k", CancellationToken.None));

            await repo.DeleteAsync("k", CancellationToken.None);
            Assert.Null(await repo.GetAsync("k", CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(configPath);
        }
    }

    /// <summary>
    /// The fixture round-trips as one document. Written and read whole because its parts are only
    /// meaningful together — a calibration belongs to the resistor it was captured through.
    /// </summary>
    [Fact]
    public async Task TheImpedanceFixtureRoundTripsAsOneDocument()
    {
        string configPath = TestDb.CreateTempPath();
        try
        {
            var (config, _) = await CreateBothAsync(configPath, TestDb.CreateTempPath());
            var repo = new ConfigRepository(config);

            var fixture = new ImpedanceFixtureConfig(
                ReferenceOhms: 100.7,
                CalibrationLevelValue: 0.0,
                LevelUnit: "Dbv",
                OpenCal: new ImpedanceCalibration([20.0, 20000.0], [1.0, 0.99], [0.0, 0.01]),
                ShortCal: new ImpedanceCalibration([20.0, 20000.0], [0.001, 0.002], [0.0, 0.003]),
                CapturedLowHz: 12.5,
                CapturedHighHz: 91200.0,
                CapturedUtc: "2026-09-09T17:00:00.0000000+00:00");

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await repo.SetAsync(ConfigRepository.ImpedanceFixtureKey,
                JsonSerializer.Serialize(fixture, options), CancellationToken.None);

            string stored = (await repo.GetAsync(ConfigRepository.ImpedanceFixtureKey, CancellationToken.None))!;
            var back = JsonSerializer.Deserialize<ImpedanceFixtureConfig>(stored, options)!;

            Assert.Equal(100.7, back.ReferenceOhms);
            Assert.Equal(91200.0, back.CapturedHighHz);
            Assert.True(back.HasCalibration);
            Assert.Equal(2, back.OpenCal!.FreqHz.Length);
            Assert.Equal(0.002, back.ShortCal!.Real[1]);
        }
        finally
        {
            TestDb.Cleanup(configPath);
        }
    }

    /// <summary>An open alone is a usable fixture; a short alone is not, since the short is corrected against the open.</summary>
    [Fact]
    public void HasCalibration_NeedsTheOpen()
    {
        var open = new ImpedanceCalibration([20.0, 20000.0], [1.0, 0.99], [0.0, 0.01]);

        Assert.False(ImpedanceFixtureConfig.Default.HasCalibration);
        Assert.False((ImpedanceFixtureConfig.Default with { ShortCal = open }).HasCalibration);
        Assert.True((ImpedanceFixtureConfig.Default with { OpenCal = open }).HasCalibration);
    }

    private static async Task<long> CountTableAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<long> MaxVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }
}
