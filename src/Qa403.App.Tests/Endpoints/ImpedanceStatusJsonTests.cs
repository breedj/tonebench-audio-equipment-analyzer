using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Qa403.App.Services;
using Qa403.Dsp;

namespace Qa403.App.Tests.Endpoints;

/// <summary>
/// The impedance status has to survive being written as JSON, including when a run has no impedance
/// to report.
///
/// <para><b>Why this needs its own test.</b> <c>System.Text.Json</c> will not write a
/// <c>double.NaN</c> — it is not a JSON number — and it finds out part way through the response,
/// after the 200 and 70 kB of trace arrays have already gone out. The body then simply stops
/// mid-array: the browser's <c>JSON.parse</c> throws, the entire status is lost, and the screen
/// looks as though the run did nothing at all. A calibration run measures a standard rather than an
/// impedance, so it has no median and hit this on every capture.</para>
///
/// <para>Nothing else catches it: the runner tests read the <c>Status</c> object directly and never
/// serialize it, and a measurement run's numbers are finite so the endpoint looks healthy. Only
/// driving the real HTTP endpoint for a <i>calibration</i> run showed it.</para>
/// </summary>
public class ImpedanceStatusJsonTests
{
    /// <summary>
    /// The app's own HTTP JSON options, so this tests the writer the endpoint actually uses —
    /// including the string enum converter Program.cs adds, without which every enum on the wire
    /// would be a number the browser's union types do not accept.
    /// </summary>
    private static JsonSerializerOptions SerializerOptions
    {
        get
        {
            var options = new JsonOptions();
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            return options.SerializerOptions;
        }
    }

    private static ImpedanceStatus Status(double? medianOhms, double? driveCurrentAmps) =>
        new(Running: false,
            FrequencyResponsePhase.Finished,
            Continuous: false,
            PassesCompleted: 1,
            AveragesTaken: 1,
            AverageCount: 1,
            GeneratorAmpDbv: -20.0,
            BinHz: 1.46484375,
            SampleRateHz: 48000.0,
            FftSize: 32768,
            ReferenceOhms: 100.0,
            RightChannelIsReference: true,
            ImpedanceCalTarget.Open,
            Error: null,
            Traces: [],
            CapturedCalibration: new ImpedanceCalibration([20.0, 20000.0], [1.0, 0.99], [0.0, 0.01]),
            medianOhms,
            driveCurrentAmps);

    [Fact]
    public void ACalibrationRunsStatus_SerializesToJsonThatParsesBack()
    {
        string json = JsonSerializer.Serialize(Status(null, null), SerializerOptions);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("medianOhms").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("driveCurrentAmps").ValueKind);
        Assert.Equal("Open", root.GetProperty("calTarget").GetString());
        Assert.Equal(2, root.GetProperty("capturedCalibration").GetProperty("freqHz").GetArrayLength());
    }

    [Fact]
    public void AMeasurementRunsStatus_StillCarriesItsNumbers()
    {
        string json = JsonSerializer.Serialize(Status(8.0, 0.000926), SerializerOptions);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        Assert.Equal(8.0, root.GetProperty("medianOhms").GetDouble());
        Assert.Equal(0.000926, root.GetProperty("driveCurrentAmps").GetDouble());
    }

    /// <summary>
    /// The property that actually failed: a NaN anywhere in this record aborts the response. Stated
    /// as a direct check on the writer so the reason the fields are nullable cannot be forgotten and
    /// quietly undone.
    /// </summary>
    [Fact]
    public void TheJsonWriterStillRefusesANaN_WhichIsWhyThoseFieldsAreNullable()
    {
        Assert.ThrowsAny<Exception>(
            () => JsonSerializer.Serialize(new { medianOhms = double.NaN }, SerializerOptions));
    }
}
