using System.Buffers.Binary;
using Tonebench.App.Services;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The RIFF reader. Its refusals matter as much as its decoding: a stimulus read wrongly is not
/// silence but a plausible waveform that is not the one in the file, and nothing downstream could
/// tell — so every case below either decodes to a figure the file was built with, or is refused by
/// name.
/// </summary>
public class WaveFileReaderTests
{
    /// <summary>
    /// A WAV in memory. <paramref name="extraChunkId"/> puts a chunk of
    /// <paramref name="extraChunkBytes"/> between <c>fmt</c> and <c>data</c>, which is what a file
    /// from a DAW looks like.
    /// </summary>
    private static byte[] Build(
        int format,
        int channels,
        int sampleRate,
        int bits,
        byte[] data,
        string? extraChunkId = null,
        int extraChunkBytes = 0,
        int fmtSize = 16,
        int subFormat = 0)
    {
        var fmt = new byte[fmtSize];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, (ushort)format);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)(sampleRate * channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bits);
        if (fmtSize >= 40)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(16), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(24), (ushort)subFormat);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(0); // patched below
        writer.Write("WAVE"u8.ToArray());

        writer.Write("fmt "u8.ToArray());
        writer.Write(fmt.Length);
        writer.Write(fmt);

        if (extraChunkId is not null)
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes(extraChunkId));
            writer.Write(extraChunkBytes);
            writer.Write(new byte[extraChunkBytes]);
            // RIFF's pad byte on an odd size, which is exactly what a reader that assumes even
            // sizes misses -- and then reads every chunk after it at the wrong offset.
            if ((extraChunkBytes & 1) != 0)
                writer.Write((byte)0);
        }

        writer.Write("data"u8.ToArray());
        writer.Write(data.Length);
        writer.Write(data);

        writer.Flush();
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static byte[] Int16Data(params short[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return bytes;
    }

    private static byte[] FloatData(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    [Fact]
    public void ReadsTheFloatFilesThisAppItselfWrites()
    {
        // 32-bit float, the format StreamRecorder and DiagnosticRecorder both produce -- so a
        // recorded stimulus can be played straight back as one.
        var file = WaveFileReader.Parse(Build(3, 1, 48_000, 32, FloatData(0f, 0.5f, -0.25f, 1f)));

        Assert.Equal(48_000, file.SampleRateHz);
        Assert.Equal(1, file.ChannelCount);
        Assert.True(file.IsFloat);
        Assert.Equal([0f, 0.5f, -0.25f, 1f], file.Samples);
        Assert.Equal(4, file.FrameCount);
    }

    [Fact]
    public void ReadsSixteenBitPcmAgainstItsOwnFullScale()
    {
        var file = WaveFileReader.Parse(Build(1, 1, 44_100, 16, Int16Data(0, 16384, -32768, 32767)));

        Assert.Equal(44_100, file.SampleRateHz);
        Assert.Equal(0f, file.Samples[0]);
        Assert.Equal(0.5f, file.Samples[1]);
        // Full negative is exactly -1 and full positive a hair under: the format's own asymmetry,
        // not a rounding choice here.
        Assert.Equal(-1f, file.Samples[2]);
        Assert.Equal(32767f / 32768f, file.Samples[3]);
    }

    /// <summary>
    /// 24-bit is three bytes with no sign bit of its own, so it has to be sign-extended by hand —
    /// the single easiest thing in this file to get wrong, and the failure is a waveform that
    /// looks like clipping on every negative half.
    /// </summary>
    [Fact]
    public void SignExtendsTwentyFourBitPcm()
    {
        // 0x000000 = 0, 0x400000 = +0.5, 0x800000 = -1, 0xFFFFFF = -1 LSB.
        byte[] data = [0, 0, 0, 0, 0, 0x40, 0, 0, 0x80, 0xFF, 0xFF, 0xFF];
        var file = WaveFileReader.Parse(Build(1, 1, 48_000, 24, data));

        Assert.Equal(0f, file.Samples[0]);
        Assert.Equal(0.5f, file.Samples[1]);
        Assert.Equal(-1f, file.Samples[2]);
        Assert.Equal(-1f / 8388608f, file.Samples[3]);
    }

    /// <summary>
    /// 8-bit PCM is <b>unsigned</b> with a 128 bias, which is RIFF's own rule — decoded as signed,
    /// every sample is out by half of full scale and the waveform is a different one.
    /// </summary>
    [Fact]
    public void ReadsEightBitPcmAsUnsignedWithItsBias()
    {
        var file = WaveFileReader.Parse(Build(1, 1, 22_050, 8, [128, 255, 0, 192]));

        Assert.Equal(0f, file.Samples[0]);
        Assert.Equal(127f / 128f, file.Samples[1]);
        Assert.Equal(-1f, file.Samples[2]);
        Assert.Equal(0.5f, file.Samples[3]);
    }

    [Fact]
    public void ReadsStereoInterleaved()
    {
        var file = WaveFileReader.Parse(Build(1, 2, 48_000, 16, Int16Data(16384, -16384, 32767, 0)));

        Assert.Equal(2, file.ChannelCount);
        Assert.Equal(2, file.FrameCount);
        Assert.Equal(0.5f, file.Samples[0]);
        Assert.Equal(-0.5f, file.Samples[1]);
    }

    /// <summary>
    /// A chunk a reader does not know is skipped by its own size — and a chunk of <i>odd</i> size
    /// carries a pad byte that is not counted in it, so missing that reads every chunk after it at
    /// the wrong offset. A DAW's <c>LIST</c> and <c>bext</c> chunks are routinely odd.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    public void WalksPastAChunkItDoesNotKnow_PadByteIncluded(int extraBytes)
    {
        var file = WaveFileReader.Parse(
            Build(1, 1, 48_000, 16, Int16Data(16384, -16384), extraChunkId: "LIST", extraChunkBytes: extraBytes));

        Assert.Equal(2, file.FrameCount);
        Assert.Equal(0.5f, file.Samples[0]);
        Assert.Equal(-0.5f, file.Samples[1]);
    }

    /// <summary>
    /// <c>WAVE_FORMAT_EXTENSIBLE</c> says nothing itself: the real format is the first two bytes of
    /// the sub-format GUID. Treated as a format of its own it would be refused, and most 24-bit
    /// files from a DAW are written this way.
    /// </summary>
    [Fact]
    public void ReadsTheSubFormatOfAnExtensibleFile()
    {
        var file = WaveFileReader.Parse(
            Build(0xFFFE, 1, 96_000, 16, Int16Data(16384, -16384), fmtSize: 40, subFormat: 1));

        Assert.Equal(96_000, file.SampleRateHz);
        Assert.False(file.IsFloat);
        Assert.Equal(0.5f, file.Samples[0]);
    }

    /// <summary>
    /// A compressed file's header parses perfectly and its data is not samples at all, so this is
    /// the refusal that matters most — and it names the format number and what to do about it.
    /// </summary>
    [Fact]
    public void RefusesACompressedFileByName()
    {
        // 0x11 = IMA ADPCM.
        var ex = Assert.Throws<WaveFileException>(
            () => WaveFileReader.Parse(Build(0x11, 1, 48_000, 4, new byte[64])));

        Assert.Contains("format 17", ex.Message);
        Assert.Contains("Convert", ex.Message);
    }

    [Theory]
    [InlineData("not a wav at all, not even close")]
    public void RefusesSomethingThatIsNotRiff(string text)
    {
        var ex = Assert.Throws<WaveFileException>(
            () => WaveFileReader.Parse(System.Text.Encoding.ASCII.GetBytes(text)));

        Assert.Contains("RIFF", ex.Message);
    }

    [Fact]
    public void RefusesAFileWithNoAudioInIt()
    {
        var ex = Assert.Throws<WaveFileException>(
            () => WaveFileReader.Parse(Build(1, 1, 48_000, 16, Int16Data(1))));

        Assert.Contains("no audio", ex.Message);
    }

    [Fact]
    public void RefusesAWidthItCannotDecode()
    {
        var ex = Assert.Throws<WaveFileException>(
            () => WaveFileReader.Parse(Build(1, 1, 48_000, 12, new byte[64])));

        Assert.Contains("12-bit", ex.Message);
    }

    /// <summary>
    /// A file still being written has a <c>data</c> size larger than what is there. The audio up to
    /// that point is perfectly good, so it is clamped rather than refused — which is also what
    /// keeps a recording that was interrupted usable as a stimulus.
    /// </summary>
    [Fact]
    public void ClampsATruncatedDataChunkRatherThanRefusingIt()
    {
        var bytes = Build(1, 1, 48_000, 16, Int16Data(16384, -16384, 8192, 0));
        var truncated = bytes[..^4];

        var file = WaveFileReader.Parse(truncated);
        Assert.Equal(2, file.FrameCount);
        Assert.Equal(0.5f, file.Samples[0]);
    }

    [Fact]
    public void ReadsARealFileFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tonebench-wav-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(path, Build(3, 1, 48_000, 32, FloatData(0.25f, -0.25f, 0.5f, -0.5f)));
            var file = WaveFileReader.Read(path);
            Assert.Equal(4, file.FrameCount);
            Assert.Equal(0.25f, file.Samples[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A missing file comes back as this reader's own exception rather than as a
    /// <see cref="FileNotFoundException"/>, which is what makes it reach the user: every caller of
    /// <c>ConfigureGeneratorAsync</c> turns a <see cref="InvalidOperationException"/> into a refusal
    /// carrying its message, and a raw IO exception would be a 500 about itself instead.
    /// </summary>
    [Fact]
    public void SaysSoWhenTheFileIsNotThere()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tonebench-missing-{Guid.NewGuid():N}.wav");

        var ex = Assert.Throws<WaveFileException>(() => WaveFileReader.Read(path));
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Contains("Could not read the file", ex.Message);
    }
}
