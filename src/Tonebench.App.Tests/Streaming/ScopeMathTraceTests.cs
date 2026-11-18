using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Tonebench.App.Endpoints;
using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Streaming;

/// <summary>
/// The math channel on a record: the derived trace, its readings, and the two places it is
/// deliberately absent.
///
/// <para>The arithmetic itself is <see cref="WaveformMath"/>'s and is tested against closed forms
/// in <c>WaveformMathTests</c>. What is tested here is the seam — that the trace is computed on the
/// <b>samples</b> and shares the slice's geometry, that its readings are of the derived waveform
/// and not combined from the channels', and that nothing it can produce truncates a response.</para>
/// </summary>
public class ScopeMathTraceTests
{
    private const double SampleRateHz = 48_000.0;

    /// <summary>The app's own HTTP JSON options, so this tests the writer the endpoint really uses.</summary>
    private static JsonSerializerOptions SerializerOptions
    {
        get
        {
            var options = new JsonOptions();
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            return options.SerializerOptions;
        }
    }

    /// <summary>
    /// A record whose two channels are a 1 kHz sine and the same sine a millivolt different — the
    /// differential pair this whole feature exists for.
    /// </summary>
    private static ScopeRecord DifferentialPair(int frames, double difference = 0.001)
    {
        var left = new double[frames];
        var right = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double common = Math.Sin(2.0 * Math.PI * 1_000.0 * i / SampleRateHz);
            left[i] = common + (difference * Math.Sin(2.0 * Math.PI * 3_000.0 * i / SampleRateHz));
            right[i] = common;
        }

        return new ScopeRecord(
            [left, right], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
    }

    // -----------------------------------------------------------------------
    // The geometry
    // -----------------------------------------------------------------------

    /// <summary>
    /// The math trace is raw exactly where the waveform is raw and has the same column count where
    /// it is not, because the two are drawn against one x array.
    /// </summary>
    [Fact]
    public void TheMathTrace_HasTheSameShapeAsTheSliceItRidesOn()
    {
        var record = DifferentialPair(96_000);

        var columns = ScopeFrameBuilder.Overview(record, channel: 0);
        var mathColumns = ScopeFrameBuilder.MathTrace(record, columns, WaveformOperation.AMinusB);
        Assert.NotNull(mathColumns);
        Assert.Null(mathColumns!.Samples);
        Assert.Equal(columns.Columns!.Length, mathColumns.Columns!.Length);

        var raw = ScopeFrameBuilder.Window(record, channel: 0, fromS: 0.0, toS: 0.001);
        Assert.True(raw.IsRaw);
        var mathRaw = ScopeFrameBuilder.MathTrace(record, raw, WaveformOperation.AMinusB);
        Assert.NotNull(mathRaw);
        Assert.Null(mathRaw!.Columns);
        Assert.Equal(raw.Samples!.Length, mathRaw.Samples!.Length);
    }

    /// <summary>
    /// The case the feature exists for, through the whole seam: a difference a thousandth of either
    /// channel, measured on the samples.
    ///
    /// <para><b>This is also the test that would fail if the trace were ever computed from the
    /// drawn columns.</b> Both extremes of a column are the near-1 V envelope, so an envelope
    /// subtraction reads about zero where the true difference is the whole signal — and the
    /// assertion below is on the <i>readings</i>, which is what a reader would act on.</para>
    /// </summary>
    [Fact]
    public void ADifferentialPair_IsMeasuredOnTheSamples_NotOnTheColumnsItIsDrawnAs()
    {
        var record = DifferentialPair(96_000);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);
        var math = ScopeFrameBuilder.MathTrace(record, slice, WaveformOperation.AMinusB);

        Assert.NotNull(math);
        Assert.Equal(0.001, math!.Measurements.MaxV, 6);
        Assert.Equal(-0.001, math.Measurements.MinV, 6);
        Assert.Equal(0.002, math.Measurements.PeakToPeakV, 6);
        Assert.Equal(0.001 / Math.Sqrt(2.0), math.Measurements.RmsV, 6);
        Assert.Equal(3_000.0, math.Measurements.FrequencyHz!.Value, 0);

        // And the derived waveform is in every column of the drawn trace, not only in the record:
        // each column spans several periods of the 3 kHz difference, so both of its extremes are
        // there.
        Assert.All(math.Columns!, column =>
        {
            Assert.Equal(0.001, column.Max, 5);
            Assert.Equal(-0.001, column.Min, 5);
        });

        // What subtracting one envelope from the other would have given instead -- the fault this
        // whole arrangement exists to prevent. Measured here rather than asserted as a tolerance:
        // the two channels' column maxima differ by a flat -0.001 at every single column, so the
        // "difference" would read as a constant -1 mV offset where it is really a 3 kHz sine of
        // +/-1 mV. Not merely coarse: the wrong sign, the wrong shape, and no minimum at all.
        var columnsB = ScopeFrameBuilder.Overview(record, channel: 1).Columns!;
        double envelopeLow = double.MaxValue, envelopeHigh = double.MinValue;
        for (int i = 0; i < columnsB.Length; i++)
        {
            double difference = slice.Columns![i].Max - columnsB[i].Max;
            envelopeLow = Math.Min(envelopeLow, difference);
            envelopeHigh = Math.Max(envelopeHigh, difference);
        }

        Assert.Equal(-0.001, envelopeLow, 5);
        Assert.Equal(-0.001, envelopeHigh, 5);
        Assert.Equal(0.0, envelopeHigh - envelopeLow, 5);
    }

    /// <summary>The unit travels with the trace, because the readings record names every field in volts.</summary>
    [Theory]
    [InlineData(WaveformOperation.AMinusB, WaveformMathUnit.Volt)]
    [InlineData(WaveformOperation.ATimesB, WaveformMathUnit.VoltSquared)]
    [InlineData(WaveformOperation.ADivB, WaveformMathUnit.Ratio)]
    [InlineData(WaveformOperation.DerivativeA, WaveformMathUnit.VoltPerSecond)]
    [InlineData(WaveformOperation.IntegralB, WaveformMathUnit.VoltSecond)]
    public void TheTraceCarriesTheUnitItsNumbersAreIn(WaveformOperation operation, WaveformMathUnit unit)
    {
        var record = DifferentialPair(8_192);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        var math = ScopeFrameBuilder.MathTrace(record, slice, operation);

        Assert.NotNull(math);
        Assert.Equal(unit, math!.Unit);
        Assert.Equal(operation, math.Operation);
    }

    // -----------------------------------------------------------------------
    // The gate
    // -----------------------------------------------------------------------

    /// <summary>
    /// The gated readings are of the gate, and they go through one <c>Combine</c> with the slice's
    /// so the two cannot disagree about what the math trace is.
    /// </summary>
    [Fact]
    public void AGate_MeasuresTheStretchItNames()
    {
        // Left a ramp, right zero: A - B is the ramp, so a window's extremes are its own ends.
        var left = new double[48_000];
        for (int i = 0; i < left.Length; i++)
            left[i] = i;
        var record = new ScopeRecord(
            [left, new double[48_000]], SampleRateHz,
            FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);

        // Half-open: [0.1, 0.2) at 48 kS/s is frames 4800..9599.
        var gated = ScopeFrameBuilder.MathGate(record, WaveformOperation.AMinusB, 0.1, 0.2);

        Assert.NotNull(gated);
        Assert.Equal(4_800.0, gated!.MinV, 6);
        Assert.Equal(9_599.0, gated.MaxV, 6);
    }

    /// <summary>
    /// A gate naming nothing answers null, exactly as <see cref="ScopeFrameBuilder.Measure"/> does
    /// — never the whole window's figures, which under a heading saying they are the cursors' is
    /// the one answer worse than no figure.
    /// </summary>
    [Fact]
    public void AGateThatNamesNothing_IsRefusedRatherThanFallingBackToTheWholeWindow()
    {
        var record = DifferentialPair(8_192);

        Assert.Null(ScopeFrameBuilder.MathGate(record, WaveformOperation.AMinusB, 0.5, 0.6));
        Assert.Null(ScopeFrameBuilder.MathGate(record, WaveformOperation.AMinusB, 0.1, 0.05));
        Assert.Null(ScopeFrameBuilder.MathGate(record, WaveformOperation.AMinusB, double.NaN, 0.01));
    }

    // -----------------------------------------------------------------------
    // Absent rather than wrong
    // -----------------------------------------------------------------------

    /// <summary>
    /// An operation that needs the right input gets no trace at all on a one-channel record.
    ///
    /// <para>Which is the honest answer: computing against an array of zeros would draw A itself
    /// and label it A − B. The screen says which box to tick.</para>
    /// </summary>
    [Fact]
    public void AnOperationThatNeedsAChannelTheRecordHasNot_IsAbsentRatherThanComputedAgainstZeros()
    {
        var mono = new ScopeRecord(
            [new double[4_096]], SampleRateHz,
            FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
        var slice = ScopeFrameBuilder.Overview(mono, channel: 0);

        Assert.Null(ScopeFrameBuilder.MathTrace(mono, slice, WaveformOperation.AMinusB));
        Assert.Null(ScopeFrameBuilder.MathTrace(mono, slice, WaveformOperation.ATimesB));
        Assert.Null(ScopeFrameBuilder.MathGate(mono, WaveformOperation.AMinusB, 0.0, 0.01));

        // The one-channel operations are unaffected, which is what makes them worth having as
        // entries of their own rather than as A - 0.
        Assert.NotNull(ScopeFrameBuilder.MathTrace(mono, slice, WaveformOperation.NegateA));
        Assert.NotNull(ScopeFrameBuilder.MathTrace(mono, slice, WaveformOperation.DerivativeA));
    }

    // -----------------------------------------------------------------------
    // The NaN rule
    // -----------------------------------------------------------------------

    /// <summary>
    /// Nothing the math channel can produce truncates a response — the rule the whole scope's
    /// status records are built around, arriving here from a new direction.
    ///
    /// <para>A math trace puts <b>floats in an array</b> as well as optional readings, and
    /// <c>System.Text.Json</c> will not write a NaN in either: it finds out part way through, after
    /// the 200 and everything before it have gone out, so the body stops mid-document and the
    /// browser discards the lot. A ratio against a denominator through zero is exactly how one
    /// would get there, so this serializes through the app's own options and parses the document
    /// back.</para>
    /// </summary>
    [Fact]
    public void AMathSliceSerializes_WhateverTheDenominatorDid()
    {
        // The right channel crosses zero on every cycle, and sits exactly on it at the start.
        var left = new double[2_048];
        var right = new double[2_048];
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = 1.0;
            right[i] = Math.Sin(2.0 * Math.PI * 100.0 * i / SampleRateHz);
        }

        var record = new ScopeRecord(
            [left, right], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);
        var math = ScopeFrameBuilder.MathTrace(record, slice, WaveformOperation.ADivB);

        Assert.NotNull(math);
        Assert.True(math!.ClampedSamples > 0, "a sine denominator has to have railed somewhere");

        var wire = new ScopeMathSlice(
            math.Operation.ToString(),
            math.Unit.ToString(),
            math.Samples,
            math.Columns?.Select(c => (float)c.Min).ToArray(),
            math.Columns?.Select(c => (float)c.Max).ToArray(),
            math.Measurements,
            math.Measurements,
            math.ClampedSamples);

        string json = JsonSerializer.Serialize(wire, SerializerOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("ADivB", document.RootElement.GetProperty("operation").GetString());
        Assert.Equal("Ratio", document.RootElement.GetProperty("unit").GetString());
    }

    /// <summary>
    /// A derivative and an integral serialize too, which is the other direction the same hazard
    /// comes from: a derivative of a flat record has no edges, no period and no settled levels, so
    /// most of its readings are absent.
    /// </summary>
    [Fact]
    public void ADerivativeOfAFlatRecord_HasNoTimingReadingsAndStillSerializes()
    {
        var flat = new double[4_096];
        Array.Fill(flat, 0.25);
        var record = new ScopeRecord(
            [flat, flat], SampleRateHz, FirstIndex: 0, TriggerPosition: 0, Epoch: 0, Spliced: false, Forced: false);
        var slice = ScopeFrameBuilder.Overview(record, channel: 0);

        var math = ScopeFrameBuilder.MathTrace(record, slice, WaveformOperation.DerivativeA);

        Assert.NotNull(math);
        Assert.Null(math!.Measurements.PeriodS);
        Assert.Null(math.Measurements.RiseTimeS);
        Assert.Null(math.Measurements.TopV);

        string json = JsonSerializer.Serialize(math.Measurements, SerializerOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("periodS").ValueKind);
    }
}
