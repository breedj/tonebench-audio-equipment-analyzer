using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// Settles whether anything can be measured through a QA451 with its loads <b>open</b>, and measures
/// the BNC output's attenuation and filter corner while it is at it. Two QuantAsylum sources
/// disagree, and the answer decides whether this app may ever measure through the box without
/// engaging a load:
///
/// <list type="bullet">
/// <item>the user's manual (p. 9): "when the QA451 is open, the QA451 +/- outputs are meaningless.
/// That is, the input signals cannot be measured when the loads are disconnected";</item>
/// <item>QuantAsylum's own blog on using external loads: the QA450 muted its output when the loads
/// were disconnected, but "on the QA451, the measurement path was decoupled from the load path" —
/// and that post's whole recipe, an external kilowatt resistor bank with the internal loads off,
/// only works if that is true.</item>
/// </list>
///
/// <para><b>Bench setup.</b> QA403 left generator out into the QA451's <b>L+/L−</b> load input
/// (centre to L+, shell to L−), and the QA451's left <b>BNC output</b> back into the QA403's left
/// input. Nothing else.</para>
///
/// <para><b>The loads stay open.</b> This test never commands one, and asserts the device reports
/// them open before and after — not caution for its own sake: the QA403's output cannot drive 8 Ω,
/// let alone 4. With the loads open the QA451 presents a high impedance to the generator — nominally
/// "~10 kΩ" per the manual, measured on this unit at <b>4.34 kΩ</b> with a DVM — which loads the
/// QA403's output by nothing.</para>
///
/// <para>Each level is measured with a single-bin DFT at a frequency snapped to an exact bin of the
/// capture length, so the tone is coherent and the reading is bin-limited rather than broadband.
/// That matters at the top of the sweep, where a 6th-order roll-off puts the signal far enough down
/// that broadband RMS would be measuring noise instead.</para>
///
/// <para>In <see cref="Qa403HardwareCollection"/> rather than the QA451's, because QA403 contention
/// is a hard failure — its interface is claimed exclusively — where two handles on a QA451 merely
/// interleave. xUnit allows a class only one collection, so this picks the one that fails loudly.
/// </para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa403HardwareCollection))]
public class Qa451MeasurementPathHardwareTests(ITestOutputHelper output)
{
    private const double SampleRateHz = 192_000.0;
    private const int CaptureFrames = 65_536;
    private const int BlockFrames = 4096;

    /// <summary>Generator level. Through the specified 12 dB it should arrive near −22 dBV.</summary>
    private const double GeneratorDbv = -10.0;

    /// <summary>A QA403 output range (−12/−2/8/18 dBV) with headroom for <see cref="GeneratorDbv"/>.</summary>
    private const double OutputRangeDbv = -2.0;

    /// <summary>Input range for a ~−22 dBV arrival.</summary>
    private const double InputRangeDbv = 0.0;

    /// <summary>Blocks to discard after each generator change, before measuring.</summary>
    private const int BlocksToDiscard = 3;

    /// <summary>The specified attenuation of the BNC outputs, differentially.</summary>
    private const double SpecifiedAttenuationDb = 12.0;

    private static readonly double[] TargetFrequenciesHz =
        [100.0, 1_000.0, 10_000.0, 20_000.0, 40_000.0, 50_000.0, 65_000.0, 80_000.0, 90_000.0];

    [Fact]
    public async Task WithTheLoadsOpen_TheMeasurementPathStillCarriesTheSignal()
    {
        using Qa451Load load = new(NullLogger<Qa451Load>.Instance);
        await load.ConnectAsync(resetToKnownState: true);
        LoadReading before = await load.ReadAsync();
        output.WriteLine(
            $"QA451 {load.Info!.Model}: status 0x{before.RawStatusBits:x3}, loads "
                + $"{before.EngagedImpedance?.ToString() ?? "unknown"}, DUT power {before.DutPowerEngaged}");
        Assert.Equal(LoadImpedance.Open, before.EngagedImpedance);
        Assert.False(before.DutPowerEngaged);

        using var analyzer = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await analyzer.ConnectAsync(CancellationToken.None);
        analyzer.AutoRangeEnabled = false;
        await analyzer.SetInputRangeAsync(InputRangeDbv, CancellationToken.None);
        await analyzer.SetOutputRangeAsync(OutputRangeDbv, CancellationToken.None);

        List<(double FrequencyHz, double MeasuredDbv, double AttenuationDb)> readings =
            await SweepAsync(analyzer);

        LoadReading after = await load.ReadAsync();
        Assert.Equal(LoadImpedance.Open, after.EngagedImpedance);
        Assert.False(after.DutPowerEngaged);

        double atOneKhz = readings.First(r => Math.Abs(r.FrequencyHz - 1000.0) < 50.0).MeasuredDbv;

        // The question the test exists for. A signal this far above the analyzer's own floor cannot
        // be anything but the generator arriving through the QA451.
        Assert.True(
            atOneKhz > -70.0,
            $"Nothing arrived at 1 kHz ({atOneKhz:F1} dBV). Either the manual is right and open loads "
                + "really do mute the measurement path, or the bench is not wired as this test assumes "
                + "(QA403 left out -> QA451 L+/L-, QA451 left BNC out -> QA403 left in). Check the "
                + "wiring before believing the manual.");

        // And it arrives at the specified level, which is what says the path is intact rather than
        // merely leaking something.
        double attenuationAtOneKhz = GeneratorDbv - atOneKhz;
        Assert.True(
            Math.Abs(attenuationAtOneKhz - SpecifiedAttenuationDb) < 2.0,
            $"Attenuation at 1 kHz is {attenuationAtOneKhz:F2} dB, not the specified "
                + $"{SpecifiedAttenuationDb} dB.");

        // Flat across the audio band.
        double[] audioBand =
            [.. readings.Where(r => r.FrequencyHz <= 20_500.0).Select(r => r.MeasuredDbv)];
        output.WriteLine(
            $"audio band spread: {audioBand.Max() - audioBand.Min():F2} dB "
                + $"({audioBand.Min():F2}..{audioBand.Max():F2} dBV)");
        Assert.True(
            audioBand.Max() - audioBand.Min() < 1.0,
            $"The 100 Hz-20 kHz readings span {audioBand.Max() - audioBand.Min():F2} dB.");

        // And rolls off above the corner. The manual says ~65 kHz, the product sheet 70 kHz, and a
        // 6th-order filter past either should be unmistakable by 90 kHz.
        (double topHz, double topDbv, _) = readings[^1];
        output.WriteLine(
            $"roll-off: {topDbv - atOneKhz:F2} dB at {topHz / 1000.0:F1} kHz relative to 1 kHz");
        Assert.True(
            atOneKhz - topDbv > 2.0,
            $"No roll-off: {topHz / 1000.0:F1} kHz is only {atOneKhz - topDbv:F2} dB below 1 kHz.");
    }

    /// <summary>
    /// The chain measured by <see cref="WithTheLoadsOpen_TheMeasurementPathStillCarriesTheSignal"/>
    /// on 2026-10-11, in the order of <see cref="TargetFrequenciesHz"/>. Kept so the control run
    /// below can subtract it and leave the QA451's own response.
    /// </summary>
    private static readonly double[] ChainDbv =
        [-22.33, -22.32, -22.34, -22.44, -23.23, -24.17, -26.50, -29.80, -32.78];

    /// <summary>
    /// The control for that curve: the QA403 looped back to <b>itself</b>, with the QA451 out of the
    /// signal path entirely. Subtracting this from <see cref="ChainDbv"/> cancels the analyzer and
    /// leaves the QA451's filter alone.
    ///
    /// <para>It is needed because the chain's −3 dB point (~57 kHz) is below what either QuantAsylum
    /// document claims, and this project has already established that the QA403's own DAC
    /// reconstruction filter is what produces the 86–90 kHz corner in every loopback figure here —
    /// so some of that droop was never the QA451's. Two sources agreeing is the same technique that
    /// corrected the 384 kHz conclusion (CLAUDE.md).</para>
    ///
    /// <para><b>Bench setup.</b> QA403 left output straight into its own left input. The QA451 may
    /// stay plugged in; nothing here touches it.</para>
    /// </summary>
    [Fact]
    public async Task TheAnalyzersOwnLoopback_SeparatesItsDacRollOffFromTheQa451sFilter()
    {
        using var analyzer = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await analyzer.ConnectAsync(CancellationToken.None);
        analyzer.AutoRangeEnabled = false;
        await analyzer.SetInputRangeAsync(InputRangeDbv, CancellationToken.None);
        await analyzer.SetOutputRangeAsync(OutputRangeDbv, CancellationToken.None);

        List<(double FrequencyHz, double MeasuredDbv, double AttenuationDb)> control =
            await SweepAsync(analyzer);

        Assert.Equal(ChainDbv.Length, control.Count);

        // The analyzer on its own: flat in the audio band, or this control is measuring something
        // other than what it thinks.
        double[] audioBand =
            [.. control.Where(r => r.FrequencyHz <= 20_500.0).Select(r => r.MeasuredDbv)];
        Assert.True(
            audioBand.Max() - audioBand.Min() < 0.5,
            $"The analyzer's own loopback spans {audioBand.Max() - audioBand.Min():F2} dB over "
                + "100 Hz-20 kHz, so it is not a usable control.");

        double controlAtOneKhz = control[1].MeasuredDbv;
        double chainAtOneKhz = ChainDbv[1];

        output.WriteLine(string.Empty);
        output.WriteLine("        f      control   analyzer re 1k   QA451 alone");
        var qa451Alone = new double[control.Count];
        for (int i = 0; i < control.Count; i++)
        {
            double analyzerRelative = control[i].MeasuredDbv - controlAtOneKhz;
            double chainRelative = ChainDbv[i] - chainAtOneKhz;
            qa451Alone[i] = chainRelative - analyzerRelative;
            output.WriteLine(
                $"  {control[i].FrequencyHz,9:F1} Hz  {control[i].MeasuredDbv,8:F2} dBV  "
                    + $"{analyzerRelative,8:F2} dB  {qa451Alone[i],10:F2} dB");
        }

        output.WriteLine(string.Empty);
        output.WriteLine($"chain  -3 dB at {MinusThreeDbHz(control.Select((r, i) => ChainDbv[i] - chainAtOneKhz))}");
        output.WriteLine($"analyzer -3 dB at {MinusThreeDbHz(control.Select(r => r.MeasuredDbv - controlAtOneKhz))}");
        output.WriteLine($"QA451  -3 dB at {MinusThreeDbHz(qa451Alone)}");
    }

    /// <summary>
    /// Linearly interpolates the frequency at which a relative-dB curve first passes −3 dB, as text.
    /// </summary>
    private static string MinusThreeDbHz(IEnumerable<double> relativeDb)
    {
        double[] curve = [.. relativeDb];
        for (int i = 1; i < curve.Length; i++)
        {
            if (curve[i] <= -3.0 && curve[i - 1] > -3.0)
            {
                double lowHz = SnapToBin(TargetFrequenciesHz[i - 1]);
                double highHz = SnapToBin(TargetFrequenciesHz[i]);
                double fraction = (-3.0 - curve[i - 1]) / (curve[i] - curve[i - 1]);
                return $"{(lowHz + (fraction * (highHz - lowHz))) / 1000.0:F1} kHz";
            }
        }

        return $"above {SnapToBin(TargetFrequenciesHz[^1]) / 1000.0:F1} kHz";
    }

    /// <summary>Plays each frequency in turn and measures what arrives, silencing the generator afterwards.</summary>
    private async Task<List<(double FrequencyHz, double MeasuredDbv, double AttenuationDb)>> SweepAsync(
        Qa40xUsbDevice analyzer)
    {
        var readings = new List<(double FrequencyHz, double MeasuredDbv, double AttenuationDb)>();
        try
        {
            foreach (double target in TargetFrequenciesHz)
            {
                double frequencyHz = SnapToBin(target);
                await analyzer.ConfigureGeneratorAsync(
                    new GeneratorConfig(
                        new ChannelGeneratorConfig(true, [new GeneratorTone(frequencyHz, GeneratorDbv)]),
                        ChannelGeneratorConfig.Off),
                    CancellationToken.None);

                double measuredDbv = await MeasureToneDbv(analyzer, frequencyHz);
                double attenuationDb = GeneratorDbv - measuredDbv;
                readings.Add((frequencyHz, measuredDbv, attenuationDb));
                output.WriteLine(
                    $"  {frequencyHz,9:F1} Hz  measured {measuredDbv,8:F2} dBV  "
                        + $"attenuation {attenuationDb,6:F2} dB");
            }
        }
        finally
        {
            await analyzer.ConfigureGeneratorAsync(
                new GeneratorConfig(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off),
                CancellationToken.None);
        }

        return readings;
    }

    /// <summary>
    /// Rounds a frequency to an exact bin of the capture length, so a rectangular-window single-bin
    /// DFT reads its amplitude exactly.
    /// </summary>
    private static double SnapToBin(double targetHz) =>
        Math.Round(targetHz * CaptureFrames / SampleRateHz) * SampleRateHz / CaptureFrames;

    /// <summary>
    /// Captures <see cref="CaptureFrames"/> frames of the left input and returns the level at
    /// <paramref name="frequencyHz"/> in dBV, by a single-bin DFT.
    /// </summary>
    private static async Task<double> MeasureToneDbv(Qa40xUsbDevice device, double frequencyHz)
    {
        var samples = new double[CaptureFrames];
        int taken = 0;
        int discarded = 0;

        var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: BlockFrames);
        await foreach (PcmBlock block in device.StreamAsync(config, CancellationToken.None))
        {
            using (block)
            {
                if (discarded < BlocksToDiscard)
                {
                    discarded++;
                }
                else
                {
                    foreach (float sample in block.Samples)
                    {
                        if (taken < samples.Length)
                        {
                            samples[taken++] = sample;
                        }
                    }
                }
            }

            if (taken >= samples.Length)
            {
                break;
            }
        }

        await device.StopAsync();

        int bin = (int)Math.Round(frequencyHz * CaptureFrames / SampleRateHz);
        double angleStep = 2.0 * Math.PI * bin / CaptureFrames;
        double real = 0.0;
        double imaginary = 0.0;
        for (int n = 0; n < samples.Length; n++)
        {
            (double sin, double cos) = Math.SinCos(angleStep * n);
            real += samples[n] * cos;
            imaginary -= samples[n] * sin;
        }

        double peak = 2.0 * Math.Sqrt((real * real) + (imaginary * imaginary)) / samples.Length;
        return 20.0 * Math.Log10(peak / Math.Sqrt(2.0));
    }
}
