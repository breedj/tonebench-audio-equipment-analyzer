using System.Buffers.Binary;

namespace Qa403.Data;

/// <summary>
/// Encodes a small float array as raw f32 little-endian bytes -- the wire format for
/// <c>capture_preview</c>'s x/min/max BLOBs. Unlike <see cref="SpectrumCodec"/>'s quantized i16,
/// no compression: a preview is capped at a couple hundred points, so the difference between 2 and
/// 4 bytes/point is a few hundred bytes per row, not worth a second lossy encoding to save.
/// </summary>
public static class PreviewCodec
{
    public static int ByteLength(int count) => count * sizeof(float);

    public static byte[] Encode(ReadOnlySpan<float> values)
    {
        byte[] bytes = new byte[ByteLength(values.Length)];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), values[i]);

        return bytes;
    }

    public static float[] Decode(ReadOnlySpan<byte> bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * sizeof(float), sizeof(float)));

        return values;
    }
}
