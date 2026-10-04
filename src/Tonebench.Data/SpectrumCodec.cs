using System.Buffers.Binary;

namespace Tonebench.Data;

/// <summary>
/// Encodes a dBV spectrum as i16 in 0.01 dB steps, 2 bytes/bin little-endian — the wire format
/// for <c>spectrum.mag_db</c>. No sentinel for out-of-range values: -327.68 dBV is ~190 dB below
/// the thermal noise floor of any audio-band measurement, and the pipeline's <see cref="double.Epsilon"/>
/// floor (used instead of -Infinity for a true zero bin) decodes to roughly -3232 dB, which clamps
/// into range cleanly rather than needing special-casing.
/// </summary>
public static class SpectrumCodec
{
    public const double StepDb = 0.01;
    public const double MinDb = short.MinValue * StepDb;
    public const double MaxDb = short.MaxValue * StepDb;

    public static int ByteLength(int binCount) => binCount * sizeof(short);

    public static void EncodeTo(ReadOnlySpan<double> db, Span<byte> destination)
    {
        if (destination.Length != ByteLength(db.Length))
            throw new ArgumentException("destination must be exactly ByteLength(db.Length) bytes long.", nameof(destination));

        for (int i = 0; i < db.Length; i++)
        {
            short raw = EncodeOne(db[i]);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * sizeof(short), sizeof(short)), raw);
        }
    }

    public static void DecodeTo(ReadOnlySpan<byte> source, Span<double> destination)
    {
        if (source.Length != ByteLength(destination.Length))
            throw new ArgumentException("source must be exactly ByteLength(destination.Length) bytes long.", nameof(source));

        for (int i = 0; i < destination.Length; i++)
        {
            short raw = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(i * sizeof(short), sizeof(short)));
            destination[i] = raw * StepDb;
        }
    }

    private static short EncodeOne(double db)
    {
        double rounded = Math.Round(db / StepDb, MidpointRounding.AwayFromZero);
        return (short)Math.Clamp(rounded, short.MinValue, short.MaxValue);
    }
}
