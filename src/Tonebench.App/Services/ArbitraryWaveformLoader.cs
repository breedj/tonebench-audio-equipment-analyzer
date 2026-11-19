using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// Turns a <c>.wav</c> file into the loop <see cref="ArbitraryWave"/> plays: one channel of it,
/// resampled to the rate the generator is running at, normalised to unit rms.
///
/// <para><b>Here rather than in the device, for the reason the quantised shapes' frequency is
/// resolved here</b> — this is the only layer that knows both the file and the rate, and the only
/// one that can report a bad file to somebody who can act on it. <see cref="Tonebench.Device"/>
/// has no reference to <see cref="Tonebench.Dsp"/> either, so the resampling could not live
/// there.</para>
///
/// <para><b>Cached, because this runs on the thread that configures the generator.</b> Reading and
/// resampling a ten-second file is hundreds of milliseconds, and every stimulus change would pay it
/// again — including the per-point reconfigure of a sweep. The cache is keyed by the file's path,
/// its last-write time and its length, the channel and the target rate: so a file edited on disk is
/// re-read, and a rate change rebuilds rather than playing a table at the wrong speed. Four entries,
/// least recently used evicted, because a table is megabytes.</para>
/// </summary>
public static class ArbitraryWaveformLoader
{
    /// <summary>
    /// The longest loop this will build, in frames at the <i>target</i> rate. 60 s at 192 kHz, i.e.
    /// 46 MB a table — past any stimulus and short of the point where holding four of them matters.
    /// A longer file is refused by name rather than truncated.
    /// </summary>
    public const int MaxFrames = 11_520_000;

    /// <summary>
    /// How many source samples are wrapped onto each end before resampling — see
    /// <see cref="Resample"/>, where the reasoning is.
    /// </summary>
    private const int WrapPad = 4096;

    /// <summary>Tables kept, least recently used evicted. Four, because each is megabytes.</summary>
    private const int CacheSize = 4;

    private static readonly Lock Gate = new();
    private static readonly List<(string Key, ArbitraryWaveformTable Table)> Cache = [];

    /// <summary>
    /// The table for <paramref name="settings"/> at <paramref name="targetRateHz"/>, read from disk
    /// the first time and from the cache after.
    /// </summary>
    /// <exception cref="WaveFileException">The file is missing, unreadable, or not one this can play.</exception>
    public static ArbitraryWaveformTable Load(ArbitrarySettings settings, double targetRateHz)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Path))
            throw new WaveFileException("No file is chosen.");
        if (!(targetRateHz > 0.0))
            throw new WaveFileException($"The analyzer's sample rate is {targetRateHz} Hz.");

        var info = new FileInfo(settings.Path);
        if (!info.Exists)
            throw new WaveFileException($"The file is no longer there: {settings.Path}");

        // The write time and the length are in the key so a file edited in place is re-read. A path
        // alone would serve a stale table for the life of the process, which on a bench where the
        // waveform is being iterated on is exactly the wrong answer.
        string key = string.Join(
            '|', info.FullName, info.LastWriteTimeUtc.Ticks, info.Length, settings.Channel, targetRateHz);

        lock (Gate)
        {
            int at = Cache.FindIndex(e => e.Key == key);
            if (at >= 0)
            {
                var hit = Cache[at];
                Cache.RemoveAt(at);
                Cache.Add(hit);
                return hit.Table;
            }
        }

        var built = Build(WaveFileReader.Read(settings.Path), settings.Channel, targetRateHz);

        lock (Gate)
        {
            Cache.RemoveAll(e => e.Key == key);
            Cache.Add((key, built));
            while (Cache.Count > CacheSize)
                Cache.RemoveAt(0);
        }

        return built;
    }

    /// <summary>Throws away every cached table. For tests, and for a bench that wants a re-read.</summary>
    public static void ClearCache()
    {
        lock (Gate)
            Cache.Clear();
    }

    /// <summary>
    /// One channel of a file, resampled and normalised. Separate from <see cref="Load"/> so the
    /// arithmetic can be tested without a file on disk.
    /// </summary>
    public static ArbitraryWaveformTable Build(
        WaveFileContents file, ArbitraryChannel channel, double targetRateHz)
    {
        ArgumentNullException.ThrowIfNull(file);

        double[] mono = TakeChannel(file, channel);
        if (mono.Length < 2)
            throw new WaveFileException("The file holds no audio.");

        long frames = (long)Math.Round(mono.Length * targetRateHz / file.SampleRateHz);
        if (frames < 2)
        {
            throw new WaveFileException(
                $"At {targetRateHz / 1000:0.###} kHz the file is {frames} frames long — too short to play.");
        }

        if (frames > MaxFrames)
        {
            throw new WaveFileException(
                $"At {targetRateHz / 1000:0.###} kHz the loop would be {frames:N0} frames; the limit is {MaxFrames:N0} "
                + $"({MaxFrames / targetRateHz:0.#} s). Use a shorter excerpt.");
        }

        double[] resampled = Resample(mono, file.SampleRateHz, targetRateHz, (int)frames);
        Normalise(resampled, out float[] samples, out double peakOfUnitRms);

        return new ArbitraryWaveformTable(
            samples, peakOfUnitRms, targetRateHz, mono.Length, file.SampleRateHz);
    }

    /// <summary>
    /// The channel to play, as doubles.
    ///
    /// <para><see cref="ArbitraryChannel.Right"/> falls back to the first channel on a mono file
    /// rather than refusing: a stereo setting left behind when the file was swapped must not stop
    /// the new one playing, and there is no second channel to be wrong about.</para>
    /// </summary>
    private static double[] TakeChannel(WaveFileContents file, ArbitraryChannel channel)
    {
        int channels = Math.Max(1, file.ChannelCount);
        int frames = file.Samples.Length / channels;
        var mono = new double[frames];

        if (channel == ArbitraryChannel.Mix && channels > 1)
        {
            for (int i = 0; i < frames; i++)
            {
                double sum = 0.0;
                for (int c = 0; c < channels; c++)
                    sum += file.Samples[(i * channels) + c];
                mono[i] = sum / channels;
            }

            return mono;
        }

        int take = channel == ArbitraryChannel.Right && channels > 1 ? 1 : 0;
        for (int i = 0; i < frames; i++)
            mono[i] = file.Samples[(i * channels) + take];

        return mono;
    }

    /// <summary>
    /// Onto the analyzer's grid, <b>circularly</b>.
    ///
    /// <para><see cref="Resampler.Decimate"/> is a windowed-sinc interpolation whose kernel reaches
    /// a few hundred samples either side, and at the ends of a buffer it has nothing to reach into:
    /// it renormalises by the weights it has, which is right for a record and wrong for a
    /// <i>loop</i>, where the samples before the first really are the ones after the last. Left
    /// alone it puts an artefact at the loop joint once per period — a click that is ours rather
    /// than the file's, on a stimulus whose whole point is that it is the file.</para>
    ///
    /// <para>So the source is wrapped onto both ends before resampling and the middle is taken
    /// back. <see cref="WrapPad"/> is comfortably past the kernel's half-length at every ratio this
    /// can see (about 106 source samples per unit of ratio, so ~425 at the 4:1 of a 192 kHz file
    /// played at 48), and a source shorter than the pad is tiled rather than part-copied. What is
    /// left at the joint is the file's own step from its last sample to its first, which is the
    /// file's to fix — and is sometimes the point.</para>
    ///
    /// <para>The loop's period is rounded to a whole frame, so it is off the file's true length by
    /// at most half a source sample. That is the same quantisation <see cref="PeriodicWave"/> makes
    /// and for the same reason: a period that is not a whole number of frames is not periodic in
    /// the sampled domain at all.</para>
    /// </summary>
    private static double[] Resample(double[] mono, double sourceRateHz, double targetRateHz, int frames)
    {
        if (Math.Abs(sourceRateHz - targetRateHz) < 1e-9)
            return mono;

        int pad = Math.Min(WrapPad, mono.Length);
        var padded = new double[pad + mono.Length + pad];
        for (int i = 0; i < pad; i++)
            padded[i] = mono[((mono.Length - pad + i) % mono.Length + mono.Length) % mono.Length];
        Array.Copy(mono, 0, padded, pad, mono.Length);
        for (int i = 0; i < pad; i++)
            padded[pad + mono.Length + i] = mono[i % mono.Length];

        double ratio = sourceRateHz / targetRateHz;
        int outPad = (int)Math.Round(pad / ratio);

        var wide = new double[outPad + frames + outPad];
        Resampler.Decimate(padded, sourceRateHz, targetRateHz, wide);

        var result = new double[frames];
        Array.Copy(wide, outPad, result, 0, frames);
        return result;
    }

    /// <summary>
    /// To unit rms, and the peak that leaves.
    ///
    /// <para>rms rather than peak, because the level means an rms for every other kind here — so
    /// two files at one setting deliver the same power into the same load, and swapping one for the
    /// other does not change how hard the DUT is driven. The peak that falls out is the file's own
    /// crest factor, which the output range reads: it is the one stimulus whose crest is measured
    /// rather than assumed.</para>
    ///
    /// <para>Digital silence is refused here rather than divided by: a file of zeros would
    /// otherwise normalise to NaN and play as a silent stimulus that reports a level.</para>
    /// </summary>
    private static void Normalise(double[] values, out float[] samples, out double peakOfUnitRms)
    {
        double sumSquares = 0.0;
        double peak = 0.0;
        foreach (double v in values)
        {
            sumSquares += v * v;
            peak = Math.Max(peak, Math.Abs(v));
        }

        double rms = Math.Sqrt(sumSquares / values.Length);
        if (!(rms > 0.0) || !double.IsFinite(rms))
            throw new WaveFileException("The file is digital silence — there is nothing to play.");

        samples = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
            samples[i] = (float)(values[i] / rms);

        peakOfUnitRms = peak / rms;
    }
}
