using Dapper;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tonebench.App.Json;
using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// Export and import at the session level: the guards, what happens to the open project while a
/// transfer runs, and the three ways a source file can arrive that
/// <see cref="ProjectTransferSource"/> exists for. The row-level copy is
/// <c>ProjectTransferRepositoryTests</c>' business.
/// </summary>
public class ProjectTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tonebench-transfer-{Guid.NewGuid():N}");
    private readonly string _configPath;
    private readonly CurrentProjectFile _current = new();
    private readonly FakeRun _run = new();
    private readonly ProjectSessionService _session;

    private sealed class FakeRun : IMeasurementRun
    {
        public bool Running { get; set; }

        public Task CancelAsync()
        {
            Running = false;
            return Task.CompletedTask;
        }
    }

    public ProjectTransferTests()
    {
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "config.db");
        var config = new ConfigRepository(new ConfigConnectionFactory(new TonebenchConfigDatabaseOptions { Path = _configPath }));

        var jsonOptions = new JsonOptions();
        jsonOptions.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);

        _session = new ProjectSessionService(
            _current, config, new DatabaseHealth(), [_run], Options.Create(jsonOptions),
            NullLogger<ProjectSessionService>.Instance);

        using var connection = SqliteConnectionFactory.OpenAtAsync(_configPath, create: true, CancellationToken.None).GetAwaiter().GetResult();
        new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            foreach (string file in Directory.EnumerateFiles(_dir))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not go away is not worth failing a passing test over.
        }
    }

    private string In(string name) => Path.Combine(_dir, name);

    // ---- export ---------------------------------------------------------------------------------

    [Fact]
    public async Task Export_ThenOpenTheExport_ThenImportItBack()
    {
        // The only test that proves the feature: out of one project, into a file, open that file as
        // a project of its own, and back into the original.
        var open = await _session.CreateAsync(In("bench"), "Bench", null, CancellationToken.None);
        var fft = await SaveFftAsync(open.Path, open.Id, "Amp 1 kHz", "run 1");
        var sweep = await SaveSweepAsync(open.Path, open.Id, "THD vs f", "sweep 1", points: 4);

        var exported = await _session.ExportAsync(
            In("to-send"), "Sent measurements", "for Joost",
            [new TransferSelection(fft.TypeId, [fft.ResultId]), new TransferSelection(sweep.TypeId, [sweep.ResultId])],
            TypeNameClash.Rename, CancellationToken.None);

        Assert.Equal(2, exported.ResultsCopied);
        Assert.Equal(5, exported.CapturesCopied); // one for the FFT, four sweep points

        // It is an ordinary project file, so the recipient can simply open it.
        var opened = await _session.OpenAsync(In("to-send.tbench"), CancellationToken.None);
        Assert.Equal("Sent measurements", opened.Name);
        Assert.Equal(
            ["Amp 1 kHz", "THD vs f"],
            (await _session.ReadContentsAsync(CancellationToken.None)).Types.Select(t => t.Name).Order());

        // And back the other way, into a project that already has those names.
        await _session.OpenAsync(open.Path, CancellationToken.None);
        var contents = await _session.ReadContentsAsync(In("to-send.tbench"), CancellationToken.None);
        var imported = await _session.ImportAsync(
            In("to-send.tbench"),
            [.. contents.Types.Select(t => new TransferSelection(t.TypeId, [.. t.Results.Select(r => r.ResultId)]))],
            TypeNameClash.Rename, CancellationToken.None);

        Assert.Equal(2, imported.ResultsCopied);
        Assert.Equal(
            ["Amp 1 kHz", "Amp 1 kHz (2)", "THD vs f", "THD vs f (2)"],
            (await _session.ReadContentsAsync(CancellationToken.None)).Types.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Export_CheckpointsTheDestination_SoTheFileAloneHoldsEverything()
    {
        var open = await _session.CreateAsync(In("bench"), "Bench", null, CancellationToken.None);
        var fft = await SaveFftAsync(open.Path, open.Id, "Amp", "run");

        await _session.ExportAsync(
            In("mailed"), "Mailed", null, [new TransferSelection(fft.TypeId, [fft.ResultId])],
            TypeNameClash.Rename, CancellationToken.None);

        // The e-mail test. A .tbench attached on its own, with the -wal left behind, would be
        // missing whatever had not been folded in — which straight after an export is the export.
        // After the checkpoint and the pool release there should be no sidecars at all.
        Assert.True(File.Exists(In("mailed.tbench")));
        Assert.False(File.Exists(In("mailed.tbench-wal")), "the WAL should have been folded in and removed");
        Assert.False(File.Exists(In("mailed.tbench-shm")), "no handle should still be open on the exported file");

        // Belt and braces: delete any sidecar that might appear and read the file cold.
        var reopened = await _session.OpenAsync(In("mailed.tbench"), CancellationToken.None);
        var contents = await _session.ReadContentsAsync(CancellationToken.None);
        Assert.Equal("Mailed", reopened.Name);
        Assert.Equal("run", contents.Types.Single().Results.Single().Name);
    }

    [Fact]
    public async Task Export_ToTheOpenProject_IsRefused()
    {
        var open = await _session.CreateAsync(In("bench"), "Bench", null, CancellationToken.None);
        var fft = await SaveFftAsync(open.Path, open.Id, "Amp", "run");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ExportAsync(
            open.Path, null, null, [new TransferSelection(fft.TypeId, [fft.ResultId])],
            TypeNameClash.Rename, CancellationToken.None));

        Assert.Contains("that is the project that is open now", error.Message);
    }

    [Fact]
    public async Task Export_WhileAMeasurementIsRunning_IsRefused_AndSaysSo()
    {
        var open = await _session.CreateAsync(In("bench"), "Bench", null, CancellationToken.None);
        var fft = await SaveFftAsync(open.Path, open.Id, "Amp", "run");
        _run.Running = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ExportAsync(
            In("out"), "Out", null, [new TransferSelection(fft.TypeId, [fft.ResultId])],
            TypeNameClash.Rename, CancellationToken.None));

        // The message names the action, rather than the "changing projects" every other caller gets.
        Assert.Contains("exporting measurements", error.Message);
        Assert.False(File.Exists(In("out.tbench")), "nothing should have been created");
    }

    [Fact]
    public async Task Export_LeavesTheOpenProjectOpenAndItsRowsUnchanged()
    {
        var open = await _session.CreateAsync(In("bench"), "Bench", null, CancellationToken.None);
        var fft = await SaveFftAsync(open.Path, open.Id, "Amp", "run");

        await _session.ExportAsync(
            In("out"), "Out", null, [new TransferSelection(fft.TypeId, [fft.ResultId])],
            TypeNameClash.Rename, CancellationToken.None);

        // Unlike Save As, an export is not a switch: nothing is closed, nothing is adopted, and the
        // recent list does not gain the file that was written.
        Assert.Equal(open.Path, _current.Path);
        Assert.Equal(open.Path, _session.Current?.Path);
        Assert.Equal("Bench", _session.Current?.Name);
        Assert.Equal(1, await ScalarAsync(open.Path, "SELECT COUNT(*) FROM measurement_result;"));
        Assert.Equal(1, await ScalarAsync(open.Path, "SELECT COUNT(*) FROM measurement_type;"));

        var recent = await _session.ReadRecentAsync(CancellationToken.None);
        Assert.Equal([open.Path], recent.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task Export_ThatFails_DeletesTheFileItCreated()
    {
        var open = await _session.CreateAsync(In("bench"), "Bench", null, CancellationToken.None);
        await SaveFftAsync(open.Path, open.Id, "Amp", "run");

        // A measurement id that is not in the source: the file is created first, so this fails
        // after it exists. A one-project file left behind would read as an export that worked and
        // copied nothing.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ExportAsync(
            In("doomed"), "Doomed", null, [new TransferSelection(9999, [])],
            TypeNameClash.Rename, CancellationToken.None));

        Assert.False(File.Exists(In("doomed.tbench")));
        Assert.False(File.Exists(In("doomed.tbench-wal")));
        Assert.False(File.Exists(In("doomed.tbench-shm")));
    }

    [Fact]
    public async Task Export_IntoAnExistingProject_AppendsAndKeepsItsOwnNameAndNotes()
    {
        var first = await _session.CreateAsync(In("theirs"), "Their bench", "their notes", CancellationToken.None);
        var theirs = await SaveFftAsync(first.Path, first.Id, "Theirs", "their run");
        await _session.CloseAsync(CancellationToken.None);

        var open = await _session.CreateAsync(In("mine"), "My bench", null, CancellationToken.None);
        var mine = await SaveFftAsync(open.Path, open.Id, "Mine", "my run");

        // The name and notes on the request are for a file that has to be created. An existing
        // project keeps its own, because an export appends to it rather than taking it over.
        await _session.ExportAsync(
            first.Path, "Ignored name", "ignored notes", [new TransferSelection(mine.TypeId, [mine.ResultId])],
            TypeNameClash.Rename, CancellationToken.None);

        Assert.Equal("Their bench", await StringAsync(first.Path, "SELECT name FROM project;"));
        Assert.Equal("their notes", await StringAsync(first.Path, "SELECT notes FROM project;"));
        Assert.Equal(2, await ScalarAsync(first.Path, "SELECT COUNT(*) FROM measurement_type;"));
        Assert.Equal(2, await ScalarAsync(first.Path, "SELECT COUNT(*) FROM measurement_result;"));
        _ = theirs;
    }

    [Fact]
    public async Task Export_WithNothingPicked_IsRefused()
    {
        await _session.CreateAsync(In("bench"), "Bench", null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<ProjectFileException>(() => _session.ExportAsync(
            In("out"), "Out", null, [], TypeNameClash.Rename, CancellationToken.None));

        Assert.Contains("Nothing was picked", error.Message);
        Assert.False(File.Exists(In("out.tbench")));
    }

    // ---- import ---------------------------------------------------------------------------------

    [Fact]
    public async Task Import_FromAnOlderSchema_MigratesACopyAndLeavesTheFileUntouched()
    {
        string source = await BuildSourceFileAsync("sent");

        // Migration 015 rounds capture.fs rows written with REAL affinity. Winding the version back
        // and planting such a row is this fault arriving through the import path: INSERT … SELECT
        // copies a value with its storage class, so an un-migrated source would carry the fractional
        // rate into the recipient and take its whole History list down with a Dapper error naming
        // nothing that is wrong.
        await ExecuteAsync(source, "DELETE FROM schema_version WHERE version = 15;");
        await ExecuteAsync(source, "UPDATE capture SET fs = 172345.6789;");
        Assert.Equal("real", await StringAsync(source, "SELECT typeof(fs) FROM capture LIMIT 1;"));

        var open = await _session.CreateAsync(In("mine"), "Mine", null, CancellationToken.None);
        var contents = await _session.ReadContentsAsync(source, CancellationToken.None);
        Assert.Equal(14, contents.SchemaVersion);

        await _session.ImportAsync(
            source, [.. contents.Types.Select(t => new TransferSelection(t.TypeId, [.. t.Results.Select(r => r.ResultId)]))],
            TypeNameClash.Rename, CancellationToken.None);

        // The copy was migrated; the file the user was sent was not.
        Assert.Equal(14, await ScalarAsync(source, "SELECT MAX(version) FROM schema_version;"));
        Assert.Equal("real", await StringAsync(source, "SELECT typeof(fs) FROM capture LIMIT 1;"));
        Assert.Equal("integer", await StringAsync(open.Path, "SELECT typeof(fs) FROM capture LIMIT 1;"));
        Assert.Equal(172346, await ScalarAsync(open.Path, "SELECT fs FROM capture LIMIT 1;"));
    }

    [Fact]
    public async Task Import_FromAFileWithAnUncheckpointedWal_SeesTheLastWrites()
    {
        // A file copied out of a running app carries writes that are still only in its -wal. Read
        // the database alone and they are simply missing -- silently, as a measurement that is not
        // there rather than as an error.
        string received = await BuildReceivedFileWithOpenWalAsync("received");
        Assert.True(File.Exists(received + "-wal"), "the fixture should have left an un-checkpointed WAL");

        var open = await _session.CreateAsync(In("mine"), "Mine", null, CancellationToken.None);
        var contents = await _session.ReadContentsAsync(received, CancellationToken.None);

        Assert.Contains("Late", contents.Types.Select(t => t.Name));

        var late = contents.Types.Single(t => t.Name == "Late");
        var report = await _session.ImportAsync(
            received, [new TransferSelection(late.TypeId, [.. late.Results.Select(r => r.ResultId)])],
            TypeNameClash.Rename, CancellationToken.None);

        Assert.Equal(1, report.ResultsCopied);
        Assert.Equal("Late", await StringAsync(open.Path, "SELECT name FROM measurement_type;"));
    }

    [Fact]
    public async Task Import_FromAReadOnlyFile_Works()
    {
        // The case the copy fallback exists for: every Tonebench file is in WAL mode, which needs a
        // writable -shm, so a file on a read-only share or straight out of a mail client cannot be
        // opened at all -- which without the fallback would stop even listing it.
        string received = await BuildReceivedFileWithOpenWalAsync("readonly");
        File.SetAttributes(received, FileAttributes.ReadOnly);
        try
        {
            var open = await _session.CreateAsync(In("mine"), "Mine", null, CancellationToken.None);
            var contents = await _session.ReadContentsAsync(received, CancellationToken.None);

            // And the -wal has to be copied with it, or the fallback loses the same writes the
            // test above is about.
            Assert.Contains("Late", contents.Types.Select(t => t.Name));

            var report = await _session.ImportAsync(
                received,
                [.. contents.Types.Select(t => new TransferSelection(t.TypeId, [.. t.Results.Select(r => r.ResultId)]))],
                TypeNameClash.Rename, CancellationToken.None);

            Assert.Equal(3, report.ResultsCopied);
            Assert.Equal(3, await ScalarAsync(open.Path, "SELECT COUNT(*) FROM measurement_result;"));
            Assert.True(File.GetAttributes(received).HasFlag(FileAttributes.ReadOnly), "the source must still be read-only");
        }
        finally
        {
            File.SetAttributes(received, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Import_FromANewerSchema_IsRefused_AndNamesBothVersions()
    {
        string source = await BuildSourceFileAsync("sent");
        await ExecuteAsync(source, "INSERT INTO schema_version (version, name, applied_utc) VALUES (999, 'from the future', '2030-01-01T00:00:00.000Z');");

        await _session.CreateAsync(In("mine"), "Mine", null, CancellationToken.None);

        // Refused rather than read: this build's column lists know nothing about whatever columns
        // version 999 added, so a copy would drop them in silence.
        var error = await Assert.ThrowsAsync<ProjectFileException>(
            () => _session.ReadContentsAsync(source, CancellationToken.None));

        Assert.Contains("999", error.Message);
        Assert.Contains("newer version of Tonebench", error.Message);
        Assert.Contains(new MigrationRunner().LatestVersion.ToString(), error.Message);
    }

    [Fact]
    public async Task Import_FromAFileThatIsNotAProject_IsRefused()
    {
        await _session.CreateAsync(In("mine"), "Mine", null, CancellationToken.None);

        await File.WriteAllTextAsync(In("notes.txt"), "this is not a database");
        var notSqlite = await Assert.ThrowsAsync<ProjectFileException>(
            () => _session.ReadContentsAsync(In("notes.txt"), CancellationToken.None));
        Assert.Contains("not an SQLite database", notSqlite.Message);

        await using (var stranger = await SqliteConnectionFactory.OpenAtAsync(In("stranger.db"), create: true, CancellationToken.None))
            await stranger.ExecuteAsync("CREATE TABLE unrelated (id INTEGER PRIMARY KEY);");
        SqliteConnection.ClearAllPools();

        var notOurs = await Assert.ThrowsAsync<ProjectFileException>(
            () => _session.ReadContentsAsync(In("stranger.db"), CancellationToken.None));
        Assert.Contains("not a Tonebench project file", notOurs.Message);

        var missing = await Assert.ThrowsAsync<ProjectFileException>(
            () => _session.ReadContentsAsync(In("nowhere.tbench"), CancellationToken.None));
        Assert.Contains("does not exist", missing.Message);
    }

    [Fact]
    public async Task Import_FromAMultiProjectDatabase_IsRefused()
    {
        // The legacy shared qa403.db held every project at once. Open refuses it by count and so
        // does this, with the same wording — it is not a project file.
        string source = await BuildSourceFileAsync("shared");
        await ExecuteAsync(source, "INSERT INTO project (name, notes, created_utc) VALUES ('second', NULL, '2026-01-01T00:00:00.000Z');");

        await _session.CreateAsync(In("mine"), "Mine", null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<ProjectFileException>(
            () => _session.ReadContentsAsync(source, CancellationToken.None));
        Assert.Contains("holds 2 projects", error.Message);
    }

    [Fact]
    public async Task Import_FromTheOpenProject_IsRefused()
    {
        var open = await _session.CreateAsync(In("bench"), "Bench", null, CancellationToken.None);
        var fft = await SaveFftAsync(open.Path, open.Id, "Amp", "run");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ImportAsync(
            open.Path, [new TransferSelection(fft.TypeId, [fft.ResultId])], TypeNameClash.Rename, CancellationToken.None));

        Assert.Contains("the project that is open now", error.Message);
    }

    [Fact]
    public async Task Import_WithNothingOpen_IsRefused()
    {
        string source = await BuildSourceFileAsync("sent");

        // A 409, not a 400: there is nothing wrong with the file, there is just nowhere to put it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ImportAsync(
            source, [new TransferSelection(1, [])], TypeNameClash.Rename, CancellationToken.None));
    }

    [Fact]
    public async Task Import_LeavesNoTempFileBehind()
    {
        string source = await BuildSourceFileAsync("sent");
        await ExecuteAsync(source, "DELETE FROM schema_version WHERE version = 15;"); // forces the copy path

        await _session.CreateAsync(In("mine"), "Mine", null, CancellationToken.None);
        int before = CountTempCopies();

        var contents = await _session.ReadContentsAsync(source, CancellationToken.None);
        await _session.ImportAsync(
            source, [.. contents.Types.Select(t => new TransferSelection(t.TypeId, [.. t.Results.Select(r => r.ResultId)]))],
            TypeNameClash.Rename, CancellationToken.None);

        Assert.Equal(before, CountTempCopies());

        // And on the failing path too, which is where a `finally` is easiest to get wrong.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ImportAsync(
            source, [new TransferSelection(9999, [])], TypeNameClash.Rename, CancellationToken.None));

        Assert.Equal(before, CountTempCopies());
    }

    // ---- contents -------------------------------------------------------------------------------

    [Fact]
    public async Task Contents_ForAProjectWithNothingSavedYet_Answers()
    {
        // The state every project is in before its first Run: measurements configured, nothing
        // stored. Reported from the Export dialog on 2026-10-17, where it came back as a Dapper
        // materialisation error and an empty tree — see
        // ProjectTransferRepositoryTests.ListContents_WithNoResultsToReport_StillAnswers for the
        // mechanism. Asserted at this level too because both dialogs reach it through the session,
        // and the import side reads another file through the same method.
        var open = await _session.CreateAsync(In("fresh"), "Fresh", null, CancellationToken.None);

        var empty = await _session.ReadContentsAsync(CancellationToken.None);
        Assert.Equal("Fresh", empty.ProjectName);
        Assert.Empty(empty.Types);

        var projects = new ProjectRepository(new SqliteConnectionFactory(_current));
        await projects.CreateTypeAsync(open.Id, "FFTSpectrum", "Amp", null, "{}", CancellationToken.None);

        var configured = await _session.ReadContentsAsync(CancellationToken.None);
        Assert.Empty(Assert.Single(configured.Types).Results);

        // And the same file read as somebody else's, which is what Import… does.
        await _session.CloseAsync(CancellationToken.None);
        await _session.CreateAsync(In("mine"), "Mine", null, CancellationToken.None);

        var asASource = await _session.ReadContentsAsync(open.Path, CancellationToken.None);
        Assert.Equal("Fresh", asASource.ProjectName);
        Assert.Empty(Assert.Single(asASource.Types).Results);
    }

    [Fact]
    public async Task Contents_ForAnotherFile_DoesNotOpenIt()
    {
        string source = await BuildSourceFileAsync("sent");
        var open = await _session.CreateAsync(In("mine"), "Mine", null, CancellationToken.None);

        // Snapshotted rather than spelled out: building the fixture legitimately touches the recent
        // list and the last-opened document, and what this test is about is that *reading another
        // file* moves none of them.
        string[] recentBefore = [.. (await _session.ReadRecentAsync(CancellationToken.None)).Entries.Select(e => e.Path)];
        string? lastOpenedBefore = (await _session.ReadLastOpenedAsync(CancellationToken.None)).Path;

        var contents = await _session.ReadContentsAsync(source, CancellationToken.None);

        Assert.Equal(source, contents.Path);
        Assert.Equal("Theirs", contents.ProjectName);
        Assert.Equal("their notes", contents.ProjectNotes);
        Assert.Equal(["Amp", "THD vs f"], contents.Types.Select(t => t.Name).Order());

        // Listing a file is not opening it. All four of these would move if it were.
        Assert.Equal(open.Path, _session.Current?.Path);
        Assert.Equal(open.Path, _current.Path);
        Assert.Equal(recentBefore, (await _session.ReadRecentAsync(CancellationToken.None)).Entries.Select(e => e.Path));
        Assert.Equal(lastOpenedBefore, (await _session.ReadLastOpenedAsync(CancellationToken.None)).Path);
    }

    [Fact]
    public async Task Contents_OfTheOpenProject_HidesTrashedRows_AndReportsSizes()
    {
        var open = await _session.CreateAsync(In("bench"), "Bench", null, CancellationToken.None);
        var kept = await SaveFftAsync(open.Path, open.Id, "Amp", "kept");
        var deleted = await SaveFftAsync(open.Path, open.Id, "Amp", "deleted", kept.TypeId);
        var goneType = await SaveFftAsync(open.Path, open.Id, "Gone", "under a deleted measurement");

        var factory = new SqliteConnectionFactory(_current);
        await new MeasurementRepository(factory).TrashResultAsync(deleted.ResultId, CancellationToken.None);
        await new ProjectRepository(factory).TrashTypeAsync(goneType.TypeId, CancellationToken.None);

        var contents = await _session.ReadContentsAsync(CancellationToken.None);

        Assert.Equal("Bench", contents.ProjectName);
        Assert.Equal(new MigrationRunner().LatestVersion, contents.SchemaVersion);
        var type = Assert.Single(contents.Types);
        Assert.Equal("Amp", type.Name);
        var result = Assert.Single(type.Results);
        Assert.Equal("kept", result.Name);
        Assert.Equal(1, result.Captures);
        Assert.True(result.ApproximateBytes > 0, "a stored spectrum has blob bytes");
    }

    // ---- fixtures -------------------------------------------------------------------------------

    /// <summary>
    /// A separate project file holding two measurements, closed and checkpointed afterwards so it
    /// stands in for one that arrived from somebody else.
    /// </summary>
    private async Task<string> BuildSourceFileAsync(string name)
    {
        var info = await _session.CreateAsync(In(name), "Theirs", "their notes", CancellationToken.None);
        await SaveFftAsync(info.Path, info.Id, "Amp", "their run");
        await SaveSweepAsync(info.Path, info.Id, "THD vs f", "their sweep", points: 3);
        await _session.CloseAsync(CancellationToken.None);
        return info.Path;
    }

    /// <summary>
    /// A project file as it arrives when somebody attaches it to an e-mail straight out of a
    /// running Tonebench: the database plus a <c>-wal</c> holding the last measurement, which is
    /// only visible once that log is recovered.
    ///
    /// <para>It has to be built by copying the pair while a connection is still open, because
    /// SQLite checkpoints and removes the WAL when the last connection to a database closes — so
    /// there is no way to simply "leave one behind".</para>
    /// </summary>
    private async Task<string> BuildReceivedFileWithOpenWalAsync(string name)
    {
        string origin = await BuildSourceFileAsync($"{name}-origin");
        string received = In($"{name}.tbench");

        await using (var live = await SqliteConnectionFactory.OpenUnpooledAtAsync(origin, create: false, CancellationToken.None))
        {
            long projectId = await live.ExecuteScalarAsync<long>("SELECT id FROM project;");
            long typeId = await live.ExecuteScalarAsync<long>(
                """
                INSERT INTO measurement_type (project_id, kind, name, notes, params_json, created_utc, sort_order)
                VALUES (@projectId, 'FFTSpectrum', 'Late', NULL, '{}', '2026-01-01T00:00:00.000Z', 99)
                RETURNING id;
                """,
                new { projectId });
            long resultId = await live.ExecuteScalarAsync<long>(
                """
                INSERT INTO measurement_result
                  (measurement_type_id, name, notes, ts, params_json, device_name, serial, fw_ver, is_simulated)
                VALUES (@typeId, 'after the checkpoint', NULL, '2026-01-01T00:00:00.000Z', '{}', NULL, NULL, NULL, 1)
                RETURNING id;
                """,
                new { typeId });
            await live.ExecuteAsync(
                """
                INSERT INTO capture (measurement_result_id, seq_index, ts, fs, n, channels)
                VALUES (@resultId, 0, '2026-01-01T00:00:00.000Z', 48000, 256, 1);
                """,
                new { resultId });

            Assert.True(File.Exists(origin + "-wal"), "a committed write should be sitting in the WAL");

            // Copied while the connection is open, which is what leaves the log un-folded in the
            // copy -- the state the recipient's file is in.
            File.Copy(origin, received);
            File.Copy(origin + "-wal", received + "-wal");
        }

        return received;
    }

    private sealed record Saved(long TypeId, long ResultId);

    private static async Task<Saved> SaveFftAsync(string path, long projectId, string typeName, string resultName, long? typeId = null)
    {
        var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
        long type = typeId ?? await new ProjectRepository(factory).CreateTypeAsync(
            projectId, "FFTSpectrum", typeName, null, "{}", CancellationToken.None);

        var dbv = new double[129];
        for (int i = 0; i < dbv.Length; i++)
            dbv[i] = -120.0 + i * 0.5;

        long resultId = await new MeasurementRepository(factory).SaveResultAsync(
            new MeasurementSaveRequest(
                type, resultName, null, """{"fftSize":256}""",
                "QA403 (Sim)", "SIM-001", "1.0.0", IsSimulated: true,
                SampleRateHz: 48000.0, FftSize: 256, Channels: 1,
                InRangeDbv: 0.0, OutRangeDbv: -12.0, GenJson: null,
                [new StoredSpectrumInput(0, WindowType.Hann, new WindowSums(128.0, 96.0, 256), 48000.0, 187.5, SpectrumNorm.Amplitude, dbv)],
                [new MeasurementMetric(0, "fundamental_hz", 1000.0, "Hz")]),
            CancellationToken.None);

        return new Saved(type, resultId);
    }

    private static async Task<Saved> SaveSweepAsync(string path, long projectId, string typeName, string resultName, int points)
    {
        var factory = new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path });
        long type = await new ProjectRepository(factory).CreateTypeAsync(
            projectId, "ThdVsFrequency", typeName, null, "{}", CancellationToken.None);

        var captures = Enumerable.Range(0, points).Select(i => new SweepCaptureInput(
            i, 100.0 * (i + 1),
            [new MeasurementMetric(0, "fundamental_hz", 100.0 * (i + 1), "Hz")],
            [new CapturePreviewInput(0, CapturePreviewKind.Fft, [1f, 2f], [-100f, -99f], [-90f, -89f])]))
            .ToList();

        long resultId = await new MeasurementRepository(factory).SaveSweepResultAsync(
            new SweepSaveRequest(
                type, resultName, null, """{"startHz":20}""",
                "QA403 (Sim)", "SIM-001", "1.0.0", IsSimulated: true,
                SampleRateHz: 48000.0, FftSize: 256, Channels: 1,
                InRangeDbv: 0.0, OutRangeDbv: -12.0, captures),
            CancellationToken.None);

        return new Saved(type, resultId);
    }

    private int CountTempCopies() =>
        Directory.EnumerateFiles(Path.GetTempPath(), "tonebench-import-*").Count();

    private static async Task<long> ScalarAsync(string path, string sql)
    {
        await using var connection = await SqliteConnectionFactory.OpenAtAsync(path, create: false, CancellationToken.None);
        return await connection.ExecuteScalarAsync<long>(sql);
    }

    private static async Task<string?> StringAsync(string path, string sql)
    {
        await using var connection = await SqliteConnectionFactory.OpenAtAsync(path, create: false, CancellationToken.None);
        return await connection.ExecuteScalarAsync<string?>(sql);
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = await SqliteConnectionFactory.OpenAtAsync(path, create: false, CancellationToken.None);
        await connection.ExecuteAsync(sql);
    }
}
