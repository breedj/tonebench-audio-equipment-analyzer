using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tonebench.App.Services;
using Tonebench.Data;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// One conversion and one predicate for every level unit, and the six measurement kinds that carry
/// a level all reading them.
///
/// <para>Each kind had its own copy of the switch and its own <c>== Watt</c> comparisons, which is
/// what made milliwatts a six-file change — and the copy that read a power unit as a voltage would
/// not have failed, it would have driven the DUT at a different level than asked. These tests pin
/// the conversion against arithmetic and the kinds against each other.</para>
/// </summary>
public class LevelUnitTests
{
    [Theory]
    [InlineData(ThdSweepLevelUnit.Dbv, false)]
    [InlineData(ThdSweepLevelUnit.Volt, false)]
    [InlineData(ThdSweepLevelUnit.MilliVolt, false)]
    [InlineData(ThdSweepLevelUnit.Watt, true)]
    [InlineData(ThdSweepLevelUnit.MilliWatt, true)]
    public void IsPower_SeparatesTheUnitsThatNeedALoadFromTheOnesThatDoNot(ThdSweepLevelUnit unit, bool expected) =>
        Assert.Equal(expected, unit.IsPower());

    [Fact]
    public void ToDbv_ConvertsEveryUnit()
    {
        Assert.Equal(-20.0, ThdSweepLevelUnit.Dbv.ToDbv(-20.0, loadOhms: 8.0), 12);
        Assert.Equal(0.0, ThdSweepLevelUnit.Volt.ToDbv(1.0, loadOhms: 8.0), 12);
        Assert.Equal(-20.0, ThdSweepLevelUnit.MilliVolt.ToDbv(100.0, loadOhms: 8.0), 12);

        // P = V^2/R: 1 W into 8 ohm is sqrt(8) = 2.828 V rms, i.e. +9.03 dBV.
        Assert.Equal(20.0 * Math.Log10(Math.Sqrt(8.0)), ThdSweepLevelUnit.Watt.ToDbv(1.0, loadOhms: 8.0), 12);

        // And a milliwatt is a thousandth of that, i.e. 30 dB down on the same load -- which is the
        // whole point of the unit: 250 mW into 32 ohm is a headphone level, not 250 W.
        Assert.Equal(
            ThdSweepLevelUnit.Watt.ToDbv(0.25, loadOhms: 32.0),
            ThdSweepLevelUnit.MilliWatt.ToDbv(250.0, loadOhms: 32.0),
            12);
        Assert.Equal(
            ThdSweepLevelUnit.Watt.ToDbv(1.0, loadOhms: 8.0) - 30.0,
            ThdSweepLevelUnit.MilliWatt.ToDbv(1.0, loadOhms: 8.0),
            9);
    }

    /// <summary>
    /// A zero level has no dBV at all. Every caller rejects one before reading this, so the floor is
    /// only reached by a path that is about to fail -- but it has to be a number, because an
    /// infinity in a generator config is a different and worse kind of wrong.
    /// </summary>
    [Fact]
    public void ToDbv_OfNothing_IsTheFloorRatherThanAnInfinity()
    {
        foreach (var unit in Enum.GetValues<ThdSweepLevelUnit>())
        {
            double dbv = unit.ToDbv(unit == ThdSweepLevelUnit.Dbv ? -240.0 : 0.0, loadOhms: 8.0);
            Assert.True(double.IsFinite(dbv), $"{unit} returned {dbv}");
            Assert.Equal(-240.0, dbv, 6);
        }
    }

    [Fact]
    public void Label_WritesTheUnitTheWayACaptionDoes()
    {
        Assert.Equal("dBV", ThdSweepLevelUnit.Dbv.Label());
        Assert.Equal("V", ThdSweepLevelUnit.Volt.Label());
        Assert.Equal("mV", ThdSweepLevelUnit.MilliVolt.Label());
        Assert.Equal("W", ThdSweepLevelUnit.Watt.Label());
        Assert.Equal("mW", ThdSweepLevelUnit.MilliWatt.Label());
    }

    /// <summary>
    /// The six kinds read one conversion, so they cannot disagree about what a level means. Asserted
    /// as a set rather than one kind at a time: a seventh kind with its own copy of the switch is
    /// exactly what this is here to stop.
    /// </summary>
    [Theory]
    [InlineData(ThdSweepLevelUnit.Watt, 2.0)]
    [InlineData(ThdSweepLevelUnit.MilliWatt, 2000.0)]
    [InlineData(ThdSweepLevelUnit.MilliVolt, 350.0)]
    public void EveryKindThatCarriesALevel_ReadsTheSameDbvForIt(ThdSweepLevelUnit unit, double value)
    {
        const double loadOhms = 4.0;
        double expected = unit.ToDbv(value, loadOhms);
        var mode = unit.IsPower() ? ThdSweepLevelMode.OutputPower : ThdSweepLevelMode.GeneratorVoltage;

        var sweep = new ThdSweepRequest(
            StartFreqHz: 20.0, EndFreqHz: 20_000.0, StepsPerOctave: 5,
            mode, LevelValue: value, unit, LoadOhms: loadOhms,
            ProcessLeft: true, ProcessRight: false,
            FftSize: 131_072, WindowType.Hann, AverageCount: 4, SampleRateHz: 192_000.0,
            MeasureNoiseFloor: true);
        Assert.Equal(expected, sweep.LevelDbv, 12);

        var response = new FrequencyResponseRequest(
            FrequencySpanHz: 96_000.0, ResolutionMultiplier: 1,
            mode, LevelValue: value, unit, LoadOhms: loadOhms,
            RightChannelIsReference: true, SmoothingDenominator: 24, AverageCount: 1, Continuous: false);
        Assert.Equal(expected, response.LevelDbv, 12);

        var stepped = new SteppedResponseRequest(
            StartFreqHz: 20.0, EndFreqHz: 20_000.0, StepsPerOctave: 5,
            mode, LevelValue: value, unit, LoadOhms: loadOhms,
            GenerateLeft: true, GenerateRight: true,
            FftSize: 32_768, WindowType.Hann, AverageCount: 1, SampleRateHz: 96_000.0,
            AdaptiveResolution: false, RightChannelIsReference: true);
        Assert.Equal(expected, stepped.LevelDbv, 12);

        var crosstalk = new CrosstalkRequest(
            StartFreqHz: 20.0, EndFreqHz: 20_000.0, StepsPerOctave: 5, CrosstalkDirection.Both,
            mode, LevelValue: value, unit, LoadOhms: loadOhms,
            FftSize: 32_768, WindowType.Hann, AverageCount: 1, SampleRateHz: 96_000.0,
            AdaptiveResolution: false);
        Assert.Equal(expected, crosstalk.LevelDbv, 12);

        var zout = new OutputImpedanceRequest(
            StartFreqHz: 20.0, EndFreqHz: 20_000.0, StepsPerOctave: 3,
            mode, LevelValue: value, unit, LoadOhms: loadOhms, SecondLoadOhms: 8.0, NominalOhms: 8.0,
            RightChannelIsReference: true,
            FftSize: 32_768, WindowType.Hann, AverageCount: 1, SampleRateHz: 96_000.0,
            AdaptiveResolution: false);
        Assert.Equal(expected, zout.LevelDbv, 12);

        var impulse = new ImpulseResponseRequest(
            FrequencySpanHz: 96_000.0, ResolutionMultiplier: 1,
            mode, LevelValue: value, unit, LoadOhms: loadOhms,
            SmoothingDenominator: 24, AverageCount: 1,
            ReferenceMode: ImpulseReferenceMode.None, ReferenceCalibrationId: null,
            GatePreMs: 1.0, GatePostMs: 20.0, GateWindow: WindowType.Hann,
            ExtractHarmonics: true, MaxHarmonicOrder: 5, Continuous: false);
        Assert.Equal(expected, impulse.LevelDbv, 12);
    }

    /// <summary>
    /// Milliwatts are a power, so they belong to the power mode exactly as watts do -- and the power
    /// mode needs one of the two. This is the server's half of the bug reported 2026-10-17: a level
    /// mode switched to DUT output power while the figure beside it was still a generator voltage.
    /// </summary>
    [Theory]
    [InlineData(ThdSweepLevelMode.GeneratorVoltage, ThdSweepLevelUnit.MilliWatt)]
    [InlineData(ThdSweepLevelMode.OutputVoltage, ThdSweepLevelUnit.MilliWatt)]
    [InlineData(ThdSweepLevelMode.GeneratorVoltage, ThdSweepLevelUnit.Watt)]
    [InlineData(ThdSweepLevelMode.OutputPower, ThdSweepLevelUnit.Dbv)]
    [InlineData(ThdSweepLevelMode.OutputPower, ThdSweepLevelUnit.Volt)]
    public void ASweep_WithAUnitThatDoesNotBelongToItsLevelMode_IsRejected(
        ThdSweepLevelMode mode, ThdSweepLevelUnit unit) =>
        Assert.Throws<ArgumentOutOfRangeException>(Sweep(mode, unit, 1.0).Validate);

    /// <summary>A negative power is the figure the report named, and no load makes it one.</summary>
    [Theory]
    [InlineData(ThdSweepLevelUnit.Watt, -20.0)]
    [InlineData(ThdSweepLevelUnit.MilliWatt, -20.0)]
    [InlineData(ThdSweepLevelUnit.Watt, 0.0)]
    public void ASweep_AskedForANonPositivePower_IsRejected(ThdSweepLevelUnit unit, double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            Sweep(ThdSweepLevelMode.OutputPower, unit, value).Validate);

    [Theory]
    [InlineData(ThdSweepLevelMode.OutputPower, ThdSweepLevelUnit.Watt)]
    [InlineData(ThdSweepLevelMode.OutputPower, ThdSweepLevelUnit.MilliWatt)]
    [InlineData(ThdSweepLevelMode.GeneratorVoltage, ThdSweepLevelUnit.Dbv)]
    [InlineData(ThdSweepLevelMode.OutputVoltage, ThdSweepLevelUnit.MilliVolt)]
    public void ASweep_WithAUnitItsModeAccepts_IsAllowed(ThdSweepLevelMode mode, ThdSweepLevelUnit unit) =>
        Sweep(mode, unit, 1.0).Validate();

    /// <summary>
    /// Every parameters record that carries a level mode must also carry the per-mode memory, and
    /// the same reflection reasoning as ProgrammableLoadParamsTests applies: a kind that stores the
    /// mode and one shared value is a kind where switching mode reinterprets the previous mode's
    /// number, which is the bug this was built for. A seventh kind must not be able to forget.
    /// </summary>
    [Fact]
    public void EveryKindThatCarriesALevelMode_AlsoCarriesThePerModeLevels()
    {
        var carrying = typeof(PerModeLevels).Assembly
            .GetTypes()
            .Where(t => t.GetProperty("LevelMode") is not null)
            .ToList();

        Assert.NotEmpty(carrying);

        foreach (Type type in carrying)
        {
            PropertyInfo? byMode = type.GetProperty("LevelByMode");
            Assert.True(byMode is not null, $"{type.Name} carries a level mode but not the per-mode levels.");
            Assert.Equal(typeof(PerModeLevels), byMode!.PropertyType);
        }
    }

    /// <summary>
    /// The per-mode levels ride on params_json as an object beside the active pair, and an older row
    /// that has no such object still deserializes -- null meaning "the panel keeps its defaults for
    /// the modes that are not selected", never a load failure.
    /// </summary>
    [Fact]
    public void PerModeLevels_RoundTripThroughParamsJson_AndAreOptional()
    {
        const string withMemory = """
            {"startFreqHz":20,"endFreqHz":20000,"stepsPerOctave":5,"levelMode":"OutputPower",
             "levelValue":250,"levelUnit":"MilliWatt","loadOhms":32,"processLeft":true,
             "processRight":false,"fftSize":131072,"window":"Hann","averageCount":4,
             "sampleRateHz":192000,"measureNoiseFloor":true,
             "levelByMode":{"generatorValue":-20,"generatorUnit":"Dbv","outputVoltageValue":1.5,
                            "outputVoltageUnit":"Volt","outputPowerValue":250,"outputPowerUnit":"MilliWatt"}}
            """;

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var parsed = JsonSerializer.Deserialize<ThdVsFrequencyParameters>(withMemory, options);

        Assert.NotNull(parsed!.LevelByMode);
        Assert.Equal(-20.0, parsed.LevelByMode!.GeneratorValue);
        Assert.Equal("Dbv", parsed.LevelByMode.GeneratorUnit);
        Assert.Equal(1.5, parsed.LevelByMode.OutputVoltageValue);
        Assert.Equal("MilliWatt", parsed.LevelByMode.OutputPowerUnit);
        Assert.Equal("MilliWatt", parsed.LevelUnit);

        // The same type as any bench wrote before the per-mode memory existed: the active pair and
        // nothing else.
        const string withoutMemory = """
            {"startFreqHz":20,"endFreqHz":20000,"stepsPerOctave":5,"levelMode":"OutputPower",
             "levelValue":250,"levelUnit":"MilliWatt","loadOhms":32,"processLeft":true,
             "processRight":false,"fftSize":131072,"window":"Hann","averageCount":4,
             "sampleRateHz":192000,"measureNoiseFloor":true}
            """;

        var older = JsonSerializer.Deserialize<ThdVsFrequencyParameters>(withoutMemory, options);
        Assert.Null(older!.LevelByMode);
        Assert.Equal("MilliWatt", older.LevelUnit);
    }

    private static ThdSweepRequest Sweep(ThdSweepLevelMode mode, ThdSweepLevelUnit unit, double value) =>
        new(
            StartFreqHz: 20.0, EndFreqHz: 20_000.0, StepsPerOctave: 5,
            mode, LevelValue: value, unit, LoadOhms: 8.0,
            ProcessLeft: true, ProcessRight: false,
            FftSize: 131_072, WindowType.Hann, AverageCount: 4, SampleRateHz: 192_000.0,
            MeasureNoiseFloor: true);
}
