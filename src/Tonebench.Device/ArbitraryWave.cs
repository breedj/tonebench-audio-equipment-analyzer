namespace Tonebench.Device;

/// <summary>
/// Which channel of a multi-channel file an arbitrary stimulus plays.
///
/// <para><b>One channel, not a mixdown, by default.</b> Summing a stereo pair is the obvious thing
/// and is the one that can be silently wrong: anti-phase material cancels, and a file that is
/// mostly difference comes out quiet or empty with nothing to say why. Taking a channel is never
/// wrong — at worst it is not the one you wanted, which is visible the moment you look at the
/// trace.</para>
/// </summary>
public enum ArbitraryChannel
{
    /// <summary>The first channel, which is the whole of a mono file.</summary>
    Left,

    /// <summary>The second, or the first again where there is only one.</summary>
    Right,

    /// <summary>The mean of every channel — see this type's own remarks for why it is not the default.</summary>
    Mix,
}

/// <summary>
/// An arbitrary stimulus as the synthesizers need it: one loop of samples at the rate they are
/// playing, already normalised.
///
/// <para><b>Normalised to unit rms, and that is what makes the level mean what it means
/// everywhere else here.</b> dBV referenced to 1 Vrms is the convention every other kind uses, so
/// the table carries the shape and the level carries the loudness — which also means switching
/// files does not change how hard the DUT is driven. <see cref="PeakOfUnitRms"/> is the file's own
/// crest factor and is what <see cref="OutputRangeSelector"/> reads: it is <b>measured</b> rather
/// than allowed for, unlike the continuous noise kinds', because the samples are all in hand. A
/// music excerpt runs 10–15 dB and a sine 3.01, and guessing either would park the DAC a range too
/// low and clip the file.</para>
///
/// <para>Built by the App layer, which is the only place that knows both the file and the rate the
/// generator will run at — the same division <c>DeviceSessionService.ResolveWaves</c> already
/// makes for the quantised shapes.</para>
/// </summary>
/// <param name="Samples">One loop, at <paramref name="SampleRateHz"/>, with an rms of 1.</param>
/// <param name="PeakOfUnitRms">The largest absolute sample of <paramref name="Samples"/>: the crest factor.</param>
/// <param name="SampleRateHz">The rate this table was built for. A rate change needs a new one.</param>
/// <param name="SourceFrames">How many frames the file held, before resampling — reported, not used.</param>
/// <param name="SourceSampleRateHz">And at what rate, on the same terms.</param>
public sealed record ArbitraryWaveformTable(
    float[] Samples,
    double PeakOfUnitRms,
    double SampleRateHz,
    int SourceFrames,
    double SourceSampleRateHz)
{
    /// <summary>The loop length in frames, which is also the stimulus's period.</summary>
    public int Frames => Samples.Length;

    /// <summary>How long one loop lasts, in seconds.</summary>
    public double DurationSeconds => SampleRateHz <= 0.0 ? 0.0 : Frames / SampleRateHz;

    /// <summary>How often the loop repeats, in Hz — what a scope's timebase is set from.</summary>
    public double RepeatHz => Frames <= 0 || SampleRateHz <= 0.0 ? 0.0 : SampleRateHz / Frames;
}

/// <summary>
/// The arbitrary stimulus, <see cref="GeneratorKind.Arbitrary"/>: a <c>.wav</c> file played on a
/// loop.
///
/// <para><b>It is periodic by construction, which is what lets it be analysed at all.</b> The loop
/// is a whole number of frames — the resampled table's own length — so the chirp's rule holds
/// without anything having to be rounded: every line of whatever is in the file lands on a multiple
/// of <c>fs/N</c>, and there is no inharmonic content from the playback. The one thing the loop
/// cannot fix is the file's own ends: if the last sample does not run into the first, there is a
/// step at the joint once per loop, and it is the file's rather than ours. That is sometimes the
/// point — a step is a stimulus — and where it is not, the answer is a file whose ends match.</para>
///
/// <para>The arithmetic here is deliberately almost nothing: a modulo and a multiply. Everything
/// that could be got wrong — the format, the channel, the resampling, the normalisation — happens
/// once, in the App layer, where it can be reported to a user who can act on it.</para>
/// </summary>
public static class ArbitraryWave
{
    /// <summary>
    /// This stimulus's value at <paramref name="frameIndex"/>, in volts, or zero where no file has
    /// been loaded.
    ///
    /// <para>Integer modulo on the frame index, like every other periodic kind here: the loop has
    /// to land on exactly the same frame however long the stream has been running, or the waveform
    /// walks off its own grid and every line smears.</para>
    /// </summary>
    public static double Sample(ArbitrarySettings? settings, long frameIndex)
    {
        var table = settings?.Table;
        if (table is null || table.Samples.Length == 0)
            return 0.0;

        long phase = frameIndex % table.Samples.Length;
        if (phase < 0)
            phase += table.Samples.Length;

        return table.Samples[(int)phase] * Math.Pow(10.0, settings!.LevelDbv / 20.0);
    }

    /// <summary>
    /// The peak volts this stimulus reaches: the level's rms times the file's <b>measured</b> crest
    /// factor. Zero with no file loaded, which is what makes a never-configured arbitrary channel
    /// select the quietest output range rather than the loudest.
    /// </summary>
    public static double PeakVolts(ArbitrarySettings? settings)
    {
        var table = settings?.Table;
        if (table is null)
            return 0.0;

        return Math.Pow(10.0, settings!.LevelDbv / 20.0) * table.PeakOfUnitRms;
    }

    /// <summary>The loop's period in frames, or 0 with nothing loaded — what the scope's stimulus trigger needs.</summary>
    public static int PeriodFrames(ArbitrarySettings? settings) => settings?.Table?.Frames ?? 0;
}
