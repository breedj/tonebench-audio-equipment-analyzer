using Qa403.App.Services;
using Qa403.App.Streaming;

namespace Qa403.App.Tests.Streaming;

/// <summary>
/// The chirp and the stepped sine emit identical frame types, and the browser keeps a separate state
/// for each. Without a marker a frame has no origin, so it lands on whichever screen happens to be
/// open — which is how running one measurement put its curves on the other's chart.
/// </summary>
public class ResponseFrameOriginTests
{
    private static FrequencyResponseTrace Trace() =>
        new(
            FrequencyResponseTraceKind.Gain,
            StartFreqHz: 20.0,
            EndFreqHz: 20_000.0,
            FreqHz: [20f, 20_000f],
            MagnitudeMinDb: [0f, 0f],
            MagnitudeMaxDb: [0f, 0f],
            PhaseDeg: [0f, -45f],
            Coherence: [1f, 1f],
            GroupDelayS: [0f, 0f],
            PhaseDelayS: [0f, 0f],
            OneDb: new BandwidthReading(1.0, true, 20.0, 20_000.0, 19_980.0, false, false),
            ThreeDb: new BandwidthReading(3.0, false, 20.0, 20_000.0, 19_980.0, false, false),
            PeakDb: 0.0,
            PeakFreqHz: 1_000.0);

    private static StreamFlags FlagsOf(byte[] frame) => StreamFrame.DecodeHeader(frame).Flags;

    [Fact]
    public void ASteppedTraceFrame_SaysSo()
    {
        Assert.True(FlagsOf(StreamFrame.EncodeFrequencyResponse(Trace(), StreamFlags.SteppedResponse)).HasFlag(StreamFlags.SteppedResponse));
        Assert.False(FlagsOf(StreamFrame.EncodeFrequencyResponse(Trace())).HasFlag(StreamFlags.SteppedResponse));
    }

    [Fact]
    public void ASteppedProgressFrame_SaysSo()
    {
        var stepped = StreamFrame.EncodeFrequencyResponseProgress(3, 50, FrequencyResponsePhase.Sweeping, -20.0, StreamFlags.SteppedResponse);
        var chirp = StreamFrame.EncodeFrequencyResponseProgress(3, 50, FrequencyResponsePhase.Measuring, -20.0);

        Assert.True(FlagsOf(stepped).HasFlag(StreamFlags.SteppedResponse));
        Assert.False(FlagsOf(chirp).HasFlag(StreamFlags.SteppedResponse));
    }

    [Fact]
    public void ACrosstalkTraceFrame_SaysSo_AndCarriesItsOwnKind()
    {
        var trace = Trace() with { Kind = FrequencyResponseTraceKind.CrosstalkRightToLeft };
        var frame = StreamFrame.EncodeFrequencyResponse(trace, StreamFlags.Crosstalk);

        Assert.True(FlagsOf(frame).HasFlag(StreamFlags.Crosstalk));
        Assert.False(FlagsOf(frame).HasFlag(StreamFlags.SteppedResponse));
        Assert.Equal(FrequencyResponseTraceKind.CrosstalkRightToLeft, StreamFrame.DecodeFrequencyResponse(frame).Kind);
    }

    [Fact]
    public void ACrosstalkProgressFrame_SaysSo()
    {
        var frame = StreamFrame.EncodeFrequencyResponseProgress(3, 50, FrequencyResponsePhase.Sweeping, -20.0, StreamFlags.Crosstalk);
        Assert.Equal(StreamFlags.Crosstalk, FlagsOf(frame));
    }

    [Fact]
    public void AnOutputImpedanceFrame_SaysSo_OnTheImpedanceFrames()
    {
        var trace = new ImpedanceTrace(
            ImpedanceTraceKind.Impedance, 20.0, 20_000.0,
            [20.0f, 20_000.0f], [-30.0f, -14.0f], [-30.0f, -14.0f], [0.0f, 80.0f], [1.0f, 1.0f],
            PeakDbOhms: -14.0, PeakFreqHz: 20_000.0);

        var frame = StreamFrame.EncodeImpedance(trace, StreamFlags.OutputImpedance);
        Assert.True(FlagsOf(frame).HasFlag(StreamFlags.OutputImpedance));
        Assert.True(FlagsOf(frame).HasFlag(StreamFlags.Decimated));
        var header = StreamFrame.DecodeHeader(frame);
        Assert.Equal(StreamMsgType.Impedance, header.MsgType);
        Assert.Equal((StreamChannel)ImpedanceTraceKind.Impedance, header.Channel);

        var progress = StreamFrame.EncodeImpedanceProgress(3, 50, FrequencyResponsePhase.WaitingForLoadChange, -20.0, StreamFlags.OutputImpedance);
        Assert.Equal(StreamFlags.OutputImpedance, FlagsOf(progress));

        // The impedance runner's own frames carry no origin bit, as before.
        Assert.False(FlagsOf(StreamFrame.EncodeImpedance(trace)).HasFlag(StreamFlags.OutputImpedance));
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamFrame.EncodeImpedance(trace, StreamFlags.Crosstalk | StreamFlags.OutputImpedance));
    }

    [Fact]
    public void AnOriginThatIsNotOne_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamFrame.EncodeFrequencyResponse(Trace(), StreamFlags.Held));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamFrame.EncodeFrequencyResponse(Trace(), StreamFlags.SteppedResponse | StreamFlags.Crosstalk));
    }

    [Fact]
    public void TheChirpsFramesAreUnchanged()
    {
        // The flag is additive, so an older client keeps reading these exactly as it did. Asserting
        // the whole frame rather than just the flag: the marker had to fit in spare header space
        // without moving a single payload byte.
        var trace = StreamFrame.EncodeFrequencyResponse(Trace());
        Assert.Equal(StreamFlags.Decimated | StreamFlags.LogSpaced, FlagsOf(trace));

        var progress = StreamFrame.EncodeFrequencyResponseProgress(3, 50, FrequencyResponsePhase.Measuring, -20.0);
        Assert.Equal(StreamFlags.None, FlagsOf(progress));
    }

    [Fact]
    public void TheFlagChangesNothingButTheFlags()
    {
        // A stepped frame has to decode to the same numbers as a chirp one — the marker rides in the
        // header's spare bits, so the payload is byte-for-byte identical.
        var chirp = StreamFrame.EncodeFrequencyResponse(Trace());
        var stepped = StreamFrame.EncodeFrequencyResponse(Trace(), StreamFlags.SteppedResponse);

        Assert.Equal(chirp.Length, stepped.Length);
        Assert.Equal(chirp[4..], stepped[4..]);

        var decoded = StreamFrame.DecodeFrequencyResponse(stepped);
        Assert.Equal(FrequencyResponseTraceKind.Gain, decoded.Kind);
        Assert.Equal(-45f, decoded.PhaseDeg[1]);
    }
}
