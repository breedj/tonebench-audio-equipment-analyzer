using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;
using Tonebench.Dsp;
using Tonebench.Dsp.Fft;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// Does the QA403 actually run at 384 kHz? Register 9 index 3 is documented
/// (<c>docs/protocol.md</c> §2.4, corroborated by a developer-supplied C# snippet, PyQa40x and
/// qa40x-rs-main) and <see cref="Qa40xRegisters.SampleRateHzToRegister"/> already encodes it, but
/// 384 kHz is absent from <see cref="DeviceCapabilities.Qa403"/> and has never been run here.
/// These tests answer five separate questions — does the ADC clock change, is the data real, does
/// the <i>loopback</i> reach past 96 kHz, does the <i>input alone</i> reach past it, and does the
/// DAC work — and they must be read in that order. The third and fourth are not the same question,
/// which is the mistake this file made once: a loopback bounds TX and RX together and can never
/// place a corner at either end, so A3's table was read as a verdict on the input and was not one.
/// See <see cref="ExternalSweep_ShowsTheInputReachesAlmostToNyquist"/>.
///
/// <para><b>The trap that shapes this whole file: a loopback test cannot measure the sample rate.</b>
/// <see cref="Qa40xGeneratorWaveform"/> synthesises against the <i>requested</i> rate
/// (<c>t = frameIndex / sampleRateHz</c>), so if the device ignored register 9 and stayed at
/// 192 kHz, a requested 1 kHz tone clocks out of the DAC as a real 500 Hz, is captured by that same
/// clock, and is read back by us as exactly 1 kHz. TX and RX scale by the identical factor, so every
/// loopback <i>frequency</i> measurement is invariant to the true clock — including the obvious
/// "play a tone above 96 kHz and see if it aliases", since a requested 150 kHz becomes a real
/// 75 kHz, still under 192 kHz-mode Nyquist, and reads back as 150 kHz. Frame arrival rate against
/// a <see cref="Stopwatch"/> is the only proof, which is what <see cref="AdcClock_At384kHz_DeliversFramesAtThatRateAgainstTheWallClock"/>
/// does and why it runs with the generator off. The analog filters are the one part of the chain
/// fixed in hertz and therefore <i>not</i> co-scaled, which is what makes
/// <see cref="Loopback_At384kHz_ReportsTheAnalogBandwidth"/> a cross-check on the clock. It is
/// <i>not</i> the measurement that says whether 384 kHz is worth having — only an external source
/// can say that, because a loopback carries the DAC, and on this device the DAC turned out to be the
/// whole of the roll-off.</para>
///
/// <para>Requires a real QA403 over USB with the outputs looped back to the inputs, L to L and R to
/// R — the same bench as <see cref="Qa40xUsbDeviceHardwareTests"/>. Excluded from the default run;
/// see <c>code/CLAUDE.md</c>. Several of these report a table that is the point even on a pass, so
/// run them with <c>-l "console;verbosity=detailed"</c> or the measurement is invisible.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa403HardwareCollection))]
public class Qa40x384kHzHardwareTests(ITestOutputHelper output)
{
    private const double ExperimentalRateHz = 384_000.0;
    private const double ReferenceRateHz = 192_000.0;

    /// <summary>Matches <see cref="Qa40xUsbDeviceHardwareTests"/>'s loopback bench so the two files' numbers are comparable.</summary>
    private const double LoopbackCommandedDbv = -6.0;

    private const double LoopbackOutputRangeDbv = 8.0;
    private const double LoopbackInputRangeDbv = 6.0;

    private const int BlockFrames = 4096;
    private const int FftSize = 131_072;

    // ---------------------------------------------------------------------------------------
    // A1 — the only test that can establish the rate.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Counts frames against the wall clock, generator off, at 192 kHz and then at 384 kHz. Both
    /// legs in one test on purpose: if the 192 kHz leg also misses, the method or the bench is
    /// wrong rather than the device, and that distinction is worth more than a second test name.
    ///
    /// <para>1 % over a 20 s window is loose against the ~165 ppm host-versus-device clock offset
    /// documented on <c>TransferPumpDiagnostics</c>'s slack metric, and tight against the factor of
    /// two a stuck rate would show. 20 s rather than 2 s because arrival is paced by RX completions
    /// delivered through the thread pool, where stalls of 46-125 ms have been measured: at 20 s one
    /// of those is a 0.6 % error, at 2 s it is 6 %.</para>
    /// </summary>
    [Fact]
    public async Task AdcClock_At384kHz_DeliversFramesAtThatRateAgainstTheWallClock()
    {
        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);

        // Pinned: an autorange relay click mid-window punches a ~110 ms hole in the stream, which
        // would read here as the clock being slow.
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(12.0, CancellationToken.None);
        await device.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);

        double referenceHz = await MeasureFrameRateAsync(device, ReferenceRateHz);
        double experimentalHz = await MeasureFrameRateAsync(device, ExperimentalRateHz);

        output.WriteLine($"192 kHz requested -> {referenceHz:0} frames/s measured ({referenceHz / ReferenceRateHz * 100.0:0.00} %)");
        output.WriteLine($"384 kHz requested -> {experimentalHz:0} frames/s measured ({experimentalHz / ExperimentalRateHz * 100.0:0.00} %)");

        Assert.InRange(referenceHz, ReferenceRateHz * 0.99, ReferenceRateHz * 1.01);

        Assert.True(
            experimentalHz >= ExperimentalRateHz * 0.99 && experimentalHz <= ExperimentalRateHz * 1.01,
            $"Register 9 was written with index 3 but frames arrived at {experimentalHz:0} Hz. A figure near "
                + "192000 means the device ignored the rate and every sample taken at this setting is mislabelled "
                + "by a factor of two — which no loopback tone test can detect, because the generator synthesises "
                + "against the requested rate and so TX and RX scale together. If this is what happened, 384 kHz "
                + "must not be exposed in the UI at all. "
                + $"(The 192 kHz control leg of this same test measured {referenceHz:0} Hz.)");
    }

    /// <summary>
    /// Frames per second of wall clock, over a 20 s window that starts only once the stream is
    /// device-paced. The first two seconds are discarded because none of what happens there is: the
    /// register 9 write, its 100 ms settle, the <c>StreamingStart</c> write, and then the whole TX
    /// and RX queue priming as fast as libusb will take it.
    /// </summary>
    private static async Task<double> MeasureFrameRateAsync(Qa40xUsbDevice device, double sampleRateHz)
    {
        const double WarmupSeconds = 2.0;
        const double WindowSeconds = 20.0;

        var config = new AcquisitionConfig(sampleRateHz, ChannelCount: 2, BlockFrameCount: BlockFrames);
        long warmupFrames = (long)(sampleRateHz * WarmupSeconds);
        long framesSeen = 0;
        long framesInWindow = 0;
        Stopwatch? sw = null;

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                framesSeen += block.FrameCount;

                if (sw is null)
                {
                    // Start the clock on the first post-warmup block and do NOT count its frames:
                    // the window must be an integer number of block periods, measured from one
                    // block's arrival to another's.
                    if (framesSeen >= warmupFrames)
                        sw = Stopwatch.StartNew();
                    continue;
                }

                framesInWindow += block.FrameCount;
            }

            if (sw.Elapsed.TotalSeconds >= WindowSeconds)
                break;
        }

        await device.StopAsync();
        return framesInWindow / sw!.Elapsed.TotalSeconds;
    }

    // ---------------------------------------------------------------------------------------
    // A2 — is the data a converter's output at all?
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Compares the front end's own noise over a <i>fixed</i> 1-20 kHz band at the two rates. That
    /// integral is a property of the analog input stage, so it must not care what rate the ADC is
    /// clocked at — which makes it a check on the sample <i>path</i> rather than on the clock:
    /// zero-stuffing adds broadband energy, a repeated-sample pattern adds a comb, a byte
    /// misalignment moves it by tens of dB. Generator off, input range 0 dBV, because the front
    /// end's noise is the subject and anything louder would swamp it.
    /// </summary>
    [Fact]
    public async Task Capture_At384kHz_IsRealAudioAndNotGarbage()
    {
        const int Records = 16;

        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(0.0, CancellationToken.None);
        await device.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);

        var reference = await CaptureAsync(device, ReferenceRateHz, Records * FftSize);
        var experimental = await CaptureAsync(device, ExperimentalRateHz, Records * FftSize);

        double noise192 = BandNoiseDbv(reference.Left, ReferenceRateHz);
        double noise384 = BandNoiseDbv(experimental.Left, ExperimentalRateHz);

        output.WriteLine($"1-20 kHz integrated noise: 192 kHz {noise192:0.00} dBV, 384 kHz {noise384:0.00} dBV (delta {noise384 - noise192:+0.00;-0.00} dB)");
        output.WriteLine($"DC: 192 kHz L {Mean(reference.Left):+0.0000;-0.0000} V / R {Mean(reference.Right):+0.0000;-0.0000} V");
        output.WriteLine($"DC: 384 kHz L {Mean(experimental.Left):+0.0000;-0.0000} V / R {Mean(experimental.Right):+0.0000;-0.0000} V");

        Assert.True(
            Math.Abs(noise384 - noise192) < 1.5,
            $"The front end's 1-20 kHz noise read {noise192:0.00} dBV at 192 kHz and {noise384:0.00} dBV at "
                + "384 kHz. That band is the same range of hertz either way and the analog stage feeding it is "
                + "unchanged, so a difference this size means the 384 kHz sample path is not simply the same "
                + "converter running faster — look for zero-stuffing (broadband lift), a repeated-sample pattern "
                + "(a comb) or a byte misalignment (tens of dB).");

        // A broken interleave at twice the rate would put one channel's DC on the other. Both idle
        // within a few mV of zero at input range 0.
        Assert.True(Math.Abs(Mean(experimental.Left)) < 0.05, $"Left idles at {Mean(experimental.Left):0.0000} V at 384 kHz.");
        Assert.True(Math.Abs(Mean(experimental.Right)) < 0.05, $"Right idles at {Mean(experimental.Right):0.0000} V at 384 kHz.");

        // The same held-constant scan DiagnosticRecorder runs on every analysed record. Far cheaper
        // to find a stuck converter here than to meet it later as an unexplained broadband floor.
        (int runLength, int at) = LongestHeldRun(experimental.Left);
        output.WriteLine($"Longest held-constant run at 384 kHz: {runLength} frames at index {at}");
        Assert.True(
            runLength <= 8,
            $"{runLength} consecutive identical samples at index {at} of the 384 kHz capture. On a live converter "
                + "with the generator off that does not happen; it is either a stalled ADC or samples being "
                + "repeated to fill a rate the device is not really running at.");
    }

    // ---------------------------------------------------------------------------------------
    // A3 — is there anything up there to measure?
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The analog bandwidth at both rates, through the loopback. <b>Read this table before deciding
    /// what to ship.</b> 384 kHz is only worth having if the anti-alias and reconstruction filters
    /// move with the rate; if they stay at the 192 kHz-mode corner (~86-90 kHz) then 384 kHz buys a
    /// wider x axis over an empty band, and the honest thing is to say so.
    ///
    /// <para>This is the one measurement the TX/RX co-scaling cannot defeat, because an analog
    /// filter's corner is fixed in hertz: if the device really is clocking twice as fast, the same
    /// requested 90 kHz tone is a real 90 kHz rather than a real 45 kHz, and the filter answers
    /// differently.</para>
    ///
    /// <para><b>Measured 2026-10-03, and read it with the caveat below.</b> Relative to each leg's
    /// own 1 kHz: at 192 kHz, −0.04 dB at 20 kHz, −0.23 at 50 k, −0.58 at 80 k, −1.14 at 90 k; at
    /// 384 kHz, −0.72 at 20 kHz, −1.42 at 50 k, −2.26 at 80 k, −8.08 at 90 k, then −32.01 at 120 k,
    /// −31.28 at 150 k and −50.81 at 180 k. So the filters do not move with the rate and 384 kHz
    /// opens no usable band — 192 kHz already reaches everything there is to reach.</para>
    ///
    /// <para><b>The caveat is that this is TX and RX in series, and at 384 kHz the TX half is
    /// broken</b> — see
    /// <see cref="Loopback_At384kHz_TheGeneratorIsGarbage_WhichIsWhyTheRateIsInputOnly"/>, measured
    /// after this and inverting what this table can be used for. Every 384 kHz figure above is
    /// therefore an upper bound on the pair and says nothing about the input path alone, including
    /// the 0.7 dB of audio-band droop, which is as likely to be the DAC as the ADC. Settling the
    /// <i>input</i> path's own bandwidth above 96 kHz needs an external signal source, which this
    /// bench does not have, so it is simply unverified rather than quietly assumed either way. That
    /// is also why the one hard assertion here is about the audio band and not about the top end.</para>
    ///
    /// <para><b>Skipped by default for the same reason A4 is:</b> nine of its fifteen captures play a
    /// tone through the DAC at 384 kHz, which cycles the QA403's output protection relay. The table
    /// above is the result; re-run it only deliberately.</para>
    /// </summary>
    [Fact(Skip = "Plays tones through the QA403's DAC at 384 kHz, which cycles its output protection relay. "
        + "The measured response table is in this method's remarks. Un-skip deliberately, and expect clicking.")]
    public async Task Loopback_At384kHz_ReportsTheAnalogBandwidth()
    {
        double[] referenceTones = [1_000, 10_000, 20_000, 50_000, 80_000, 90_000];
        double[] experimentalTones = [1_000, 10_000, 20_000, 50_000, 80_000, 90_000, 120_000, 150_000, 180_000];

        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(LoopbackInputRangeDbv, CancellationToken.None);
        await device.SetOutputRangeAsync(LoopbackOutputRangeDbv, CancellationToken.None);

        var reference = await ToneResponseAsync(device, ReferenceRateHz, referenceTones);
        var experimental = await ToneResponseAsync(device, ExperimentalRateHz, experimentalTones);

        output.WriteLine("        tone        192 kHz leg     384 kHz leg   (dB, each relative to its own 1 kHz)");
        foreach (double tone in experimentalTones)
        {
            string at192 = reference.TryGetValue(tone, out double r) ? $"{r,10:+0.00;-0.00}" : "         -";
            output.WriteLine($"{tone,12:0} Hz {at192}      {experimental[tone],10:+0.00;-0.00}");
        }

        Assert.True(
            experimental[20_000.0] > -1.0,
            $"At 384 kHz the loopback reads {experimental[20_000.0]:0.00} dB at 20 kHz relative to its own 1 kHz "
                + $"(the 192 kHz leg reads {reference[20_000.0]:0.00} dB). Whatever 384 kHz does above the audio band, "
                + "it must not break the audio band itself. Measured 2026-10-03 at −0.72 dB, so this has about "
                + "0.3 dB of margin — and since the generator is half of this path and is known bad at 384 kHz, a "
                + "regression here is at least as likely to be the DAC as the ADC.");
    }

    /// <summary>Each tone's loopback level in dB relative to this rate's own 1 kHz reading.</summary>
    private async Task<Dictionary<double, double>> ToneResponseAsync(Qa40xUsbDevice device, double sampleRateHz, double[] toneHz)
    {
        const int Records = 4;
        var levels = new Dictionary<double, double>();

        foreach (double tone in toneHz)
        {
            await device.ConfigureGeneratorAsync(
                new GeneratorConfig(
                    new ChannelGeneratorConfig(true, [new GeneratorTone(tone, LoopbackCommandedDbv)]),
                    ChannelGeneratorConfig.Off),
                CancellationToken.None);

            var capture = await CaptureAsync(device, sampleRateHz, Records * FftSize);
            levels[tone] = ToneDbv(capture.Left, sampleRateHz, tone);
        }

        double referenceDbv = levels[1_000.0];
        return levels.ToDictionary(kv => kv.Key, kv => kv.Value - referenceDbv);
    }

    /// <summary>
    /// One tone's absolute level. Separate from <see cref="ToneResponseAsync"/> and synchronous
    /// because <see cref="PsdSpectrum"/> is a ref struct: it cannot be held across an await, so the
    /// capture and the analysis are deliberately two methods rather than one.
    /// </summary>
    private static double ToneDbv(float[] mono, double sampleRateHz, double toneHz)
    {
        var (values, binSizeHz, sums) = AveragedPsd(mono, sampleRateHz);
        var psd = PsdSpectrum.FromValues(values, sampleRateHz, binSizeHz, sums);
        var measured = HarmonicAnalyzer.MeasureTone(psd, psd.BinOfFrequency(toneHz), peakSearchSpanBins: 4);
        return 20.0 * Math.Log10(Math.Max(measured.AmplitudeVrms, double.Epsilon));
    }

    // ---------------------------------------------------------------------------------------
    // A3b — the same question asked of the input alone, which A3 cannot answer.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The <b>input path's own</b> response, measured with an external generator sweeping into the
    /// inputs and this analyzer's generator silent. <b>This is the test that overturned A3's
    /// conclusion, and the reasoning matters more than the numbers.</b>
    ///
    /// <para><see cref="Loopback_At384kHz_ReportsTheAnalogBandwidth"/> reads TX <i>and</i> RX in
    /// series, and both filters can only <i>lose</i> — so a loopback bounds the pair and leaves each
    /// end <b>at least as flat as the figure shown</b>. It can establish an upper bound on a corner
    /// and never the corner itself. A3's table was therefore read as "the analog filters stop near
    /// 90 kHz, so 384 kHz buys no bandwidth", and that inference was wrong: the loss was all in the
    /// DAC.</para>
    ///
    /// <para><b>Measured 2026-10-05, serial 1077527945, fw 60</b>, through three sweeps recorded by
    /// <c>StreamRecorder</c> at 384 kHz. Relative to 1 kHz, the input alone reads <b>−0.0 dB at
    /// 20 kHz, −0.1 at 50 k, −0.3 at 80 k, −0.4 at 90 k, −0.6 at 120 k, −0.8 at 150 k, −1.2 at 180 k
    /// and −3.0 at ≈189 kHz</b> — against the loopback's −32.01 at 120 k and −50.81 at 180 k. The
    /// difference, ~31 dB at 120 kHz, is the DAC's reconstruction filter. What finally stops the
    /// input is the ADC's <i>digital</i> decimation filter at 98 % of Nyquist, which moves with the
    /// sample rate and is not an analog corner at all.</para>
    ///
    /// <para><b>Two generators, not one, and that is the whole point of the measurement.</b> A sweep
    /// reads source × input, so one generator cannot separate its own flatness from the analyzer's.
    /// Four sweeps from two unrelated generators, at three levels spanning 10.4 dB, agreed to
    /// <b>0.00–0.02 dB from 110 to 170 kHz</b> and on the knee at 185 and 189 kHz to 0.08 dB.
    /// Sources do not share a roll-off by coincidence, so the roll-off is the input's; the spread of
    /// level says it does not move with level either, which is what an input stage compressing or
    /// slewing at the top of the band would have looked like. It also settles the clock question A1 exists for, directly: a 150 kHz tone
    /// read back <i>as</i> 150 kHz, where a device secretly still at 192 kHz would have folded it to
    /// 42 kHz.</para>
    ///
    /// <para><b>Which part of that is analog and which is digital</b> was settled by capturing the
    /// same sweep at <b>192 kHz</b>, where 90 kHz sits at 0.94 of Nyquist instead of 0.47. Below
    /// 90 kHz the two rates agree to <b>0.03 dB at the same absolute frequency</b>, so that gentle
    /// droop is fixed in hertz — analog. From 91 kHz the 192 kHz capture dives alone (−0.48 dB at
    /// 92 k, −1.41 at 94 k, −3 dB at <b>95.8 kHz = 0.998 of its own Nyquist</b>) against −0.22,
    /// −0.24 and −0.25 for the 384 kHz capture at those same frequencies, so that cliff is the
    /// decimation filter. The usable band is therefore <b>≈0.94 × Nyquist at either rate</b> —
    /// 90 kHz at 192 kHz, 180 kHz at 384 kHz.</para>
    ///
    /// <para><b>Skipped because it needs gear, not because it is settled</b> — unlike A4, which is
    /// skipped to avoid cycling a relay. Un-skip it with a generator sweeping L+ (shorting plug on
    /// L−) from about 20 kHz to 190 kHz, ~1 Vrms, slowly and at constant amplitude, over the capture
    /// window below. The assertions are the two claims that matter and are deliberately loose: a
    /// response near 150 kHz, which no analog corner at 90 kHz could produce, and a fall by 190 kHz,
    /// which is the brick wall. Everything finer than that is in the table it prints.</para>
    /// </summary>
    [Fact(Skip = "Needs an external generator sweeping into the inputs; the measured table is in this method's remarks.")]
    public async Task ExternalSweep_ShowsTheInputReachesAlmostToNyquist()
    {
        const double CaptureSeconds = 40.0;
        const double ReferenceHz = 30_000.0;

        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(LoopbackInputRangeDbv, CancellationToken.None);

        // Silent: the whole point is that nothing of ours is in the path. A3's own generator would
        // sum with the external one and make the reading meaningless.
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off),
            CancellationToken.None);

        output.WriteLine($"Sweep an external generator into L+ now — 20 kHz to 190 kHz over {CaptureSeconds:0} s, constant amplitude.");
        var capture = await CaptureAsync(device, ExperimentalRateHz, (int)(CaptureSeconds * ExperimentalRateHz));
        var response = SweepResponse(capture.Left, ExperimentalRateHz);

        Assert.True(
            response.Count >= 50,
            $"Only {response.Count} blocks held a tone. Was the generator sweeping into the left input?");

        double reference = LevelNear(response, ReferenceHz)
            ?? throw new Xunit.Sdk.XunitException($"The sweep never passed {ReferenceHz / 1000:0} kHz, so there is nothing to reference against.");

        output.WriteLine($"        tone     dB re {ReferenceHz / 1000:0} kHz   (input alone, generator silent)");
        foreach (double toneHz in new[] { 20e3, 50e3, 80e3, 90e3, 100e3, 120e3, 150e3, 170e3, 180e3, 185e3, 189e3 })
        {
            double? level = LevelNear(response, toneHz);
            output.WriteLine($"{toneHz,12:0} Hz {(level is null ? "         -" : $"{level - reference,10:+0.00;-0.00}")}");
        }

        double? at150k = LevelNear(response, 150_000);
        double? at190k = LevelNear(response, 190_000);
        Assert.True(at150k is not null, "The sweep never reached 150 kHz, which is the frequency the claim rests on.");
        Assert.True(at190k is not null, "The sweep never reached 190 kHz, so the brick wall was not crossed.");

        Assert.True(
            at150k - reference > -2.0,
            $"The input reads {at150k - reference:0.00} dB at 150 kHz relative to {ReferenceHz / 1000:0} kHz. Measured 2026-10-05 at "
                + "−0.8 dB, and the point of the figure is that it is nowhere near a corner: the loopback is 31 dB down there, "
                + "all of it the DAC. A reading below −2 dB means either the input path really has changed or the external "
                + "generator is not flat — check the generator against a second one before believing the analyzer has moved.");

        Assert.True(
            at190k - reference < -1.5,
            $"The input reads {at190k - reference:0.00} dB at 190 kHz, i.e. no brick wall. Measured 2026-10-05 at −3 dB by "
                + "189 kHz, which is the ADC's decimation filter at 98 % of Nyquist. Reading flat here means the sweep never "
                + "actually got above 180 kHz, or something is aliasing a lower tone up into this band.");
    }

    /// <summary>
    /// A swept tone's level against its frequency, one point per block, both read out of the signal
    /// itself.
    ///
    /// <para>Two things here are what make the reading survive a hand-driven sweep. The level is the
    /// <b>summed power of the bins around the peak</b>, not the peak bin: a sweep moves within the
    /// block, so its energy is spread over several bins and the peak bin alone reads low by an
    /// amount that varies with sweep rate. And the frequency is the power-weighted <b>centroid</b> of
    /// those same bins, i.e. the mean frequency the block swept through — so nothing depends on
    /// elapsed time, the operator's sweep rate, or its linearity, and a pause or a stumble costs
    /// resolution rather than correctness.</para>
    /// </summary>
    private static List<SweepPoint> SweepResponse(float[] mono, double sampleRateHz)
    {
        const int Block = 16_384;      // 42.7 ms at 384 kHz: 23 Hz bins, and little smear at a hand sweep rate
        const int HalfBandBins = 40;   // the tone plus its smear; everything outside is the floor

        var window = Window.Get(WindowType.Hann, Block);
        var windowed = new double[Block];
        var spectrum = new Complex[(Block / 2) + 1];
        var backend = new FftFlatBackend();
        double binHz = sampleRateHz / Block;
        double s1 = window.Sums.S1;
        var points = new List<SweepPoint>();

        for (int start = 0; start + Block <= mono.Length; start += Block)
        {
            Window.Apply(mono.AsSpan(start, Block), window, windowed);
            backend.Forward(windowed, spectrum);

            int peak = 1;
            for (int bin = 2; bin < spectrum.Length; bin++)
            {
                if (spectrum[bin].Magnitude > spectrum[peak].Magnitude)
                    peak = bin;
            }

            int lo = Math.Max(1, peak - HalfBandBins);
            int hi = Math.Min(spectrum.Length - 1, peak + HalfBandBins);
            double floor = MedianPowerOutside(spectrum, lo, hi);

            double power = 0;
            double weighted = 0;
            for (int bin = lo; bin <= hi; bin++)
            {
                double p = Math.Max((spectrum[bin].Magnitude * spectrum[bin].Magnitude) - floor, 0);
                power += p;
                weighted += p * bin * binHz;
            }

            if (power <= 0)
                continue;

            double amplitude = 2.0 * Math.Sqrt(power) / s1;
            points.Add(new SweepPoint(weighted / power, 20.0 * Math.Log10(Math.Max(amplitude / Math.Sqrt(2), double.Epsilon))));
        }

        return points;
    }

    /// <summary>
    /// The median level of the blocks within 1.5 % of <paramref name="frequencyHz"/>, or null where
    /// the sweep never went. A median because the stream recorder's own dropped buffers — and any
    /// real splice — show up as isolated blocks reading 1–2 dB low, which a mean would carry into
    /// the answer.
    /// </summary>
    private static double? LevelNear(List<SweepPoint> response, double frequencyHz)
    {
        double span = Math.Max(900, 0.015 * frequencyHz);
        var levels = response
            .Where(p => Math.Abs(p.FrequencyHz - frequencyHz) < span)
            .Select(p => p.LevelDbv)
            .OrderBy(v => v)
            .ToArray();

        return levels.Length == 0 ? null : levels[levels.Length / 2];
    }

    private static double MedianPowerOutside(Complex[] spectrum, int lo, int hi)
    {
        var outside = new List<double>(spectrum.Length);
        for (int bin = 1; bin < spectrum.Length; bin++)
        {
            if (bin < lo || bin > hi)
                outside.Add(spectrum[bin].Magnitude * spectrum[bin].Magnitude);
        }

        if (outside.Count == 0)
            return 0;

        outside.Sort();
        return outside[outside.Count / 2];
    }

    private sealed record SweepPoint(double FrequencyHz, double LevelDbv);

    // ---------------------------------------------------------------------------------------
    // A4 — the ASIO40x FAQ's claim on trial.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>reference/ASIO40x-asio40x-2.2/.../FAQ.md</c> states that 384 kHz is supported "only for
    /// input (recording)" and that "if you try to use the outputs at 384 kHz, you will get garbage.
    /// This is a hardware limitation." Nearly every screen in this app needs the generator, so that
    /// claim decides what can be built. This puts it on trial as a <b>delta against 192 kHz over the
    /// same cable</b>, so the bench's own wiring cannot be mistaken for a DAC fault.
    ///
    /// <para>The three named failure frequencies are each a specific mechanism, and checking them by
    /// name is what turns a THD number into a diagnosis. <c>fs/2 - f</c> is the image a DAC fed at
    /// half rate and zero-stuffed produces; <c>fs/4 ± f</c> is what every-other-sample output gives;
    /// and <c>f/2</c> is the one thing a stuck DAC clock shows through the TX/RX co-scaling, because
    /// here the ADC is clocked correctly (A1 proved that) while the DAC would not be.</para>
    ///
    /// <para><b>Verdict, measured 2026-10-03 on serial 4039_C589: the FAQ is right.</b> A −6 dBV
    /// 1 kHz tone came back at <b>−18.69 dBV</b> with THD <b>−7.1 dBc</b> (~44 %), against
    /// −6.58 dBV and −106.9 dBc at 192 kHz over the same cable. None of the three named mechanisms
    /// accounts for it on its own — the tone is still at 999.02 Hz and f/2 sits 42.3 dB down, so it
    /// is not a half-rate DAC, and fs/2−f and fs/4±f are at −59.8 and −53.5/−54.5 dBc, elevated
    /// against the 192 kHz leg's −139 but not dominant. It is broadband mangling, and the loudest
    /// thing in it is near DC: <b>−14.9 dBc at 3 Hz</b> where 192 kHz reads −47.1. That is what
    /// cycled the QA403's own output protection relay about once a second, and a <c>StreamingStop</c>
    /// did not clear it — the device needed a power cycle.</para>
    ///
    /// <para><b>Skipped by default, deliberately.</b> Running it drives the DAC at 384 kHz again, so
    /// it cycles that protection relay for the length of the run to re-answer a question already
    /// answered. The assertions are therefore inverted: they pin the fault, so that un-skipping this
    /// after a firmware update either confirms it is still there or fails and tells you to revisit
    /// <see cref="IAnalyzerDevice.MaxGeneratorSampleRateHz"/>. Do not fold it back into the ordinary
    /// hardware run.</para>
    /// </summary>
    [Fact(Skip = "Drives the QA403's DAC at 384 kHz, which cycles its output protection relay. The "
        + "measured result is recorded in this method's remarks and in DeviceCapabilities.Qa403Experimental. "
        + "Un-skip only to re-test after a firmware change, and expect relay clicking while it runs.")]
    public async Task Loopback_At384kHz_TheGeneratorIsGarbage_WhichIsWhyTheRateIsInputOnly()
    {
        const double ToneHz = 1_000.0;

        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(LoopbackInputRangeDbv, CancellationToken.None);
        await device.SetOutputRangeAsync(LoopbackOutputRangeDbv, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(ToneHz, LoopbackCommandedDbv)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var reference = await DacReadingAsync(device, ReferenceRateHz, ToneHz);
        var experimental = await DacReadingAsync(device, ExperimentalRateHz, ToneHz);

        output.WriteLine(reference.ToString());
        output.WriteLine(experimental.ToString());

        // The control leg, first: without a clean 192 kHz reading over this same cable, nothing
        // below can be attributed to the rate rather than to the bench.
        Assert.True(
            Math.Abs(reference.FundamentalDbv - LoopbackCommandedDbv) < 0.7,
            $"The 192 kHz control leg read {reference.FundamentalDbv:0.00} dBV for a {LoopbackCommandedDbv:0.0} dBV "
                + $"request. Fix the loopback before reading anything into the 384 kHz leg. {reference}");

        Assert.True(
            reference.ThdDbc < -90.0,
            $"The 192 kHz control leg read THD {reference.ThdDbc:0.0} dBc, which is not a clean loopback. Nothing "
                + $"can be concluded about 384 kHz from a bench that does this at 192. {reference}");

        // And now the fault itself, pinned. Each of these fails if the DAC starts working.
        Assert.True(
            LoopbackCommandedDbv - experimental.FundamentalDbv > 6.0,
            $"At 384 kHz a {LoopbackCommandedDbv:0.0} dBV tone now reads {experimental.FundamentalDbv:0.00} dBV, "
                + "which is far closer to correct than the 12.1 dB shortfall measured on 2026-10-03. The DAC may "
                + "have been fixed in firmware: re-measure, and if it holds, revisit "
                + $"IAnalyzerDevice.MaxGeneratorSampleRateHz and this rate's input-only status. {experimental}");

        Assert.True(
            experimental.ThdDbc > reference.ThdDbc + 40.0,
            $"At 384 kHz THD now reads {experimental.ThdDbc:0.0} dBc against the 192 kHz leg's {reference.ThdDbc:0.0}, "
                + "far better than the −7.1 dBc measured on 2026-10-03. If the generator genuinely works at this rate "
                + $"now, the input-only restriction is obsolete and should be revisited rather than kept. {experimental}");

        // Not a half-rate DAC, and worth keeping pinned: it is the one mechanism the TX/RX
        // co-scaling could otherwise hide, so "the tone is at f, not f/2" is what rules it out.
        Assert.True(
            Math.Abs(experimental.FundamentalHz - ToneHz) < 2.0 * experimental.BinSizeHz,
            $"The 384 kHz tone came back at {experimental.FundamentalHz:0.00} Hz rather than {ToneHz:0} Hz. If it is "
                + "near 500 Hz the DAC is being clocked at half rate, which is a different fault from the broadband "
                + $"mangling measured here and would need its own handling. {experimental}");
    }

    /// <summary>Everything A4 needs from one rate's loopback capture, in one pass so the two legs are strictly comparable.</summary>
    private sealed record DacReading(
        double SampleRateHz, double BinSizeHz, double FundamentalHz, double FundamentalDbv,
        double ThdDbc, double WorstSpurDbc, double WorstSpurHz,
        double ZeroStuffImageDbc, double QuarterRateLowDbc, double QuarterRateHighDbc, double HalfToneDbc)
    {
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"""
                {SampleRateHz / 1000.0:0} kHz leg: fundamental {FundamentalHz:0.00} Hz at {FundamentalDbv:0.00} dBV, bin {BinSizeHz:0.000} Hz
                    THD (H2..H5)            {ThdDbc,8:0.0} dBc
                    worst spur              {WorstSpurDbc,8:0.0} dBc at {WorstSpurHz:0} Hz
                    fs/2-f  zero-stuff      {ZeroStuffImageDbc,8:0.0} dBc
                    fs/4-f  every-other     {QuarterRateLowDbc,8:0.0} dBc
                    fs/4+f  every-other     {QuarterRateHighDbc,8:0.0} dBc
                    f/2     half-rate DAC   {HalfToneDbc,8:0.0} dBc
                """);
    }

    private static async Task<DacReading> DacReadingAsync(Qa40xUsbDevice device, double sampleRateHz, double toneHz)
    {
        const int Records = 8;

        var capture = await CaptureAsync(device, sampleRateHz, Records * FftSize);
        return AnalyseDac(capture.Left, sampleRateHz, toneHz);
    }

    /// <summary>
    /// A4's whole measurement set from one capture. Synchronous and separate from
    /// <see cref="DacReadingAsync"/> for two reasons the compiler enforces: <see cref="PsdSpectrum"/>
    /// is a ref struct and cannot cross an await, and <c>stackalloc</c> is not allowed in an async
    /// method at all.
    /// </summary>
    private static DacReading AnalyseDac(float[] mono, double sampleRateHz, double toneHz)
    {
        const int HarmonicCount = 5;
        const int SpurGuardBins = 8;

        var (psdValues, binSizeHz, sums) = AveragedPsd(mono, sampleRateHz);
        var psd = PsdSpectrum.FromValues(psdValues, sampleRateHz, binSizeHz, sums);

        int fundamentalBin = psd.BinOfFrequency(toneHz);
        Span<Harmonic> harmonics = stackalloc Harmonic[HarmonicCount];
        int found = HarmonicAnalyzer.Analyze(psd, fundamentalBin, HarmonicCount, harmonics, peakSearchSpanBins: 4);

        double fundamentalVrms = found > 0 ? harmonics[0].AmplitudeVrms : 0.0;
        double harmonicPower = 0.0;
        for (int i = 1; i < found; i++)
            harmonicPower += harmonics[i].AmplitudeVrms * harmonics[i].AmplitudeVrms;

        double thdDbc = 20.0 * Math.Log10(Math.Max(Math.Sqrt(harmonicPower), double.Epsilon) / Math.Max(fundamentalVrms, double.Epsilon));

        // Worst discrete line that is neither the fundamental nor one of its harmonics. One scan
        // catching zero-stuffing, sample-dropping and chopping at once, since all three leave an
        // image somewhere.
        double worstPower = 0.0;
        int worstBin = 0;
        for (int bin = 1; bin < psdValues.Length; bin++)
        {
            bool nearATone = false;
            for (int order = 1; order <= HarmonicCount && !nearATone; order++)
                nearATone = Math.Abs(bin - (fundamentalBin * order)) <= SpurGuardBins;

            if (nearATone || psdValues[bin] <= worstPower)
                continue;

            worstPower = psdValues[bin];
            worstBin = bin;
        }

        return new DacReading(
            sampleRateHz,
            binSizeHz,
            (found > 0 ? harmonics[0].Bin : fundamentalBin) * binSizeHz,
            found > 0 ? harmonics[0].AmplitudeDbv : double.NegativeInfinity,
            thdDbc,
            20.0 * Math.Log10(Math.Max(Math.Sqrt(worstPower * binSizeHz), double.Epsilon) / Math.Max(fundamentalVrms, double.Epsilon)),
            worstBin * binSizeHz,
            DbcAt(psd, (sampleRateHz / 2.0) - toneHz, fundamentalVrms),
            DbcAt(psd, (sampleRateHz / 4.0) - toneHz, fundamentalVrms),
            DbcAt(psd, (sampleRateHz / 4.0) + toneHz, fundamentalVrms),
            DbcAt(psd, toneHz / 2.0, fundamentalVrms));
    }

    /// <summary>
    /// One named frequency's level relative to the fundamental. A free-standing method rather than a
    /// local function because <see cref="PsdSpectrum"/> is a ref struct, which a lambda or local
    /// function may not capture — it has to be passed.
    /// </summary>
    private static double DbcAt(PsdSpectrum psd, double frequencyHz, double fundamentalVrms)
    {
        int bin = psd.BinOfFrequency(frequencyHz);
        if (bin < 1 || bin >= psd.ValuesV2PerHz.Length)
            return double.NegativeInfinity;

        var tone = HarmonicAnalyzer.MeasureTone(psd, bin, peakSearchSpanBins: 4);
        return 20.0 * Math.Log10(Math.Max(tone.AmplitudeVrms, double.Epsilon) / Math.Max(fundamentalVrms, double.Epsilon));
    }

    // ---------------------------------------------------------------------------------------
    // A5 — does it stream, and does the queue hold?
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Sustained streaming at 384 kHz with the generator on, asserting the real drop counters and
    /// the pump's own diagnostics.
    ///
    /// <para>Deliberately not modelled on
    /// <c>SustainedCapture_60SecondsAt192kHz_HasNoDroppedSequenceNumbers</c>: per <c>CLAUDE.md</c>
    /// that test cannot see USB-level drops, because <see cref="PcmBlock.SequenceNumber"/> is
    /// assigned locally and stays contiguous across a hole. <see cref="IAnalyzerDevice.DroppedRxChunks"/>
    /// and <see cref="IAnalyzerDevice.DroppedBlocks"/> are what see it, and both are needed — one
    /// combined number cannot say which half fell behind.</para>
    ///
    /// <para><b>The generator stays off</b>, which is a change from how this was first written. It
    /// was on, because a DAC gap only exists if the DAC is being fed and TX is half of what the queue
    /// protects — but at 384 kHz driving the DAC is precisely what cycles the QA403's output
    /// protection relay (see
    /// <see cref="Loopback_At384kHz_TheGeneratorIsGarbage_WhichIsWhyTheRateIsInputOnly"/>), and two
    /// minutes of that to measure a DAC nobody is allowed to use at this rate is a bad trade. The
    /// pump still submits TX — it is duplex by construction — so the queue arithmetic below is
    /// unchanged; what is given up is only that a gap here would be a gap in silence. 120 s because
    /// the dropout rate measured at 192 kHz was about once every 39 s and 384 kHz halves every
    /// margin.</para>
    ///
    /// <para><b>This test is also the proof the queue-depth clamp fix worked.</b> Before it,
    /// <c>QueueDepthFor(384000)</c> = 96 is clamped to <c>MaxQueueDepth</c> = 48 and the summary
    /// reports a primed slack of 64 ms — half <c>TargetInFlightSeconds</c> — against pump stalls
    /// measured at up to 125 ms. After, it reports 128 ms. Run it on both.</para>
    ///
    /// <para>Honest limit: the 46.5-125.3 ms stalls on record were provoked by DSP and broadcast
    /// bursts this test does not create, so a clean run here is necessary and not sufficient. The
    /// sufficient check is the app itself at 384 kHz with a browser attached for fifteen minutes.</para>
    /// </summary>
    [Fact]
    public async Task SustainedStream_At384kHz_DropsNothingAndNeverOutrunsTheTxQueue()
    {
        const double TargetSeconds = 120.0;

        var logger = new CapturingLogger<Qa40xUsbDevice>();
        using var device = new Qa40xUsbDevice(logger);
        await device.ConnectAsync(CancellationToken.None);
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(LoopbackInputRangeDbv, CancellationToken.None);
        await device.SetOutputRangeAsync(LoopbackOutputRangeDbv, CancellationToken.None);
        await device.ConfigureGeneratorAsync(GeneratorConfig.Off, CancellationToken.None);

        var config = new AcquisitionConfig(ExperimentalRateHz, ChannelCount: 2, BlockFrameCount: BlockFrames);
        double secondsSeen = 0.0;

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
                secondsSeen += block.FrameCount / ExperimentalRateHz;

            if (secondsSeen >= TargetSeconds)
                break;
        }

        await device.StopAsync();

        var summary = logger.Entries.LastOrDefault(e => e.Message.Contains("transfer summary", StringComparison.Ordinal));
        Assert.NotNull(summary);

        double primedMs = summary.Number("PrimedMs");
        double minSlackMs = summary.Number("MinSlackMs");
        double worstIterationMs = summary.Number("WorstMs");
        long shortWrites = (long)summary.Number("ShortWrites");
        long shortReads = (long)summary.Number("ShortReads");

        var stalls = logger.Entries.Where(e => e.Message.Contains("DAC starvation", StringComparison.Ordinal)).ToArray();
        double worstStallMs = stalls.Length == 0 ? 0.0 : stalls.Max(e => e.Number("IterationMs"));

        output.WriteLine($"{secondsSeen:0.0} s at 384 kHz: {device.DroppedRxChunks} RX chunks, {device.DroppedBlocks} blocks dropped");
        output.WriteLine($"TX queue primed at {primedMs:0.0} ms; worst pump iteration {worstIterationMs:0.0} ms; {stalls.Length} starvation events, worst {worstStallMs:0.0} ms");
        output.WriteLine($"short writes {shortWrites}, short reads {shortReads}");

        // Reported, never asserted: the slack metric compares frames submitted against the *host*
        // clock while submission is paced by the *device* clock, so over 120 s its ~165 ppm drift
        // ramp puts it tens of ms low with nothing wrong. Its steps mean something; its level does not.
        output.WriteLine($"TX slack floor {minSlackMs:0.0} ms of {primedMs:0.0} (drift ramp, not a level to judge)");

        Assert.True(
            device.DroppedRxChunks == 0,
            $"{device.DroppedRxChunks} RX chunks dropped over {secondsSeen:0.0} s at 384 kHz. Each one is a hole in "
                + "the sample stream, so any FFT record spanning it is spliced. The RX channel holds 341 ms at this "
                + "rate, not the 683 ms its comment quotes for 192 kHz.");

        Assert.True(
            device.DroppedBlocks == 0,
            $"{device.DroppedBlocks} assembled blocks dropped over {secondsSeen:0.0} s at 384 kHz — the consumer fell "
                + "behind the assembler, which is the other half of the pipeline from a dropped chunk.");

        Assert.True(
            worstStallMs < primedMs,
            $"A pump iteration took {worstStallMs:0.0} ms against a TX queue holding {primedMs:0.0} ms. A stall longer "
                + "than the queue drains the DAC by arithmetic, and the result is a held-constant span in the generator "
                + $"output. If {primedMs:0.0} is 64, this is the MaxQueueDepth clamp: QueueDepthFor(384000) wants 96 "
                + "transfers and the clamp allows 48, halving TargetInFlightSeconds at exactly the rate that needs it most.");

        Assert.True(
            shortWrites == 0 && shortReads == 0,
            $"{shortWrites} short writes and {shortReads} short reads at 384 kHz. A short read is a hole no other "
                + "counter sees. The ASIO40x FAQ notes the device's hardware queue is only 1024 samples, needing a "
                + "refresh every 2.5 ms at this rate, which is exactly what a short transfer at 384 kHz would mean.");
    }

    // ---------------------------------------------------------------------------------------
    // Shared helpers.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Streams until <paramref name="targetFrames"/> frames have been collected, deinterleaved per
    /// channel.
    ///
    /// <para>The warm-up discard is <see cref="IAnalyzerDevice.StimulusLatencyFrames"/> and not the
    /// single block <see cref="Qa40xUsbDeviceHardwareTests"/> drops, because every caller here opens
    /// a fresh stream per measurement: at stream start the TX queue is primed as fast as libusb will
    /// take it, so the first queue-depth of RX — 64 ms at 384 kHz, 128 ms at 192 — carries whatever
    /// the DAC was emitting before the tone reached it. One 4096-frame block is 10.7 ms at 384 kHz,
    /// which would leave that transient inside the first averaged record. A block flagged
    /// <see cref="PcmBlock.StartsAfterDiscontinuity"/> restarts the collection, since the analysis
    /// window must break there rather than splice across it.</para>
    /// </summary>
    private static async Task<(float[] Left, float[] Right)> CaptureAsync(Qa40xUsbDevice device, double sampleRateHz, int targetFrames)
    {
        var config = new AcquisitionConfig(sampleRateHz, ChannelCount: 2, BlockFrameCount: BlockFrames);
        var left = new float[targetFrames];
        var right = new float[targetFrames];
        int warmupFrames = device.StimulusLatencyFrames(sampleRateHz) + BlockFrames;
        int discarded = 0;
        int frames = 0;

        await foreach (var block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                if (discarded < warmupFrames || block.StartsAfterDiscontinuity)
                {
                    discarded += block.FrameCount;

                    // A discontinuity invalidates everything already collected.
                    frames = 0;
                    continue;
                }

                var samples = block.Samples;
                int take = Math.Min(block.FrameCount, targetFrames - frames);
                for (int i = 0; i < take; i++)
                {
                    left[frames + i] = samples[i * 2];
                    right[frames + i] = samples[(i * 2) + 1];
                }

                frames += take;
            }

            if (frames >= targetFrames)
                break;
        }

        await device.StopAsync();
        Assert.Equal(targetFrames, frames);
        return (left, right);
    }

    /// <summary>
    /// Hann-windowed PSD averaged over as many whole records as the capture holds. The PSD arrays
    /// are averaged rather than their dB values, and <see cref="PsdSpectrum"/> is a ref struct, so
    /// the accumulation is over plain doubles and the result is wrapped with
    /// <see cref="PsdSpectrum.FromValues"/>. <see cref="FftFlatBackend"/> because that is the
    /// backend production uses.
    /// </summary>
    private static (double[] Values, double BinSizeHz, WindowSums Sums) AveragedPsd(float[] mono, double sampleRateHz)
    {
        int records = mono.Length / FftSize;
        Assert.True(records >= 1, "Capture is shorter than one FFT record.");

        var window = Window.Get(WindowType.Hann, FftSize);
        var windowed = new double[FftSize];
        var spectrum = new Complex[(FftSize / 2) + 1];
        var scratch = new double[(FftSize / 2) + 1];
        var accumulated = new double[(FftSize / 2) + 1];
        var backend = new FftFlatBackend();

        for (int record = 0; record < records; record++)
        {
            Window.Apply(mono.AsSpan(record * FftSize, FftSize), window, windowed);
            backend.Forward(windowed, spectrum);
            var psd = PsdSpectrum.Compute(spectrum, sampleRateHz, window.Sums, scratch);

            for (int bin = 0; bin < accumulated.Length; bin++)
                accumulated[bin] += psd.ValuesV2PerHz[bin];
        }

        for (int bin = 0; bin < accumulated.Length; bin++)
            accumulated[bin] /= records;

        return (accumulated, sampleRateHz / FftSize, window.Sums);
    }

    private static double BandNoiseDbv(float[] mono, double sampleRateHz)
    {
        var (values, binSizeHz, sums) = AveragedPsd(mono, sampleRateHz);
        var psd = PsdSpectrum.FromValues(values, sampleRateHz, binSizeHz, sums);
        return 20.0 * Math.Log10(Math.Max(psd.IntegrateVrms(1_000.0, 20_000.0), double.Epsilon));
    }

    private static double Mean(float[] samples)
    {
        double sum = 0.0;
        foreach (float sample in samples)
            sum += sample;

        return sum / samples.Length;
    }

    /// <summary>Longest run of bit-identical consecutive samples, and where it starts.</summary>
    private static (int Length, int StartIndex) LongestHeldRun(float[] samples)
    {
        int best = 0;
        int bestAt = 0;
        int run = 1;

        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i].Equals(samples[i - 1]))
            {
                run++;
                if (run > best)
                {
                    best = run;
                    bestAt = i - run + 1;
                }
            }
            else
            {
                run = 1;
            }
        }

        return (best, bestAt);
    }

    /// <summary>
    /// One captured log entry, with the template's named values. The pump's diagnostics are not
    /// reachable from a test — <c>TransferPump</c> is a private nested class created inside
    /// <c>StreamAsync</c> — but every figure A5 needs is already a named value on a log template
    /// (<c>Qa40xUsbDevice.LogPumpStalls</c> and <c>LogPumpSummary</c>), so reading them off the
    /// logger needs no production change and no string parsing.
    /// </summary>
    private sealed record LogEntry(LogLevel Level, string Message, Dictionary<string, object?> Values)
    {
        public double Number(string name) =>
            Values.TryGetValue(name, out object? value) && value is not null
                ? Convert.ToDouble(value, CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"Log entry has no numeric '{name}': {Message}");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                foreach (var pair in structured)
                    values[pair.Key] = pair.Value;
            }

            lock (_entries)
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), values));
        }
    }
}
