using Qa403.Device.Simulation;

namespace Qa403.Device.Tests;

/// <summary>
/// The simulated DUT models, measured the only way that proves they are actually in the signal
/// path: by streaming from <see cref="SimulatedDevice"/> and reading the level that comes back.
///
/// <para>Every assertion is a <b>ratio against the same tone through a straight wire</b>, never an
/// absolute level. That isolates one section of one model — a peaking filter's own lift, a
/// high-pass's own roll-off, the reflection's own comb — from everything else in the chain, and it
/// is what keeps these tests from turning into a transcription of the coefficients.</para>
/// </summary>
public class SimulatedDutTests
{
    private const double SampleRateHz = 48_000.0;

    /// <summary>
    /// Streams a tone through the given model and returns its RMS. Only the second half of the
    /// capture is measured: a Q of 20 rings for tens of milliseconds, and a filter chain read from
    /// rest reports its transient rather than its response.
    /// </summary>
    private static async Task<double> ToneRmsAsync(
        SimulatedDutModel model, double freqHz, double seconds = 0.25, int channel = 0)
    {
        var device = new SimulatedDevice(randomSeed: 7);
        await device.ConnectAsync(CancellationToken.None);

        var tone = new ChannelGeneratorConfig(Enabled: true, Tones: [new GeneratorTone(freqHz, 0.0)]);
        await device.ConfigureGeneratorAsync(new GeneratorConfig(tone, tone) { SnapToBin = false }, CancellationToken.None);

        // After the generator config, which rewrites GeneratorLeft/Right on the profile.
        device.SignalProfile = device.SignalProfile with
        {
            Dut = model,
            NoiseEnabled = false,
            HumEnabled = false,
            HarmonicsEnabled = false,
        };

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 2, BlockFrameCount: 512);
        int wanted = (int)(seconds * SampleRateHz);
        var samples = new List<double>(wanted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var block in device.StreamAsync(config, cts.Token))
        {
            for (int frame = 0; frame * config.ChannelCount < block.SampleCount; frame++)
                samples.Add(block.Samples[(frame * config.ChannelCount) + channel]);

            block.Dispose();
            if (samples.Count >= wanted)
                break;
        }

        double sum = 0.0;
        int from = samples.Count / 2;
        for (int i = from; i < samples.Count; i++)
            sum += samples[i] * samples[i];

        return Math.Sqrt(sum / (samples.Count - from));
    }

    /// <summary>Raw samples from a run with the generator <b>off</b> — what the DUT produces unprompted.</summary>
    private static async Task<double[]> SilentCaptureAsync(SimulatedDutModel model, double seconds, int channel = 0)
    {
        var device = new SimulatedDevice(randomSeed: 7);
        await device.ConnectAsync(CancellationToken.None);

        device.SignalProfile = device.SignalProfile with
        {
            Dut = model,
            NoiseEnabled = false,
            HumEnabled = false,
            HarmonicsEnabled = false,
        };

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 2, BlockFrameCount: 512);
        int wanted = (int)(seconds * SampleRateHz);
        var samples = new List<double>(wanted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var block in device.StreamAsync(config, cts.Token))
        {
            for (int frame = 0; frame * config.ChannelCount < block.SampleCount; frame++)
                samples.Add(block.Samples[(frame * config.ChannelCount) + channel]);

            block.Dispose();
            if (samples.Count >= wanted)
                break;
        }

        return [.. samples];
    }

    private static async Task<double> GainDbAsync(SimulatedDutModel model, double freqHz, double seconds = 0.25, int channel = 0)
    {
        double reference = await ToneRmsAsync(SimulatedDutModel.StraightWire, freqHz, seconds, channel);
        double measured = await ToneRmsAsync(model, freqHz, seconds, channel);
        return 20.0 * Math.Log10(measured / reference);
    }

    /// <summary>
    /// The default has to stay a bit-for-bit no-op. Everything else in the app was built and tested
    /// against a simulator with no DUT in it, so a straight wire that was not quite straight would
    /// show up as a hundred small unexplained changes rather than as one.
    /// </summary>
    [Fact]
    public async Task StraightWire_ChangesNothing()
    {
        Assert.Equal(0.0, await GainDbAsync(SimulatedDutModel.StraightWire, 1000.0), 6);
    }

    /// <summary>
    /// The two resonances, and the thing that catches anyone reading the model's coefficients and
    /// expecting to find them: <b>the reflection combs the response everywhere</b>, so no single
    /// frequency reads its own section's gain on its own.
    ///
    /// <para>230 Hz: +4.0 dB of cabinet panel, +0.95 dB of comb (3 ms is 0.69 of a period there) =
    /// +4.95. 4200 Hz: +6.0 of cone breakup plus the comb again = +7.6. 2000 Hz has no resonance at
    /// all and still reads -2.9, because 3 ms is exactly six periods of it and an inverted echo
    /// 12 dB down subtracts 2.5 dB — with the 900 Hz dip's skirt supplying the rest.</para>
    /// </summary>
    [Fact]
    public async Task Loudspeaker_LiftsItsResonances_OverTheReflectionsComb()
    {
        Assert.Equal(4.99, await GainDbAsync(SimulatedDutModel.Loudspeaker, 230.0), 0.3);
        Assert.Equal(7.62, await GainDbAsync(SimulatedDutModel.Loudspeaker, 4200.0), 0.3);
        Assert.Equal(-2.89, await GainDbAsync(SimulatedDutModel.Loudspeaker, 2000.0), 0.3);
    }

    /// <summary>And the box alignment below it: a second-order roll-off at 45 Hz.</summary>
    [Fact]
    public async Task Loudspeaker_RollsOffBelowItsAlignment()
    {
        double gainDb = await GainDbAsync(SimulatedDutModel.Loudspeaker, 20.0, seconds: 0.5);
        Assert.InRange(gainDb, -30.0, -8.0);
    }

    /// <summary>
    /// The reflection, measured as the comb it makes rather than as an echo in the waveform: a copy
    /// 3 ms late and inverted adds at 1500 Hz (four and a half periods of delay) and subtracts at
    /// 1666.7 Hz (five), which is 4.4 dB between two frequencies a tenth of an octave apart. No
    /// filter in the chain can do that, so this is the delay line and nothing else.
    ///
    /// <para>The tube amplifier has no reflection and is the control: across the same two
    /// frequencies it is flat.</para>
    /// </summary>
    [Fact]
    public async Task Loudspeaker_CombsTheResponseWhereItsReflectionSays()
    {
        double peakDb = await GainDbAsync(SimulatedDutModel.Loudspeaker, 1500.0);
        double nullDb = await GainDbAsync(SimulatedDutModel.Loudspeaker, 1666.667);
        Assert.Equal(4.44, peakDb - nullDb, 1.0);

        double controlPeak = await GainDbAsync(SimulatedDutModel.TubeAmplifier, 1500.0);
        double controlNull = await GainDbAsync(SimulatedDutModel.TubeAmplifier, 1666.667);
        Assert.Equal(0.0, controlPeak - controlNull, 0.3);
    }

    /// <summary>The output transformer's low end. Its high end lives above 24 kHz and needs a 96 kHz span to see.</summary>
    [Fact]
    public async Task TubeAmplifier_RollsOffBelowItsTransformer()
    {
        Assert.True(await GainDbAsync(SimulatedDutModel.TubeAmplifier, 10.0, seconds: 0.6) < -3.0);
        Assert.Equal(0.0, await GainDbAsync(SimulatedDutModel.TubeAmplifier, 1000.0), 0.5);
    }

    /// <summary>
    /// The band-pass is the model that exists to be checked rather than looked at, so its own shape
    /// has to be exact: unity at the centre, and <b>-3 dB at the two half-power frequencies</b>,
    /// which for a Q of 20 sit at 1000 x (1 +/- 1/40) = 975 and 1025 Hz.
    ///
    /// <para>That is the assertion that matters, because Q is what fixes the ring: a second-order
    /// resonance loses 27.29/Q dB per period of its own centre frequency, so pinning the bandwidth
    /// pins the decay rate the two surfaces must draw without needing to run either of them here.</para>
    /// </summary>
    [Fact]
    public async Task BandpassFilter_HasTheBandwidthItsQPromises()
    {
        Assert.Equal(0.0, await GainDbAsync(SimulatedDutModel.BandpassFilter, 1000.0), 0.15);
        Assert.Equal(-3.0, await GainDbAsync(SimulatedDutModel.BandpassFilter, 975.0), 0.3);
        Assert.Equal(-3.0, await GainDbAsync(SimulatedDutModel.BandpassFilter, 1025.0), 0.3);
    }

    /// <summary>
    /// And nothing else in the path: an octave either side a lone second-order band-pass is down by
    /// <c>20 log10(Q |f/f0 - f0/f|)</c>, which is 29.5 dB at both 500 Hz and 2 kHz. A model that had
    /// picked up a second section, a roll-off or a reflection would not be symmetric about its
    /// centre like this, and would stop being the thing you can check an analysis against.
    /// </summary>
    [Fact]
    public async Task BandpassFilter_IsSymmetricAndAloneInThePath()
    {
        double below = await GainDbAsync(SimulatedDutModel.BandpassFilter, 500.0);
        double above = await GainDbAsync(SimulatedDutModel.BandpassFilter, 2000.0);

        Assert.Equal(-29.5, below, 0.5);
        Assert.Equal(below, above, 0.3);
    }

    /// <summary>
    /// The marginally stable amplifier, measured where it matters: the resonance is <b>narrow</b>,
    /// and narrow is what "about to oscillate" means. At Q 500 the half-power points sit 1 Hz either
    /// side of 1 kHz, so 1010 Hz — ten bandwidths away — is already back to flat.
    ///
    /// <para>Q is also what sets the ring, at 27.29/Q = 0.055 dB per period, so pinning the shape
    /// pins the decay the surfaces will draw. Every capture here is long: a resonance that takes a
    /// third of a second to die also takes that long to reach steady state, and a shorter one would
    /// measure the transient rather than the response.</para>
    /// </summary>
    [Fact]
    public async Task MarginallyStable_IsTallAndVeryNarrow()
    {
        Assert.Equal(12.0, await GainDbAsync(SimulatedDutModel.MarginallyStable, 1000.0, seconds: 3.0), 0.5);
        Assert.Equal(0.0, await GainDbAsync(SimulatedDutModel.MarginallyStable, 1010.0, seconds: 0.5), 0.5);
        Assert.Equal(0.0, await GainDbAsync(SimulatedDutModel.MarginallyStable, 500.0, seconds: 0.5), 0.3);
    }

    /// <summary>
    /// The oscillator's whole point: with the generator off and no noise, a straight wire is silent
    /// and this is not. Nothing else in the simulator produces an output its input had no part in.
    /// </summary>
    [Fact]
    public async Task Oscillating_SingsWithNoInputAtAll()
    {
        var quiet = await SilentCaptureAsync(SimulatedDutModel.StraightWire, 1.5);
        Assert.All(quiet, v => Assert.Equal(0.0, v, 12));

        var singing = await SilentCaptureAsync(SimulatedDutModel.Oscillating, 1.5);
        var settled = singing[(singing.Length / 2)..];
        double rms = Math.Sqrt(settled.Sum(v => v * v) / settled.Length);

        // A limit cycle against a 0.5 V rail: somewhere between a sine at the rail (0.354) and a
        // square at it (0.5).
        Assert.InRange(rms, 0.30, 0.50);
    }

    /// <summary>
    /// <b>Bounded, and that is the property the whole model hangs on.</b> An unstable pole grows
    /// without limit; in <c>double</c> it reaches infinity in about a minute and a half at this pole
    /// radius, and the first subtraction after that yields a NaN which then spreads into the FFT,
    /// the metrics and anything saved. The rail lives inside the recursion so the fed-back values
    /// are the clamped ones — clamping only the output would bound what the analyzer sees while the
    /// state ran away behind it, which looks correct right up until it doesn't.
    /// </summary>
    [Fact]
    public async Task Oscillating_StaysFiniteAndInsideItsRail()
    {
        var samples = await SilentCaptureAsync(SimulatedDutModel.Oscillating, 3.0);

        Assert.All(samples, v => Assert.True(double.IsFinite(v), "a non-finite sample reached the stream"));
        Assert.True(samples.Max(Math.Abs) <= 0.51, $"peak {samples.Max(Math.Abs)} V exceeded the rail");

        // And it is still going at the end rather than having decayed — it is unstable, not merely rung.
        var tail = samples[^4800..];
        Assert.True(Math.Sqrt(tail.Sum(v => v * v) / tail.Length) > 0.3);
    }

    /// <summary>
    /// It sings at its own frequency, not at whatever the input happens to be doing — and that
    /// frequency is about 1% above the pole rather than on it.
    ///
    /// <para>That 1% is the model working, not drifting: clipping drops the loop gain to exactly
    /// what sustains oscillation and the frequency moves to where the phase still closes,
    /// <c>cos w = cos(w0)/r</c>. The number is pinned here because it is also the check that the
    /// pole excess is scaled to w0^2 — with a radius fixed per sample instead, the same pole sings
    /// at 1029 Hz at 48 kHz and 1390 Hz at 192 kHz, and a demo that changes pitch with the sample
    /// rate is a bug however well it oscillates.</para>
    /// </summary>
    [Fact]
    public async Task Oscillating_SingsJustAboveItsPole()
    {
        var samples = await SilentCaptureAsync(SimulatedDutModel.Oscillating, 2.0);
        var settled = samples[(samples.Length / 2)..];

        int crossings = 0;
        for (int i = 1; i < settled.Length; i++)
        {
            if ((settled[i - 1] < 0.0 && settled[i] >= 0.0) || (settled[i - 1] >= 0.0 && settled[i] < 0.0))
                crossings++;
        }

        double hz = crossings / 2.0 / (settled.Length / SampleRateHz);
        Assert.Equal(1010.0, hz, 6.0);
    }

    /// <summary>
    /// The DUT is on the left input only, and that is load-bearing rather than an economy: the
    /// impulse response's <c>RightChannel</c> reference divides left by right, so a DUT on both
    /// would cancel itself out and report a flat line for every model.
    /// </summary>
    [Fact]
    public async Task Dut_IsOnTheLeftChannelOnly()
    {
        Assert.True(await GainDbAsync(SimulatedDutModel.Loudspeaker, 20.0, seconds: 0.5, channel: 0) < -8.0);
        Assert.Equal(0.0, await GainDbAsync(SimulatedDutModel.Loudspeaker, 20.0, seconds: 0.5, channel: 1), 6);
    }
}
