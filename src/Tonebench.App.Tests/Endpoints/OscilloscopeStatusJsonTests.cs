using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The oscilloscope status has to survive being written as JSON, above all before anything has been
/// captured.
///
/// <para><b>Why this needs its own test, and why this measurement needs it most.</b>
/// <c>System.Text.Json</c> will not write a <c>double.NaN</c> — it is not a JSON number — and it
/// finds out part way through the response, after the 200 and everything before the offending
/// field have already gone out. The body stops mid-object, the browser's <c>JSON.parse</c> throws,
/// the whole status is discarded, and the screen looks as though the run did nothing. A scope
/// carries more optional readings than anything else in the app — a trigger offset and a record
/// span that do not exist until something has been caught, and in Roll mode never — so it has the
/// most opportunities to produce one.</para>
///
/// <para>Nothing else catches it: the runner tests read the <c>Status</c> object directly and never
/// serialize it, so a NaN sails through every one of them.</para>
/// </summary>
public class OscilloscopeStatusJsonTests
{
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

    private static OscilloscopeStatus Status(double? triggerOffsetS, double? fromS, double? toS) =>
        new(
            FrequencyResponsePhase.WaitingForTrigger,
            Error: null,
            SampleRateHz: 192_000.0,
            RecordFrames: 8192,
            PreTriggerFrames: 4096,
            RecordsTaken: 0,
            HasRecord: false,
            Spliced: false,
            Forced: false,
            triggerOffsetS,
            fromS,
            toS);

    [Fact]
    public void AStatusWithNothingCapturedYet_SerializesWhole()
    {
        // The state the screen opens in, and the one a Normal run sits in for as long as nothing
        // crosses the level. Every optional reading absent.
        string json = JsonSerializer.Serialize(Status(null, null, null), SerializerOptions);

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("triggerOffsetS").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recordFromS").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recordToS").ValueKind);
        Assert.Equal("WaitingForTrigger", root.GetProperty("phase").GetString());
    }

    [Fact]
    public void AStatusWithARecord_SerializesWhole()
    {
        string json = JsonSerializer.Serialize(Status(0.005, -0.005, 0.0149), SerializerOptions);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(0.005, parsed.RootElement.GetProperty("triggerOffsetS").GetDouble(), 9);
        Assert.Equal(-0.005, parsed.RootElement.GetProperty("recordFromS").GetDouble(), 9);

        // The capture-rate pair, both present and camelCase. The client declares recordsCommitted
        // required, so a field that went out under another name would arrive as undefined and the
        // badge would quietly read the collected count as though it were the committed one.
        Assert.Equal(0, parsed.RootElement.GetProperty("recordsTaken").GetInt32());
        Assert.Equal(0, parsed.RootElement.GetProperty("recordsCommitted").GetInt32());
    }

    [Fact]
    public void ANonFiniteReading_IsWhatThisTestExistsToForbid()
    {
        // Asserted rather than assumed, so the test says why the nullable fields are nullable. If
        // this ever stops throwing, the rule has changed and the comments above are wrong.
        Assert.ThrowsAny<Exception>(
            () => JsonSerializer.Serialize(Status(double.NaN, null, null), SerializerOptions));
    }

    [Fact]
    public void EveryRunnerPhase_HasAStringOnTheWire()
    {
        // The client's union types are strings; a phase that arrived as a number would be an
        // unhandled case on a screen rather than a parse error, which is harder to notice.
        foreach (var phase in Enum.GetValues<FrequencyResponsePhase>())
        {
            var status = Status(null, null, null) with { Phase = phase };
            string json = JsonSerializer.Serialize(status, SerializerOptions);

            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.String, parsed.RootElement.GetProperty("phase").ValueKind);
            Assert.Equal(phase.ToString(), parsed.RootElement.GetProperty("phase").GetString());
        }
    }

    [Fact]
    public void ARecordsOwnSpanIsNeverNonFinite()
    {
        // The source the two span fields come from. A zero-length or rate-less record would make
        // TimeAt divide by zero and put an infinity on the wire -- so the record type is what has
        // to refuse it, and this is where that is pinned.
        var record = new ScopeRecord(
            [new double[16], new double[16]],
            SampleRateHz: 192_000.0,
            FirstIndex: 1000,
            TriggerPosition: 1004.25,
            Epoch: 0,
            Spliced: false,
            Forced: false);

        var (fromS, toS) = record.Span;

        Assert.True(double.IsFinite(fromS));
        Assert.True(double.IsFinite(toS));
        Assert.True(double.IsFinite(record.TriggerOffsetS));
        Assert.True(fromS < 0.0, "a record with pre-trigger starts before t = 0");
    }
}
