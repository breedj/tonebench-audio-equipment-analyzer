namespace Tonebench.Dsp;

/// <summary>Which way through the level a qualifying crossing goes.</summary>
public enum TriggerSlope
{
    Rising,
    Falling,
    Either,
}

/// <summary>Which signal a trigger watches.</summary>
public enum TriggerSource
{
    ChannelA,
    ChannelB,
    /// <summary>The math trace, when one is configured; the engine resolves it to samples.</summary>
    Math,
    /// <summary>
    /// The generator's own phase rather than the measured signal. No real oscilloscope has this and
    /// this analyzer does, because it composes the stimulus: see the chirp measurement, which rests
    /// on the same fact. Resolved by the engine, not by a scanner.
    /// </summary>
    Stimulus,
}

/// <summary>What kind of event qualifies.</summary>
public enum TriggerKind
{
    Edge,
    PulseWidth,
    Window,
    Dropout,
    Runt,
}

/// <summary>
/// Where a qualifying crossing was found, to better than a sample.
///
/// <para>The crossing lies at <c>Index + Fraction</c> in absolute sample indices:
/// <see cref="Index"/> is the last sample on the near side of the level and <see cref="Fraction"/>
/// is in [0, 1]. Reporting it this way rather than as a bare index is not a refinement — at
/// 192 kS/s one sample is 5.2 us, so a trace triggered on the integer index alone jitters by that
/// much and a high-frequency waveform visibly walks sideways on screen.</para>
/// </summary>
/// <param name="Index">The last sample index before the crossing.</param>
/// <param name="Fraction">How far from <paramref name="Index"/> to the next sample the crossing lies, in [0, 1].</param>
public readonly record struct TriggerHit(long Index, double Fraction)
{
    /// <summary>The crossing's position in fractional absolute samples.</summary>
    public double Position => Index + Fraction;
}

/// <summary>
/// Everything that decides whether a sample qualifies. One record for every
/// <see cref="TriggerKind"/>, because the scope's settings panel edits one object and the
/// unused fields of the kind not selected are simply not read -- the same shape as
/// <c>ImpedanceRequest</c> carrying both stimulus groups and validating only the one in use.
/// </summary>
/// <param name="Kind">Which scanner the engine builds.</param>
/// <param name="Source">Which signal it watches.</param>
/// <param name="Slope">Which direction qualifies. <see cref="TriggerKind.Window"/> reads this as enter (Rising) or leave (Falling).</param>
/// <param name="LevelV">The threshold, in the same volts the samples are in.</param>
/// <param name="HysteresisV">
/// How far past the level the signal must sit on the near side before a crossing counts, in volts.
/// Zero disables it. This is the noise-reject control: without it a signal sitting on the level
/// fires on every sample of its own noise.
/// </param>
/// <param name="LevelLowV">The second threshold, for <see cref="TriggerKind.Window"/> and <see cref="TriggerKind.Runt"/>.</param>
/// <param name="WidthLoS">The low end of the qualifying width, in seconds.</param>
/// <param name="WidthHiS">The high end of the qualifying width, in seconds. Not finite means unbounded.</param>
/// <param name="HoldoffFrames">
/// How many frames after a hit the scanner refuses to fire again. A count of frames and not a wall
/// clock, for the reason the chirp's period is a frame count: a time would round differently at
/// each sample rate and the holdoff would not be the same holdoff.
/// </param>
public readonly record struct TriggerSpec(
    TriggerKind Kind,
    TriggerSource Source,
    TriggerSlope Slope,
    double LevelV,
    double HysteresisV = 0.0,
    double LevelLowV = 0.0,
    double WidthLoS = 0.0,
    double WidthHiS = double.PositiveInfinity,
    int HoldoffFrames = 0)
{
    /// <summary>A plain rising-edge trigger at <paramref name="levelV"/> on channel A.</summary>
    public static TriggerSpec Edge(double levelV, TriggerSlope slope = TriggerSlope.Rising, double hysteresisV = 0.0) =>
        new(TriggerKind.Edge, TriggerSource.ChannelA, slope, levelV, hysteresisV);
}
