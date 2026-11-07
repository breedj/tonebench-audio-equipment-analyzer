using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonebench.App.Json;
using Tonebench.App.Services;
using Tonebench.Data;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// One project per file. Everything here runs on temp files under a throwaway directory, with a
/// config database of its own for the recent list, and the "busy" signal faked — the session asks
/// <see cref="IMeasurementRun"/> and nothing else about the device.
/// </summary>
public class ProjectSessionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qa403-session-{Guid.NewGuid():N}");
    private readonly string _configPath;
    private readonly CurrentProjectFile _current = new();
    private readonly DatabaseHealth _health = new();
    private readonly FakeRun _run = new();
    private readonly ConfigRepository _config;
    private readonly ProjectSessionService _session;
    private readonly SqliteConnectionFactory _factory;

    private sealed class FakeRun : IMeasurementRun
    {
        public bool Running { get; set; }

        public Task CancelAsync()
        {
            Running = false;
            return Task.CompletedTask;
        }
    }

    public ProjectSessionServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.db");
        _config = new ConfigRepository(new ConfigConnectionFactory(new TonebenchConfigDatabaseOptions { Path = _configPath }));

        var jsonOptions = new JsonOptions();
        jsonOptions.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);

        _session = new ProjectSessionService(
            _current, _config, _health, [_run], Options.Create(jsonOptions),
            NullLogger<ProjectSessionService>.Instance);
        _factory = new SqliteConnectionFactory(_current);

        using var connection = SqliteConnectionFactory.OpenAtAsync(_configPath, create: true, CancellationToken.None).GetAwaiter().GetResult();
        new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string In(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task Create_MakesAMigratedFileWithOneProjectRow_AndOpensIt()
    {
        var info = await _session.CreateAsync(In("amp"), "GM70 amp", "the big one", CancellationToken.None);

        Assert.Equal(In("amp.tbench"), info.Path);
        Assert.Equal("GM70 amp", info.Name);
        Assert.Equal(In("amp.tbench"), _current.Path);
        Assert.True(File.Exists(In("amp.tbench")));

        // The repositories see the new file through the holder.
        var projects = new ProjectRepository(_factory);
        long typeId = await projects.CreateTypeAsync(info.Id, "FftSpectrum", "noise", null, "{}", CancellationToken.None);
        Assert.Single(await projects.ListTypesAsync(info.Id, CancellationToken.None));

        await using var connection = await _factory.OpenAsync(CancellationToken.None);
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM project;"));
        Assert.True(await connection.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_version;") >= 11);

        var recent = await _session.ReadRecentAsync(CancellationToken.None);
        Assert.Equal([In("amp.tbench")], recent.Entries.Select(e => e.Path));
        Assert.Equal(In("amp.tbench"), (await _session.ReadLastOpenedAsync(CancellationToken.None)).Path);
    }

    [Fact]
    public async Task Open_RefusesAMissingFile_AndCreatesNothing()
    {
        var ex = await Assert.ThrowsAsync<ProjectFileException>(() => _session.OpenAsync(In("nope.tbench"), CancellationToken.None));
        Assert.Contains("does not exist", ex.Message);
        Assert.False(File.Exists(In("nope.tbench")));
        Assert.Null(_current.Path);
    }

    [Fact]
    public async Task Open_RefusesARelativePath()
    {
        await Assert.ThrowsAsync<ProjectFileException>(() => _session.OpenAsync("amp.tbench", CancellationToken.None));
    }

    [Fact]
    public async Task Open_RefusesAFileThatIsNotSqlite_WithoutTouchingIt()
    {
        await File.WriteAllTextAsync(In("notes.tbench"), "just some text");
        var before = await File.ReadAllBytesAsync(In("notes.tbench"));

        var ex = await Assert.ThrowsAsync<ProjectFileException>(() => _session.OpenAsync(In("notes.tbench"), CancellationToken.None));
        Assert.Contains("not an SQLite database", ex.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(In("notes.tbench")));
        Assert.Null(_current.Path);
    }

    /// <summary>
    /// A stranger's SQLite file has no schema_version table; migrating it would silently turn it
    /// into an empty project, so it is refused before the migration runner sees it.
    /// </summary>
    [Fact]
    public async Task Open_RefusesAnUnrelatedSqliteDatabase_WithoutMigratingIt()
    {
        await using (var connection = await SqliteConnectionFactory.OpenAtAsync(In("other.tbench"), create: true, CancellationToken.None))
            await connection.ExecuteAsync("CREATE TABLE stuff (id INTEGER PRIMARY KEY);");
        SqliteConnection.ClearAllPools();

        var ex = await Assert.ThrowsAsync<ProjectFileException>(() => _session.OpenAsync(In("other.tbench"), CancellationToken.None));
        Assert.Contains("not a Tonebench project file", ex.Message);

        await using var check = await SqliteConnectionFactory.OpenAtAsync(In("other.tbench"), create: false, CancellationToken.None);
        Assert.Equal(0L, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'schema_version';"));
    }

    /// <summary>The legacy qa403.db: every project in one file. Refused by name, so the user knows what they opened.</summary>
    [Fact]
    public async Task Open_RefusesAFileHoldingSeveralProjects_AndNamesThem()
    {
        await using (var connection = await SqliteConnectionFactory.OpenAtAsync(In("legacy.db"), create: true, CancellationToken.None))
        {
            await new MigrationRunner().RunAsync(connection, CancellationToken.None);
            await ProjectRepository.CreateProjectAsync(connection, "GM70", null);
            await ProjectRepository.CreateProjectAsync(connection, "EL84", null);
        }
        SqliteConnection.ClearAllPools();

        var ex = await Assert.ThrowsAsync<ProjectFileException>(() => _session.OpenAsync(In("legacy.db"), CancellationToken.None));
        Assert.Contains("holds 2 projects", ex.Message);
        Assert.Contains("'GM70'", ex.Message);
        Assert.Null(_current.Path);
    }

    [Fact]
    public async Task Open_RefusesAFileHoldingNoProject()
    {
        await using (var connection = await SqliteConnectionFactory.OpenAtAsync(In("empty.tbench"), create: true, CancellationToken.None))
            await new MigrationRunner().RunAsync(connection, CancellationToken.None);
        SqliteConnection.ClearAllPools();

        var ex = await Assert.ThrowsAsync<ProjectFileException>(() => _session.OpenAsync(In("empty.tbench"), CancellationToken.None));
        Assert.Contains("holds no project", ex.Message);
    }

    /// <summary>
    /// The 11 Sept 2026 damage — the last pages zeroed — opens, with the problem on the status, the
    /// way the old startup check handled it: every undamaged row is still readable.
    /// </summary>
    [Fact]
    public async Task Open_OnADamagedFile_OpensAndReportsTheProblem_AndCloseClearsIt()
    {
        var info = await _session.CreateAsync(In("hurt"), "hurt", null, CancellationToken.None);
        await using (var connection = await _factory.OpenAsync(CancellationToken.None))
            await connection.ExecuteAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, blob BLOB); INSERT INTO t (blob) VALUES (randomblob(200000));");
        await _session.CloseAsync(CancellationToken.None);
        DatabaseLifecycleHostedServiceTests.ZeroLastPages(info.Path);

        await _session.OpenAsync(info.Path, CancellationToken.None);
        Assert.NotNull(_health.ProjectProblem);
        Assert.Contains("project file is damaged", _health.Problem);

        await _session.CloseAsync(CancellationToken.None);
        Assert.Null(_health.Problem);
    }

    [Fact]
    public async Task Close_ClearsTheHolderFirst_AndLeavesTheFileDeletable()
    {
        var info = await _session.CreateAsync(In("c"), "c", null, CancellationToken.None);
        await new ProjectRepository(_factory).CreateTypeAsync(info.Id, "FftSpectrum", "t", null, "{}", CancellationToken.None);

        await _session.CloseAsync(CancellationToken.None);

        Assert.Null(_current.Path);
        Assert.Null(_session.Current);
        await Assert.ThrowsAsync<NoProjectOpenException>(() => _factory.OpenAsync(CancellationToken.None));
        Assert.True(!File.Exists(info.Path + "-wal") || new FileInfo(info.Path + "-wal").Length == 0, "the WAL should be empty after close");
        File.Delete(info.Path);

        // Closing keeps the entry: the file was closed, not forgotten.
        Assert.Single((await _session.ReadRecentAsync(CancellationToken.None)).Entries);
        Assert.Equal(info.Path, (await _session.ReadLastOpenedAsync(CancellationToken.None)).Path);
    }

    [Fact]
    public async Task Delete_RemovesTheFileItsWalAndShm_AndForgetsIt()
    {
        var info = await _session.CreateAsync(In("d"), "d", null, CancellationToken.None);
        await new ProjectRepository(_factory).CreateTypeAsync(info.Id, "FftSpectrum", "t", null, "{}", CancellationToken.None);
        Assert.True(File.Exists(info.Path + "-wal"));

        await _session.DeleteAsync(CancellationToken.None);

        Assert.False(File.Exists(info.Path));
        Assert.False(File.Exists(info.Path + "-wal"));
        Assert.False(File.Exists(info.Path + "-shm"));
        Assert.Null(_session.Current);
        Assert.Empty((await _session.ReadRecentAsync(CancellationToken.None)).Entries);
        Assert.Null((await _session.ReadLastOpenedAsync(CancellationToken.None)).Path);
    }

    [Fact]
    public async Task SaveAs_WithResults_CopiesEveryRow_AndSwitchesToTheCopy()
    {
        var source = await _session.CreateAsync(In("src"), "src", "notes", CancellationToken.None);
        var projects = new ProjectRepository(_factory);
        long typeId = await projects.CreateTypeAsync(source.Id, "FftSpectrum", "noise", null, "{}", CancellationToken.None);
        await using (var connection = await _factory.OpenAsync(CancellationToken.None))
        {
            await connection.ExecuteAsync(
                "INSERT INTO measurement_result (measurement_type_id, name, notes, ts, params_json, device_name, serial, fw_ver, is_simulated) "
                    + "VALUES (@typeId, 'r1', NULL, '2026-09-19T10:00:00Z', '{}', 'QA403', '1', '1', 1);",
                new { typeId });
        }

        var copy = await _session.SaveAsAsync(In("copy"), "copy", "copied", includeResults: true, CancellationToken.None);

        Assert.Equal(In("copy.tbench"), copy.Path);
        Assert.Equal("copy", copy.Name);
        Assert.Equal(In("copy.tbench"), _current.Path);
        Assert.Single(await projects.ListTypesAsync(copy.Id, CancellationToken.None));
        await using (var connection = await _factory.OpenAsync(CancellationToken.None))
        {
            Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM measurement_result;"));
            Assert.Equal("copied", await connection.ExecuteScalarAsync<string>("SELECT notes FROM project;"));
            Assert.Equal("wal", await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;"), StringComparer.OrdinalIgnoreCase);
        }

        // The source is untouched, and both are in the recent list, the copy first.
        SqliteConnection.ClearAllPools();
        await using (var connection = await SqliteConnectionFactory.OpenAtAsync(source.Path, create: false, CancellationToken.None))
            Assert.Equal("src", await connection.ExecuteScalarAsync<string>("SELECT name FROM project;"));
        Assert.Equal([In("copy.tbench"), In("src.tbench")], (await _session.ReadRecentAsync(CancellationToken.None)).Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task SaveAs_WithoutResults_CopiesTheTypesInOrder_AndNoResults()
    {
        var source = await _session.CreateAsync(In("src"), "src", null, CancellationToken.None);
        var projects = new ProjectRepository(_factory);
        long a = await projects.CreateTypeAsync(source.Id, "FftSpectrum", "a", null, "{}", CancellationToken.None);
        long b = await projects.CreateTypeAsync(source.Id, "ThdVsFrequency", "b", "nb", "{\"x\":1}", CancellationToken.None);
        await projects.ReorderTypesAsync(source.Id, [b, a], CancellationToken.None);
        await using (var connection = await _factory.OpenAsync(CancellationToken.None))
        {
            await connection.ExecuteAsync(
                "INSERT INTO measurement_result (measurement_type_id, name, notes, ts, params_json, device_name, serial, fw_ver, is_simulated) "
                    + "VALUES (@a, 'r1', NULL, '2026-09-19T10:00:00Z', '{}', 'QA403', '1', '1', 1);",
                new { a });
        }

        var copy = await _session.SaveAsAsync(In("tmpl"), "template", null, includeResults: false, CancellationToken.None);

        var types = await projects.ListTypesAsync(copy.Id, CancellationToken.None);
        Assert.Equal(["b", "a"], types.Select(t => t.Name));
        Assert.Equal("{\"x\":1}", types[0].ParamsJson);
        await using (var connection = await _factory.OpenAsync(CancellationToken.None))
            Assert.Equal(0L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM measurement_result;"));
    }

    [Fact]
    public async Task SaveAs_RefusesAnExistingTarget_AndTheOpenFileItself()
    {
        var source = await _session.CreateAsync(In("src"), "src", null, CancellationToken.None);
        await File.WriteAllTextAsync(In("taken.tbench"), "x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.SaveAsAsync(In("taken"), "t", null, true, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.SaveAsAsync(source.Path, "t", null, true, CancellationToken.None));
        Assert.Equal(source.Path, _current.Path);
    }

    [Fact]
    public async Task Create_RefusesAnExistingFile()
    {
        await _session.CreateAsync(In("one"), "one", null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.CreateAsync(In("one"), "again", null, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateProperties_RenamesTheProject_InTheFileAndTheRecentList()
    {
        var info = await _session.CreateAsync(In("p"), "old", null, CancellationToken.None);

        var updated = await _session.UpdatePropertiesAsync("new name", "desc", CancellationToken.None);

        Assert.Equal("new name", updated.Name);
        Assert.Equal("new name", _session.Current!.Name);
        await using (var connection = await _factory.OpenAsync(CancellationToken.None))
            Assert.Equal("new name", await connection.ExecuteScalarAsync<string>("SELECT name FROM project WHERE id = @id;", new { id = info.Id }));
        Assert.Equal("new name", (await _session.ReadRecentAsync(CancellationToken.None)).Entries.Single().Name);
    }

    /// <summary>
    /// A sweep between points is not acquiring, which is why the check is IMeasurementRun and not
    /// the device: every operation that could change the file is refused while anything runs.
    /// </summary>
    [Fact]
    public async Task EveryOperation_WhileAMeasurementRuns_IsRefused()
    {
        var info = await _session.CreateAsync(In("busy"), "busy", null, CancellationToken.None);
        _run.Running = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.OpenAsync(info.Path, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.CreateAsync(In("x"), "x", null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.CloseAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.DeleteAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.SaveAsAsync(In("y"), "y", null, true, CancellationToken.None));

        Assert.Equal(info.Path, _current.Path);
        Assert.True(File.Exists(info.Path));
        Assert.False(File.Exists(In("x.tbench")));
    }

    [Fact]
    public async Task Open_ReplacesTheOpenProject_AndKeepsTheRecentListAtTen()
    {
        for (int i = 0; i < 12; i++)
            await _session.CreateAsync(In($"p{i:00}"), $"p{i:00}", null, CancellationToken.None);

        var recent = await _session.ReadRecentAsync(CancellationToken.None);
        Assert.Equal(10, recent.Entries.Count);
        Assert.Equal(In("p11.tbench"), recent.Entries[0].Path);

        await _session.OpenAsync(In("p05.tbench"), CancellationToken.None);
        Assert.Equal("p05", _session.Current!.Name);
        Assert.Equal(In("p05.tbench"), (await _session.ReadRecentAsync(CancellationToken.None)).Entries[0].Path);
    }

    /// <summary>
    /// The retention purge runs on open, which is the one path every project file arrives through —
    /// startup included, since ProjectStartupHostedService reopens the last file through it. No
    /// hosted service can do it: none of them has a project file open.
    /// </summary>
    [Fact]
    public async Task Open_PurgesWhatHasBeenInTheTrashLongerThanTheRetention()
    {
        var info = await _session.CreateAsync(In("amp"), "GM70 amp", null, CancellationToken.None);
        var projects = new ProjectRepository(_factory);
        long expired = await projects.CreateTypeAsync(info.Id, "FFTSpectrum", "Long gone", null, "{}", CancellationToken.None);
        long recent = await projects.CreateTypeAsync(info.Id, "FFTSpectrum", "Just deleted", null, "{}", CancellationToken.None);
        long live = await projects.CreateTypeAsync(info.Id, "FFTSpectrum", "Still here", null, "{}", CancellationToken.None);
        await projects.TrashTypeAsync(expired, CancellationToken.None);
        await projects.TrashTypeAsync(recent, CancellationToken.None);
        await BackdateTrashedTypeAsync(expired, DateTimeOffset.UtcNow.AddDays(-20));
        await BackdateTrashedTypeAsync(recent, DateTimeOffset.UtcNow.AddDays(-2));

        await _session.CloseAsync(CancellationToken.None);
        await _session.OpenAsync(info.Path, CancellationToken.None);

        await using var connection = await _factory.OpenAsync(CancellationToken.None);
        Assert.Equal(0L, await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM measurement_type WHERE id = {expired};"));
        // Inside the 14-day default, so still restorable; and the live one is never a candidate.
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM measurement_type WHERE id = {recent};"));
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM measurement_type WHERE id = {live};"));
    }

    [Fact]
    public async Task Open_UsesTheStoredRetentionRatherThanTheDefault()
    {
        await _config.SetAsync(ConfigRepository.TrashRetentionKey, """{"days":1}""", CancellationToken.None);

        var info = await _session.CreateAsync(In("amp"), "GM70 amp", null, CancellationToken.None);
        var projects = new ProjectRepository(_factory);
        long trashed = await projects.CreateTypeAsync(info.Id, "FFTSpectrum", "Deleted", null, "{}", CancellationToken.None);
        await projects.TrashTypeAsync(trashed, CancellationToken.None);
        await BackdateTrashedTypeAsync(trashed, DateTimeOffset.UtcNow.AddDays(-2));

        await _session.CloseAsync(CancellationToken.None);
        await _session.OpenAsync(info.Path, CancellationToken.None);

        await using var connection = await _factory.OpenAsync(CancellationToken.None);
        Assert.Equal(0L, await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM measurement_type WHERE id = {trashed};"));
    }

    /// <summary>
    /// A project that will not open because tidying up failed would be a worse fault than the one
    /// the trash saves the user from. An unreadable retention document falls back to the default
    /// rather than throwing, and anything else is logged and swallowed.
    /// </summary>
    [Fact]
    public async Task Open_WithAnUnreadableRetentionDocument_StillOpensTheProject()
    {
        await _config.SetAsync(ConfigRepository.TrashRetentionKey, "not json at all", CancellationToken.None);

        var info = await _session.CreateAsync(In("amp"), "GM70 amp", null, CancellationToken.None);
        await _session.CloseAsync(CancellationToken.None);

        var reopened = await _session.OpenAsync(info.Path, CancellationToken.None);

        Assert.Equal(info.Path, reopened.Path);
        Assert.Equal("GM70 amp", _session.Current!.Name);
    }

    private async Task BackdateTrashedTypeAsync(long typeId, DateTimeOffset when)
    {
        await using var connection = await _factory.OpenAsync(CancellationToken.None);
        Assert.Equal(1, await connection.ExecuteAsync(
            "UPDATE measurement_type SET deleted_utc = @when WHERE id = @typeId;",
            new { when = TonebenchTimestamp.FormatUtc(when), typeId }));
    }

    [Fact]
    public async Task RemoveRecent_DropsOneEntry()
    {
        await _session.CreateAsync(In("a"), "a", null, CancellationToken.None);
        await _session.CreateAsync(In("b"), "b", null, CancellationToken.None);

        await _session.RemoveRecentAsync(In("a.tbench"), CancellationToken.None);

        Assert.Equal([In("b.tbench")], (await _session.ReadRecentAsync(CancellationToken.None)).Entries.Select(e => e.Path));
    }
}
