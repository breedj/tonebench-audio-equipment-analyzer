namespace Tonebench.Data.Tests;

/// <summary>
/// Storing a captured oscilloscope waveform in <c>capture.pcm</c> and getting it back.
///
/// <para>Those columns have been in the schema since migration 001 and until now carried nothing
/// but imported simulations, so reusing them is what makes a stored waveform cost no migration, no
/// new child table of <c>capture</c>, and therefore no <c>CaptureCascade</c> entry and no
/// orphan-repair migration of the kind 005, 007 and 010 exist to be.</para>
/// </summary>
public class WaveformStorageTests
{
    private static double[][] Waveform(int frames, double offset = 0.0)
    {
        var left = new double[frames];
        var right = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            left[i] = Math.Sin(2.0 * Math.PI * 16 * i / frames) + offset;
            right[i] = Math.Cos(2.0 * Math.PI * 16 * i / frames) - offset;
        }

        return [left, right];
    }

    private static async Task<(SqliteConnectionFactory Factory, long TypeId)> NewProjectAsync(string path)
    {
        var factory = await TestDb.CreateMigratedFactoryAsync(path);
        var projects = new ProjectRepository(factory);
        long projectId = await projects.CreateProjectAsync("Scope bench", null, CancellationToken.None);
        long typeId = await projects.CreateTypeAsync(projectId, "Oscilloscope", "capture", null, "{}", CancellationToken.None);
        return (factory, typeId);
    }

    [Fact]
    public async Task AWaveformComesBackSampleForSample_AcrossARestart()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (factory, typeId) = await NewProjectAsync(path);
            var samples = Waveform(4096);

            // Quantised by the engine before anything measured it, so storage changes nothing more.
            foreach (var channel in samples)
                PcmCodec.QuantizeInPlace(channel);

            long resultId = await new MeasurementRepository(factory).SaveWaveformResultAsync(
                new WaveformSaveRequest(
                    typeId, "capture 1", null, "{}", 192_000.0, samples,
                    InputRangeDbv: 6.0, OutputRangeDbv: -2.0, GenJson: null,
                    DeviceName: "QA403", Serial: "1077527945", FwVer: "60"),
                CancellationToken.None);

            // A fresh factory, standing in for a full app restart.
            var reread = new MeasurementRepository(await TestDb.CreateMigratedFactoryAsync(path));
            var detail = await reread.GetResultAsync(resultId, CancellationToken.None);
            Assert.NotNull(detail);

            var pcm = await reread.GetCapturePcmAsync(detail!.CaptureId, CancellationToken.None);
            Assert.NotNull(pcm);
            Assert.Equal(192_000.0, pcm!.SampleRateHz, 6);
            Assert.Equal(2, pcm.Channels.Length);
            Assert.Equal(4096, pcm.Channels[0].Length);

            for (int ch = 0; ch < samples.Length; ch++)
            {
                for (int i = 0; i < samples[ch].Length; i++)
                    Assert.Equal(samples[ch][i], pcm.Channels[ch][i], 12);
            }
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task TheMeanIsKept_BecauseDcIsOneOfTheReadings()
    {
        // The simulation import pairs QuantizeInPlace with RemoveMeanInPlace. A scope must not:
        // its DC level is a measurement, not a nuisance to be subtracted before storing.
        string path = TestDb.CreateTempPath();
        try
        {
            var (factory, typeId) = await NewProjectAsync(path);
            var samples = Waveform(1024, offset: 0.75);

            long resultId = await new MeasurementRepository(factory).SaveWaveformResultAsync(
                new WaveformSaveRequest(typeId, "offset capture", null, "{}", 48_000.0, samples),
                CancellationToken.None);

            var repo = new MeasurementRepository(factory);
            var detail = await repo.GetResultAsync(resultId, CancellationToken.None);
            var pcm = await repo.GetCapturePcmAsync(detail!.CaptureId, CancellationToken.None);

            Assert.Equal(0.75, pcm!.Channels[0].Average(), 5);
            Assert.Equal(-0.75, pcm.Channels[1].Average(), 5);
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task TheAnalyzerThatTookItIsRecordedWithIt()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (factory, typeId) = await NewProjectAsync(path);

            long resultId = await new MeasurementRepository(factory).SaveWaveformResultAsync(
                new WaveformSaveRequest(
                    typeId, "capture", null, "{}", 96_000.0, Waveform(256),
                    InputRangeDbv: 12.0, OutputRangeDbv: 8.0, GenJson: null,
                    DeviceName: "QA403", Serial: "4039_C589", FwVer: "60"),
                CancellationToken.None);

            // Read straight out of the row, since MeasurementDetail does not surface the analyzer's
            // identity -- what matters is that the save wrote it, so a stored capture can say which
            // hardware produced it.
            await using var connection = await TestDb.OpenMigratedAsync(path);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT device_name || '|' || serial || '|' || fw_ver FROM measurement_result WHERE id = $id;";
            command.Parameters.AddWithValue("$id", resultId);

            Assert.Equal("QA403|4039_C589|60", (string?)await command.ExecuteScalarAsync());
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task AWaveformWithRaggedChannels_IsRefused()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (factory, typeId) = await NewProjectAsync(path);
            double[][] ragged = [new double[64], new double[32]];

            await Assert.ThrowsAsync<ArgumentException>(
                () => new MeasurementRepository(factory).SaveWaveformResultAsync(
                    new WaveformSaveRequest(typeId, "bad", null, "{}", 48_000.0, ragged),
                    CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    [Fact]
    public async Task AStoredWaveformExportsAsOneRowPerSample_TimedFromTheTrigger()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var (factory, typeId) = await NewProjectAsync(path);
            long resultId = await new MeasurementRepository(factory).SaveWaveformResultAsync(
                new WaveformSaveRequest(typeId, "capture", null, "{}", 48_000.0, Waveform(64)),
                CancellationToken.None);

            var repo = new MeasurementRepository(factory);
            var detail = await repo.GetResultAsync(resultId, CancellationToken.None);
            var pcm = await repo.GetCapturePcmAsync(detail!.CaptureId, CancellationToken.None);

            // A quarter of the record before the trigger: 16 frames at 48 kHz.
            string csv = CsvExporter.ExportWaveform(detail, pcm!, triggerOffsetS: 16 / 48_000.0);
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var rows = lines.Where(l => !l.StartsWith('#')).ToArray();

            Assert.Equal("time_s,left_volt,right_volt", rows[0]);
            Assert.Equal(65, rows.Length); // header + 64 samples

            // Time runs from before the trigger to after it, and goes negative to its left rather
            // than re-zeroing on the first sample.
            Assert.StartsWith("-0.000333333", rows[1]);
            Assert.Contains("trigger", csv);
            Assert.Contains("0,", rows[17]); // the trigger sample itself is t = 0
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }
}
