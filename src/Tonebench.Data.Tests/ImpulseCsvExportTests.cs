using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Tonebench.Data.Tests;

/// <summary>
/// An impulse response saved, read back on a fresh connection, and exported — the whole path the
/// CSV button walks, rather than the renderers against hand-built records.
///
/// <para>The renderers are pinned on their own in <c>CsvExporterTests</c> and the endpoint's choice
/// of renderer in <c>CsvExportShapeTests</c>; what neither of those can catch is the two halves
/// disagreeing about what the database holds. Until 2026-10-09 an impulse result had no CSV arm at
/// all: it fell through to the spectrum renderer, which looks for <c>spectrum</c> rows an impulse
/// result does not have, and answered 404 to a button the history panel renders on every row.</para>
/// </summary>
public class ImpulseCsvExportTests
{
    private const int Columns = 64;
    private const int IrSamples = 1009;
    private const int PeakIndex = 48;
    private const double SampleRateHz = 48000.0;

    /// <summary>
    /// The samples, at full resolution and timed from the peak. This is the part the plain link
    /// returns, and the one thing an impulse result stores that nothing else can reproduce.
    /// </summary>
    [Fact]
    public async Task ExportImpulse_OverARoundTrip_WritesEverySampleTimedFromThePeak()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (detail, stored) = await SaveAndReloadAsync(path);

            string csv = CsvExporter.ExportImpulse(detail, stored);
            string[] data = DataRows(csv);

            Assert.Equal("time_ms,left_volts", data[0]);
            Assert.Equal(IrSamples, data.Length - 1);

            // Full resolution: the stored IR is not decimated, so the row count is the sample count
            // and not a display column budget.
            Assert.Equal(IrSamples, stored.Impulses.Single().Ir.Length);

            // The peak is 48 samples in, so the file opens 1 ms before it and crosses zero there.
            Assert.Equal(-PeakIndex / SampleRateHz * 1000.0, TimeMs(data[1]), 6);
            Assert.Equal(0.0, TimeMs(data[1 + PeakIndex]), 9);
            Assert.Equal(1.0, Volts(data[1 + PeakIndex]), 6);

            // And the last row is the rest of the record after it, not wrapped round.
            Assert.Equal((IrSamples - 1 - PeakIndex) / SampleRateHz * 1000.0, TimeMs(data[^1]), 6);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The gate and the reference are what a gated impulse cannot be read without, and they survive
    /// the round trip into the header comments rather than being lost with the runner's state.
    /// </summary>
    [Fact]
    public async Task ExportImpulse_OverARoundTrip_CarriesTheGateAndTheReference()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (detail, stored) = await SaveAndReloadAsync(path);

            string csv = CsvExporter.ExportImpulse(detail, stored);

            Assert.Contains("# left_gate_pre_ms: 1.0000", csv);
            Assert.Contains("# left_gate_post_ms: 20.0000", csv);
            Assert.Contains("# left_gate_window: Hann", csv);
            Assert.Contains("# left_resolution_limit_hz: 47.6000", csv);
            Assert.Contains("# left_reference_mode: StoredLoopback", csv);
            Assert.Contains("# left_reference_name: bench", csv);
            Assert.Contains("# left_latency_samples: 12.5000", csv);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// Long format, and <c>upper_valid_hz</c> per order in the header: the k-th harmonic is only
    /// observable up to the sweep's end over k, which is a fact about the order and not about any
    /// row.
    /// </summary>
    [Fact]
    public async Task ExportImpulseHarmonics_OverARoundTrip_IsOneRowPerOrderAndPoint()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (detail, stored) = await SaveAndReloadAsync(path);

            string csv = CsvExporter.ExportImpulseHarmonics(detail, stored);
            string[] data = DataRows(csv);

            Assert.Equal("order,fundamental_hz,level_dbc", data[0]);
            Assert.Equal(4 * Columns, data.Length - 1);

            foreach (int order in (int[])[2, 3, 4, 5])
            {
                Assert.Equal(Columns, data.Count(l => l.StartsWith($"{order},", StringComparison.Ordinal)));
                Assert.Contains(
                    $"# h{order}_upper_valid_hz: {(22800.0 / order).ToString("F4", CultureInfo.InvariantCulture)}",
                    csv);
            }
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The third part is not a new renderer: an impulse result's gated magnitude and phase are
    /// <c>response_trace</c> rows, so they go out through the frequency response's own exporter and
    /// come back with the same columns a response does.
    /// </summary>
    [Fact]
    public async Task TheCurvesPart_GoesOutThroughTheResponseExporter()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long resultId = await SaveAsync(factory);
            var repo = new MeasurementRepository(factory);

            var detail = await repo.GetResultAsync(resultId, CancellationToken.None);
            var traces = await repo.GetResponseTracesAsync(resultId, CancellationToken.None);

            string csv = CsvExporter.ExportResponse(detail!, traces);
            string header = DataRows(csv)[0];

            Assert.StartsWith("freq_hz", header);
            Assert.Contains("left_max_db", header);
            Assert.Contains("gain_phase_deg", header);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    private static string[] DataRows(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#'))
            .ToArray();

    private static double TimeMs(string row) =>
        double.Parse(row.Split(',')[0], CultureInfo.InvariantCulture);

    private static double Volts(string row) =>
        double.Parse(row.Split(',')[1], CultureInfo.InvariantCulture);

    /// <summary>Saves, drops every pooled connection, and reads back through a new factory — a stand-in for a restart.</summary>
    private static async Task<(MeasurementDetail Detail, StoredImpulseResult Stored)> SaveAndReloadAsync(string path)
    {
        var factory = await TestDb.CreateMigratedFactoryAsync(path);
        long resultId = await SaveAsync(factory);

        SqliteConnection.ClearAllPools();
        var reloaded = new MeasurementRepository(new SqliteConnectionFactory(new TonebenchDatabaseOptions { Path = path }));

        var detail = await reloaded.GetResultAsync(resultId, CancellationToken.None);
        var stored = await reloaded.GetImpulseResultAsync(resultId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(stored);
        return (detail, stored);
    }

    private static async Task<long> SaveAsync(SqliteConnectionFactory factory)
    {
        var projects = new ProjectRepository(factory);
        long projectId = await projects.CreateProjectAsync("Bench", null, CancellationToken.None);
        long typeId = await projects.CreateTypeAsync(
            projectId, "ImpulseResponse", "Driver impulse", null, """{"gatePreMs":1}""", CancellationToken.None);

        return await new MeasurementRepository(factory)
            .SaveImpulseResultAsync(BuildRequest(typeId), CancellationToken.None);
    }

    /// <summary>
    /// Deliberately the same shape <c>ImpulseRepositoryTests</c> saves — two impulses, three gated
    /// curves and four harmonic orders — so this exercises what that test already proves round-trips.
    /// </summary>
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

        ir[PeakIndex] = 1.0f;

        var impulse = new ImpulseTraceInput(
            ImpulseTraceKind.Left, SampleRateHz, PeakIndex, PeakVolts: 1.0,
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
            typeId, "pass 1", null, """{"gatePreMs":1,"gatePostMs":20}""",
            DeviceName: "QA403", Serial: "4039", FwVer: "60", IsSimulated: false,
            SampleRateHz: SampleRateHz, FftSize: 262144, Channels: 2,
            InRangeDbv: 6.0, OutRangeDbv: -2.0,
            [Trace(ResponseTraceKind.Left), Trace(ResponseTraceKind.Right), Trace(ResponseTraceKind.Gain)],
            [impulse],
            [Harmonic(2), Harmonic(3), Harmonic(4), Harmonic(5)]);
    }
}
