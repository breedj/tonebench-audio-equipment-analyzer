using System.Reflection;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

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
            CalibrationLevelValue: 3.5,
            CalibrationStimulus: "SteppedSine",
            StartFreqHz: 31.5,
            EndFreqHz: 6300.0,
            StepsPerOctave: 9,
            AdaptiveResolution: false,
            SteppedFftSize: 32768,
            SteppedSampleRateHz: 96_000.0,
            NoiseCutoffHz: 37.0);

        var measurement = wire.ToMeasurement(
            ImpedanceMethod.ReferenceResistor,
            ImpedanceStimulus.Chirp,
            ThdSweepLevelUnit.Dbv,
            ImpedanceCalTarget.Open,
            ImpedanceCableMode.Fixed,
            ImpedanceStimulus.SteppedSine);

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
        Assert.Equal(37.0, measurement.NoiseCutoffHz);
        Assert.Equal(3.5, measurement.CalibrationLevelValue);
        Assert.Equal(ImpedanceStimulus.SteppedSine, measurement.CalibrationStimulus);
        Assert.Equal(31.5, measurement.StartFreqHz);
        Assert.Equal(6300.0, measurement.EndFreqHz);
        Assert.Equal(9, measurement.StepsPerOctave);
        Assert.False(measurement.AdaptiveResolution);
        Assert.Equal(32768, measurement.SteppedFftSize);
        Assert.Equal(96_000.0, measurement.SteppedSampleRateHz);
    }

    /// <summary>
    /// Fails the moment a parameter is added to either record, because the assertions above cannot
    /// know about a field nobody told them about. Deliberately blunt, for the same reason the
    /// frequency response's version is.
    /// </summary>
    [Fact]
    public void BothRecordsStillHaveTheFieldsTheMappingTestChecks()
    {
        Assert.Equal(29, ConstructorParameterCount(typeof(ImpedanceStartRequest)));
        Assert.Equal(29, ConstructorParameterCount(typeof(ImpedanceRequest)));
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

    /// <summary>
    /// A calibration may play a different stimulus from the measurement it will correct, so every
    /// derived property has to read <see cref="ImpedanceRequest.EffectiveStimulus"/> rather than
    /// <c>Stimulus</c>. Reading the wrong one would configure a chirp calibration with the stepped
    /// grid's sample rate, silently.
    /// </summary>
    [Fact]
    public void EffectiveStimulus_IsTheCalibrationsWhileCalibrating()
    {
        var request = Measurement() with
        {
            Stimulus = ImpedanceStimulus.SteppedSine,
            CalibrationStimulus = ImpedanceStimulus.Chirp,
            StartFreqHz = 20.0,
            EndFreqHz = 40_000.0,
        };

        Assert.Equal(ImpedanceStimulus.SteppedSine, request.EffectiveStimulus);
        Assert.True(request.IsStepped);

        var calibrating = request with { CalTarget = ImpedanceCalTarget.Open };
        Assert.Equal(ImpedanceStimulus.Chirp, calibrating.EffectiveStimulus);
        Assert.False(calibrating.IsStepped);

        // And the derived grid follows it: the chirp's own rate, not the stepped run's chosen one --
        // and a broadband calibration always takes the widest span, so it is 2 x 96 kHz rather than
        // twice whatever FrequencySpanHz says. See ImpedanceRequest.EffectiveSpanHz: a standard is
        // meant to be reused across spans, and one captured at 24 kHz silently has no correction
        // above 22.8 kHz.
        Assert.Equal(192_000.0, calibrating.SampleRateHz);
        Assert.Equal(96_000.0, request.SampleRateHz);
    }

    /// <summary>
    /// A chirp or noise calibration is captured at the widest span whatever the measurement's own
    /// is, so one capture of a fixture serves every span it is later measured at. A stepped
    /// standard walks its own grid and is unaffected.
    /// </summary>
    [Fact]
    public void ABroadbandCalibration_IsAlwaysCapturedAtTheWidestSpan()
    {
        foreach (double span in FrequencyResponseRequest.SupportedSpansHz)
        {
            var measurement = Measurement() with { FrequencySpanHz = span };

            // The measurement itself still spans what it was asked to.
            Assert.Equal(span, measurement.EffectiveSpanHz);
            Assert.Equal(span * 2.0, measurement.SampleRateHz);

            foreach (var stimulus in new[] { ImpedanceStimulus.Chirp, ImpedanceStimulus.PinkNoise })
            {
                var calibrating = measurement with
                {
                    CalTarget = ImpedanceCalTarget.Open,
                    CalibrationStimulus = stimulus,
                };

                Assert.Equal(ImpedanceRequest.MaxSpanHz, calibrating.EffectiveSpanHz);
                Assert.Equal(ImpedanceRequest.MaxSpanHz * 2.0, calibrating.SampleRateHz);
            }

            // A stepped standard takes its rate from its own grid, where a span means nothing.
            var stepped = measurement with
            {
                CalTarget = ImpedanceCalTarget.Short,
                CalibrationStimulus = ImpedanceStimulus.SteppedSine,
                AdaptiveResolution = false,
                SteppedSampleRateHz = 48_000.0,
            };
            Assert.Equal(48_000.0, stepped.SampleRateHz);
        }
    }

    /// <summary>
    /// A stepped run is measured once. Repeating a minutes-long sweep until stopped is not a mode
    /// anyone wants, and it is the same call the stepped-sine response makes.
    /// </summary>
    [Fact]
    public void Validate_RejectsAContinuousSteppedRun()
    {
        var request = Measurement() with
        {
            Stimulus = ImpedanceStimulus.SteppedSine,
            Continuous = true,
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(request.Validate);
        Assert.Equal(nameof(ImpedanceRequest.Continuous), ex.ParamName);

        // The chirp keeps it.
        (request with { Stimulus = ImpedanceStimulus.Chirp }).Validate();
    }

    /// <summary>
    /// Both settings groups always ride on the request, so a group no run will read must not be able
    /// to block one. A stepped measurement with a stepped calibration never plays a chirp, and a
    /// nonsense span left on the type from an earlier chirp run is therefore none of its business.
    /// </summary>
    [Fact]
    public void Validate_OnlyChecksTheSettingsSomeRunWillActuallyRead()
    {
        var stepped = Measurement() with
        {
            Stimulus = ImpedanceStimulus.SteppedSine,
            CalibrationStimulus = ImpedanceStimulus.SteppedSine,
            StartFreqHz = 20.0,
            EndFreqHz = 20_000.0,
            FrequencySpanHz = 12_345.0,
            SmoothingDenominator = 7,
        };

        stepped.Validate();

        // Point the calibration back at the chirp and that same span is now a 400.
        Assert.Throws<ArgumentOutOfRangeException>(
            (stepped with { CalibrationStimulus = ImpedanceStimulus.Chirp }).Validate);
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
