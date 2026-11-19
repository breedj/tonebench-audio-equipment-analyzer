using System.Buffers.Binary;

namespace Tonebench.App.Services;

/// <summary>What a <c>.wav</c> file turned out to hold.</summary>
/// <param name="Samples">Every frame, interleaved, as a value in [-1, 1].</param>
/// <param name="SampleRateHz">The file's own rate, which is not usually the analyzer's.</param>
/// <param name="ChannelCount">How many channels <paramref name="Samples"/> interleaves.</param>
/// <param name="BitsPerSample">What the file stored, for the panel to report — the samples above are always float.</param>
/// <param name="IsFloat">Whether the file stored floats rather than integers, on the same terms.</param>
public sealed record WaveFileContents(
    float[] Samples,
    int SampleRateHz,
    int ChannelCount,
    int BitsPerSample,
    bool IsFloat)
{
    /// <summary>Frames, i.e. samples per channel.</summary>
    public int FrameCount => ChannelCount <= 0 ? 0 : Samples.Length / ChannelCount;

    /// <summary>How long the file is, in seconds.</summary>
    public double DurationSeconds => SampleRateHz <= 0 ? 0.0 : (double)FrameCount / SampleRateHz;
}

/// <summary>
/// Why a <c>.wav</c> could not be used — always with a reason a user can act on.
///
/// <para><b>It derives from <see cref="InvalidOperationException"/> deliberately.</b> The load
/// happens inside <c>DeviceSessionService.ConfigureGeneratorAsync</c>, which every screen, every
/// runner and the oscilloscope's own start reach through; those call sites already turn an
/// <c>InvalidOperationException</c> into a 409 carrying its message, and each of them is right to.
/// Deriving means a bad file is reported the same way everywhere without a dozen new catch
/// clauses, each of which would be a chance to swallow it — and swallowing it would play silence
/// and report success.</para>
/// </summary>
public sealed class WaveFileException(string message) : InvalidOperationException(message);

/// <summary>
/// Reads a RIFF/WAVE file into float samples.
///
/// <para><b>A reader, where this app has only ever had writers.</b>
/// <see cref="Diagnostics.StreamRecorder"/> and <see cref="Diagnostics.DiagnosticRecorder"/> both
/// write 32-bit float WAVs and neither reads one, so nothing here could be reused: the one thing
/// they share is the header layout, and this has to accept far more of it than they produce.</para>
///
/// <para><b>What it accepts and why that list is short.</b> Integer PCM at 8, 16, 24 and 32 bits,
/// IEEE float at 32 and 64, any channel count, any sample rate, and <c>WAVE_FORMAT_EXTENSIBLE</c>
/// wrapping either of those — which is every file a converter or a DAW produces for this purpose.
/// It refuses everything else <b>by name</b> rather than guessing, because a stimulus read wrongly
/// is not silence: it is a plausible waveform that is not the one in the file, and nothing
/// downstream could tell. Compressed formats (ADPCM, A-law, MP3-in-WAV) are the refusals that
/// matter, since their headers parse perfectly and their data is not samples at all.</para>
///
/// <para><b>Chunks are walked, not assumed.</b> A file from a DAW routinely carries <c>LIST</c>,
/// <c>bext</c>, <c>cue </c> or <c>fact</c> chunks before <c>data</c>, and plenty put them between
/// <c>fmt </c> and <c>data</c>. Every chunk's size is read and skipped (with RIFF's own pad byte on
/// an odd size), so nothing depends on the order they come in.</para>
/// </summary>
public static class WaveFileReader
{
    /// <summary>Integer PCM.</summary>
    private const int FormatPcm = 1;

    /// <summary>IEEE float.</summary>
    private const int FormatFloat = 3;

    /// <summary>The wrapper, whose real format is the first two bytes of its sub-format GUID.</summary>
    private const int FormatExtensible = 0xFFFE;

    /// <summary>
    /// The most frames this will read, per channel. 10 minutes at 192 kHz — far past any stimulus,
    /// and short of the point where the float array itself is a problem. A longer file is refused
    /// rather than truncated: a stimulus silently cut to a tenth of its length is the kind of wrong
    /// answer this whole class is written against.
    /// </summary>
    public const int MaxFrames = 115_200_000;

    /// <summary>Reads <paramref name="path"/>. Throws <see cref="WaveFileException"/> with a reason a user can act on.</summary>
    public static WaveFileContents Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new WaveFileException($"Could not read the file: {ex.Message}");
        }

        return Parse(bytes);
    }

    /// <summary>
    /// The parse itself, over bytes already in hand — which is what makes it testable without a
    /// file, and what lets the loader read once and parse once.
    /// </summary>
    public static WaveFileContents Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
            throw new WaveFileException("The file is too short to be a WAV.");

        if (!bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new WaveFileException("Not a RIFF/WAVE file (no RIFF or WAVE marker).");

        int format = 0, channels = 0, sampleRate = 0, bits = 0;
        bool haveFormat = false;
        ReadOnlySpan<byte> data = default;
        bool haveData = false;

        int at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = bytes.Slice(at, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at + 4, 4));
            int body = at + 8;

            // A truncated final chunk is common enough on a file that was still being written, and
            // the data is still usable up to where it stops -- so clamp rather than refuse.
            int available = Math.Max(0, bytes.Length - body);
            int length = (int)Math.Min(size, (uint)available);

            if (id.SequenceEqual("fmt "u8))
            {
                if (length < 16)
                    throw new WaveFileException("The fmt chunk is too short.");

                var fmt = bytes.Slice(body, length);
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);

                // EXTENSIBLE says nothing itself: the real format is the first two bytes of the
                // sub-format GUID, 22 bytes into the extension.
                if (format == FormatExtensible)
                {
                    if (length < 40)
                        throw new WaveFileException("The fmt chunk says WAVE_FORMAT_EXTENSIBLE but carries no sub-format.");
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
                }

                haveFormat = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = bytes.Slice(body, length);
                haveData = true;
            }

            // RIFF pads every odd-sized chunk to an even boundary, and the pad byte is not counted
            // in the size -- miss it and every chunk after an odd one is read at the wrong offset.
            at = body + length + (length & 1);
        }

        if (!haveFormat)
            throw new WaveFileException("The file has no fmt chunk.");
        if (!haveData)
            throw new WaveFileException("The file has no data chunk.");
        if (channels < 1)
            throw new WaveFileException($"The file declares {channels} channels.");
        if (sampleRate < 1)
            throw new WaveFileException($"The file declares a sample rate of {sampleRate} Hz.");

        bool isFloat = format == FormatFloat;
        if (format != FormatPcm && !isFloat)
        {
            throw new WaveFileException(
                $"Only uncompressed PCM and IEEE float are supported; this file is format {format}. "
                + "Convert it to 16-, 24- or 32-bit PCM or 32-bit float first.");
        }

        int bytesPerSample = bits / 8;
        if (bytesPerSample < 1 || bits % 8 != 0 || !IsSupportedWidth(bits, isFloat))
        {
            throw new WaveFileException(
                $"{bits}-bit {(isFloat ? "float" : "PCM")} is not supported. "
                + "Use 8-, 16-, 24- or 32-bit PCM, or 32- or 64-bit float.");
        }

        int totalSamples = data.Length / bytesPerSample;
        int frames = totalSamples / channels;
        if (frames < 2)
            throw new WaveFileException("The file holds no audio.");
        if (frames > MaxFrames)
            throw new WaveFileException($"The file is {frames:N0} frames long; the limit is {MaxFrames:N0}.");

        var samples = new float[frames * channels];
        Decode(data, bits, isFloat, samples);

        return new WaveFileContents(samples, sampleRate, channels, bits, isFloat);
    }

    private static bool IsSupportedWidth(int bits, bool isFloat) =>
        isFloat ? bits is 32 or 64 : bits is 8 or 16 or 24 or 32;

    /// <summary>
    /// Every supported width into [-1, 1].
    ///
    /// <para>8-bit PCM is the odd one and is <b>unsigned</b> with a 128 bias, which is RIFF's own
    /// rule and not a quirk of any writer — decode it as signed and every sample is out by half of
    /// full scale. The signed widths divide by <c>2^(bits-1)</c>, so full negative reads exactly
    /// -1 and full positive a hair under it; that asymmetry is the format's.</para>
    /// </summary>
    private static void Decode(ReadOnlySpan<byte> data, int bits, bool isFloat, Span<float> destination)
    {
        if (isFloat && bits == 32)
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4, 4));
            return;
        }

        if (isFloat)
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(i * 8, 8));
            return;
        }

        switch (bits)
        {
            case 8:
                for (int i = 0; i < destination.Length; i++)
                    destination[i] = (data[i] - 128) / 128f;
                return;

            case 16:
                for (int i = 0; i < destination.Length; i++)
                    destination[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2)) / 32768f;
                return;

            case 24:
                for (int i = 0; i < destination.Length; i++)
                {
                    var s = data.Slice(i * 3, 3);
                    int value = s[0] | (s[1] << 8) | (s[2] << 16);
                    // Sign-extend the 24-bit value into an int.
                    if ((value & 0x800000) != 0)
                        value |= unchecked((int)0xFF000000);
                    destination[i] = value / 8388608f;
                }
                return;

            default:
                for (int i = 0; i < destination.Length; i++)
                    destination[i] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4)) / 2147483648f;
                return;
        }
    }
}
