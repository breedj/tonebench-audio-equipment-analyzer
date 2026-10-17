using Dapper;
using Microsoft.Data.Sqlite;
using Tonebench.Dsp;

namespace Tonebench.Data.Tests;

/// <summary>
/// The cross-file copy behind Export and Import. Two of these tests are structural rather than
/// behavioural and are the ones that earn their keep over time:
/// <see cref="ChildTables_IsEveryTableThatReferencesCapture"/> fails when a seventh child of
/// <c>capture</c> is added to the schema and not to the copy, and
/// <see cref="ColumnLists_MatchPragmaTableInfo"/> fails when a migration adds a column, forcing
/// whoever adds it to decide whether it is carried, nulled or an id that has to be rewritten.
/// </summary>
public class ProjectTransferRepositoryTests
{
    [Fact]
    public async Task ChildTables_IsEveryTableThatReferencesCapture()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await using var connection = await TestDb.OpenMigratedAsync(path);

            // Asks the migrated schema rather than a hand-written list: that is what makes a table
            // added by a future migration and forgotten here fail loudly. `capture` itself appears
            // nowhere in the answer because its own dangling REFERENCES names session(id), not
            // capture(id).
            var referencing = (await connection.QueryAsync<string>(
                """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND sql LIKE '%REFERENCES capture(id)%';
                """)).ToHashSet();

            Assert.Equal(referencing.OrderBy(n => n, StringComparer.Ordinal), CaptureCascade.ChildTables.OrderBy(n => n, StringComparer.Ordinal));

            // Stated separately, because it is a decision and not an omission: impulse_reference
            // describes the analyzer, hangs off nothing, and must never be copied between benches.
            Assert.DoesNotContain("impulse_reference", CaptureCascade.ChildTables);
            Assert.True(await TestDb.TableExistsAsync(connection, "impulse_reference"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task ColumnLists_MatchPragmaTableInfo()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            await using var connection = await TestDb.OpenMigratedAsync(path);

            foreach (var plan in ProjectTransferRepository.Plans)
            {
                var actual = (await connection.QueryAsync<string>($"SELECT name FROM pragma_table_info('{plan.Table}');"))
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
                var planned = plan.Columns.Concat(plan.Rewritten)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();

                Assert.Equal(actual, planned);
                Assert.Empty(plan.Columns.Intersect(plan.Rewritten));
            }

            // Every table the copy touches has a plan, and the child tables come from the one list
            // the delete cascade uses.
            Assert.Equal(
                new[] { "measurement_type", "measurement_result", "capture" }.Concat(CaptureCascade.ChildTables).Order(),
                ProjectTransferRepository.Plans.Select(p => p.Table).Order());
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task Copy_ANewType_CarriesItsResultsCapturesAndAllSixChildTables()
    {
        await using var bench = await TransferBench.CreateAsync();

        var fft = await bench.Source.SaveFftAsync("FFT", "spectrum");
        var sweep = await bench.Source.SaveSweepAsync("Sweep", "thd vs f", points: 5);
        var response = await bench.Source.SaveResponseAsync("Response", "chirp");
        var impulse = await bench.Source.SaveImpulseAsync("Impulse", "ir");

        var report = await bench.TransferAsync(
            [.. new[] { fft, sweep, response, impulse }.Select(r => new TransferSelection(r.TypeId, [r.ResultId]))]);

        Assert.Equal(4, report.ResultsCopied);
        Assert.All(report.Types, t => Assert.Equal(TransferOutcome.Created, t.Outcome));

        // Every table, counted on both sides. A per-table count rather than one total, so a missed
        // table names itself.
        string[] everyTable = ["measurement_type", "measurement_result", "capture", .. CaptureCascade.ChildTables];
        foreach (string table in everyTable)
            Assert.Equal(await bench.Source.CountAsync(table), await bench.Destination.CountAsync(table));

        // Nothing is orphaned: every copied child row resolves to a copied capture, and every
        // capture to a copied result. This is the fault the capture offset exists to prevent, and a
        // row count alone cannot see it.
        foreach (string table in CaptureCascade.ChildTables)
        {
            Assert.Equal(0, await bench.Destination.ScalarAsync(
                $"SELECT COUNT(*) FROM {table} ch LEFT JOIN capture c ON c.id = ch.capture_id WHERE c.id IS NULL;"));
        }

        Assert.Equal(0, await bench.Destination.ScalarAsync(
            """
            SELECT COUNT(*) FROM capture c
            LEFT JOIN measurement_result mr ON mr.id = c.measurement_result_id
            WHERE mr.id IS NULL;
            """));
        Assert.Equal(0, await bench.Destination.ScalarAsync(
            """
            SELECT COUNT(*) FROM measurement_result mr
            LEFT JOIN measurement_type mt ON mt.id = mr.measurement_type_id
            WHERE mt.id IS NULL;
            """));
    }

    [Fact]
    public async Task Copy_CarriesTheStoredSpectrumAndTheTimestampRatherThanRestampingIt()
    {
        await using var bench = await TransferBench.CreateAsync();
        var saved = await bench.Source.SaveFftAsync("FFT", "spectrum");

        string? sourceTs = await bench.Source.StringAsync("SELECT ts FROM measurement_result WHERE id = @id;", new { id = saved.ResultId });
        byte[] sourceMag = await bench.Source.BlobAsync("SELECT mag_db FROM spectrum WHERE channel = 0;");

        await bench.TransferAsync([new TransferSelection(saved.TypeId, [saved.ResultId])]);

        // When a measurement was taken is the whole reason for keeping it, so `ts` is carried and
        // not replaced with TonebenchTimestamp.Now() the way every save path does.
        Assert.Equal(sourceTs, await bench.Destination.StringAsync("SELECT ts FROM measurement_result;"));
        Assert.Equal(sourceMag, await bench.Destination.BlobAsync("SELECT mag_db FROM spectrum WHERE channel = 0;"));
        Assert.Equal("notes", await bench.Destination.StringAsync("SELECT notes FROM measurement_result;"));
        Assert.Equal(1, await bench.Destination.ScalarAsync("SELECT is_simulated FROM measurement_result;"));
    }

    [Fact]
    public async Task Copy_ASimulationResult_CarriesThePcmBlobAndItsOwnSampleRate()
    {
        await using var bench = await TransferBench.CreateAsync();
        var saved = await bench.Source.SaveSimulationAsync("Sim", "ltspice");

        byte[] sourcePcm = await bench.Source.BlobAsync("SELECT pcm FROM capture WHERE pcm IS NOT NULL;");

        await bench.TransferAsync([new TransferSelection(saved.TypeId, [saved.ResultId])]);

        Assert.Equal(sourcePcm, await bench.Destination.BlobAsync("SELECT pcm FROM capture;"));
        Assert.Equal(MeasurementSource.Simulation, await bench.Destination.StringAsync("SELECT source FROM measurement_result;"));

        // pcm_fs is deliberately exact and unrounded -- the stored samples depend on it -- while
        // capture.fs must stay an integer, which is what migration 015 exists to repair.
        Assert.Equal(TransferBench.SimulationPcmRateHz, await bench.Destination.DoubleAsync("SELECT pcm_fs FROM capture;"), 9);
        Assert.Equal("integer", await bench.Destination.StringAsync("SELECT typeof(fs) FROM capture;"));
    }

    [Fact]
    public async Task Copy_WritesSessionIdNull()
    {
        await using var bench = await TransferBench.CreateAsync();
        var saved = await bench.Source.SaveFftAsync("FFT", "spectrum");

        // A file that has been through the 001/002 era can hold a session_id pointing at a table
        // migration 003 dropped. Carrying it would copy a reference to nothing.
        await bench.Source.ExecuteAsync("UPDATE capture SET session_id = 7;");

        await bench.TransferAsync([new TransferSelection(saved.TypeId, [saved.ResultId])]);

        Assert.Equal(1, await bench.Destination.ScalarAsync("SELECT COUNT(*) FROM capture WHERE session_id IS NULL;"));
    }

    [Fact]
    public async Task Copy_PreservesSeqIndex()
    {
        await using var bench = await TransferBench.CreateAsync();
        var saved = await bench.Source.SaveSweepAsync("Sweep", "thd vs f", points: 5);

        await bench.TransferAsync([new TransferSelection(saved.TypeId, [saved.ResultId])]);

        long copiedResultId = await bench.Destination.ScalarAsync("SELECT id FROM measurement_result;");
        var points = await bench.Destination.Repository.GetSweepPointsAsync(copiedResultId, CancellationToken.None);

        // seq_index is the sweep-point order that ix_capture_result sorts by; renumbering it would
        // scramble the curve while leaving every row count right.
        Assert.Equal(5, points.Count);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => 100.0 * (i + 1)), points.Select(p => p.FreqHz));
    }

    [Fact]
    public async Task Copy_SkipsTrashedTypesAndResults_AndWritesDeletedUtcNull()
    {
        await using var bench = await TransferBench.CreateAsync();
        var kept = await bench.Source.SaveFftAsync("Kept", "live");
        var trashedResult = await bench.Source.SaveFftAsync("Kept", "deleted", typeId: null);
        var trashedType = await bench.Source.SaveFftAsync("Gone", "under a deleted measurement");

        await bench.Source.Repository.TrashResultAsync(trashedResult.ResultId, CancellationToken.None);
        await bench.Source.Projects.TrashTypeAsync(trashedType.TypeId, CancellationToken.None);

        // The trashed result is asked for explicitly, and so is the trashed measurement: a request
        // naming them must not resurrect them in somebody else's project.
        var report = await bench.TransferAsync(
            [
                new TransferSelection(kept.TypeId, [kept.ResultId, trashedResult.ResultId]),
            ]);

        Assert.Equal(1, report.ResultsCopied);
        Assert.Equal(1, await bench.Destination.CountAsync("measurement_result"));
        Assert.Equal("live", await bench.Destination.StringAsync("SELECT name FROM measurement_result;"));
        Assert.Equal(0, await bench.Destination.ScalarAsync("SELECT COUNT(*) FROM measurement_result WHERE deleted_utc IS NOT NULL;"));
        Assert.Equal(0, await bench.Destination.ScalarAsync("SELECT COUNT(*) FROM measurement_type WHERE deleted_utc IS NOT NULL;"));

        // A measurement that is itself in the source's trash cannot be picked at all.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bench.TransferAsync([new TransferSelection(trashedType.TypeId, [])]));
        Assert.Contains("is not in the file being copied from", refused.Message);
    }

    [Fact]
    public async Task Copy_TwiceIntoTheSameDestination_DoesNotCollideOnAUniqueChildIndex()
    {
        await using var bench = await TransferBench.CreateAsync();
        var response = await bench.Source.SaveResponseAsync("Response", "chirp");
        var sweep = await bench.Source.SaveSweepAsync("Sweep", "thd vs f", points: 3);

        var selection = new[]
        {
            new TransferSelection(response.TypeId, [response.ResultId]),
            new TransferSelection(sweep.TypeId, [sweep.ResultId]),
        };

        await bench.TransferAsync(selection);

        // The second copy is the assertion. Every child of `capture` is unique on capture_id plus
        // one more column, so a capture offset that did not account for the rows already there
        // fails here with "UNIQUE constraint failed" -- the signature migrations 005, 007 and 010
        // exist to repair. A row count would pass against that bug on a fresh file, which is why
        // this asserts the call itself succeeding.
        await bench.TransferAsync(selection);

        Assert.Equal(2 * await bench.Source.CountAsync("response_trace"), await bench.Destination.CountAsync("response_trace"));
        Assert.Equal(2 * await bench.Source.CountAsync("capture_preview"), await bench.Destination.CountAsync("capture_preview"));
        Assert.Equal(2 * await bench.Source.CountAsync("capture"), await bench.Destination.CountAsync("capture"));

        // And the two copies are separate rows, not one set pointing at both results.
        Assert.Equal(0, await bench.Destination.ScalarAsync(
            "SELECT COUNT(*) FROM response_trace ch LEFT JOIN capture c ON c.id = ch.capture_id WHERE c.id IS NULL;"));
    }

    [Fact]
    public async Task Copy_IntoADestinationWithItsOwnResults_LeavesThoseRowsAlone()
    {
        await using var bench = await TransferBench.CreateAsync();
        var mine = await bench.Destination.SaveFftAsync("Mine", "my own run");
        var theirs = await bench.Source.SaveFftAsync("Theirs", "their run");

        byte[] before = await bench.Destination.BlobAsync("SELECT mag_db FROM spectrum WHERE channel = 0;");

        await bench.TransferAsync([new TransferSelection(theirs.TypeId, [theirs.ResultId])]);

        Assert.Equal(2, await bench.Destination.CountAsync("measurement_result"));
        Assert.Equal(before, await bench.Destination.BlobAsync(
            """
            SELECT s.mag_db FROM spectrum s
            JOIN capture c ON c.id = s.capture_id
            WHERE c.measurement_result_id = @id AND s.channel = 0;
            """,
            new { id = mine.ResultId }));
        Assert.Equal("my own run", await bench.Destination.StringAsync(
            "SELECT name FROM measurement_result WHERE id = @id;", new { id = mine.ResultId }));
    }

    [Fact]
    public async Task Copy_AppendsNewTypesAtTheEndOfTheSortOrder_CountingTrashedRows()
    {
        await using var bench = await TransferBench.CreateAsync();
        long first = await bench.Destination.Projects.CreateTypeAsync(bench.Destination.ProjectId, "FFTSpectrum", "Mine", null, "{}", CancellationToken.None);
        long trashed = await bench.Destination.Projects.CreateTypeAsync(bench.Destination.ProjectId, "FFTSpectrum", "Deleted", null, "{}", CancellationToken.None);
        await bench.Destination.Projects.TrashTypeAsync(trashed, CancellationToken.None);

        var a = await bench.Source.SaveFftAsync("A", "a");
        var b = await bench.Source.SaveFftAsync("B", "b");

        await bench.TransferAsync(
            [new TransferSelection(a.TypeId, []), new TransferSelection(b.TypeId, [])]);

        // MAX(sort_order) counts the trashed row deliberately, matching CreateTypeAsync: a restored
        // measurement keeps the number it had, so excluding the trash would hand it out twice and
        // leave the sidebar's order decided by the id tiebreak instead of by the user's drag.
        long trashedOrder = await bench.Destination.ScalarAsync("SELECT sort_order FROM measurement_type WHERE id = @id;", new { id = trashed });
        var live = await bench.Destination.Projects.ListTypesAsync(bench.Destination.ProjectId, CancellationToken.None);

        Assert.Equal(["Mine", "A", "B"], live.Select(t => t.Name));
        Assert.All(live.Where(t => t.Name != "Mine"), t => Assert.True(t.SortOrder > trashedOrder));
    }

    [Fact]
    public async Task Copy_WithRename_SuffixesTheName()
    {
        await using var bench = await TransferBench.CreateAsync();
        await bench.Destination.Projects.CreateTypeAsync(bench.Destination.ProjectId, "FFTSpectrum", "Amp", null, "{}", CancellationToken.None);
        var source = await bench.Source.SaveFftAsync("Amp", "run");

        var first = await bench.TransferAsync([new TransferSelection(source.TypeId, [source.ResultId])], TypeNameClash.Rename);
        var second = await bench.TransferAsync([new TransferSelection(source.TypeId, [source.ResultId])], TypeNameClash.Rename);

        Assert.Equal("Amp (2)", Assert.Single(first.Types).DestinationName);
        Assert.Equal("Amp (3)", Assert.Single(second.Types).DestinationName);
        Assert.Equal(TransferOutcome.Created, Assert.Single(first.Types).Outcome);
    }

    [Fact]
    public async Task Copy_WithRename_IsNotBlockedByATrashedNameAndIsCaseSensitive()
    {
        await using var bench = await TransferBench.CreateAsync();

        // "amp" is not "Amp": measurement_type.name has no COLLATE NOCASE, so the unique index is
        // a binary compare and these two coexist. A case-insensitive rename rule would suffix a
        // name the destination would have accepted as it stood.
        await bench.Destination.Projects.CreateTypeAsync(bench.Destination.ProjectId, "FFTSpectrum", "amp", null, "{}", CancellationToken.None);
        var lower = await bench.Source.SaveFftAsync("Amp", "run");

        var kept = await bench.TransferAsync([new TransferSelection(lower.TypeId, [])], TypeNameClash.Rename);
        Assert.Equal("Amp", Assert.Single(kept.Types).DestinationName);

        // A name sitting in the trash does not clash either -- ix_measurement_type_name is partial
        // on deleted_utc IS NULL -- so the suffix search must skip it rather than step over it.
        long trashed = await bench.Destination.Projects.CreateTypeAsync(bench.Destination.ProjectId, "FFTSpectrum", "Amp (2)", null, "{}", CancellationToken.None);
        await bench.Destination.Projects.TrashTypeAsync(trashed, CancellationToken.None);

        var renamed = await bench.TransferAsync([new TransferSelection(lower.TypeId, [])], TypeNameClash.Rename);
        Assert.Equal("Amp (2)", Assert.Single(renamed.Types).DestinationName);
    }

    [Fact]
    public async Task Copy_WithMerge_PutsResultsUnderTheExistingType_AndLeavesItsSettingsAlone()
    {
        await using var bench = await TransferBench.CreateAsync();
        long existing = await bench.Destination.Projects.CreateTypeAsync(
            bench.Destination.ProjectId, "FFTSpectrum", "Amp", "my notes", """{"fftSize":4096}""", CancellationToken.None);
        var source = await bench.Source.SaveFftAsync("Amp", "their run");

        var report = await bench.TransferAsync(
            [new TransferSelection(source.TypeId, [source.ResultId])], TypeNameClash.Merge);

        var placed = Assert.Single(report.Types);
        Assert.Equal(TransferOutcome.Merged, placed.Outcome);
        Assert.Equal(existing, placed.DestinationTypeId);

        Assert.Equal(1, await bench.Destination.CountAsync("measurement_type"));
        Assert.Equal(existing, await bench.Destination.ScalarAsync("SELECT measurement_type_id FROM measurement_result;"));

        // A merge is about where the results land. The measurement's own configuration is the
        // destination bench's and is never overwritten by the incoming one.
        Assert.Equal("my notes", await bench.Destination.StringAsync("SELECT notes FROM measurement_type WHERE id = @id;", new { id = existing }));
        Assert.Equal("""{"fftSize":4096}""", await bench.Destination.StringAsync("SELECT params_json FROM measurement_type WHERE id = @id;", new { id = existing }));
    }

    [Fact]
    public async Task Copy_WithMerge_WhenTheKindsDiffer_Throws()
    {
        await using var bench = await TransferBench.CreateAsync();
        await bench.Destination.Projects.CreateTypeAsync(
            bench.Destination.ProjectId, "ThdVsFrequency", "Amp", null, "{}", CancellationToken.None);
        var source = await bench.Source.SaveFftAsync("Amp", "run");

        // Never a silent fall back to Rename: an FFT result under a sweep measurement is a
        // NullReferenceException 500 out of MeasurementSaveService.ApplyAsync the moment that
        // measurement is merely selected, so the message has to name both kinds.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bench.TransferAsync([new TransferSelection(source.TypeId, [source.ResultId])], TypeNameClash.Merge));

        Assert.Contains("ThdVsFrequency", error.Message);
        Assert.Contains("FFTSpectrum", error.Message);
        Assert.Equal(0, await bench.Destination.CountAsync("measurement_result"));
    }

    [Fact]
    public async Task Copy_WithSkip_CopiesNoneOfThatTypesResults()
    {
        await using var bench = await TransferBench.CreateAsync();
        await bench.Destination.Projects.CreateTypeAsync(bench.Destination.ProjectId, "FFTSpectrum", "Amp", null, "{}", CancellationToken.None);
        var clashing = await bench.Source.SaveFftAsync("Amp", "theirs");
        var fresh = await bench.Source.SaveFftAsync("Other", "also theirs");

        var report = await bench.TransferAsync(
            [
                new TransferSelection(clashing.TypeId, [clashing.ResultId], TypeNameClash.Skip),
                new TransferSelection(fresh.TypeId, [fresh.ResultId]),
            ]);

        Assert.Equal(1, report.ResultsCopied);
        Assert.Equal(TransferOutcome.Skipped, report.Types.Single(t => t.SourceName == "Amp").Outcome);
        Assert.Null(report.Types.Single(t => t.SourceName == "Amp").DestinationTypeId);
        Assert.Equal("also theirs", await bench.Destination.StringAsync("SELECT name FROM measurement_result;"));
    }

    [Fact]
    public async Task Copy_TakesThePerItemPolicyOverTheRequestDefault()
    {
        await using var bench = await TransferBench.CreateAsync();
        long existingA = await bench.Destination.Projects.CreateTypeAsync(bench.Destination.ProjectId, "FFTSpectrum", "A", null, "{}", CancellationToken.None);
        await bench.Destination.Projects.CreateTypeAsync(bench.Destination.ProjectId, "FFTSpectrum", "B", null, "{}", CancellationToken.None);

        var a = await bench.Source.SaveFftAsync("A", "a");
        var b = await bench.Source.SaveFftAsync("B", "b");

        var report = await bench.TransferAsync(
            [
                new TransferSelection(a.TypeId, [a.ResultId], TypeNameClash.Merge),
                new TransferSelection(b.TypeId, [b.ResultId]),
            ],
            TypeNameClash.Rename);

        Assert.Equal(existingA, report.Types.Single(t => t.SourceName == "A").DestinationTypeId);
        Assert.Equal(TransferOutcome.Merged, report.Types.Single(t => t.SourceName == "A").Outcome);
        Assert.Equal("B (2)", report.Types.Single(t => t.SourceName == "B").DestinationName);
    }

    [Fact]
    public async Task Copy_ASubsetOfResults_CopiesOnlyThose()
    {
        await using var bench = await TransferBench.CreateAsync();
        var first = await bench.Source.SaveFftAsync("Amp", "first");
        var second = await bench.Source.SaveFftAsync("Amp", "second", typeId: null);
        var third = await bench.Source.SaveFftAsync("Amp", "third", typeId: null);

        Assert.Equal(first.TypeId, second.TypeId);

        var report = await bench.TransferAsync([new TransferSelection(first.TypeId, [first.ResultId, third.ResultId])]);

        Assert.Equal(2, report.ResultsCopied);
        Assert.Equal(["first", "third"], (await bench.Destination.StringsAsync("SELECT name FROM measurement_result ORDER BY name;")).Order());
        _ = second;
    }

    [Fact]
    public async Task Copy_AMeasurementWithNoResults_CarriesItsSettingsOnly()
    {
        await using var bench = await TransferBench.CreateAsync();
        var saved = await bench.Source.SaveFftAsync("Amp", "run");

        // "Export the measurement's settings and none of its runs" -- the same thing Save As
        // without results does for a whole project, and a selection a dialog can legitimately send.
        var report = await bench.TransferAsync([new TransferSelection(saved.TypeId, [])]);

        Assert.Equal(0, report.ResultsCopied);
        Assert.Equal(0, report.CapturesCopied);
        Assert.Equal(1, await bench.Destination.CountAsync("measurement_type"));
        Assert.Equal(0, await bench.Destination.CountAsync("measurement_result"));
        Assert.Equal(0, await bench.Destination.CountAsync("capture"));
    }

    [Fact]
    public async Task Copy_NeverTouchesImpulseReference()
    {
        await using var bench = await TransferBench.CreateAsync();
        var saved = await bench.Source.SaveImpulseAsync("Impulse", "ir");

        // A file written by an older build can still carry rows here. They describe that bench's
        // own analyzer, so carrying them into another project is the thing config migration 002
        // moved this table out of the project file to prevent.
        await bench.Source.ExecuteAsync(
            """
            INSERT INTO impulse_reference
              (name, taken_utc, sample_rate_hz, source_fft_size, chirp_start_hz, chirp_end_hz,
               removed_delay_samples, peak_offset, ir_samples, ir, device_serial, is_simulated)
            VALUES ('their loopback', '2026-01-01T00:00:00.000Z', 48000.0, 65536, 20.0, 20000.0,
                    0.0, 0, 1, zeroblob(4), 'SIM-001', 1);
            """);

        await bench.TransferAsync([new TransferSelection(saved.TypeId, [saved.ResultId])]);

        Assert.Equal(1, await bench.Source.CountAsync("impulse_reference"));
        Assert.Equal(0, await bench.Destination.CountAsync("impulse_reference"));
    }

    [Fact]
    public async Task Copy_LeavesTheSourceUnchanged()
    {
        await using var bench = await TransferBench.CreateAsync();
        var fft = await bench.Source.SaveFftAsync("FFT", "spectrum");
        var impulse = await bench.Source.SaveImpulseAsync("Impulse", "ir");

        string[] tables = ["project", "measurement_type", "measurement_result", "capture", .. CaptureCascade.ChildTables];
        var before = new Dictionary<string, long>();
        foreach (string table in tables)
            before[table] = await bench.Source.CountAsync(table);

        await bench.TransferAsync(
            [new TransferSelection(fft.TypeId, [fft.ResultId]), new TransferSelection(impulse.TypeId, [impulse.ResultId])]);

        // The source is attached read-write, because SQLite gives an attached database the main
        // connection's flags. Nothing writes to it, and this is what says so.
        foreach (string table in tables)
            Assert.Equal(before[table], await bench.Source.CountAsync(table));

        Assert.Null(await SqliteConnectionFactory.QuickCheckAsync(bench.Source.Path, CancellationToken.None));
    }

    [Fact]
    public async Task ListContents_WithNoResultsToReport_StillAnswers()
    {
        // Reported from the app on 2026-10-17: Export… showed "There are no measurements here" and
        // a Dapper materialisation error, on a project that simply had nothing saved in it yet.
        //
        // Both queries below carry a *computed* column -- a correlated COUNT, and a SUM over a CTE
        // -- and a computed column has no declared type. Dapper builds its deserializer from the
        // reader's schema BEFORE it reads a row, and with no row for Microsoft.Data.Sqlite to infer
        // from, GetFieldType falls back to byte[]. The record's `long` cannot match that, and the
        // exception names the whole constructor signature rather than the one column that is wrong
        // -- the same trap ListResultsAsync's IFNULL guards document, arriving from the empty-set
        // direction, where IFNULL cannot help because there is no value to coerce.
        await using var bench = await TransferBench.CreateAsync();

        // Nothing at all: a project the moment it is created.
        Assert.Empty(await bench.Source.ContentsAsync());

        // And the commoner case: measurements configured, none of them run yet.
        await bench.Source.Projects.CreateTypeAsync(bench.Source.ProjectId, "FFTSpectrum", "Amp", null, "{}", CancellationToken.None);
        await bench.Source.Projects.CreateTypeAsync(bench.Source.ProjectId, "ThdVsFrequency", "Sweep", null, "{}", CancellationToken.None);

        var contents = await bench.Source.ContentsAsync();
        Assert.Equal(["Amp", "Sweep"], contents.Select(t => t.Name));
        Assert.All(contents, t => Assert.Empty(t.Results));
    }

    [Fact]
    public async Task ListContents_HidesTrashedRows_AndSizesGrowWithAStoredBlob()
    {
        await using var bench = await TransferBench.CreateAsync();
        var fft = await bench.Source.SaveFftAsync("FFT", "spectrum");
        var simulation = await bench.Source.SaveSimulationAsync("Sim", "ltspice");
        var trashedResult = await bench.Source.SaveFftAsync("FFT", "deleted", typeId: null);
        var trashedType = await bench.Source.SaveFftAsync("Gone", "under a deleted measurement");

        await bench.Source.Repository.TrashResultAsync(trashedResult.ResultId, CancellationToken.None);
        await bench.Source.Projects.TrashTypeAsync(trashedType.TypeId, CancellationToken.None);

        await using var connection = await SqliteConnectionFactory.OpenAtAsync(bench.Source.Path, create: false, CancellationToken.None);
        var contents = await ProjectTransferRepository.ListContentsAsync(connection, bench.Source.ProjectId, CancellationToken.None);

        Assert.Equal(["FFT", "Sim"], contents.Select(t => t.Name));
        Assert.Equal(["spectrum"], contents.Single(t => t.Name == "FFT").Results.Select(r => r.Name));

        var fftResult = contents.Single(t => t.Name == "FFT").Results.Single();
        Assert.Equal(fft.ResultId, fftResult.ResultId);
        Assert.Equal(1, fftResult.Captures);
        Assert.True(fftResult.ApproximateBytes > 0, "a stored spectrum has blob bytes");

        // The simulation carries its samples as well as its spectrum, so it is the larger of the
        // two -- which is the whole reason the dialog shows a size at all.
        var simResult = contents.Single(t => t.Name == "Sim").Results.Single();
        Assert.True(
            simResult.ApproximateBytes > fftResult.ApproximateBytes,
            $"simulation {simResult.ApproximateBytes} should exceed FFT {fftResult.ApproximateBytes}");
        Assert.Equal(MeasurementSource.Simulation, simResult.Source);
        _ = trashedType;
    }

    // ---- bench ---------------------------------------------------------------------------------

    /// <summary>Two migrated project files, each with one project row, and the copy between them.</summary>
    private sealed class TransferBench : IAsyncDisposable
    {
        public const double SimulationPcmRateHz = 172_345.678_9;

        public required ProjectFile Source { get; init; }
        public required ProjectFile Destination { get; init; }

        public static async Task<TransferBench> CreateAsync() =>
            new()
            {
                Source = await ProjectFile.CreateAsync("Theirs"),
                Destination = await ProjectFile.CreateAsync("Mine"),
            };

        public Task<TransferReport> TransferAsync(
            IReadOnlyList<TransferSelection> selection, TypeNameClash onClash = TypeNameClash.Rename) =>
            ProjectTransferRepository.TransferAsync(
                Destination.Path, Source.Path, Destination.ProjectId, selection, onClash, CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            TestDb.Cleanup(Source.Path);
            TestDb.Cleanup(Destination.Path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SavedResult(long TypeId, long ResultId);

    /// <summary>One project file with the repositories pointed at it, plus the save builders.</summary>
    private sealed class ProjectFile
    {
        private static int _counter;

        public required string Path { get; init; }
        public required long ProjectId { get; init; }
        public required ProjectRepository Projects { get; init; }
        public required MeasurementRepository Repository { get; init; }

        public static async Task<ProjectFile> CreateAsync(string projectName)
        {
            string path = TestDb.CreateTempPath();
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            var projects = new ProjectRepository(factory);
            long projectId = await projects.CreateProjectAsync(projectName, $"{projectName} notes", CancellationToken.None);

            return new ProjectFile
            {
                Path = path,
                ProjectId = projectId,
                Projects = projects,
                Repository = new MeasurementRepository(factory),
            };
        }

        /// <summary>
        /// A measurement of <paramref name="kind"/> named <paramref name="typeName"/>, created on
        /// first use and reused afterwards — so two results can be saved under one measurement by
        /// naming it twice.
        /// </summary>
        private async Task<long> TypeAsync(string typeName, string kind, string paramsJson = "{}")
        {
            var existing = (await Projects.ListTypesAsync(ProjectId, CancellationToken.None))
                .FirstOrDefault(t => t.Name == typeName);
            return existing?.Id
                ?? await Projects.CreateTypeAsync(ProjectId, kind, typeName, null, paramsJson, CancellationToken.None);
        }

        public async Task<SavedResult> SaveFftAsync(string typeName, string resultName, long? typeId = null)
        {
            long type = typeId ?? await TypeAsync(typeName, "FFTSpectrum");
            int seed = Interlocked.Increment(ref _counter);

            var dbv = new double[257];
            var random = new Random(seed);
            for (int i = 0; i < dbv.Length; i++)
                dbv[i] = random.NextDouble() * 200.0 - 180.0;

            long resultId = await Repository.SaveResultAsync(
                new MeasurementSaveRequest(
                    type, resultName, "notes", """{"fftSize":512}""",
                    "QA403 (Sim)", "SIM-001", "1.0.0", IsSimulated: true,
                    SampleRateHz: 48000.0, FftSize: 512, Channels: 1,
                    InRangeDbv: 12.0, OutRangeDbv: 8.0, GenJson: """{"left":{"enabled":true}}""",
                    [new StoredSpectrumInput(0, WindowType.Hann, new WindowSums(256.0, 192.0, 512), 48000.0, 93.75, SpectrumNorm.Amplitude, dbv)],
                    [new MeasurementMetric(0, "fundamental_hz", 1000.0, "Hz"), new MeasurementMetric(0, "thd_percent", 0.01, "%")]),
                CancellationToken.None);

            return new SavedResult(type, resultId);
        }

        /// <summary>A sweep: one capture per point, each with its own <c>capture_preview</c> rows.</summary>
        public async Task<SavedResult> SaveSweepAsync(string typeName, string resultName, int points)
        {
            long type = await TypeAsync(typeName, "ThdVsFrequency");

            var captures = Enumerable.Range(0, points).Select(i => new SweepCaptureInput(
                i, 100.0 * (i + 1),
                [new MeasurementMetric(0, "fundamental_hz", 100.0 * (i + 1), "Hz"), new MeasurementMetric(0, "thd_percent", 0.01 * (i + 1), "%")],
                [new CapturePreviewInput(0, CapturePreviewKind.Fft, [1f, 2f, 3f], [-100f, -99f, -98f], [-90f, -89f, -88f])]))
                .ToList();

            long resultId = await Repository.SaveSweepResultAsync(
                new SweepSaveRequest(
                    type, resultName, "notes", """{"startHz":20}""",
                    "QA403 (Sim)", "SIM-001", "1.0.0", IsSimulated: true,
                    SampleRateHz: 48000.0, FftSize: 512, Channels: 1,
                    InRangeDbv: 12.0, OutRangeDbv: 8.0, captures),
                CancellationToken.None);

            return new SavedResult(type, resultId);
        }

        /// <summary>A chirp response: <c>response_trace</c> rows and nothing per point.</summary>
        public async Task<SavedResult> SaveResponseAsync(string typeName, string resultName)
        {
            long type = await TypeAsync(typeName, "FrequencyResponse");

            long resultId = await Repository.SaveResponseResultAsync(
                new ResponseSaveRequest(
                    type, resultName, "notes", """{"fftSize":512}""",
                    "QA403 (Sim)", "SIM-001", "1.0.0", IsSimulated: true,
                    SampleRateHz: 48000.0, FftSize: 512, Channels: 2,
                    InRangeDbv: 12.0, OutRangeDbv: 8.0,
                    [BuildTrace(ResponseTraceKind.Left), BuildTrace(ResponseTraceKind.Gain)]),
                CancellationToken.None);

            return new SavedResult(type, resultId);
        }

        private static ResponseTraceInput BuildTrace(string kind) =>
            new(kind, 20.0, 20000.0, 0.5, 1000.0,
                new ResponseBandwidth(20.0, 20000.0, false, false),
                new ResponseBandwidth(15.0, 25000.0, false, false),
                [20f, 200f, 2000f], [-1f, -0.5f, -1f], [-0.9f, -0.4f, -0.9f], [0f, -5f, -45f], [0.99f, 1f, 0.98f],
                ReferenceDb: 0.0, ReferenceMode: "BandMedian",
                GroupDelayS: [1e-4f, 1e-4f, 1e-4f], PhaseDelayS: [2e-4f, 2e-4f, 2e-4f]);

        /// <summary>An impulse response: a trace, an <c>impulse_result</c> and two harmonic curves.</summary>
        public async Task<SavedResult> SaveImpulseAsync(string typeName, string resultName)
        {
            long type = await TypeAsync(typeName, "ImpulseResponse", """{"referenceCalibrationId":17}""");

            long resultId = await Repository.SaveImpulseResultAsync(
                new ImpulseSaveRequest(
                    type, resultName, "notes", """{"referenceCalibrationId":17}""",
                    "QA403 (Sim)", "SIM-001", "1.0.0", IsSimulated: true,
                    SampleRateHz: 48000.0, FftSize: 512, Channels: 2,
                    InRangeDbv: 12.0, OutRangeDbv: 8.0,
                    [BuildTrace(ResponseTraceKind.Gain)],
                    [new ImpulseTraceInput(ResponseTraceKind.Gain, 48000.0, 12, 0.9, 1.0, 5.0, "Hann", 200.0, "StoredLoopback", "their loopback", 0.5, [0f, 0.9f, 0.1f])],
                    [
                        new HarmonicCurveInput(2, 10000.0, [100f, 1000f], [-80f, -85f]),
                        new HarmonicCurveInput(3, 6000.0, [100f, 1000f], [-90f, -95f]),
                    ]),
                CancellationToken.None);

            return new SavedResult(type, resultId);
        }

        /// <summary>An imported simulation: the one result shape that stores samples in <c>capture.pcm</c>.</summary>
        public async Task<SavedResult> SaveSimulationAsync(string typeName, string resultName)
        {
            long type = await TypeAsync(typeName, "FFTSpectrum");

            var dbv = new double[257];
            for (int i = 0; i < dbv.Length; i++)
                dbv[i] = -100.0 + i * 0.1;

            long resultId = await Repository.SaveSimulationResultAsync(
                new SimulationSaveRequest(
                    type, resultName, "notes", """{"fftSize":512}""",
                    AnalysisRateHz: 48000.0, FftSize: 512,
                    PcmSampleRateHz: TransferBench.SimulationPcmRateHz,
                    [Enumerable.Range(0, 4096).Select(i => Math.Sin(i * 0.01)).ToArray()],
                    [new StoredSpectrumInput(0, WindowType.Hann, new WindowSums(256.0, 192.0, 512), 48000.0, 93.75, SpectrumNorm.Amplitude, dbv)],
                    [new MeasurementMetric(0, "fundamental_hz", 1000.0, "Hz")]),
                CancellationToken.None);

            return new SavedResult(type, resultId);
        }

        // ---- reads, on their own connections so nothing is cached ------------------------------

        public Task<long> CountAsync(string table) => ScalarAsync($"SELECT COUNT(*) FROM {table};");

        public async Task<long> ScalarAsync(string sql, object? param = null)
        {
            await using var connection = await SqliteConnectionFactory.OpenAtAsync(Path, create: false, CancellationToken.None);
            return await connection.ExecuteScalarAsync<long>(sql, param);
        }

        public async Task<double> DoubleAsync(string sql, object? param = null)
        {
            await using var connection = await SqliteConnectionFactory.OpenAtAsync(Path, create: false, CancellationToken.None);
            return await connection.ExecuteScalarAsync<double>(sql, param);
        }

        public async Task<string?> StringAsync(string sql, object? param = null)
        {
            await using var connection = await SqliteConnectionFactory.OpenAtAsync(Path, create: false, CancellationToken.None);
            return await connection.ExecuteScalarAsync<string?>(sql, param);
        }

        public async Task<IReadOnlyList<string>> StringsAsync(string sql, object? param = null)
        {
            await using var connection = await SqliteConnectionFactory.OpenAtAsync(Path, create: false, CancellationToken.None);
            return (await connection.QueryAsync<string>(sql, param)).ToList();
        }

        public async Task<byte[]> BlobAsync(string sql, object? param = null)
        {
            await using var connection = await SqliteConnectionFactory.OpenAtAsync(Path, create: false, CancellationToken.None);
            return await connection.ExecuteScalarAsync<byte[]>(sql, param) ?? [];
        }

        public async Task<IReadOnlyList<ProjectContentsType>> ContentsAsync()
        {
            await using var connection = await SqliteConnectionFactory.OpenAtAsync(Path, create: false, CancellationToken.None);
            return await ProjectTransferRepository.ListContentsAsync(connection, ProjectId, CancellationToken.None);
        }

        public async Task ExecuteAsync(string sql, object? param = null)
        {
            await using var connection = await SqliteConnectionFactory.OpenAtAsync(Path, create: false, CancellationToken.None);
            await connection.ExecuteAsync(sql, param);
        }
    }
}
