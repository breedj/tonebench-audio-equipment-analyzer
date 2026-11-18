using System.Reflection;
using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The wire shape of an oscilloscope start, mapped into the measurement record field by field.
///
/// <para><b>This is the rule that has already cost this project an evening once.</b> A field present
/// on the wire record and missing from <c>ToMeasurement</c> is not an error — it is a
/// <i>default</i>, so the run succeeds and returns a perfectly plausible measurement of settings
/// the user did not ask for. <c>ChirpStartHz</c> went three values deep on the frequency-response
/// screen that way, and the tell was a right-channel level that had not moved between runs.</para>
/// </summary>
public class OscilloscopeStartRequestTests
{
    private static OscilloscopeStartRequest Wire() =>
        new(
            SampleRateHz: 96_000.0,
            RecordSeconds: 0.05,
            PreTriggerFraction: 0.75,
            Mode: "Normal",
            TriggerKind: "PulseWidth",
            TriggerSource: "ChannelB",
            TriggerSlope: "Falling",
            TriggerLevelV: 0.125,
            TriggerHysteresisV: 0.002,
            HoldoffSeconds: 0.003,
            TriggerLevelLowV: -0.25,
            PulseWidthLoSeconds: 30e-6,
            PulseWidthHiSeconds: 120e-6,
            DropoutSeconds: 0.4,
            AutoTimeoutSeconds: 0.25,
            Accumulation: "Average",
            AverageCount: 8,
            ProcessLeft: false,
            ProcessRight: true,
            GeneratorLeft: null,
            GeneratorRight: null,
            SnapToBin: null);

    [Fact]
    public void EveryFieldOnTheWireReachesTheMeasurement()
    {
        var generator = new GeneratorConfig(
            new ChannelGeneratorConfig(true, [new GeneratorTone(1_000.0, -6.0)]),
            new ChannelGeneratorConfig(false, []));

        var measurement = Wire().ToMeasurement(
            ScopeRunMode.Normal,
            TriggerKind.PulseWidth,
            TriggerSource.ChannelB,
            TriggerSlope.Falling,
            RecordAccumulation.Average,
            generator);

        Assert.Equal(96_000.0, measurement.SampleRateHz);
        Assert.Equal(0.05, measurement.RecordSeconds);
        Assert.Equal(0.75, measurement.PreTriggerFraction);
        Assert.Equal(ScopeRunMode.Normal, measurement.Mode);
        Assert.Equal(TriggerKind.PulseWidth, measurement.TriggerKind);
        Assert.Equal(TriggerSource.ChannelB, measurement.TriggerSource);
        Assert.Equal(TriggerSlope.Falling, measurement.TriggerSlope);
        Assert.Equal(0.125, measurement.TriggerLevelV);
        Assert.Equal(0.002, measurement.TriggerHysteresisV);
        Assert.Equal(0.003, measurement.HoldoffSeconds);
        Assert.Equal(-0.25, measurement.TriggerLevelLowV);
        Assert.Equal(30e-6, measurement.PulseWidthLoSeconds);
        Assert.Equal(120e-6, measurement.PulseWidthHiSeconds);
        Assert.Equal(0.4, measurement.DropoutSeconds);
        Assert.Equal(0.25, measurement.AutoTimeoutSeconds);
        Assert.Equal(RecordAccumulation.Average, measurement.Accumulation);
        Assert.Equal(8, measurement.AverageCount);
        Assert.False(measurement.ProcessLeft);
        Assert.True(measurement.ProcessRight);
        Assert.Same(generator, measurement.Generator);
    }

    [Fact]
    public void AnAbsentUpperWidth_StaysAbsentRatherThanBecomingAFigure()
    {
        // Null is the "longer than T" condition rather than a missing number, and it is nullable all
        // the way out because an infinity is not a JSON number -- the rule that truncates a 200 part
        // way through writing it. A `?? something` anywhere on this path would quietly bound a
        // condition that must not be bounded, and the pulse that never ends would stop firing.
        var measurement = (Wire() with { PulseWidthHiSeconds = null }).ToMeasurement(
            ScopeRunMode.Normal, TriggerKind.PulseWidth, TriggerSource.ChannelB,
            TriggerSlope.Falling, RecordAccumulation.Normal, null);

        Assert.Null(measurement.PulseWidthHiSeconds);
    }

    [Fact]
    public void BothRecordsStillHaveTheFieldsTheMappingTestChecks()
    {
        // The wire record carries three fields the measurement does not -- the two generator
        // channels and the bin snap, which are folded into one GeneratorConfig before the mapping --
        // and the measurement carries the GeneratorConfig itself. Hence 22 against 20. A field added
        // to either without the other fails here rather than defaulting silently, which is exactly
        // what it did the first time: the dropout timeout was added to the measurement record and
        // this refused to compile until it was on the wire too.
        Assert.Equal(22, ConstructorParameterCount(typeof(OscilloscopeStartRequest)));
        Assert.Equal(20, ConstructorParameterCount(typeof(OscilloscopeRequest)));
    }

    [Fact]
    public void TheSavedParametersRecordCarriesEveryRunSettingButTheOneItHasNoBusinessWith()
    {
        // A stored measurement has to be able to say what produced it, so every setting of the run
        // is also a field of what is written to params_json -- except the Auto timeout, which is
        // how long the analyzer WAITED rather than anything about the capture. What that setting
        // produced is already recorded, as `Forced`.
        var exceptions = new HashSet<string>(StringComparer.Ordinal) { "AutoTimeoutSeconds" };

        var runSettings = typeof(OscilloscopeRequest)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .Where(name => !exceptions.Contains(name));

        var saved = typeof(Tonebench.Data.OscilloscopeParameters)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = runSettings.Where(name => !saved.Contains(name)).ToArray();
        Assert.True(missing.Length == 0, $"OscilloscopeParameters is missing: {string.Join(", ", missing)}");
    }

    private static int ConstructorParameterCount(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .MaxBy(c => c.GetParameters().Length)!
            .GetParameters()
            .Length;
}
