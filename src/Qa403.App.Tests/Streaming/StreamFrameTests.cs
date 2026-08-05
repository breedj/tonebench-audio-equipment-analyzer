using Qa403.App.Streaming;
using Qa403.Dsp;

namespace Qa403.App.Tests.Streaming;

public class StreamFrameTests
{
    [Fact]
    public void Spectrum_frame_round_trips_header_and_columns()
    {
        float[] freqs = [995f, 1998.4f, 42000f];
        MinMax[] columns = [new(-100.0, -20.0), new(-95.5, -30.25), new(-180.0, -179.9)];

        byte[] frame = StreamFrame.EncodeSpectrum(StreamChannel.Right, seq: 42, x0: 10f, dx: 5.5f, freqs, columns);
        var header = StreamFrame.DecodeHeader(frame);

        Assert.Equal(StreamMsgType.Spectrum, header.MsgType);
        Assert.Equal(StreamChannel.Right, header.Channel);
        Assert.Equal(StreamFlags.Decimated, header.Flags);
        Assert.Equal(42u, header.Seq);
        Assert.Equal(10f, header.X0);
        Assert.Equal(5.5f, header.Dx);
        Assert.Equal((uint)columns.Length, header.NPoints);

        SpectrumColumn[] decoded = StreamFrame.DecodeSpectrumColumns(frame);
        Assert.Equal(columns.Length, decoded.Length);
        for (int i = 0; i < columns.Length; i++)
        {
            Assert.Equal(freqs[i], decoded[i].FreqHz, precision: 2);
            Assert.Equal(columns[i].Min, decoded[i].Value.Min, precision: 4);
            Assert.Equal(columns[i].Max, decoded[i].Value.Max, precision: 4);
        }
    }

    [Fact]
    public void Spectrum_frame_length_matches_header_plus_three_floats_per_point()
    {
        var columns = new MinMax[1600];
        var freqs = new float[1600];
        byte[] frame = StreamFrame.EncodeSpectrum(StreamChannel.Left, seq: 0, x0: 0f, dx: 0f, freqs, columns);

        Assert.Equal(StreamFrame.HeaderLength + (1600 * 3 * sizeof(float)), frame.Length);
    }

    [Fact]
    public void Log_spaced_spectrum_frame_carries_lo_hi_edge_frequencies_in_x0_dx()
    {
        float[] freqs = [12f, 8000f];
        MinMax[] columns = [new(-150.0, -60.0), new(-140.0, -70.0)];

        byte[] frame = StreamFrame.EncodeSpectrum(
            StreamChannel.Left, seq: 3, x0: 10f, dx: 96000f, freqs, columns, logSpaced: true);

        var header = StreamFrame.DecodeHeader(frame);
        Assert.True(header.Flags.HasFlag(StreamFlags.LogSpaced));
        Assert.True(header.Flags.HasFlag(StreamFlags.Decimated));
        Assert.Equal(10f, header.X0);
        Assert.Equal(96000f, header.Dx);
    }

    [Fact]
    public void Scope_frame_uses_requested_msg_type()
    {
        byte[] frame = StreamFrame.EncodeSpectrum(StreamChannel.Left, seq: 1, x0: 0f, dx: 1f, [500f], [new MinMax(-1, 1)], StreamMsgType.Scope);

        Assert.Equal(StreamMsgType.Scope, StreamFrame.DecodeHeader(frame).MsgType);
    }

    [Fact]
    public void EncodeSpectrum_RejectsMismatchedFreqAndColumnLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            StreamFrame.EncodeSpectrum(StreamChannel.Left, seq: 0, x0: 0f, dx: 0f, [1f, 2f], [new MinMax(-1, 1)]));
    }

    [Fact]
    public void Meters_frame_round_trips_scalars_and_harmonics()
    {
        Harmonic[] harmonics =
        [
            new Harmonic(Order: 1, Bin: 1000, AmplitudeVrms: 1.0, AmplitudeDbv: 0.0, Dbc: 0.0),
            new Harmonic(Order: 2, Bin: 2000, AmplitudeVrms: 0.01, AmplitudeDbv: -40.0, Dbc: -40.0),
            new Harmonic(Order: 3, Bin: 3000, AmplitudeVrms: 0.001, AmplitudeDbv: -60.0, Dbc: -60.0),
        ];

        byte[] frame = StreamFrame.EncodeMeters(
            StreamChannel.Left, seq: 7, fundamentalFreqHz: 1000f, binSizeHz: 1.46f,
            thdPercent: 1.001f, thdNPercent: 1.05f, snrDb: 95.5f, fundamentalDbv: -3.01f, noiseFloorDbv: -140f,
            peakDbv: -2.9f, totalRmsDbv: -3.05f,
            harmonics, otherPeaks: []);

        var header = StreamFrame.DecodeHeader(frame);
        Assert.Equal(StreamMsgType.Meters, header.MsgType);
        Assert.Equal(StreamFlags.None, header.Flags);
        Assert.Equal(1000f, header.X0);
        Assert.Equal(1.46f, header.Dx, precision: 4);
        Assert.Equal((uint)harmonics.Length, header.NPoints);

        var meters = StreamFrame.DecodeMeters(frame);
        Assert.Equal(1000f, meters.FundamentalFreqHz);
        Assert.Equal(1.46f, meters.BinSizeHz, precision: 4);
        Assert.Equal(1.001f, meters.ThdPercent, precision: 4);
        Assert.Equal(1.05f, meters.ThdNPercent, precision: 4);
        Assert.Equal(95.5f, meters.SnrDb, precision: 4);
        Assert.Equal(-3.01f, meters.FundamentalDbv, precision: 4);
        Assert.Equal(-140f, meters.NoiseFloorDbv, precision: 4);
        Assert.Equal(-2.9f, meters.PeakDbv, precision: 4);
        Assert.Equal(-3.05f, meters.TotalRmsDbv, precision: 4);

        Assert.Equal(3, meters.Harmonics.Length);
        Assert.Equal(1000f, meters.Harmonics[0].FreqHz);
        Assert.Equal(0f, meters.Harmonics[0].Dbc);
        Assert.Equal(2000f, meters.Harmonics[1].FreqHz);
        Assert.Equal(-40f, meters.Harmonics[1].Dbc);
        Assert.Equal(3000f, meters.Harmonics[2].FreqHz);
        Assert.Equal(-60f, meters.Harmonics[2].Dbc);

        Assert.Empty(meters.OtherPeaks);
    }

    [Fact]
    public void Meters_frame_round_trips_other_peaks_alongside_harmonics()
    {
        Harmonic[] harmonics = [new Harmonic(Order: 1, Bin: 1000, AmplitudeVrms: 1.0, AmplitudeDbv: 0.0, Dbc: 0.0)];
        (float FreqHz, float Dbc)[] otherPeaks = [(50f, -90f), (100f, -96f), (1234.5f, -55.5f)];

        byte[] frame = StreamFrame.EncodeMeters(
            StreamChannel.Right, seq: 9, fundamentalFreqHz: 1000f, binSizeHz: 1.0f,
            thdPercent: 0f, thdNPercent: 0f, snrDb: 90f, fundamentalDbv: 0f, noiseFloorDbv: -90f,
            peakDbv: 0f, totalRmsDbv: 0f,
            harmonics, otherPeaks);

        var header = StreamFrame.DecodeHeader(frame);
        Assert.Equal(1u, header.NPoints); // still just the harmonic count

        var meters = StreamFrame.DecodeMeters(frame);
        Assert.Single(meters.Harmonics);
        Assert.Equal(3, meters.OtherPeaks.Length);
        Assert.Equal(50f, meters.OtherPeaks[0].FreqHz);
        Assert.Equal(-90f, meters.OtherPeaks[0].Dbc, precision: 3);
        Assert.Equal(100f, meters.OtherPeaks[1].FreqHz);
        Assert.Equal(-96f, meters.OtherPeaks[1].Dbc, precision: 3);
        Assert.Equal(1234.5f, meters.OtherPeaks[2].FreqHz, precision: 1);
        Assert.Equal(-55.5f, meters.OtherPeaks[2].Dbc, precision: 3);
    }

    [Fact]
    public void Decode_throws_on_frame_shorter_than_header()
    {
        Assert.Throws<ArgumentException>(() => StreamFrame.DecodeHeader(new byte[10]));
    }
}
