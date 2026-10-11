using System.Buffers.Binary;

namespace Tonebench.Data;

/// <summary>
/// Encodes time-domain samples as f32 little-endian, interleaved in logical channel order — the
/// format of <c>capture.pcm</c>.
///
/// <para>f32 rather than f64, and that is measured rather than assumed: LTspice's own default
/// output is single precision (the <c>double</c> flag only appears with
/// <c>.options numdgt=7</c>), and the same amplifier run read at both precisions gives harmonic
/// levels identical to four decimal places down to −110 dBc. Halving the blob is worth more than a
/// precision nothing in an audio measurement can see.</para>
/// </summary>
public static class PcmCodec
{
    public static int ByteLength(int channels, int frames) => channels * frames * sizeof(float);

    /// <summary>
    /// Rounds <paramref name="samples"/> to the precision they will be stored at.
    ///
    /// <para>Called before the samples are first analysed, not only before they are written. The
    /// stored waveform is the durable truth, so the import's own spectrum has to be a spectrum
    /// <i>of that</i> -- otherwise it is computed from slightly better data than any later
    /// re-analysis can read back, and re-analysing at the identical settings moves the trace for no
    /// reason the user can see. Measured: about 12 dB, on bins 200 dB down, which is nothing at all
    /// and was still worth removing, because "re-analyse changed something" is a bug report nobody
    /// could act on.</para>
    /// </summary>
    /// <summary>
    /// Subtracts and returns the mean of <paramref name="samples"/> -- the node's DC operating
    /// point. Must run <i>before</i> <see cref="QuantizeInPlace"/>: see
    /// <c>SimulationParameters.RemovedDcV</c> for what it costs the other way round.
    /// </summary>
    public static double RemoveMeanInPlace(Span<double> samples)
    {
        if (samples.Length == 0)
            return 0.0;

        // Summed in order rather than pairwise: at f64 over a few hundred thousand samples the
        // error is far below the f32 step this exists to protect.
        double sum = 0.0;
        foreach (double v in samples)
            sum += v;

        double mean = sum / samples.Length;
        for (int i = 0; i < samples.Length; i++)
            samples[i] -= mean;

        return mean;
    }

    public static void QuantizeInPlace(Span<double> samples)
    {
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)samples[i];
    }

    /// <summary>
    /// Interleaves <paramref name="channels"/> (all the same length) into
    /// <paramref name="destination"/>.
    /// </summary>
    public static void EncodeTo(IReadOnlyList<double[]> channels, Span<byte> destination)
    {
        if (channels.Count == 0)
            throw new ArgumentException("At least one channel is needed.", nameof(channels));

        int frames = channels[0].Length;
        foreach (double[] channel in channels)
        {
            if (channel.Length != frames)
                throw new ArgumentException("Every channel must have the same number of samples.", nameof(channels));
        }

        if (destination.Length != ByteLength(channels.Count, frames))
            throw new ArgumentException(
                "destination must be exactly ByteLength(channels, frames) bytes long.", nameof(destination));

        int at = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            for (int c = 0; c < channels.Count; c++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(destination[at..], (float)channels[c][frame]);
                at += sizeof(float);
            }
        }
    }

    /// <summary>De-interleaves <paramref name="source"/> into <paramref name="channelCount"/> arrays.</summary>
    public static double[][] Decode(ReadOnlySpan<byte> source, int channelCount)
    {
        if (channelCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(channelCount));

        int stride = channelCount * sizeof(float);
        if (source.Length % stride != 0)
            throw new ArgumentException(
                $"{source.Length} bytes is not a whole number of {channelCount}-channel frames.", nameof(source));

        int frames = source.Length / stride;
        var channels = new double[channelCount][];
        for (int c = 0; c < channelCount; c++)
            channels[c] = new double[frames];

        int at = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            for (int c = 0; c < channelCount; c++)
            {
                channels[c][frame] = BinaryPrimitives.ReadSingleLittleEndian(source[at..]);
                at += sizeof(float);
            }
        }

        return channels;
    }
}
