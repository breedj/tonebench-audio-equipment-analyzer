using System.Text.Json;
using System.Text.Json.Serialization;
using Tonebench.App.Endpoints;
using Tonebench.Data;
using Tonebench.Device;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// The four facts a stored oscilloscope capture carries about itself, read out of its own
/// <c>params_json</c>.
///
/// <para><b>The first case is the regression and is the reason this class exists.</b> These were
/// read by deserializing the whole <see cref="OscilloscopeParameters"/> record, which carries a
/// <see cref="GeneratorConfig"/> whose kind, noise shape and chirp smoothing are <i>enums written
/// as strings</i> by the app's own converter. A bare <c>JsonSerializerDefaults.Web</c> options
/// instance has no converter for those, so every stored scope capture — the one kind that stores a
/// generator — threw and fell back to the defaults: a reference trace drawn with its trigger at
/// sample 0 and both channels shown, with nothing anywhere saying so.</para>
/// </summary>
public class StoredWaveformFactsTests
{
    /// <summary>
    /// A document in exactly the shape the save path writes — serialized through the app's own
    /// options, so the enums are strings here for the same reason they are strings on a bench.
    /// </summary>
    private static readonly JsonSerializerOptions SaveOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static string StoredDocument(bool left = true, bool right = false, double preTrigger = 0.25) =>
        JsonSerializer.Serialize(
            new OscilloscopeParameters(
                SampleRateHz: 192_000,
                RecordSeconds: 0.02,
                PreTriggerFraction: preTrigger,
                Mode: "Normal",
                TriggerKind: "Edge",
                TriggerSource: "ChannelA",
                TriggerSlope: "Rising",
                TriggerLevelV: 0.0,
                TriggerHysteresisV: 0.002,
                HoldoffSeconds: 0.0,
                ProcessLeft: left,
                ProcessRight: right,
                Generator: new GeneratorConfig(
                    new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(1000.0, -6.0)])
                    {
                        Kind = GeneratorKind.Tones,
                        Chirp = new EchoChirpSettings(),
                        Noise = new WhiteNoiseSettings(),
                    },
                    ChannelGeneratorConfig.Off)),
            SaveOptions);

    private static StoredPcm Pcm(int frames, double rateHz = 192_000) =>
        new([new double[frames], new double[frames]], rateHz);

    [Fact]
    public void ADocumentCarryingAGeneratorWithStringEnums_IsStillRead()
    {
        // Proof the document really does hold the enums as strings, i.e. that this test reproduces
        // a bench document rather than a convenient one.
        string json = StoredDocument();
        Assert.Contains("\"Tones\"", json);
        Assert.Contains("\"None\"", json);

        var facts = MeasurementsEndpoints.ReadStoredWaveformFacts(json);

        Assert.Equal(0.25, facts.PreTriggerFraction, 12);
        Assert.True(facts.ProcessLeft);
        Assert.False(facts.ProcessRight);
    }

    [Fact]
    public void TheChannelSet_IsTheMeasurementsOwn_NotWhateverTheBlobHolds()
    {
        var leftOnly = MeasurementsEndpoints.ReadStoredWaveformFacts(StoredDocument(left: true, right: false));
        Assert.True(leftOnly.Captures(0));
        Assert.False(leftOnly.Captures(1));

        var rightOnly = MeasurementsEndpoints.ReadStoredWaveformFacts(StoredDocument(left: false, right: true));
        Assert.False(rightOnly.Captures(0));
        Assert.True(rightOnly.Captures(1));

        var both = MeasurementsEndpoints.ReadStoredWaveformFacts(StoredDocument(left: true, right: true));
        Assert.True(both.Captures(0));
        Assert.True(both.Captures(1));
    }

    [Fact]
    public void ADocumentThatAsksForNeitherInput_DrawsBoth()
    {
        // A capture that happened asked for at least one input, so neither means a document this
        // build cannot make sense of. A trace too many is a better answer than a history item that
        // silently does not appear.
        var facts = MeasurementsEndpoints.ReadStoredWaveformFacts(StoredDocument(left: false, right: false));

        Assert.True(facts.Captures(0));
        Assert.True(facts.Captures(1));
    }

    [Fact]
    public void TheTriggerOffset_IsThePreTriggerFractionOfTheStoredRecord()
    {
        var facts = MeasurementsEndpoints.ReadStoredWaveformFacts(StoredDocument(preTrigger: 0.25));

        // 0.25 of a 20 ms record at 192 kS/s: 960 frames, 240 of them ahead of the trigger.
        Assert.Equal(240.0 / 192_000.0, MeasurementsEndpoints.WaveformTriggerOffsetS(facts, Pcm(960)), 12);
    }

    [Fact]
    public void AZeroSampleRate_ReadsNoOffsetRatherThanDividingByIt()
    {
        var facts = MeasurementsEndpoints.ReadStoredWaveformFacts(StoredDocument());

        Assert.Equal(0.0, MeasurementsEndpoints.WaveformTriggerOffsetS(facts, Pcm(960, rateHz: 0.0)));
    }

    [Fact]
    public void BothFlags_AreReadFromTheDocumentRatherThanInferred()
    {
        // Spliced and Forced are facts nothing in the samples says: a splice is two pieces of the
        // stream side by side and looks like a waveform, and a forced record looks triggered.
        string json = """
            {"preTriggerFraction":0.5,"processLeft":true,"processRight":true,"spliced":true,"forced":true}
            """;

        var facts = MeasurementsEndpoints.ReadStoredWaveformFacts(json);

        Assert.True(facts.Spliced);
        Assert.True(facts.Forced);
    }

    [Fact]
    public void AMemberInPascalCase_IsReadToo()
    {
        // The app writes camelCase; a hand-edited or older document may hold the other spelling,
        // and a trigger offset silently read as zero is the fault this whole class is about.
        var facts = MeasurementsEndpoints.ReadStoredWaveformFacts(
            """{"PreTriggerFraction":0.5,"ProcessLeft":false,"ProcessRight":true,"Spliced":true}""");

        Assert.Equal(0.5, facts.PreTriggerFraction, 12);
        Assert.False(facts.ProcessLeft);
        Assert.True(facts.ProcessRight);
        Assert.True(facts.Spliced);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{}")]
    public void ADocumentThatCannotBeRead_ReadsAsUnknownRatherThanThrowing(string json)
    {
        var facts = MeasurementsEndpoints.ReadStoredWaveformFacts(json);

        Assert.Equal(0.0, facts.PreTriggerFraction);
        Assert.True(facts.Captures(0));
        Assert.True(facts.Captures(1));
        Assert.False(facts.Spliced);
        Assert.False(facts.Forced);
    }
}
