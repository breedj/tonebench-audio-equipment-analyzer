using Dapper;
using Microsoft.Data.Sqlite;

namespace Qa403.Data.Tests;

/// <summary>
/// A sweep stored as one <c>measurement_result</c> owning N <c>capture</c> rows — the shape
/// migration 003 kept <c>capture.seq_index</c> for, exercised for the first time here.
/// </summary>
public class SweepRepositoryTests
{
    private const int PointCount = 12;

    [Fact]
    public async Task SaveSweep_ThenReadBackOnAFreshConnection_ReturnsEveryPointInSweepOrder()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateSweepTypeAsync(factory);

            var request = BuildSweepRequest(typeId);
            long resultId = await new MeasurementRepository(factory).SaveSweepResultAsync(request, CancellationToken.None);

            // A fresh factory and repository, standing in for a full app restart.
            SqliteConnection.ClearAllPools();
            var reloaded = new MeasurementRepository(new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path }));

            var points = await reloaded.GetSweepPointsAsync(resultId, CancellationToken.None);

            Assert.Equal(PointCount, points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                Assert.Equal(i, points[i].SeqIndex);
                Assert.Equal(request.Captures[i].FreqHz, points[i].FreqHz, 6);
            }

            // Ascending in frequency, not just in seq_index.
            for (int i = 1; i < points.Count; i++)
                Assert.True(points[i].FreqHz > points[i - 1].FreqHz);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveSweep_StereoPoints_KeepsBothChannelsMetricsSeparate()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateSweepTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            long resultId = await repo.SaveSweepResultAsync(BuildSweepRequest(typeId), CancellationToken.None);
            var points = await repo.GetSweepPointsAsync(resultId, CancellationToken.None);

            var first = points[0];
            Assert.Equal(2, first.MetricsByChannel.Count);

            // BuildSweepRequest gives the right channel exactly twice the left's THD, so a mix-up
            // between the two shows up as a value, not just a missing key.
            Assert.Equal(first.MetricsByChannel[0]["thd_percent"] * 2.0, first.MetricsByChannel[1]["thd_percent"], 9);
            Assert.Equal(first.FreqHz, first.MetricsByChannel[0]["fundamental_hz"], 6);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveSweep_WithNoPreviews_ReadsBackWithNullPreviewsByChannel()
    {
        // BuildSweepRequest's captures pass no Previews at all (the default) -- a sweep saved
        // before this feature existed, or one the pipeline didn't capture previews for. Must round
        // trip cleanly rather than throwing on a missing capture_preview row.
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateSweepTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            long resultId = await repo.SaveSweepResultAsync(BuildSweepRequest(typeId), CancellationToken.None);
            var points = await repo.GetSweepPointsAsync(resultId, CancellationToken.None);

            Assert.Null(points[0].PreviewsByChannel);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveSweep_WithPreviews_ReadsBackTheDecimatedTraces()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateSweepTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            var request = BuildSweepRequest(typeId);
            var withPreviews = request with
            {
                Captures = [
                    request.Captures[0] with
                    {
                        Previews = [
                            new CapturePreviewInput(0, CapturePreviewKind.Fft, [100f, 200f], [-90f, -80f], [-85f, -75f]),
                            new CapturePreviewInput(0, CapturePreviewKind.Scope, [0f, 0.0005f], [-1f, -0.5f], [1f, 0.5f]),
                            new CapturePreviewInput(1, CapturePreviewKind.Fft, [100f, 200f], [-91f, -81f], [-86f, -76f]),
                        ],
                    },
                    .. request.Captures.Skip(1),
                ],
            };

            long resultId = await repo.SaveSweepResultAsync(withPreviews, CancellationToken.None);
            var points = await repo.GetSweepPointsAsync(resultId, CancellationToken.None);

            var previews = points[0].PreviewsByChannel;
            Assert.NotNull(previews);
            Assert.Equal(2, previews!.Count); // channels 0 and 1

            var ch0Fft = previews[0][CapturePreviewKind.Fft];
            Assert.Equal<float>([100f, 200f], ch0Fft.X);
            Assert.Equal<float>([-90f, -80f], ch0Fft.Min);
            Assert.Equal<float>([-85f, -75f], ch0Fft.Max);

            var ch0Scope = previews[0][CapturePreviewKind.Scope];
            Assert.Equal<float>([0f, 0.0005f], ch0Scope.X);

            // Channel 1 only got an FFT preview above -- must not silently pick up channel 0's
            // scope trace or vice versa.
            Assert.Single(previews[1]);
            Assert.True(previews[1].ContainsKey(CapturePreviewKind.Fft));

            // The other 11 points never got Previews at all -- must not throw, must read back null.
            Assert.Null(points[1].PreviewsByChannel);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task DeleteResult_ForASweepWithPreviews_RemovesTheCapturePreviewRowsToo()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateSweepTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            var request = BuildSweepRequest(typeId);
            var withPreview = request with
            {
                Captures = [
                    request.Captures[0] with
                    {
                        Previews = [new CapturePreviewInput(0, CapturePreviewKind.Fft, [100f], [-90f], [-85f])],
                    },
                    .. request.Captures.Skip(1),
                ],
            };
            long resultId = await repo.SaveSweepResultAsync(withPreview, CancellationToken.None);

            bool deleted = await repo.DeleteResultAsync(resultId, CancellationToken.None);
            Assert.True(deleted);

            // Checked directly against the table, not via GetSweepPointsAsync -- that query joins
            // through `capture`, which is already gone either way, so it would read empty even if
            // DeleteResultAsync forgot to also delete capture_preview and left it orphaned.
            await using var connection = await factory.OpenAsync(CancellationToken.None);
            long remaining = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM capture_preview;");
            Assert.Equal(0, remaining);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task GetSweepPoints_ForAResultThatIsNotASweep_ReturnsNothing()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            var repo = new MeasurementRepository(factory);

            var points = await repo.GetSweepPointsAsync(9999, CancellationToken.None);

            Assert.Empty(points);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveSweep_DoesNotDisturbTheSingleCaptureListing()
    {
        // ListResultsAsync/GetResultAsync both join `capture ... AND c.seq_index = 0`. A sweep has
        // a seq_index 0 capture too, so it must still list rather than vanish from the history.
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateSweepTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            long resultId = await repo.SaveSweepResultAsync(BuildSweepRequest(typeId), CancellationToken.None);

            var listed = await repo.ListResultsAsync(new MeasurementFilter(), CancellationToken.None);

            Assert.Contains(listed, r => r.ResultId == resultId);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveSweep_HeadlineThd_ComesFromTheFirstPointAtOrAbove1kHz()
    {
        // The history list quotes one THD per channel, and for a sweep it must be the 1 kHz figure
        // -- not seq_index 0, which is the sweep's lowest frequency and its worst THD.
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateSweepTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            var request = BuildSweepRequest(typeId);
            long resultId = await repo.SaveSweepResultAsync(request, CancellationToken.None);

            var listed = await repo.ListResultsAsync(new MeasurementFilter(), CancellationToken.None);
            var headlines = listed.Single(r => r.ResultId == resultId).ChannelHeadlines;

            // BuildSweepRequest's grid steps by a half-octave from 100 Hz, so the first point at or
            // above 1 kHz is 1131.37 Hz, three points before the end.
            var atOneKhz = request.Captures.First(c => c.FreqHz >= 1000.0);
            double expected = atOneKhz.Metrics.Single(m => m.Channel == 0 && m.Name == "thd_percent").Value;

            Assert.Equal(expected, headlines[0].ThdPercent, 9);
            Assert.Equal(expected * 2.0, headlines[1].ThdPercent, 9);

            // And explicitly not the lowest-frequency point, which is what the seq_index 0 join gave.
            double atSeqZero = request.Captures[0].Metrics.Single(m => m.Channel == 0 && m.Name == "thd_percent").Value;
            Assert.NotEqual(atSeqZero, headlines[0].ThdPercent, 9);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveSweep_EndingBelow1kHz_HeadlineThdFallsBackToTheHighestPoint()
    {
        // A 20-500 Hz sweep has no 1 kHz point to quote. Reporting nothing would be worse than
        // reporting the nearest thing it measured, so the top of the sweep stands in.
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateSweepTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            var captures = new List<SweepCaptureInput>();
            for (int i = 0; i < 4; i++)
            {
                double freqHz = 20.0 * Math.Pow(2.0, i);   // 20, 40, 80, 160 Hz
                captures.Add(new SweepCaptureInput(i, freqHz, [
                    new MeasurementMetric(0, "fundamental_hz", freqHz, "Hz"),
                    new MeasurementMetric(0, "thd_percent", 0.1 * (i + 1), "%"),
                ]));
            }

            long resultId = await repo.SaveSweepResultAsync(
                new SweepSaveRequest(
                    typeId, "bass sweep", null, "{}", "QA403", "SIM-0001", "sim-1.0", IsSimulated: true,
                    SampleRateHz: 48000, FftSize: 32768, Channels: 1,
                    InRangeDbv: 0, OutRangeDbv: -12, captures),
                CancellationToken.None);

            var listed = await repo.ListResultsAsync(new MeasurementFilter(), CancellationToken.None);
            var headlines = listed.Single(r => r.ResultId == resultId).ChannelHeadlines;

            Assert.Equal(0.4, headlines[0].ThdPercent, 9);   // the 160 Hz point
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task DeleteType_ForASweepWithPreviews_RemovesTheCapturePreviewRowsToo()
    {
        // Regression: DeleteTypeAsync deleted metric/spectrum/capture for every result under the
        // type but forgot capture_preview, the same bug DeleteResultAsync had before it was fixed
        // (see DeleteResult_ForASweepWithPreviews_RemovesTheCapturePreviewRowsToo above). Left
        // uncaught, deleting a type emptied the capture table, SQLite reused the freed rowids for
        // the next sweep saved anywhere in the app, and its capture_preview insert collided with
        // the orphaned row still sitting at that (capture_id, channel, kind) -- "UNIQUE constraint
        // failed: capture_preview.capture_id, capture_preview.channel, capture_preview.kind" on
        // POST /api/sweep/save for what looked like an unrelated, later save.
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            var projectRepo = new ProjectRepository(factory);
            var repo = new MeasurementRepository(factory);

            long typeId = await CreateSweepTypeAsync(factory);
            var withPreview = BuildSweepRequest(typeId) with
            {
                Captures = [
                    BuildSweepRequest(typeId).Captures[0] with
                    {
                        Previews = [new CapturePreviewInput(0, CapturePreviewKind.Fft, [100f], [-90f], [-85f])],
                    },
                    .. BuildSweepRequest(typeId).Captures.Skip(1),
                ],
            };
            await repo.SaveSweepResultAsync(withPreview, CancellationToken.None);

            bool deleted = await projectRepo.DeleteTypeAsync(typeId, CancellationToken.None);
            Assert.True(deleted);

            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                long remaining = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM capture_preview;");
                Assert.Equal(0, remaining);
            }

            // A second sweep, saved under a brand-new type after the first was deleted -- this is
            // where the orphaned rows used to surface as a 500 on an entirely unrelated save.
            long secondProjectId = await projectRepo.CreateProjectAsync("GM70 v2", null, CancellationToken.None);
            long secondTypeId = await projectRepo.CreateTypeAsync(secondProjectId, "ThdVsFrequency", "sweep", null, "{}", CancellationToken.None);
            var secondWithPreview = BuildSweepRequest(secondTypeId) with
            {
                Captures = [
                    BuildSweepRequest(secondTypeId).Captures[0] with
                    {
                        Previews = [new CapturePreviewInput(0, CapturePreviewKind.Fft, [200f], [-80f], [-70f])],
                    },
                    .. BuildSweepRequest(secondTypeId).Captures.Skip(1),
                ],
            };

            await repo.SaveSweepResultAsync(secondWithPreview, CancellationToken.None);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task SaveSweep_WithPreviewsOnEveryCapture_DoesNotCollideOnCapturePreview()
    {
        // What the app actually saves: every point carries its own FFT/scope preview, not just the
        // first one. capture_preview is UNIQUE on (capture_id, channel, kind), so this is the shape
        // that catches two captures being written under one capture id.
        string path = TestDb.CreateTempPath();
        try
        {
            var factory = await TestDb.CreateMigratedFactoryAsync(path);
            long typeId = await CreateSweepTypeAsync(factory);
            var repo = new MeasurementRepository(factory);

            var request = BuildSweepRequest(typeId);
            var withPreviews = request with
            {
                Captures = [.. request.Captures.Select(c => c with
                {
                    Previews = [
                        new CapturePreviewInput(0, CapturePreviewKind.Fft, [100f, 200f], [-90f, -80f], [-85f, -75f]),
                        new CapturePreviewInput(0, CapturePreviewKind.Scope, [0f, 0.0005f], [-1f, -0.5f], [1f, 0.5f]),
                        new CapturePreviewInput(1, CapturePreviewKind.Fft, [100f, 200f], [-91f, -81f], [-86f, -76f]),
                        new CapturePreviewInput(1, CapturePreviewKind.Scope, [0f, 0.0005f], [-1f, -0.5f], [1f, 0.5f]),
                    ],
                })],
            };

            long resultId = await repo.SaveSweepResultAsync(withPreviews, CancellationToken.None);
            var points = await repo.GetSweepPointsAsync(resultId, CancellationToken.None);

            Assert.Equal(PointCount, points.Count);
            foreach (var point in points)
            {
                Assert.NotNull(point.PreviewsByChannel);
                Assert.True(point.PreviewsByChannel![0].ContainsKey(CapturePreviewKind.Fft));
                Assert.True(point.PreviewsByChannel[1].ContainsKey(CapturePreviewKind.Scope));
            }
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    private static async Task<long> CreateSweepTypeAsync(SqliteConnectionFactory factory)
    {
        var projectRepo = new ProjectRepository(factory);
        long projectId = await projectRepo.CreateProjectAsync("GM70", null, CancellationToken.None);
        return await projectRepo.CreateTypeAsync(projectId, "ThdVsFrequency", "sweep", null, "{}", CancellationToken.None);
    }

    private static SweepSaveRequest BuildSweepRequest(long typeId)
    {
        var captures = new List<SweepCaptureInput>(PointCount);
        for (int i = 0; i < PointCount; i++)
        {
            double freqHz = 100.0 * Math.Pow(2.0, i / 2.0);
            double thd = 0.5 + (i * 0.01);

            captures.Add(new SweepCaptureInput(i, freqHz, [
                new MeasurementMetric(0, "fundamental_hz", freqHz, "Hz"),
                new MeasurementMetric(0, "thd_percent", thd, "%"),
                new MeasurementMetric(0, "h2_percent", thd * 0.9, "%"),
                new MeasurementMetric(1, "fundamental_hz", freqHz, "Hz"),
                new MeasurementMetric(1, "thd_percent", thd * 2.0, "%"),
                new MeasurementMetric(1, "h2_percent", thd * 1.8, "%"),
            ]));
        }

        return new SweepSaveRequest(
            typeId, "sweep run", null, "{}", "QA403", "SIM-0001", "sim-1.0", IsSimulated: true,
            SampleRateHz: 192000, FftSize: 32768, Channels: 2,
            InRangeDbv: 0, OutRangeDbv: -12, captures);
    }
}
