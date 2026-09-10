using Microsoft.Data.Sqlite;

namespace Qa403.Data.Tests;

/// <summary>
/// A frequency response stored as one <c>measurement_result</c> owning a single <c>capture</c> whose
/// curves live in <c>response_trace</c> — the shape migration 006 adds.
/// </summary>
public class ResponseRepositoryTests
{
    private const int Columns = 64;

    [Fact]
    public async Task SaveResponse_ThenReadBackOnAFreshConnection_ReturnsEveryCurve()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateResponseTypeAsync(factory);

            var request = BuildRequest(typeId);
            long resultId = await new MeasurementRepository(factory).SaveResponseResultAsync(request, CancellationToken.None);

            // A fresh factory and repository, standing in for a full app restart.
            SqliteConnection.ClearAllPools();
            var reloaded = new MeasurementRepository(new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path }));

            var traces = await reloaded.GetResponseTracesAsync(resultId, CancellationToken.None);

            Assert.Equal(3, traces.Count);
            Assert.Equal(
                [ResponseTraceKind.Left, ResponseTraceKind.Right, ResponseTraceKind.Gain],
                traces.Select(t => t.Kind));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveResponse_RoundTripsEveryArrayBitForBit()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateResponseTypeAsync(factory);

            var request = BuildRequest(typeId);
            var repo = new MeasurementRepository(factory);
            long resultId = await repo.SaveResponseResultAsync(request, CancellationToken.None);

            var stored = (await repo.GetResponseTracesAsync(resultId, CancellationToken.None))
                .Single(t => t.Kind == ResponseTraceKind.Gain);
            var original = request.Traces.Single(t => t.Kind == ResponseTraceKind.Gain);

            // f32 in, f32 out, no quantization anywhere — unlike the spectrum path's i16, so exact
            // equality is the right assertion and a lossy encoding sneaking in would fail here.
            Assert.Equal(original.FreqHz, stored.FreqHz);
            Assert.Equal(original.MagnitudeMinDb, stored.MagnitudeMinDb);
            Assert.Equal(original.MagnitudeMaxDb, stored.MagnitudeMaxDb);
            Assert.Equal(original.PhaseDeg, stored.PhaseDeg);
            Assert.Equal(original.Coherence, stored.Coherence);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveResponse_RoundTripsTheCornersAndTheirClampedFlags()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateResponseTypeAsync(factory);

            var repo = new MeasurementRepository(factory);
            long resultId = await repo.SaveResponseResultAsync(BuildRequest(typeId), CancellationToken.None);

            var stored = (await repo.GetResponseTracesAsync(resultId, CancellationToken.None))
                .Single(t => t.Kind == ResponseTraceKind.Left);

            Assert.Equal(8.8, stored.OneDb.LowerHz, 3);
            Assert.Equal(63200.0, stored.OneDb.UpperHz, 3);
            Assert.False(stored.OneDb.LowerClamped);
            Assert.False(stored.OneDb.UpperClamped);

            // SQLite has no boolean type, so these ride as integers — a per-end flag that came back
            // wrong would turn "still flat where we stopped looking" into a measured corner.
            Assert.Equal(4.4, stored.ThreeDb.LowerHz, 3);
            Assert.True(stored.ThreeDb.UpperClamped);
            Assert.False(stored.ThreeDb.LowerClamped);

            Assert.Equal(34.49, stored.PeakDb, 3);
            Assert.Equal(461.4, stored.PeakHz, 3);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task GetResponseTraces_ForAResultThatIsNotAResponse_IsEmpty()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            var repo = new MeasurementRepository(factory);

            Assert.Empty(await repo.GetResponseTracesAsync(4242, CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task ListResults_FilteredToAResponseType_DoesNotTripTheDapperNullColumnTrap()
    {
        // A response result has no metric rows at all, so filtering the history list to one makes
        // the headline query's correlated fundamental_hz subquery NULL for every row. SQLite types a
        // column from the values it returns, so an all-NULL column has no type, Dapper materializes
        // it as byte[], and binding the row fails with "a parameterless default constructor ... is
        // required" -- an error naming nothing that is actually wrong. generator_dbv already had the
        // IFNULL guard; this kind is what showed fundamental_hz needed it too.
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateResponseTypeAsync(factory);

            var repo = new MeasurementRepository(factory);
            await repo.SaveResponseResultAsync(BuildRequest(typeId), CancellationToken.None);

            var listed = await repo.ListResultsAsync(new MeasurementFilter(MeasurementTypeId: typeId), CancellationToken.None);

            var only = Assert.Single(listed);
            Assert.Equal("loopback", only.Name);
            // No metrics to headline with, which is correct rather than an omission -- a response is
            // described by its curves and its corner frequencies, not by a THD figure.
            Assert.Empty(only.ChannelHeadlines);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// Deleting a response result has to take its <c>response_trace</c> rows with it, and the test
    /// is the <i>next save</i> rather than a row count: foreign keys are off, so an orphan is
    /// invisible until SQLite hands the freed capture rowid to a later insert, whose three trace
    /// rows then collide with it on the UNIQUE (capture_id, kind) index. That is a 500 on
    /// POST /api/frequency-response/save naming a measurement with nothing wrong with it, and it
    /// repeats forever. Exactly the bug migration 005 fixed for capture_preview, one table later.
    /// </summary>
    [Fact]
    public async Task DeleteResponseResult_ThenSaveAnother_DoesNotCollideOnARecycledCaptureRowid()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateResponseTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            long first = await repo.SaveResponseResultAsync(BuildRequest(typeId), CancellationToken.None);
            Assert.True(await repo.DeleteResultAsync(first, CancellationToken.None));

            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                Assert.Equal(0L, await TestDb.ScalarAsync(
                    connection, "SELECT COUNT(*) FROM response_trace WHERE capture_id NOT IN (SELECT id FROM capture);"));
            }

            long second = await repo.SaveResponseResultAsync(BuildRequest(typeId), CancellationToken.None);
            var traces = await repo.GetResponseTracesAsync(second, CancellationToken.None);
            Assert.Equal(3, traces.Count);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>The same cascade one level up: deleting the whole type must not leave traces behind either.</summary>
    [Fact]
    public async Task DeleteResponseType_TakesItsTracesWithIt()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateResponseTypeAsync(factory);

            await new MeasurementRepository(factory).SaveResponseResultAsync(BuildRequest(typeId), CancellationToken.None);
            Assert.True(await new ProjectRepository(factory).DeleteTypeAsync(typeId, CancellationToken.None));

            await using var connection = await factory.OpenAsync(CancellationToken.None);
            Assert.Equal(0L, await TestDb.ScalarAsync(connection, "SELECT COUNT(*) FROM response_trace;"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveResponse_RoundTripsTheBandwidthReference()
    {
        // The level the corners were measured down from is no longer derivable from peak_db — a
        // passband-referenced trace's reference is a median that is nowhere in the stored scalars —
        // so it has to survive the round trip or a reloaded result cannot say what its corners mean,
        // and the chart cannot draw the line they were found against.
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateResponseTypeAsync(factory);

            var repo = new MeasurementRepository(factory);
            long resultId = await repo.SaveResponseResultAsync(BuildRequest(typeId), CancellationToken.None);

            var traces = await repo.GetResponseTracesAsync(resultId, CancellationToken.None);

            Assert.All(traces, t => Assert.Equal(33.75, t.ReferenceDb!.Value, 6));
            Assert.All(traces, t => Assert.Equal("BandMedian", t.ReferenceMode));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveResponse_WithNoReferenceRecorded_ReadsBackAsNull()
    {
        // What a row written by an older build reads as. Null rather than a substituted peak: the
        // reader draws no reference line for it, which is honest, where inventing one would assert
        // something the row does not say.
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateResponseTypeAsync(factory);

            var request = BuildRequest(typeId);
            var withoutReference = request with
            {
                Traces = [.. request.Traces.Select(t => t with { ReferenceDb = null, ReferenceMode = null })],
            };

            var repo = new MeasurementRepository(factory);
            long resultId = await repo.SaveResponseResultAsync(withoutReference, CancellationToken.None);

            var traces = await repo.GetResponseTracesAsync(resultId, CancellationToken.None);

            Assert.All(traces, t => Assert.Null(t.ReferenceDb));
            Assert.All(traces, t => Assert.Null(t.ReferenceMode));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    private static async Task<long> CreateResponseTypeAsync(SqliteConnectionFactory factory)
    {
        var projectRepo = new ProjectRepository(factory);
        long projectId = await projectRepo.CreateProjectAsync("GM70", null, CancellationToken.None);
        return await projectRepo.CreateTypeAsync(projectId, "FrequencyResponse", "response", null, "{}", CancellationToken.None);
    }

    private static ResponseSaveRequest BuildRequest(long typeId)
    {
        var freqHz = new float[Columns];
        var magMin = new float[Columns];
        var magMax = new float[Columns];
        var phase = new float[Columns];
        var coherence = new float[Columns];

        for (int i = 0; i < Columns; i++)
        {
            freqHz[i] = (float)(20.0 * Math.Pow(2.0, i / 6.0));
            magMin[i] = -0.5f - (i * 0.01f);
            magMax[i] = 0.5f - (i * 0.01f);
            phase[i] = -i * 3.0f;
            coherence[i] = 1.0f - (i * 0.001f);
        }

        ResponseTraceInput Trace(string kind) => new(
            kind,
            StartHz: 4.0,
            EndHz: 22800.0,
            PeakDb: 34.49,
            PeakHz: 461.4,
            OneDb: new ResponseBandwidth(8.8, 63200.0, false, false),
            ThreeDb: new ResponseBandwidth(4.4, 90800.0, false, true),
            freqHz, magMin, magMax, phase, coherence,
            ReferenceDb: 33.75,
            ReferenceMode: "BandMedian");

        return new ResponseSaveRequest(
            typeId, "loopback", "notes", "{}",
            "QA403", "1077527945", "60", IsSimulated: false,
            SampleRateHz: 192000.0, FftSize: 131072, Channels: 2,
            InRangeDbv: 0.0, OutRangeDbv: -12.0,
            [Trace(ResponseTraceKind.Left), Trace(ResponseTraceKind.Right), Trace(ResponseTraceKind.Gain)]);
    }
}
