using Tonebench.App.Endpoints;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Dsp;

namespace Tonebench.App.Tests.Endpoints;

/// <summary>
/// Which inputs a record answers for — the Acquisition panel's Left and Right boxes, which are a
/// scope's CH1/CH2 buttons.
///
/// <para><b>Why the filter is here and not at the capture.</b> The record keeps both inputs: the
/// buffer holds them, the trigger may watch either, and what is written to <c>capture.pcm</c> is
/// what the analyzer saw — a CSV column headed <c>right_volt</c> has to be the right input, which a
/// record cut down to one unlabelled channel could not promise. So the <i>screen</i> is where a
/// measurement's own channel set applies, and without this filter a left-only capture drew an
/// orange trace along the right input's DC offset (reported 2026-11-17, with only Left ticked).</para>
/// </summary>
public class OscilloscopeCapturedChannelsTests
{
    private static OscilloscopeRequest Request(bool left, bool right) =>
        new(
            SampleRateHz: 48_000,
            RecordSeconds: 0.02,
            PreTriggerFraction: 0.25,
            Mode: ScopeRunMode.Normal,
            TriggerKind: TriggerKind.Edge,
            TriggerSource: TriggerSource.ChannelA,
            TriggerSlope: TriggerSlope.Rising,
            TriggerLevelV: 0.0,
            ProcessLeft: left,
            ProcessRight: right);

    [Fact]
    public void OnlyTheChannelsTheMeasurementAsksFor_AreAnsweredFor()
    {
        var leftOnly = Request(left: true, right: false);
        Assert.True(OscilloscopeEndpoints.WasCaptured(leftOnly, 0));
        Assert.False(OscilloscopeEndpoints.WasCaptured(leftOnly, 1));

        var rightOnly = Request(left: false, right: true);
        Assert.False(OscilloscopeEndpoints.WasCaptured(rightOnly, 0));
        Assert.True(OscilloscopeEndpoints.WasCaptured(rightOnly, 1));

        var both = Request(left: true, right: true);
        Assert.True(OscilloscopeEndpoints.WasCaptured(both, 0));
        Assert.True(OscilloscopeEndpoints.WasCaptured(both, 1));
    }

    [Fact]
    public void WithNoRequestToAsk_EveryChannelIsAnsweredFor()
    {
        // A record parked by a measurement this runner has since been handed away from. Nothing
        // here knows better than the record itself, and answering with nothing would be an empty
        // chart rather than an honest one.
        Assert.True(OscilloscopeEndpoints.WasCaptured(null, 0));
        Assert.True(OscilloscopeEndpoints.WasCaptured(null, 1));
    }

    [Fact]
    public void WithNeitherChannelAsked_EveryChannelIsAnsweredFor()
    {
        // Refused by Validate long before it reaches a record, so this is the state that cannot
        // arrive -- and the answer to it is still a picture rather than a blank one.
        var neither = Request(left: false, right: false);
        Assert.True(OscilloscopeEndpoints.WasCaptured(neither, 0));
        Assert.True(OscilloscopeEndpoints.WasCaptured(neither, 1));
    }
}
