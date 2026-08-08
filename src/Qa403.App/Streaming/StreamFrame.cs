using System.Buffers.Binary;
using Qa403.Dsp;

namespace Qa403.App.Streaming;

/// <summary>Frame kind, wire byte per docs/qa403-app-stack-spec.md §4.2.</summary>
public enum StreamMsgType : byte
{
    Spectrum = 1,
    Scope = 2,
    Meters = 3,
}

public enum StreamChannel : byte
{
    Left = 0,
    Right = 1,
}

[Flags]
public enum StreamFlags : ushort
{
    None = 0,

    /// <summary>Payload is nPoints (freqHz,min,max) f32 triples rather than nPoints raw f32 samples.</summary>
    Decimated = 1 << 0,

    /// <summary>
    /// Informational only: columns are log-frequency-spaced rather than uniform. A client does
    /// NOT need this to plot the data — each column's own frequency is in the payload (see
    /// <see cref="StreamFrame.EncodeSpectrum"/>) precisely because a continuous log-spacing
    /// formula silently diverges from the server's actual (integer-bin, floor-clamped) column
    /// boundaries whenever there are few source bins relative to the column count — e.g. a small
    /// FFT size decimated to ~1600 columns. This flag is only a hint for axis-scale choice
    /// (log vs. linear); x0/dx are the span's low/high edge frequencies for that purpose, not a
    /// per-column step.
    /// </summary>
    LogSpaced = 1 << 1,

    /// <summary>
    /// This frame covers only the client's current zoom range, not the whole spectrum — the same
    /// column budget spread over a narrower span, so zooming resolves detail the overview cannot.
    /// Sent only to the client that asked for it (see <see cref="SpectrumBroadcastHub"/>), and only
    /// once it is zoomed in far enough to be worth the extra encode.
    ///
    /// A client must NOT use x0/dx from a zoomed frame to size its axis — they are the zoom
    /// window's edges, not the spectrum's. Only an un-flagged (overview) frame describes the full
    /// span.
    /// </summary>
    Zoomed = 1 << 2,
}

/// <summary>
/// Encodes/decodes the binary WebSocket frame from docs/qa403-app-stack-spec.md §4.2:
///
/// <code>
/// offset  type      field
/// 0       u8        msgType     1=spectrum 2=scope 3=meters
/// 1       u8        channel     0=L 1=R
/// 2       u16       flags       bit0: decimated, bit1: log-spaced (informational, see StreamFlags.LogSpaced)
/// 4       u32       seq
/// 8       f32       x0          first freq (Hz)/time (s), or (decimated) the span's low edge freq (Hz)
/// 12      f32       dx          bin width (Hz)/sample period (s), or (decimated) the span's high edge freq (Hz)
/// 16      u32       nPoints
/// 20      f32[]     payload     if decimated: nPoints*(freqHz,min,max) triples — see EncodeSpectrum
/// </code>
///
/// All fields little-endian. Each decimated column carries its own frequency rather than relying
/// on the client to reconstruct one from x0/dx/nPoints: see <see cref="StreamFlags.LogSpaced"/>
/// for why a formula-based reconstruction is unsafe. <see cref="StreamMsgType.Meters"/> repurposes
/// the header the same way (x0 = fundamental frequency in Hz, dx unused/0) but its payload is not
/// a min/max series — see <see cref="EncodeMeters"/> for that layout.
/// </summary>
public static class StreamFrame
{
    public const int HeaderLength = 20;

    /// <summary>
    /// Encodes a decimated spectrum/scope frame: header + nPoints (freqHz,min,max) f32 triples.
    /// <paramref name="columnFreqsHz"/> must be exactly <paramref name="columns"/>.Length long.
    /// <paramref name="x0"/>/<paramref name="dx"/> are the span's low/high edge frequencies —
    /// informational (e.g. for setting a chart's axis bounds), not a reconstruction formula.
    /// </summary>
    public static byte[] EncodeSpectrum(
        StreamChannel channel, uint seq, float x0, float dx, ReadOnlySpan<float> columnFreqsHz, ReadOnlySpan<MinMax> columns,
        StreamMsgType msgType = StreamMsgType.Spectrum, bool logSpaced = false, bool zoomed = false)
    {
        if (columnFreqsHz.Length != columns.Length)
            throw new ArgumentException("columnFreqsHz must be exactly columns.Length long.", nameof(columnFreqsHz));

        int nPoints = columns.Length;
        byte[] buffer = new byte[HeaderLength + (nPoints * 3 * sizeof(float))];
        var flags = StreamFlags.Decimated
            | (logSpaced ? StreamFlags.LogSpaced : StreamFlags.None)
            | (zoomed ? StreamFlags.Zoomed : StreamFlags.None);
        WriteHeader(buffer, msgType, channel, flags, seq, x0, dx, (uint)nPoints);

        int offset = HeaderLength;
        for (int i = 0; i < nPoints; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), columnFreqsHz[i]);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + 4, 4), (float)columns[i].Min);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + 8, 4), (float)columns[i].Max);
            offset += 12;
        }

        return buffer;
    }

    /// <summary>
    /// Encodes a meters frame. Payload (not min/max — flags has no Decimated bit set):
    /// <code>
    /// 20  f32  thdPercent
    /// 24  f32  thdNPercent
    /// 28  f32  snrDb
    /// 32  f32  fundamentalDbv
    /// 36  f32  noiseFloorDbv
    /// 40  f32  peakDbv          -- true instantaneous time-domain peak, RMS-equivalent (peakVpk/sqrt2), dBV
    /// 44  f32  totalRmsDbv      -- true broadband time-domain RMS (not just the fundamental bin), dBV
    /// 48  u32  otherPeakCount
    /// 52  (f32 freqHz, f32 dbc) * nPoints           -- one entry per Harmonic, order 1..N in order
    /// ..  (f32 freqHz, f32 dbc) * otherPeakCount    -- non-harmonic peaks (hum, IMD, spurs), strongest first
    /// </code>
    /// x0 = fundamental frequency (Hz), dx = FFT bin size (Hz), nPoints = harmonic count.
    /// <paramref name="otherPeaks"/> is whatever <c>PeakFinder.FindPeaks</c> found outside the
    /// fundamental/harmonics — dbc is relative to the fundamental, same convention as harmonics,
    /// for a fair side-by-side comparison.
    /// </summary>
    public static byte[] EncodeMeters(
        StreamChannel channel, uint seq, float fundamentalFreqHz, float binSizeHz,
        float thdPercent, float thdNPercent, float snrDb, float fundamentalDbv, float noiseFloorDbv,
        float peakDbv, float totalRmsDbv,
        ReadOnlySpan<Harmonic> harmonics, ReadOnlySpan<(float FreqHz, float Dbc)> otherPeaks)
    {
        const int scalarsAndCountLength = (7 * sizeof(float)) + sizeof(uint);
        int nPoints = harmonics.Length;
        int otherPeakCount = otherPeaks.Length;
        byte[] buffer = new byte[HeaderLength + scalarsAndCountLength + ((nPoints + otherPeakCount) * 2 * sizeof(float))];
        WriteHeader(buffer, StreamMsgType.Meters, channel, StreamFlags.None, seq, fundamentalFreqHz, binSizeHz, (uint)nPoints);

        int offset = HeaderLength;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), thdPercent); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), thdNPercent); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), snrDb); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), fundamentalDbv); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), noiseFloorDbv); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), peakDbv); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), totalRmsDbv); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)otherPeakCount); offset += 4;

        foreach (var h in harmonics)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), h.Order * fundamentalFreqHz);
            offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)h.Dbc);
            offset += 4;
        }

        foreach (var p in otherPeaks)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), p.FreqHz);
            offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), p.Dbc);
            offset += 4;
        }

        return buffer;
    }

    private static void WriteHeader(
        Span<byte> buffer, StreamMsgType msgType, StreamChannel channel, StreamFlags flags, uint seq, float x0, float dx, uint nPoints)
    {
        buffer[0] = (byte)msgType;
        buffer[1] = (byte)channel;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), (ushort)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), seq);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(8, 4), x0);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(12, 4), dx);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(16, 4), nPoints);
    }

    public static StreamFrameHeader DecodeHeader(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength)
            throw new ArgumentException($"Frame too short: {frame.Length} bytes, header is {HeaderLength}.", nameof(frame));

        return new StreamFrameHeader(
            (StreamMsgType)frame[0],
            (StreamChannel)frame[1],
            (StreamFlags)BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(2, 2)),
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(8, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(12, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(16, 4)));
    }

    /// <summary>Decodes a spectrum/scope frame's (freqHz,min,max) payload. <paramref name="frame"/> must have <see cref="StreamFlags.Decimated"/> set.</summary>
    public static SpectrumColumn[] DecodeSpectrumColumns(ReadOnlySpan<byte> frame)
    {
        var header = DecodeHeader(frame);
        if ((header.Flags & StreamFlags.Decimated) == 0)
            throw new ArgumentException("Frame is not a decimated min/max payload.", nameof(frame));

        var result = new SpectrumColumn[header.NPoints];
        int offset = HeaderLength;
        for (int i = 0; i < header.NPoints; i++)
        {
            float freqHz = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4));
            double min = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset + 4, 4));
            double max = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset + 8, 4));
            result[i] = new SpectrumColumn(freqHz, new MinMax(min, max));
            offset += 12;
        }

        return result;
    }

    public static DecodedMeters DecodeMeters(ReadOnlySpan<byte> frame)
    {
        var header = DecodeHeader(frame);
        if (header.MsgType != StreamMsgType.Meters)
            throw new ArgumentException("Frame is not a meters frame.", nameof(frame));

        int offset = HeaderLength;
        float thdPercent = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float thdNPercent = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float snrDb = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float fundamentalDbv = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float noiseFloorDbv = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float peakDbv = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float totalRmsDbv = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        uint otherPeakCount = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset, 4)); offset += 4;

        var harmonics = new (float FreqHz, float Dbc)[header.NPoints];
        for (int i = 0; i < header.NPoints; i++)
        {
            float freqHz = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
            float dbc = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
            harmonics[i] = (freqHz, dbc);
        }

        var otherPeaks = new (float FreqHz, float Dbc)[otherPeakCount];
        for (int i = 0; i < otherPeakCount; i++)
        {
            float freqHz = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
            float dbc = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
            otherPeaks[i] = (freqHz, dbc);
        }

        return new DecodedMeters(
            header.X0, header.Dx, thdPercent, thdNPercent, snrDb, fundamentalDbv, noiseFloorDbv, peakDbv, totalRmsDbv,
            harmonics, otherPeaks);
    }
}

public readonly record struct StreamFrameHeader(
    StreamMsgType MsgType, StreamChannel Channel, StreamFlags Flags, uint Seq, float X0, float Dx, uint NPoints);

public readonly record struct SpectrumColumn(float FreqHz, MinMax Value);

public readonly record struct DecodedMeters(
    float FundamentalFreqHz, float BinSizeHz, float ThdPercent, float ThdNPercent, float SnrDb, float FundamentalDbv,
    float NoiseFloorDbv, float PeakDbv, float TotalRmsDbv,
    (float FreqHz, float Dbc)[] Harmonics, (float FreqHz, float Dbc)[] OtherPeaks);
