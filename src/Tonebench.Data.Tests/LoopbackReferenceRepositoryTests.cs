namespace Tonebench.Data.Tests;

/// <summary>
/// Loopback calibrations live in the <b>configuration</b> database (config migration 002), not in a
/// project file: they describe the analyzer and its cables, and one calibration has to serve every
/// project on this bench. These tests therefore run against a config-migrated file only — a
/// measurement-migrated one would also have the (empty, unused) table and pass for the wrong reason.
/// </summary>
public class LoopbackReferenceRepositoryTests
{
    [Fact]
    public async Task LoopbackReference_RoundTripsItsIrAndOffsets_AndReplacesOnTheSameName()
    {
        string path = TestDb.CreateTempPath();
        try
        {
            var repo = new LoopbackReferenceRepository(await TestDb.CreateMigratedConfigFactoryAsync(path));

            var original = BuildReference("bench");
            long id = await repo.SaveLoopbackReferenceAsync(original, CancellationToken.None);

            var stored = await repo.GetLoopbackReferenceAsync(id, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(original.Ir, stored.Ir);
            Assert.Equal(original.PeakOffset, stored.PeakOffset);
            Assert.Equal(original.RemovedDelaySamples, stored.RemovedDelaySamples);
            Assert.Equal(original.SampleRateHz, stored.SampleRateHz);
            Assert.True(stored.IsSimulated);

            // Saving over the same name replaces rather than duplicating: the name is how the picker
            // identifies a calibration, and two rows sharing one would be indistinguishable there.
            long again = await repo.SaveLoopbackReferenceAsync(
                BuildReference("bench") with { SampleRateHz = 96000.0 }, CancellationToken.None);

            Assert.Equal(id, again);
            Assert.Single(await repo.ListLoopbackReferencesAsync(CancellationToken.None));
            Assert.Equal(96000.0, (await repo.GetLoopbackReferenceAsync(id, CancellationToken.None))!.SampleRateHz);

            Assert.True(await repo.DeleteLoopbackReferenceAsync(id, CancellationToken.None));
            Assert.Empty(await repo.ListLoopbackReferencesAsync(CancellationToken.None));
        }
        finally
        {
            TestDb.Cleanup(path);
        }
    }

    /// <summary>
    /// The load-bearing property of the move: a calibration is written to the configuration file and
    /// never to the project file, so closing, deleting or copying a project cannot touch it.
    /// </summary>
    [Fact]
    public async Task LoopbackReference_IsWrittenToTheConfigFile_NeverToAProjectFile()
    {
        string configPath = TestDb.CreateTempPath();
        string projectPath = TestDb.CreateTempPath();
        try
        {
            var repo = new LoopbackReferenceRepository(await TestDb.CreateMigratedConfigFactoryAsync(configPath));
            var projectFactory = await TestDb.CreateMigratedFactoryAsync(projectPath);

            await repo.SaveLoopbackReferenceAsync(BuildReference("bench"), CancellationToken.None);

            await using var project = await projectFactory.OpenAsync(CancellationToken.None);
            Assert.Equal(0L, await TestDb.ScalarAsync(project, "SELECT COUNT(*) FROM impulse_reference;"));
        }
        finally
        {
            TestDb.Cleanup(configPath);
            TestDb.Cleanup(projectPath);
        }
    }

    private static StoredLoopbackReference BuildReference(string name)
    {
        var ir = new float[8193];
        ir[1024] = 1.0f;
        ir[1023] = -0.25f;
        ir[1025] = -0.25f;

        return new StoredLoopbackReference(
            Id: 0, name, DateTimeOffset.UtcNow, SampleRateHz: 48000.0, SourceFftSize: 32768,
            ChirpStartHz: 2.0, ChirpEndHz: 23520.0, RemovedDelaySamples: 41.5, PeakOffset: 1024,
            ir, DeviceSerial: "1077527945", IsSimulated: true);
    }
}
