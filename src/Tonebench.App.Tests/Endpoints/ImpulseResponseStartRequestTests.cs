using System.Reflection;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The wire request maps into <see cref="ImpulseResponseRequest"/> field by field, which is a shape
/// that fails silently: a field left out of the mapping is not an error, it is a default, and the run
/// returns a plausible measurement of settings the user did not ask for.
///
/// <para>This record has seventeen fields against the frequency response's eleven, so it carries
/// proportionally more of that risk — and the frequency response is where the bug actually happened.
/// </para>
/// </summary>
public class ImpulseResponseStartRequestTests
{
    [Fact]
    public void ToMeasurement_CarriesEveryFieldAcross()
    {
        // Every value deliberately different from the record's own default, so a field that is not
        // mapped shows up as the default rather than passing by coincidence.
        var wire = new ImpulseResponseStartRequest(
            FrequencySpanHz: 48_000.0,
            ResolutionMultiplier: 4,
            LevelMode: "GeneratorVoltage",
            LevelValue: -13.5,
            LevelUnit: "Dbv",
            SmoothingDenominator: 6,
            AverageCount: 7,
            ReferenceMode: "StoredLoopback",
            ReferenceCalibrationId: 17,
            GatePreMs: 2.5,
            GatePostMs: 12.5,
            GateWindow: "BlackmanHarris",
            ExtractHarmonics: false,
            MaxHarmonicOrder: 3,
            Continuous: true,
            ChirpStartHz: 8.0,
            LoadOhms: 4.0,
            Stimulus: "SpeechFemale2011",
            NoiseCutoffHz: 37.0);

        var measurement = wire.ToMeasurement(
            ThdSweepLevelMode.GeneratorVoltage,
            ThdSweepLevelUnit.Dbv,
            ImpulseReferenceMode.StoredLoopback,
            WindowType.BlackmanHarris,
            ResponseStimulus.SpeechFemale2011);

        Assert.Equal(48_000.0, measurement.FrequencySpanHz);
        Assert.Equal(4, measurement.ResolutionMultiplier);
        Assert.Equal(ThdSweepLevelMode.GeneratorVoltage, measurement.LevelMode);
        Assert.Equal(-13.5, measurement.LevelValue);
        Assert.Equal(ThdSweepLevelUnit.Dbv, measurement.LevelUnit);
        Assert.Equal(6, measurement.SmoothingDenominator);
        Assert.Equal(7, measurement.AverageCount);
        Assert.Equal(ImpulseReferenceMode.StoredLoopback, measurement.ReferenceMode);
        Assert.Equal(17, measurement.ReferenceCalibrationId);
        Assert.Equal(2.5, measurement.GatePreMs);
        Assert.Equal(12.5, measurement.GatePostMs);
        Assert.Equal(WindowType.BlackmanHarris, measurement.GateWindow);
        Assert.False(measurement.ExtractHarmonics);
        Assert.Equal(3, measurement.MaxHarmonicOrder);
        Assert.True(measurement.Continuous);
        Assert.Equal(8.0, measurement.ChirpStartHz);
        Assert.Equal(4.0, measurement.LoadOhms);
        Assert.Equal(ResponseStimulus.SpeechFemale2011, measurement.Stimulus);
        Assert.Equal(37.0, measurement.NoiseCutoffHz);

        // And through to the acquisition half, which is where the runner reads them.
        Assert.Equal(ResponseStimulus.SpeechFemale2011, measurement.AsAcquisition().Stimulus);
        Assert.Equal(37.0, measurement.AsAcquisition().NoiseCutoffHz);
    }

    /// <summary>
    /// Fails the moment a parameter is added to either record, because the assertions above cannot
    /// know about a field nobody told them about. Deliberately blunt: the alternative is an evening
    /// spent measuring a setting that never left the browser.
    /// </summary>
    [Fact]
    public void BothRecordsStillHaveTheFieldsTheMappingTestChecks()
    {
        Assert.Equal(19, ConstructorParameterCount(typeof(ImpulseResponseStartRequest)));
        Assert.Equal(19, ConstructorParameterCount(typeof(ImpulseResponseRequest)));
    }

    /// <summary>
    /// The stored settings have to say what produced the result, so every run field also has to
    /// reach <c>params_json</c>. Same failure mode as the mapping above, one layer further out: a
    /// saved measurement whose parameters cannot describe it is a picture, not a measurement.
    /// </summary>
    [Fact]
    public void TheStoredParametersCoverEveryRunSetting()
    {
        var runFields = typeof(ImpulseResponseRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .Where(name => name != "Continuous")   // how the run was started, not what it measured
            .ToHashSet(StringComparer.Ordinal);

        var storedFields = typeof(Tonebench.Data.ImpulseResponseParameters)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = runFields.Except(storedFields).ToList();
        Assert.True(missing.Count == 0, $"params_json cannot describe: {string.Join(", ", missing)}");
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
