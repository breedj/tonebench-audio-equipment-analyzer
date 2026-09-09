using System.Reflection;
using Qa403.App.Endpoints;
using Qa403.App.Services;
using Qa403.Dsp;

namespace Qa403.App.Tests.Endpoints;

/// <summary>
/// The wire request maps into <see cref="ImpedanceRequest"/> field by field, which is a shape that
/// fails silently: a field left out of the mapping is not an error, it is a default, and the run
/// returns a plausible measurement of settings the user did not ask for.
/// </summary>
public class ImpedanceStartRequestTests
{
    [Fact]
    public void ToMeasurement_CarriesEveryFieldAcross()
    {
        var open = new ImpedanceCalibration([20.0, 20000.0], [1.0, 0.9], [0.0, 0.1]);
        var shortCal = new ImpedanceCalibration([20.0, 20000.0], [0.01, 0.02], [0.0, 0.03]);

        // Every value deliberately different from the record's own default, so a field that is not
        // mapped shows up as the default rather than passing by coincidence.
        var wire = new ImpedanceStartRequest(
            Method: "ReferenceResistor",
            Stimulus: "Chirp",
            ReferenceOhms: 47.5,
            RightChannelIsReference: true,
            FrequencySpanHz: 48_000.0,
            ResolutionMultiplier: 4,
            LevelValue: -13.5,
            LevelUnit: "Dbv",
            SmoothingDenominator: 6,
            AverageCount: 7,
            Continuous: true,
            ChirpStartHz: 8.0,
            CalTarget: "Open",
            CableMode: "Fixed",
            FixedCableOhms: 0.17,
            FixedCableMillihenry: 0.42,
            AnalyzerInputOhms: 100_000.0,
            ApplySweepGate: true,
            OpenCal: open,
            ShortCal: shortCal,
            CalibrationLevelValue: 3.5);

        var measurement = wire.ToMeasurement(
            ImpedanceMethod.ReferenceResistor,
            ImpedanceStimulus.Chirp,
            ThdSweepLevelUnit.Dbv,
            ImpedanceCalTarget.Open,
            ImpedanceCableMode.Fixed);

        Assert.Equal(ImpedanceMethod.ReferenceResistor, measurement.Method);
        Assert.Equal(ImpedanceStimulus.Chirp, measurement.Stimulus);
        Assert.Equal(47.5, measurement.ReferenceOhms);
        Assert.True(measurement.RightChannelIsReference);
        Assert.Equal(48_000.0, measurement.FrequencySpanHz);
        Assert.Equal(4, measurement.ResolutionMultiplier);
        Assert.Equal(-13.5, measurement.LevelValue);
        Assert.Equal(ThdSweepLevelUnit.Dbv, measurement.LevelUnit);
        Assert.Equal(6, measurement.SmoothingDenominator);
        Assert.Equal(7, measurement.AverageCount);
        Assert.True(measurement.Continuous);
        Assert.Equal(8.0, measurement.ChirpStartHz);
        Assert.Equal(ImpedanceCalTarget.Open, measurement.CalTarget);
        Assert.Equal(ImpedanceCableMode.Fixed, measurement.CableMode);
        Assert.Equal(0.17, measurement.FixedCableOhms);
        Assert.Equal(0.42, measurement.FixedCableMillihenry);
        Assert.Equal(100_000.0, measurement.AnalyzerInputOhms);
        Assert.True(measurement.ApplySweepGate);
        Assert.Same(open, measurement.OpenCal);
        Assert.Same(shortCal, measurement.ShortCal);
        Assert.Equal(3.5, measurement.CalibrationLevelValue);
    }

    /// <summary>
    /// Fails the moment a parameter is added to either record, because the assertions above cannot
    /// know about a field nobody told them about. Deliberately blunt, for the same reason the
    /// frequency response's version is.
    /// </summary>
    [Fact]
    public void BothRecordsStillHaveTheFieldsTheMappingTestChecks()
    {
        Assert.Equal(21, ConstructorParameterCount(typeof(ImpedanceStartRequest)));
        Assert.Equal(21, ConstructorParameterCount(typeof(ImpedanceRequest)));
    }

    /// <summary>
    /// A calibration run applies no calibration to itself. The open standard <i>is</i> the channel
    /// mismatch, so normalising it against a previous open would report the difference between two
    /// calibrations as an impedance, and the short has to be measured through the same uncorrected
    /// path the open was.
    /// </summary>
    [Fact]
    public void SolveOptions_ForACalibrationRun_AppliesNothing()
    {
        var request = Measurement() with
        {
            CalTarget = ImpedanceCalTarget.Open,
            OpenCal = new ImpedanceCalibration([20.0, 20000.0], [1.0, 0.9], [0.0, 0.1]),
            CableMode = ImpedanceCableMode.Fixed,
            FixedCableOhms = 0.5,
        };

        var options = request.SolveOptions();

        Assert.Null(options.OpenCal);
        Assert.Null(options.ShortCal);
        Assert.Equal(0.0, options.CableOhms);
    }

    /// <summary>
    /// The stored short is only applied when the cable mode asks for it — picking "fixed" or "none"
    /// must not quietly keep subtracting a calibration that happens to still be stored.
    /// </summary>
    [Fact]
    public void SolveOptions_OnlyUsesTheShortWhenTheCableModeSaysMeasured()
    {
        var shortCal = new ImpedanceCalibration([20.0, 20000.0], [0.01, 0.02], [0.0, 0.03]);

        Assert.Null((Measurement() with { CableMode = ImpedanceCableMode.None, ShortCal = shortCal }).SolveOptions().ShortCal);
        Assert.Null((Measurement() with { CableMode = ImpedanceCableMode.Fixed, ShortCal = shortCal }).SolveOptions().ShortCal);
        Assert.NotNull((Measurement() with { CableMode = ImpedanceCableMode.Measured, ShortCal = shortCal }).SolveOptions().ShortCal);
    }

    /// <summary>Millihenries on the wire, henries in the solver. A factor of a thousand is an easy thing to get wrong and a hard one to notice.</summary>
    [Fact]
    public void SolveOptions_ConvertsTheFixedCableInductanceToHenries()
    {
        var options = (Measurement() with
        {
            CableMode = ImpedanceCableMode.Fixed,
            FixedCableOhms = 0.15,
            FixedCableMillihenry = 0.42,
        }).SolveOptions();

        Assert.Equal(0.15, options.CableOhms);
        Assert.Equal(0.00042, options.CableHenry, 12);
    }

    private static ImpedanceRequest Measurement() =>
        new(ImpedanceMethod.ReferenceResistor,
            ImpedanceStimulus.Chirp,
            ReferenceOhms: 100.0,
            RightChannelIsReference: false,
            FrequencySpanHz: 24_000.0,
            ResolutionMultiplier: 1,
            LevelValue: -20.0,
            LevelUnit: ThdSweepLevelUnit.Dbv,
            SmoothingDenominator: 48,
            AverageCount: 2);

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Max(c => c.GetParameters().Length);
}
