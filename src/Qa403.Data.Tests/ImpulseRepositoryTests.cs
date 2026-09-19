using Microsoft.Data.Sqlite;

namespace Qa403.Data.Tests;

/// <summary>
/// An impulse response stored as one <c>measurement_result</c> owning a single <c>capture</c> whose
/// children are <c>response_trace</c> (the gated magnitude and phase, shared with the frequency
/// response), <c>impulse_result</c> and <c>impulse_harmonic</c> — the shape migration 009 adds.
/// </summary>
public class ImpulseRepositoryTests
{
    private const int Columns = 64;
    private const int IrSamples = 1009;

    [Fact]
    public async Task SaveImpulse_ThenReadBackOnAFreshConnection_ReturnsEverything()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateImpulseTypeAsync(factory);

            long resultId = await new MeasurementRepository(factory)
                .SaveImpulseResultAsync(BuildRequest(typeId), CancellationToken.None);

            // A fresh factory and repository, standing in for a full app restart.
            SqliteConnection.ClearAllPools();
            var reloaded = new MeasurementRepository(new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path }));

            var stored = await reloaded.GetImpulseResultAsync(resultId, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal([ImpulseTraceKind.Left, ImpulseTraceKind.Gain], stored.Impulses.Select(i => i.Kind));
            Assert.Equal([2, 3, 4, 5], stored.Harmonics.Select(h => h.Order));

            // The gated curves are response_trace rows, read by the frequency response's own reader.
            var traces = await reloaded.GetResponseTracesAsync(resultId, CancellationToken.None);
            Assert.Equal(3, traces.Count);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The impulse response is stored at full resolution and unquantized, unlike every curve beside
    /// it — a decimated or requantized one could not be re-gated or re-transformed, which is the only
    /// reason to keep it. f32 in, f32 out, so exact equality is the right assertion and a lossy
    /// encoding sneaking in would fail here.
    /// </summary>
    [Fact]
    public async Task SaveImpulse_RoundTripsTheIrBitForBit_AndKeepsItsPeakIndex()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateImpulseTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            var request = BuildRequest(typeId);
            long resultId = await repo.SaveImpulseResultAsync(request, CancellationToken.None);

            var stored = await repo.GetImpulseResultAsync(resultId, CancellationToken.None);
            var left = stored!.Impulses.Single(i => i.Kind == ImpulseTraceKind.Left);
            var original = request.Impulses.Single(i => i.Kind == ImpulseTraceKind.Left);

            Assert.Equal(original.Ir, left.Ir);
            Assert.Equal(IrSamples, left.Ir.Length);

            // Where the impulse sits inside that array. Stored rather than re-found: the array keeps
            // pre-impulse ringing, so a reader assuming index 0 would mis-time everything after it.
            Assert.Equal(original.PeakIndex, left.PeakIndex);
            Assert.Equal(original.GateWindow, left.GateWindow);
            Assert.Equal(original.ReferenceMode, left.ReferenceMode);
            Assert.Equal(original.ReferenceName, left.ReferenceName);
            Assert.Equal(original.LatencySamples, left.LatencySamples);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The guard this schema has needed twice already. Deleting a result must take its
    /// <c>impulse_result</c> and <c>impulse_harmonic</c> rows with it, and the assertion is the
    /// <i>next save</i> rather than a row count: foreign keys are off, so an orphan is invisible
    /// until SQLite hands the freed capture rowid to a later insert, whose own rows then collide
    /// with it on the UNIQUE index. A row count passes against the bug on a fresh database.
    /// </summary>
    [Fact]
    public async Task DeleteImpulseResult_ThenSaveAnother_DoesNotCollideOnARecycledCaptureRowid()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateImpulseTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            long first = await repo.SaveImpulseResultAsync(BuildRequest(typeId), CancellationToken.None);
            Assert.True(await repo.DeleteResultAsync(first, CancellationToken.None));

            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                Assert.Equal(0L, await TestDb.ScalarAsync(
                    connection, "SELECT COUNT(*) FROM impulse_result WHERE capture_id NOT IN (SELECT id FROM capture);"));
                Assert.Equal(0L, await TestDb.ScalarAsync(
                    connection, "SELECT COUNT(*) FROM impulse_harmonic WHERE capture_id NOT IN (SELECT id FROM capture);"));
            }

            long second = await repo.SaveImpulseResultAsync(BuildRequest(typeId), CancellationToken.None);
            var stored = await repo.GetImpulseResultAsync(second, CancellationToken.None);
            Assert.Equal(2, stored!.Impulses.Count);
            Assert.Equal(4, stored.Harmonics.Count);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>The same cascade one level up: deleting the whole type must not leave rows behind either.</summary>
    [Fact]
    public async Task DeleteImpulseType_TakesItsImpulsesAndHarmonicsWithIt()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateImpulseTypeAsync(factory);

            await new MeasurementRepository(factory).SaveImpulseResultAsync(BuildRequest(typeId), CancellationToken.None);
            Assert.True(await new ProjectRepository(factory).DeleteTypeAsync(typeId, CancellationToken.None));

            await using var connection = await factory.OpenAsync(CancellationToken.None);
            Assert.Equal(0L, await TestDb.ScalarAsync(connection, "SELECT COUNT(*) FROM impulse_result;"));
            Assert.Equal(0L, await TestDb.ScalarAsync(connection, "SELECT COUNT(*) FROM impulse_harmonic;"));
            Assert.Equal(0L, await TestDb.ScalarAsync(connection, "SELECT COUNT(*) FROM response_trace;"));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    private static async Task<long> CreateImpulseTypeAsync(SqliteConnectionFactory factory)
    {
        var projectRepo = new ProjectRepository(factory);
        long projectId = await projectRepo.CreateProjectAsync("GM70", null, CancellationToken.None);
        return await projectRepo.CreateTypeAsync(projectId, "ImpulseResponse", "impulse", null, "{}", CancellationToken.None);
    }

    private static ImpulseSaveRequest BuildRequest(long typeId)
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
            kind, StartHz: 4.0, EndHz: 22800.0, PeakDb: 0.02, PeakHz: 1000.0,
            OneDb: new ResponseBandwidth(8.8, 21000.0, false, false),
            ThreeDb: new ResponseBandwidth(4.4, 22000.0, false, true),
            freqHz, magMin, magMax, phase, coherence);

        var ir = new float[IrSamples];
        for (int i = 0; i < IrSamples; i++)
            ir[i] = (float)(Math.Sin(i * 0.31) * Math.Exp(-i / 120.0));
        ir[48] = 1.0f;

        ImpulseTraceInput Impulse(string kind) => new(
            kind, SampleRateHz: 48000.0, PeakIndex: 48, PeakVolts: 1.0,
            GatePreMs: 1.0, GatePostMs: 20.0, GateWindow: "Hann", ResolutionLimitHz: 47.6,
            ReferenceMode: "StoredLoopback", ReferenceName: "bench", LatencySamples: 12.5, ir);

        HarmonicCurveInput Harmonic(int order)
        {
            var fundamentals = new float[Columns];
            var levels = new float[Columns];
            for (int i = 0; i < Columns; i++)
            {
                fundamentals[i] = (float)(20.0 * Math.Pow(2.0, i / 8.0));
                levels[i] = -60.0f - order - (i * 0.1f);
            }

            return new HarmonicCurveInput(order, 22800.0 / order, fundamentals, levels);
        }

        return new ImpulseSaveRequest(
            typeId, "gated response", "notes", "{}",
            "QA403", "1077527945", "60", IsSimulated: false,
            SampleRateHz: 48000.0, FftSize: 32768, Channels: 2,
            InRangeDbv: 0.0, OutRangeDbv: -12.0,
            [Trace(ResponseTraceKind.Left), Trace(ResponseTraceKind.Right), Trace(ResponseTraceKind.Gain)],
            [Impulse(ImpulseTraceKind.Left), Impulse(ImpulseTraceKind.Gain)],
            [Harmonic(2), Harmonic(3), Harmonic(4), Harmonic(5)]);
    }
}
