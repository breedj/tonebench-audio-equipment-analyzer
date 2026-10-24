namespace Tonebench.Device;

/// <summary>One generator tone: frequency and amplitude.</summary>
public readonly record struct GeneratorTone(double FrequencyHz, double AmplitudeDbv);

/// <summary>
/// Which stimulus a channel's generator produces. Exactly one is active per channel — this is a
/// mode selector, not a set of flags, mirroring the legacy app's <c>OutputSources</c> enum and the
/// fact that a real DAC output carries one signal at a time.
/// </summary>
public enum GeneratorKind
{
    /// <summary>Silence, regardless of the settings below.</summary>
    Off,

    /// <summary>The explicit <see cref="ChannelGeneratorConfig.Tones"/> list (the original sine generator).</summary>
    Tones,

    /// <summary>Log-spaced tone comb derived from <see cref="MultitoneSettings"/> — see <see cref="MultitoneGenerator"/>.</summary>
    Multitone,

    /// <summary>Gaussian white noise at <see cref="WhiteNoiseSettings.LevelDbv"/>.</summary>
    WhiteNoise,

    /// <summary>
    /// The same noise shaped to 1/f (see <see cref="PinkNoiseShaper"/>), at the same broadband RMS —
    /// <see cref="WhiteNoiseSettings.LevelDbv"/> serves both kinds, since they differ in spectral
    /// shape and in nothing else.
    ///
    /// <para>The stimulus a noise-driven impedance measurement plays. Pink rather than white because
    /// the divider it measures is noise-limited at the bottom of the band, which is where a white
    /// stimulus has the least energy per octave.</para>
    /// </summary>
    PinkNoise,

    /// <summary>Exponential ("Farina") sine sweep, the stimulus a frequency-response measurement uses.</summary>
    EchoChirp,

    /// <summary>
    /// Hard-switched square wave at <see cref="PeriodicWaveSettings"/>'s frequency and level — a
    /// transient test rather than a spectral one: overshoot, ringing, settling and slew behaviour
    /// are what it shows. See <see cref="PeriodicWave"/> for why the period is quantised to a whole
    /// even number of samples and what that costs.
    /// </summary>
    Square,

    /// <summary>
    /// Triangle wave from the same <see cref="PeriodicWaveSettings"/>. Odd harmonics like the
    /// square but falling as 1/n^2 instead of 1/n, so it asks far less of a DUT's high end: the
    /// stimulus to reach for when a square's own edge is the thing being measured rather than the
    /// amplifier.
    /// </summary>
    Triangle,
}

/// <summary>
/// Fractional-octave smoothing width for a frequency-response trace. Stored on
/// <see cref="EchoChirpSettings"/> and round-tripped through the API, but not consumed yet: the FR
/// measurement itself (capture, deconvolution, smoothed trace) is a later phase — see
/// <c>docs/TODO.txt</c>. Expect this to move onto the measurement config when that lands.
/// </summary>
public enum SmoothingOctaves
{
    None,
    OneOver96,
    OneOver48,
    OneOver24,
    OneOver12,
    OneOver6,
    OneOver3,
}

/// <summary>Parameters for <see cref="GeneratorKind.Multitone"/>. The band is fixed — see <see cref="MultitoneGenerator"/>.</summary>
public sealed record MultitoneSettings
{
    public int TonesPerOctave { get; init; } = 5;

    /// <summary>
    /// Combined RMS of all tones, dBV referenced to 1 Vrms. Exact only at 0 dB/octave rolloff:
    /// a non-zero <see cref="RolloffDbPerOctave"/> attenuates the upper tones and so lowers the
    /// actual total. See <see cref="MultitoneGenerator.Build"/>.
    /// </summary>
    public double TotalRmsDbv { get; init; } = -50.0;

    /// <summary>Positive = each octave above the band start is this many dB lower.</summary>
    public double RolloffDbPerOctave { get; init; }
}

/// <summary>
/// Parameters for <see cref="GeneratorKind.WhiteNoise"/> and <see cref="GeneratorKind.PinkNoise"/>.
///
/// <para>One record for both kinds, and not merely to save a type: a level is the only thing a noise
/// generator is configured with, and both kinds mean exactly the same thing by it — the shaping is
/// RMS-normalised (<see cref="PinkNoiseShaper.RmsGain"/>) so that switching between them changes the
/// spectrum and not the loudness. A second record would have to be kept in step with this one field
/// for field, through the request DTO and the range selector, to say nothing new.</para>
/// </summary>
public sealed record WhiteNoiseSettings
{
    /// <summary>
    /// Broadband RMS, dBV referenced to 1 Vrms. Note this is a *level*, not the dBV/sqrt(Hz)
    /// spectral density convention used by the simulated DUT's own noise floor
    /// (<c>SimulatedSignalProfile.NoiseDensityDbvPerRootHz</c>) — the two are not interchangeable.
    /// </summary>
    public double LevelDbv { get; init; } = -20.0;

    /// <summary>
    /// Makes the pink stimulus <b>periodic</b> over this many frames instead of a continuous random
    /// stream — see <see cref="PeriodicNoise"/>. Zero, the default, keeps the filtered-white
    /// noise the generator screen plays.
    ///
    /// <para>Set to the analysis record length by a measurement that wants one whole period per
    /// record, which is what makes every bin's amplitude deterministic rather than Rayleigh and lets
    /// the analysis use a rectangular window. Read only for <see cref="GeneratorKind.PinkNoise"/>;
    /// white noise has no periodic form here, having no shape to hold.</para>
    ///
    /// <para>It is a frame <i>count</i> rather than a duration for the chirp's reason: a period off
    /// by one frame puts a drifting ripple on every record.</para>
    /// </summary>
    public int PeriodFrames { get; init; }

    /// <summary>
    /// Where a periodic pink sequence stops being pink, in Hz: flat below, &#8722;3 dB per octave
    /// above. Ignored unless <see cref="PeriodFrames"/> is set, and meaningless to
    /// <see cref="PeriodicNoiseShape.White"/>, which has no slope for it to start.
    /// </summary>
    public double CutoffHz { get; init; } = PeriodicNoise.DefaultCutoffHz;

    /// <summary>
    /// The spectral shape of a <b>periodic</b> sequence, and read only when
    /// <see cref="PeriodFrames"/> is set: the continuous forms take their shape from
    /// <see cref="ChannelGeneratorConfig.Kind"/> instead, which is why a periodic white sequence is
    /// still asked for as <see cref="GeneratorKind.PinkNoise"/> with this set to
    /// <see cref="PeriodicNoiseShape.White"/> rather than as the white kind — one place decides the
    /// spectrum of a periodic sequence, and it is this field.
    ///
    /// <para>Pink by default, so an impedance run, which asked for periodic noise before any of the
    /// other shapes existed, plays exactly the sequence it always did.</para>
    /// </summary>
    public PeriodicNoiseShape Shape { get; init; } = PeriodicNoiseShape.Pink;
}

/// <summary>
/// Parameters for <see cref="GeneratorKind.Square"/> and <see cref="GeneratorKind.Triangle"/>.
///
/// <para>One record for both, for the reason <see cref="WhiteNoiseSettings"/> gives for serving two
/// noise kinds: a frequency and a level is the whole of what either shape is configured with, and
/// both mean the same thing by both. The shape is <see cref="ChannelGeneratorConfig.Kind"/>, so
/// switching between them changes the waveform and neither the pitch nor the loudness.</para>
/// </summary>
public sealed record PeriodicWaveSettings
{
    /// <summary>
    /// Requested fundamental. The frequency actually played is <see cref="PeriodicWave.EffectiveFrequencyHz"/>
    /// — only whole even sample periods exist — and that is what device status reports back.
    /// </summary>
    public double FrequencyHz { get; init; } = 1000.0;

    /// <summary>
    /// Total rms of the waveform, dBV referenced to 1 Vrms, the same convention every other kind
    /// here uses. <b>Not a peak</b>: see <see cref="PeriodicWave.PeakVolts"/> for the three
    /// different peaks one level means, and why that is the right way round.
    ///
    /// <para>Quieter by default than the sine generator's -6 dBV on purpose. A square carries its
    /// full amplitude at every instant and drives a DUT's high end as hard as the sample rate
    /// allows, so the first thing a new user hears should not be a full-level edge into an
    /// amplifier.</para>
    /// </summary>
    public double LevelDbv { get; init; } = -20.0;
}

/// <summary>Parameters for <see cref="GeneratorKind.EchoChirp"/>.</summary>
public sealed record EchoChirpSettings
{
    /// <summary>Sweep amplitude, dBV referenced to 1 Vrms.</summary>
    public double AmplitudeDbv { get; init; } = -20.0;

    public double StartFrequencyHz { get; init; } = 20.0;

    public double EndFrequencyHz { get; init; } = 20_000.0;

    /// <summary>Display smoothing for the resulting frequency-response trace — stored only, see <see cref="SmoothingOctaves"/>.</summary>
    public SmoothingOctaves Smoothing { get; init; } = SmoothingOctaves.None;

    /// <summary>
    /// Frames after which the sweep restarts. <b>The frequency-response measurement sets this to its
    /// own FFT size, and that equality is what makes the measurement work at all.</b>
    ///
    /// <para>The QA403 streams continuously and offers no capture trigger, so an analyzer record
    /// begins at an arbitrary point in the stimulus. When the period is exactly the record length,
    /// every record holds exactly one whole sweep — circularly rotated by an unknown amount — and a
    /// circular rotation leaves a magnitude spectrum completely unchanged. The response is then
    /// correct with no triggering, no synchronous start, and no alignment search; only phase carries
    /// the rotation, and removing it is the same step that removes real DUT delay.</para>
    ///
    /// <para>A period in <i>frames</i> rather than seconds is what makes that exact. Seconds would
    /// have to be converted at some sample rate and rounded, and a period off by a single frame puts
    /// a drifting ripple on every trace. It also means the value only has meaning alongside the
    /// sample rate it will be played at.</para>
    ///
    /// <para>The default matches the largest FFT the app uses, so a chirp configured through
    /// <c>POST /api/generator</c> without a measurement behind it is still periodic and still
    /// analysable.</para>
    /// </summary>
    public int RepeatFrames { get; init; } = 131072;
}

/// <summary>
/// One channel's generator state: on/off, which <see cref="GeneratorKind"/> is active, and the
/// settings for each kind.
/// </summary>
/// <remarks>
/// <see cref="Kind"/> and the settings blocks are init-only properties rather than positional
/// parameters so the original <c>new ChannelGeneratorConfig(enabled, tones)</c> call sites keep
/// working and keep meaning what they meant (a plain sine tone list).
///
/// <see cref="Tones"/> is the *effective* tone list: for <see cref="GeneratorKind.Multitone"/> it
/// holds the comb derived from <see cref="Multitone"/>, so everything downstream that reads the
/// generator's tones (device status, the UI's fundamental tracking) works for multitone without
/// knowing anything about how the comb was built. For <see cref="GeneratorKind.Square"/> and
/// <see cref="GeneratorKind.Triangle"/> it holds the single line those shapes' fundamental produces,
/// at its own level rather than the waveform's total (<see cref="PeriodicWave.FundamentalDbv"/>) and
/// at the frequency the quantised period really plays — so the scope trigger, the Gen tile and the
/// gain reading all describe the line the analyzer measures. The synthesizers ignore it for those
/// kinds and read <see cref="Wave"/>, so there is no second copy of the stimulus to disagree with.
/// </remarks>
public sealed record ChannelGeneratorConfig(bool Enabled, IReadOnlyList<GeneratorTone> Tones)
{
    public GeneratorKind Kind { get; init; } = GeneratorKind.Tones;

    public MultitoneSettings? Multitone { get; init; }

    /// <summary>Level for <see cref="GeneratorKind.WhiteNoise"/> and <see cref="GeneratorKind.PinkNoise"/> alike — see <see cref="WhiteNoiseSettings"/>.</summary>
    public WhiteNoiseSettings? Noise { get; init; }

    public EchoChirpSettings? Chirp { get; init; }

    /// <summary>Shared by <see cref="GeneratorKind.Square"/> and <see cref="GeneratorKind.Triangle"/> — see <see cref="PeriodicWaveSettings"/>.</summary>
    public PeriodicWaveSettings? Wave { get; init; }

    public static ChannelGeneratorConfig Off { get; } = new(Enabled: false, Tones: []) { Kind = GeneratorKind.Off };
}

/// <summary>Generator (DAC output) configuration, independent per channel. Mirrors <c>POST /api/generator</c>.</summary>
public sealed record GeneratorConfig(ChannelGeneratorConfig Left, ChannelGeneratorConfig Right)
{
    /// <summary>
    /// Whether every tone frequency is snapped to the nearest coherent FFT bin
    /// (<see cref="Tonebench.Dsp.CoherentFrequency.Nearest"/>) before it reaches the DAC. On by default,
    /// matching the QA403 software's own generator checkbox.
    ///
    /// A tone that does not land exactly on a bin leaks across its neighbours no matter which window
    /// is used, and at 192 kHz with a 131072-point FFT a requested 1000 Hz sits 0.67 of a bin off —
    /// close to the worst case. Snapping moves it to 1000.4883 Hz (bin 683, coprime to N), where the
    /// transform is exact and everything but the fundamental's own three bins drops into the floor.
    ///
    /// Coherence is a property of the *analysis* length, so the snapped frequency depends on the
    /// current FFT size and sample rate: the app re-snaps whenever either changes, and reports the
    /// effective frequency back rather than the requested one. Applied by
    /// <c>DeviceSessionService.ConfigureGeneratorAsync</c>, not here — this record has no idea what
    /// the analyzer is set to.
    /// </summary>
    public bool SnapToBin { get; init; } = true;

    public static GeneratorConfig Off { get; } = new(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off);
}
