using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device.Simulation;

namespace Qa403.App.Tests.Services;

/// <summary>
/// What a measurement type's <c>params_json</c> becomes when the UI saves it — the decision
/// <see cref="MeasurementSaveService.ResolveTypeParamsJson"/> makes for both the create and the
/// update endpoint.
/// </summary>
public class SweepTypeParamsTests
{
    /// <summary>
    /// The bug this pins: a sweep type saved with no explicit params_json used to fall through to
    /// the live-state snapshot, which is an <c>FftSpectrumParameters</c> — silently replacing the
    /// sweep's own settings with a different measurement's, after which nothing could read them
    /// back. A sweep's configuration is not live server state, so there is nothing to snapshot.
    /// </summary>
    [Fact]
    public async Task SweepTypeWithNoExplicitParams_KeepsWhatItAlreadyHas()
    {
        await WithSaveServiceAsync(saveService =>
        {
            const string stored = """{"startFreqHz":10,"endFreqHz":20000,"stepsPerOctave":7}""";

            string resolved = saveService.ResolveTypeParamsJson(
                requestedJson: null,
                kind: MeasurementSaveService.ThdVsFrequencyKind,
                existingParamsJson: stored,
                staged: null);

            Assert.Equal(stored, resolved);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// A new sweep type's seeded params have to come out in the same wire shape every other
    /// params_json does — camelCase keys, enums by name. Serializing with bare defaults instead of
    /// the app's own options wrote PascalCase and a numeric window, which the browser reads as an
    /// object with none of the fields it looks for: the type came up showing SweepConfigPanel's
    /// hard-coded defaults rather than its own stored settings, and looked like it had simply not
    /// saved. Asserted on the JSON rather than a round-trip, because a round-trip through the same
    /// options would pass either way.
    /// </summary>
    [Fact]
    public async Task NewSweepType_SeedsSweepParametersInTheApisOwnWireShape()
    {
        await WithSaveServiceAsync(saveService =>
        {
            string resolved = saveService.ResolveTypeParamsJson(
                requestedJson: null,
                kind: MeasurementSaveService.ThdVsFrequencyKind,
                existingParamsJson: null,
                staged: null);

            using var document = JsonDocument.Parse(resolved);
            var root = document.RootElement;

            Assert.True(root.TryGetProperty("startFreqHz", out var start), $"expected camelCase keys, got: {resolved}");
            Assert.True(start.GetDouble() > 0);
            Assert.True(root.GetProperty("endFreqHz").GetDouble() > start.GetDouble());
            Assert.True(root.GetProperty("stepsPerOctave").GetInt32() >= 1);
            Assert.True(root.GetProperty("processLeft").GetBoolean() || root.GetProperty("processRight").GetBoolean());
            Assert.Equal(JsonValueKind.String, root.GetProperty("window").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("levelMode").ValueKind);
        });
    }

    /// <summary>
    /// The amplitude axis has its own record and its own defaults, and the seeding branch has to
    /// pick the right one off the kind — seeding a level sweep with a frequency sweep's parameters
    /// deserializes to nothing the panel can read, the exact failure the test above documents.
    /// </summary>
    [Fact]
    public async Task NewAmplitudeSweepType_SeedsItsOwnParametersNotTheFrequencySweeps()
    {
        await WithSaveServiceAsync(saveService =>
        {
            string resolved = saveService.ResolveTypeParamsJson(
                requestedJson: null,
                kind: MeasurementSaveService.ThdVsAmplitudeKind,
                existingParamsJson: null,
                staged: null);

            using var document = JsonDocument.Parse(resolved);
            var root = document.RootElement;

            Assert.Equal(1000.0, root.GetProperty("fixedFreqHz").GetDouble());
            Assert.Equal(0.001, root.GetProperty("startAmplitudeVolt").GetDouble());
            Assert.Equal(1.0, root.GetProperty("endAmplitudeVolt").GetDouble());
            Assert.True(root.GetProperty("stepsPerOctave").GetInt32() >= 1);
            Assert.True(root.GetProperty("loadOhms").GetDouble() > 0);
            Assert.Equal(JsonValueKind.String, root.GetProperty("window").ValueKind);

            // Shared with the frequency sweep -- both axes offer the generator-off residual pass.
            Assert.True(root.GetProperty("measureNoiseFloor").GetBoolean());

            // The frequency sweep's own fields must not be here — that record is a different shape.
            Assert.False(root.TryGetProperty("startFreqHz", out _), $"amplitude params carry a frequency sweep's fields: {resolved}");
            Assert.False(root.TryGetProperty("levelMode", out _));
        });
    }

    [Fact]
    public async Task ExplicitParams_AreWrittenThroughUnchanged()
    {
        await WithSaveServiceAsync(saveService =>
        {
            const string requested = """{"startFreqHz":100}""";

            Assert.Equal(requested, saveService.ResolveTypeParamsJson(
                requested, MeasurementSaveService.ThdVsFrequencyKind, existingParamsJson: null, staged: null));
            Assert.Equal(requested, saveService.ResolveTypeParamsJson(
                requested, "FFTSpectrum", existingParamsJson: """{"fftSize":4096}""", staged: null));
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// An FFT type with nothing explicit still snapshots the live pipeline — the behaviour the
    /// sweep branch had to be carved out of, kept here so carving it out did not change it.
    /// </summary>
    [Fact]
    public async Task FftTypeWithNoExplicitParams_StillSnapshotsTheLiveState()
    {
        await WithSaveServiceAsync(saveService =>
        {
            string resolved = saveService.ResolveTypeParamsJson(
                requestedJson: null,
                kind: "FFTSpectrum",
                existingParamsJson: """{"fftSize":1024}""",
                staged: null);

            var readOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            readOptions.Converters.Add(new JsonStringEnumConverter());
            var parameters = JsonSerializer.Deserialize<FftSpectrumParameters>(resolved, readOptions);

            Assert.NotNull(parameters);
            Assert.NotEqual(1024, parameters!.FftSize); // the live pipeline's size, not the stored one
        });
    }

    /// <summary>
    /// A connected simulated session, a migrated temp database, and a MeasurementSaveService over
    /// both. No acquisition is started — none of these paths read a capture.
    /// </summary>
    private static async Task WithSaveServiceAsync(Func<MeasurementSaveService, Task> body)
    {
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var session = new DeviceSessionService(new SimulatedDevice(), pipeline, NullLogger<DeviceSessionService>.Instance);
        await session.ConnectAsync(CancellationToken.None);

        string path = Path.Combine(Path.GetTempPath(), $"qa403-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new Qa403DatabaseOptions { Path = path });
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
                await new MigrationRunner().RunAsync(connection, CancellationToken.None);

            // The two settings AppJsonContext carries that params_json actually depends on:
            // camelCase (already the JsonOptions default, via JsonSerializerDefaults.Web) and enums
            // by name. Reproduced rather than referenced because AppJsonContext is internal, and its
            // source-generated resolver only matters for the trimmed publish — under test the
            // reflection resolver produces the same JSON. A bare JsonOptions writes enums as
            // numbers, which is exactly the class of bug these tests exist to catch.
            var jsonOptions = new JsonOptions();
            jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

            var sweepRunner = new ThdSweepRunner(
                session, pipeline, new SpectrumBroadcastHub(), NullLogger<ThdSweepRunner>.Instance);
            var saveService = new MeasurementSaveService(
                session, pipeline, sweepRunner, new ProjectRepository(factory), new MeasurementRepository(factory),
                Options.Create(jsonOptions));

            await body(saveService);
        }
        finally
        {
            await pipeline.StopAsync();

            SqliteConnection.ClearAllPools();
            foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }

    private static Task WithSaveServiceAsync(Action<MeasurementSaveService> body) =>
        WithSaveServiceAsync(saveService =>
        {
            body(saveService);
            return Task.CompletedTask;
        });
}
