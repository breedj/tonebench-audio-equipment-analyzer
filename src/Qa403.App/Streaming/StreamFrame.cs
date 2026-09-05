using System.Buffers.Binary;
using Qa403.App.Services;
using Qa403.Dsp;

namespace Qa403.App.Streaming;

/// <summary>Frame kind, wire byte per docs/qa403-app-stack-spec.md §4.2.</summary>
public enum StreamMsgType : byte
{
    Spectrum = 1,
    Scope = 2,
    Meters = 3,

    /// <summary>One finished THD-vs-frequency sweep point — see <see cref="StreamFrame.EncodeSweepPoint"/>.</summary>
    SweepPoint = 4,

    /// <summary>Where a running sweep is. Carries no per-channel data; channel is always 0 — see <see cref="StreamFrame.EncodeSweepProgress"/>.</summary>
    SweepProgress = 5,

    /// <summary>
    /// One finished frequency-response curve. The header's channel byte carries a
    /// <see cref="FrequencyResponseTraceKind"/> (0 left, 1 right, 2 gain) rather than an input
    /// channel — the gain trace belongs to no single input. See
    /// <see cref="StreamFrame.EncodeFrequencyResponse"/>.
    /// </summary>
    FrequencyResponse = 6,

    /// <summary>How many records a running frequency-response measurement has taken — see <see cref="StreamFrame.EncodeFrequencyResponseProgress"/>.</summary>
    FrequencyResponseProgress = 7,
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

    /// <summary>
    /// Meters-frame-only: this channel's <see cref="Qa403.App.Streaming.SpectrumPipelineOptions.SingleShot"/>
    /// capture just completed on this tick, and the pipeline has stopped broadcasting further
    /// frames for it (see <see cref="Qa403.App.Streaming.SpectrumPipeline"/>'s single-shot hold). A
    /// client waiting on a single shot should treat this as "capture complete" — no more frames for
    /// this channel are coming until the next arm.
    /// </summary>
    Held = 1 << 3,

    /// <summary>
    /// Response-frame-only: this frame came from <see cref="Qa403.App.Services.SteppedResponseRunner"/>
    /// rather than <see cref="Qa403.App.Services.FrequencyResponseRunner"/>.
    ///
    /// <para>The two measurements produce identical frame types and a client keeps a separate state
    /// for each, so without this a frame has no origin and lands on whichever screen happens to be
    /// open — a long stepped run painting the chirp's chart the moment you switch away to look at an
    /// old result. Routing on the client's own idea of which screen it is showing is a guess; this
    /// is the fact.</para>
    ///
    /// <para>Backward compatible: an older client ignores flag bits it does not know, and the
    /// chirp's frames are byte-identical to what they were before this existed.</para>
    /// </summary>
    SteppedResponse = 1 << 4,
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
/// 20      f32[]     payload     if decimated: nPoints*(freqHz,min,max) triples — see EncodeSpectrum;
///                                otherwise (scope): nPoints raw samples — see EncodeScope
/// </code>
///
/// All fields little-endian. Each decimated column carries its own frequency rather than relying
/// on the client to reconstruct one from x0/dx/nPoints: see <see cref="StreamFlags.LogSpaced"/>
/// for why a formula-based reconstruction is unsafe. <see cref="StreamMsgType.Meters"/> repurposes
/// the header the same way (x0 = fundamental frequency in Hz, dx unused/0) but its payload is not
/// a min/max series — see <see cref="EncodeMeters"/> for that layout. <see cref="StreamMsgType.Scope"/>
/// is never decimated: x0 = 0, dx = sample period (s), payload is nPoints raw f32 volts.
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
    /// 52  u32  averagesTaken    -- how many spectra have contributed so far, capped at the configured target; 0 if averaging is off
    /// 56  (f32 freqHz, f32 dbc) * nPoints           -- one entry per Harmonic, order 1..N in order
    /// ..  (f32 freqHz, f32 dbc) * otherPeakCount    -- non-harmonic peaks (hum, IMD, spurs), strongest first
    /// </code>
    /// x0 = fundamental frequency (Hz), dx = FFT bin size (Hz), nPoints = harmonic count.
    /// <paramref name="otherPeaks"/> is whatever <c>PeakFinder.FindPeaks</c> found outside the
    /// fundamental/harmonics — dbc is relative to the fundamental, same convention as harmonics,
    /// for a fair side-by-side comparison. <paramref name="averagesTaken"/> defaults to 0 so
    /// existing positional call sites (e.g. StreamFrameTests) keep compiling unchanged.
    /// <paramref name="held"/> sets <see cref="StreamFlags.Held"/> — see its remarks.
    /// </summary>
    public static byte[] EncodeMeters(
        StreamChannel channel, uint seq, float fundamentalFreqHz, float binSizeHz,
        float thdPercent, float thdNPercent, float snrDb, float fundamentalDbv, float noiseFloorDbv,
        float peakDbv, float totalRmsDbv,
        ReadOnlySpan<Harmonic> harmonics, ReadOnlySpan<(float FreqHz, float Dbc)> otherPeaks, uint averagesTaken = 0,
        bool held = false)
    {
        const int scalarsAndCountLength = (7 * sizeof(float)) + (2 * sizeof(uint));
        int nPoints = harmonics.Length;
        int otherPeakCount = otherPeaks.Length;
        byte[] buffer = new byte[HeaderLength + scalarsAndCountLength + ((nPoints + otherPeakCount) * 2 * sizeof(float))];
        WriteHeader(buffer, StreamMsgType.Meters, channel, held ? StreamFlags.Held : StreamFlags.None, seq, fundamentalFreqHz, binSizeHz, (uint)nPoints);

        int offset = HeaderLength;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), thdPercent); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), thdNPercent); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), snrDb); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), fundamentalDbv); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), noiseFloorDbv); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), peakDbv); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), totalRmsDbv); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)otherPeakCount); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), averagesTaken); offset += 4;

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

    /// <summary>
    /// Encodes a scope (time-domain) frame: header + nPoints raw f32 samples in volts, one per
    /// point. Not decimated — <see cref="StreamFlags.Decimated"/> stays unset, so unlike
    /// <see cref="EncodeSpectrum"/> the payload is a flat sample array, not (freq,min,max) triples.
    /// x0 is always 0: each frame is a fresh, self-contained snapshot of the latest samples (like a
    /// real scope's single-shot capture), not a continuous timeline a client accumulates across
    /// frames. dx is the sample period (s) = 1/sampleRateHz.
    /// </summary>
    public static byte[] EncodeScope(StreamChannel channel, uint seq, float dx, ReadOnlySpan<float> samples)
    {
        int nPoints = samples.Length;
        byte[] buffer = new byte[HeaderLength + (nPoints * sizeof(float))];
        WriteHeader(buffer, StreamMsgType.Scope, channel, StreamFlags.None, seq, 0f, dx, (uint)nPoints);

        int offset = HeaderLength;
        for (int i = 0; i < nPoints; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), samples[i]);
            offset += 4;
        }

        return buffer;
    }

    /// <summary>
    /// Encodes one finished sweep point. Payload:
    /// <code>
    /// 20  f32  thdPercent
    /// 24  f32  thdNPercent
    /// 28  f32  snrDb
    /// 32  f32  fundamentalDbv
    /// 36  f32  gainDb          -- fundamentalDbv - the generator amplitude actually used
    /// 40  f32  powerWatt       -- V1^2 / load; 0 when no load was given
    /// 44  f32  d6PlusPercent   -- 6th harmonic and above, power-summed
    /// 48  f32  noisePercent    -- generator-off residual as a % of this point's fundamental; 0 if the noise pass was skipped
    /// 52  f32  generatorDbv    -- the level this point was played at; constant on a frequency sweep, the x axis on an amplitude one
    /// 56  u32  harmonicCount   -- how many of D2..D5 follow (may be short near Nyquist)
    /// 60  f32 * harmonicCount  -- D2..D5 as a % of the fundamental
    /// ..  u32  fftPreviewCount
    /// ..  (f32 freqHz, f32 dbvMin, f32 dbvMax) * fftPreviewCount  -- see SweepPreviewSeries
    /// ..  u32  scopePreviewCount
    /// ..  (f32 timeS, f32 vMin, f32 vMax) * scopePreviewCount     -- see SweepPreviewSeries
    /// </code>
    /// x0 = the point's frequency (Hz), dx = 0, nPoints = harmonicCount, seq = the point's index in
    /// the sweep, channel = 0/1. One frame per channel per point, so a stereo sweep sends two.
    /// A count of 0 (both preview arrays absent, e.g. <see cref="Qa403.App.Streaming.SpectrumPipelineOptions.CapturePreview"/>
    /// was off) is a valid, empty payload — decoding never assumes either count is nonzero.
    /// </summary>
    public static byte[] EncodeSweepPoint(ThdSweepPoint point, uint pointIndex)
    {
        const int scalarsAndCountLength = (9 * sizeof(float)) + sizeof(uint);
        int harmonicCount = point.HarmonicPercent.Count;
        int fftCount = point.PreviewFft?.X.Length ?? 0;
        int scopeCount = point.PreviewScope?.X.Length ?? 0;

        int length = HeaderLength + scalarsAndCountLength + (harmonicCount * sizeof(float))
            + sizeof(uint) + (fftCount * 3 * sizeof(float))
            + sizeof(uint) + (scopeCount * 3 * sizeof(float));
        byte[] buffer = new byte[length];

        var channel = point.Channel == 0 ? StreamChannel.Left : StreamChannel.Right;
        WriteHeader(buffer, StreamMsgType.SweepPoint, channel, StreamFlags.None, pointIndex, (float)point.FreqHz, 0f, (uint)harmonicCount);

        int offset = HeaderLength;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)point.ThdPercent); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)point.ThdNPercent); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)point.SnrDb); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)point.FundamentalDbv); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)point.GainDb); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)point.PowerWatt); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)point.D6PlusPercent); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)point.NoisePercent); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)point.GeneratorDbv); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)harmonicCount); offset += 4;

        foreach (double percent in point.HarmonicPercent)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)percent);
            offset += 4;
        }

        offset = WriteSweepPreview(buffer, offset, point.PreviewFft);
        WriteSweepPreview(buffer, offset, point.PreviewScope);

        return buffer;
    }

    private static int WriteSweepPreview(byte[] buffer, int offset, SweepPreviewSeries? preview)
    {
        int count = preview?.X.Length ?? 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)count); offset += 4;

        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), preview!.X[i]); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), preview.Min[i]); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), preview.Max[i]); offset += 4;
        }

        return offset;
    }

    /// <summary>
    /// Encodes a sweep progress frame: x0 = the point currently being measured on the sweep's own
    /// axis — hertz for a <see cref="SweepAxis.Frequency"/> sweep, generator volts for a
    /// <see cref="SweepAxis.Amplitude"/> one, and 0 when not on a point — dx = the total point
    /// count, nPoints = the index reached, seq = the phase as its <see cref="ThdSweepPhase"/>
    /// ordinal. No payload.
    ///
    /// One x slot for two possible units is deliberate: the frame is a progress readout, and the
    /// client already knows which axis it started (it chose the endpoint). Splitting it into two
    /// message types would double the decoder for a label.
    ///
    /// The phase rides in <c>seq</c> rather than in <see cref="StreamFlags"/> because it is an
    /// enumeration, not a set of independent bits — a sweep is determining its level, or measuring
    /// the noise floor, or sweeping, never two at once.
    /// </summary>
    public static byte[] EncodeSweepProgress(int index, int total, double currentX, ThdSweepPhase phase)
    {
        byte[] buffer = new byte[HeaderLength];
        WriteHeader(
            buffer, StreamMsgType.SweepProgress, StreamChannel.Left, StreamFlags.None,
            (uint)phase, (float)currentX, total, (uint)Math.Max(0, index));

        return buffer;
    }

    /// <summary>
    /// Encodes one frequency-response curve. Header: x0/dx are the band's low and high edge
    /// frequencies, nPoints the column count, channel the <see cref="FrequencyResponseTraceKind"/>.
    /// Payload, all little-endian:
    /// <code>
    /// per column (20 bytes x nPoints):
    ///   f32  freqHz
    ///   f32  magnitudeMinDb
    ///   f32  magnitudeMaxDb
    ///   f32  phaseDeg
    ///   f32  coherence
    /// then two bandwidth readings (-1 dB, then -3 dB), 20 bytes each:
    ///   f32  dropDb
    ///   f32  lowerHz
    ///   f32  upperHz
    ///   f32  bandwidthHz
    ///   u8   lowerClamped
    ///   u8   upperClamped
    ///   u16  (padding)
    /// then:
    ///   f32  peakDb
    ///   f32  peakFreqHz
    /// </code>
    ///
    /// Both magnitude values are sent, and a client must draw both: the max as the line and the min
    /// as a band under it. Plotting the max alone redraws a rippled response as a curve riding along
    /// the top of its own ripple — the same aggregation bias the spectrum path's min/max band exists
    /// to avoid.
    /// </summary>
    public static byte[] EncodeFrequencyResponse(FrequencyResponseTrace trace, bool stepped = false)
    {
        int columns = trace.FreqHz.Length;
        if (trace.MagnitudeMinDb.Length != columns || trace.MagnitudeMaxDb.Length != columns
            || trace.PhaseDeg.Length != columns || trace.Coherence.Length != columns)
        {
            throw new ArgumentException("Every trace array must be the same length.", nameof(trace));
        }

        const int perColumn = 5 * sizeof(float);
        const int perBandwidth = (4 * sizeof(float)) + 4;
        const int trailer = 2 * sizeof(float);

        byte[] buffer = new byte[HeaderLength + (columns * perColumn) + (2 * perBandwidth) + trailer];
        WriteHeader(
            buffer, StreamMsgType.FrequencyResponse, (byte)trace.Kind,
            StreamFlags.Decimated | StreamFlags.LogSpaced | (stepped ? StreamFlags.SteppedResponse : StreamFlags.None),
            seq: 0,
            (float)trace.StartFreqHz, (float)trace.EndFreqHz, (uint)columns);

        int offset = HeaderLength;
        for (int i = 0; i < columns; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), trace.FreqHz[i]);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + 4, 4), trace.MagnitudeMinDb[i]);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + 8, 4), trace.MagnitudeMaxDb[i]);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + 12, 4), trace.PhaseDeg[i]);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + 16, 4), trace.Coherence[i]);
            offset += perColumn;
        }

        offset = WriteBandwidth(buffer, offset, trace.OneDb);
        offset = WriteBandwidth(buffer, offset, trace.ThreeDb);

        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), (float)trace.PeakDb);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + 4, 4), (float)trace.PeakFreqHz);

        return buffer;
    }

    /// <summary>
    /// How many records a running measurement has taken. Header only: seq is the
    /// <see cref="FrequencyResponsePhase"/> ordinal, nPoints the records taken, dx the total, and x0
    /// the generator level in dBV. Same shape as <see cref="EncodeSweepProgress"/>, and for the same
    /// reason — the phase is an enumeration, not a set of independent bits, so it rides in seq
    /// rather than in the flags.
    ///
    /// <para>The level is here rather than left to the status endpoint because in output-voltage
    /// mode it is <i>solved</i> by the probe, so it is the one number a reader most wants during a
    /// run — and nothing polls status while one is going, which left the readout showing whatever
    /// the previous run had settled on.</para>
    /// </summary>
    public static byte[] EncodeFrequencyResponseProgress(
        int taken, int total, FrequencyResponsePhase phase, double generatorAmpDbv, bool stepped = false)
    {
        byte[] buffer = new byte[HeaderLength];
        WriteHeader(
            buffer, StreamMsgType.FrequencyResponseProgress, (byte)StreamChannel.Left,
            stepped ? StreamFlags.SteppedResponse : StreamFlags.None,
            (uint)phase, (float)generatorAmpDbv, total, (uint)Math.Max(0, taken));

        return buffer;
    }

    private static int WriteBandwidth(Span<byte> buffer, int offset, BandwidthReading reading)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(offset, 4), (float)reading.DropDb);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(offset + 4, 4), (float)reading.LowerHz);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(offset + 8, 4), (float)reading.UpperHz);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(offset + 12, 4), (float)reading.BandwidthHz);
        buffer[offset + 16] = reading.LowerClamped ? (byte)1 : (byte)0;
        buffer[offset + 17] = reading.UpperClamped ? (byte)1 : (byte)0;
        buffer[offset + 18] = 0;
        buffer[offset + 19] = 0;
        return offset + 20;
    }

    private static void WriteHeader(
        Span<byte> buffer, StreamMsgType msgType, StreamChannel channel, StreamFlags flags, uint seq, float x0, float dx, uint nPoints)
        => WriteHeader(buffer, msgType, (byte)channel, flags, seq, x0, dx, nPoints);

    private static void WriteHeader(
        Span<byte> buffer, StreamMsgType msgType, byte channel, StreamFlags flags, uint seq, float x0, float dx, uint nPoints)
    {
        buffer[0] = (byte)msgType;
        buffer[1] = channel;
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

    /// <summary>Decodes a scope frame's raw f32 sample payload. <paramref name="frame"/> must be a <see cref="StreamMsgType.Scope"/> frame.</summary>
    public static float[] DecodeScopeSamples(ReadOnlySpan<byte> frame)
    {
        var header = DecodeHeader(frame);
        if (header.MsgType != StreamMsgType.Scope)
            throw new ArgumentException("Frame is not a scope frame.", nameof(frame));

        var result = new float[header.NPoints];
        int offset = HeaderLength;
        for (int i = 0; i < header.NPoints; i++)
        {
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4));
            offset += 4;
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
        uint averagesTaken = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset, 4)); offset += 4;

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
            harmonics, otherPeaks, averagesTaken);
    }

    /// <summary>Decodes a <see cref="StreamMsgType.SweepPoint"/> frame — see <see cref="EncodeSweepPoint"/> for the layout.</summary>
    public static DecodedSweepPoint DecodeSweepPoint(ReadOnlySpan<byte> frame)
    {
        var header = DecodeHeader(frame);
        if (header.MsgType != StreamMsgType.SweepPoint)
            throw new ArgumentException($"Frame is {header.MsgType}, not a sweep point.", nameof(frame));

        int offset = HeaderLength;
        float thdPercent = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float thdNPercent = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float snrDb = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float fundamentalDbv = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float gainDb = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float powerWatt = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float d6PlusPercent = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float noisePercent = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        float generatorDbv = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        uint harmonicCount = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset, 4)); offset += 4;

        var harmonicPercent = new float[harmonicCount];
        for (int i = 0; i < harmonicCount; i++)
        {
            harmonicPercent[i] = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4));
            offset += 4;
        }

        var previewFft = ReadSweepPreview(frame, ref offset);
        var previewScope = ReadSweepPreview(frame, ref offset);

        return new DecodedSweepPoint(
            header.X0, header.Channel, header.Seq, thdPercent, thdNPercent, snrDb, fundamentalDbv,
            gainDb, powerWatt, d6PlusPercent, noisePercent, generatorDbv, harmonicPercent, previewFft, previewScope);
    }

    private static SweepPreviewSeries? ReadSweepPreview(ReadOnlySpan<byte> frame, ref int offset)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset, 4));
        offset += 4;
        if (count == 0)
            return null;

        var x = new float[count];
        var min = new float[count];
        var max = new float[count];
        for (int i = 0; i < count; i++)
        {
            x[i] = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
            min[i] = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
            max[i] = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)); offset += 4;
        }

        return new SweepPreviewSeries(x, min, max);
    }

    /// <summary>Decodes a <see cref="StreamMsgType.SweepProgress"/> frame — see <see cref="EncodeSweepProgress"/>.</summary>
    public static DecodedSweepProgress DecodeSweepProgress(ReadOnlySpan<byte> frame)
    {
        var header = DecodeHeader(frame);
        if (header.MsgType != StreamMsgType.SweepProgress)
            throw new ArgumentException($"Frame is {header.MsgType}, not sweep progress.", nameof(frame));

        return new DecodedSweepProgress((int)header.NPoints, (int)header.Dx, header.X0, (ThdSweepPhase)header.Seq);
    }

    /// <summary>Decodes a <see cref="StreamMsgType.FrequencyResponse"/> frame — see <see cref="EncodeFrequencyResponse"/>.</summary>
    public static FrequencyResponseTrace DecodeFrequencyResponse(ReadOnlySpan<byte> frame)
    {
        var header = DecodeHeader(frame);
        if (header.MsgType != StreamMsgType.FrequencyResponse)
            throw new ArgumentException($"Frame is {header.MsgType}, not a frequency response.", nameof(frame));

        int columns = (int)header.NPoints;
        var freqHz = new float[columns];
        var magMin = new float[columns];
        var magMax = new float[columns];
        var phaseDeg = new float[columns];
        var coherence = new float[columns];

        int offset = HeaderLength;
        for (int i = 0; i < columns; i++)
        {
            freqHz[i] = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4));
            magMin[i] = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset + 4, 4));
            magMax[i] = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset + 8, 4));
            phaseDeg[i] = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset + 12, 4));
            coherence[i] = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset + 16, 4));
            offset += 20;
        }

        (var oneDb, offset) = ReadBandwidth(frame, offset);
        (var threeDb, offset) = ReadBandwidth(frame, offset);

        float peakDb = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4));
        float peakFreqHz = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset + 4, 4));

        return new FrequencyResponseTrace(
            (FrequencyResponseTraceKind)(byte)header.Channel,
            header.X0, header.Dx,
            freqHz, magMin, magMax, phaseDeg, coherence,
            oneDb, threeDb, peakDb, peakFreqHz);
    }

    /// <summary>Decodes a <see cref="StreamMsgType.FrequencyResponseProgress"/> frame — see <see cref="EncodeFrequencyResponseProgress"/>.</summary>
    public static DecodedFrequencyResponseProgress DecodeFrequencyResponseProgress(ReadOnlySpan<byte> frame)
    {
        var header = DecodeHeader(frame);
        if (header.MsgType != StreamMsgType.FrequencyResponseProgress)
            throw new ArgumentException($"Frame is {header.MsgType}, not frequency-response progress.", nameof(frame));

        return new DecodedFrequencyResponseProgress(
            (int)header.NPoints, (int)header.Dx, (FrequencyResponsePhase)header.Seq, header.X0);
    }

    private static (BandwidthReading Reading, int Offset) ReadBandwidth(ReadOnlySpan<byte> frame, int offset)
    {
        var reading = new BandwidthReading(
            BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset + 4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset + 8, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(offset + 12, 4)),
            frame[offset + 16] != 0,
            frame[offset + 17] != 0);

        return (reading, offset + 20);
    }
}

public readonly record struct StreamFrameHeader(
    StreamMsgType MsgType, StreamChannel Channel, StreamFlags Flags, uint Seq, float X0, float Dx, uint NPoints);

public readonly record struct SpectrumColumn(float FreqHz, MinMax Value);

public readonly record struct DecodedMeters(
    float FundamentalFreqHz, float BinSizeHz, float ThdPercent, float ThdNPercent, float SnrDb, float FundamentalDbv,
    float NoiseFloorDbv, float PeakDbv, float TotalRmsDbv,
    (float FreqHz, float Dbc)[] Harmonics, (float FreqHz, float Dbc)[] OtherPeaks, uint AveragesTaken);

public readonly record struct DecodedSweepPoint(
    float FreqHz, StreamChannel Channel, uint PointIndex,
    float ThdPercent, float ThdNPercent, float SnrDb, float FundamentalDbv,
    float GainDb, float PowerWatt, float D6PlusPercent, float NoisePercent, float GeneratorDbv,
    float[] HarmonicPercent, SweepPreviewSeries? PreviewFft, SweepPreviewSeries? PreviewScope);

/// <param name="CurrentX">The point being measured, in the sweep axis' own unit — Hz or generator volts.</param>
public readonly record struct DecodedSweepProgress(int Index, int Total, float CurrentX, ThdSweepPhase Phase);

public readonly record struct DecodedFrequencyResponseProgress(
    int Taken, int Total, FrequencyResponsePhase Phase, float GeneratorAmpDbv);
