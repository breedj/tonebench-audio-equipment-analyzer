using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// The QA402 handover. <b>Every test here is skipped, and not because anything is settled — because
/// this bench has no QA402.</b> Whoever first attaches one should un-skip the class and run it; that
/// is the moment <see cref="Qa40xModel.Qa402"/>'s <c>Verified</c> flag may be set to true, which also
/// removes the status bar's amber badge and the warning on connect.
///
/// <para><b>Why the support exists without a measurement.</b> Four independent trees treat the QA402
/// and QA403 as one protocol: QuantAsylum's own bare-metal example has a "Connect to QA402" button
/// calling the same open function (Form1.cs:29-30 against :42-43), their PyQa40x opens the QA402's
/// product id first and then runs a single code path (analyzer.py:56-58), the ASIO40x driver says
/// outright that "this code also work with the QA402 since the protocol is supposed to be identical"
/// and constructs a QA402 <i>as its QA403 class</i> (qa403.h:13-14, asio401.cpp:279-282), and
/// qa40x-rs parametrises one implementation by a model enum whose only consequences are the
/// sample-rate table and firmware flashing (types.rs:11-108). The calibration page is the one thing
/// only a third party asserts: "Factory calibration data stored in the QA402/QA403 flash"
/// (ASIO40x calibration.h:13). That is good evidence and it is not a measurement, which is the
/// distinction this class exists to close.</para>
///
/// <para>Bench: a real QA402 over USB, outputs looped back to the inputs L to L and R to R — the same
/// bench as <see cref="Qa40xUsbDeviceHardwareTests"/>, deliberately, so the two files' numbers are
/// directly comparable. In <see cref="Qa403HardwareCollection"/> because both models claim interface
/// 0 of a QA40x and xunit would otherwise run the two classes in parallel.</para>
///
/// <para><b>Run them in this order and stop at the first failure</b>, since each assumes the previous
/// one held. If A2 fails, the calibration page layout differs between the models and
/// <see cref="Qa40xCalibration"/> needs a per-model table — that is the one finding that would turn
/// this from a parametrisation into real work.</para>
///
/// <para><b>An emulator cannot stand in for this.</b> <see cref="Qa402VirtualDeviceTests"/> runs the
/// same mechanics against <c>virtual-qa40x-rs</c> attached over USB/IP, sharing this class's capture
/// loops through <see cref="Qa40xLoopbackMeasurement"/>. That is worth having — it is the only
/// coverage the QA402 product id, and the whole of <c>Qa40xTransport</c>, get without hardware — but
/// it is not this, and it says so itself: a green virtual run <i>includes</i> an assertion that the
/// amber Unverified badge is still there. Only this class can close the distinction above, because
/// the calibration page is the part an emulator built from the same references cannot independently
/// confirm.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa403HardwareCollection))]
public class Qa402HardwareTests(ITestOutputHelper output)
{
    /// <summary>Matches <see cref="Qa40xUsbDeviceHardwareTests"/>'s loopback bench.</summary>
    private const double LoopbackCommandedDbv = -6.0;

    private const double LoopbackOutputRangeDbv = 8.0;

    private const string SkipReason =
        "No QA402 on this bench. Un-skip when one is attached — this is the measurement that would let "
        + "Qa40xModel.Qa402 be marked Verified.";

    /// <summary>
    /// A1 — detection. The only thing that can establish that the probe order and the product-id list
    /// are right: a QA402 must be found, and must report itself as a QA402 rather than inheriting the
    /// QA403's name from a fallback.
    /// </summary>
    [Fact(Skip = SkipReason)]
    public async Task EnumerateAndConnect_FindsTheQa402_AndNamesItAsOne()
    {
        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);

        DeviceInfo info = await device.ConnectAsync(CancellationToken.None);

        Assert.False(info.IsSimulated);
        Assert.Equal("QA402", info.ProductName);
        Assert.False(string.IsNullOrWhiteSpace(info.SerialNumber));

        // The badge and the log warning both ride on this, so it is part of the acceptance criterion
        // rather than an implementation detail: it must read true until this class has been run.
        Assert.True(device.ModelUnverified);

        output.WriteLine($"QA402 serial {info.SerialNumber}, firmware {info.FirmwareVersion}");
    }

    /// <summary>
    /// A2 — <b>the test that matters most.</b> Calibration is the one part of the protocol whose
    /// equivalence rests on a single third-party sentence, and it is also the part that cannot fail
    /// loudly: a wrong page layout yields plausible voltages that are simply wrong, with nothing on
    /// screen to say so. Same bench and same tolerance as the QA403's own loopback theory, so a
    /// failure here is a real difference rather than a looser test.
    ///
    /// <para>Run it at every input range, not just one. The 24 dBV rung is a relay rather than a gain
    /// setting on the QA403 and takes about a second to release
    /// (<see cref="Qa40xAutoRanger.AttenuatorReleaseSettleMs"/>); whether the QA402's front end
    /// behaves the same way is unknown, and the ranges either side of that boundary are where it
    /// would show.</para>
    /// </summary>
    [Theory(Skip = SkipReason)]
    [InlineData(0.0)]
    [InlineData(6.0)]
    [InlineData(12.0)]
    [InlineData(18.0)]
    [InlineData(24.0)]
    [InlineData(30.0)]
    [InlineData(36.0)]
    [InlineData(42.0)]
    public async Task Loopback_MeasuredAmplitudeMatchesCommanded_Within0Point1Db(double inputRangeDbv)
    {
        const double sampleRateHz = 48_000.0;
        const double toneFreqHz = 1000.0;
        const double captureSeconds = 2.0;

        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(inputRangeDbv, CancellationToken.None);
        await device.SetOutputRangeAsync(LoopbackOutputRangeDbv, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(toneFreqHz, LoopbackCommandedDbv)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var config = new AcquisitionConfig(sampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
        double measuredDbv = await Qa40xLoopbackMeasurement.SingleChannelRmsDbvAsync(
            device, config, captureSeconds, CancellationToken.None);

        await device.StopAsync();
        output.WriteLine($"Input range {inputRangeDbv,5} dBV: measured {measuredDbv:F3} dBV");

        Assert.True(
            Math.Abs(measuredDbv - LoopbackCommandedDbv) < 0.1,
            $"Input range {inputRangeDbv} dBV: commanded {LoopbackCommandedDbv} dBV, measured {measuredDbv} dBV. "
                + "If this fails at every range the calibration page layout differs between the models; if it "
                + "fails only either side of 24 dBV it is the input attenuator's settle time.");
    }

    /// <summary>
    /// A3 — the L/R swap. The references say the DAC swaps on the QA402 as well as the QA403
    /// ("left and right data is swapped on QA402, QA403, QA404", baremetal Acquisition.cs:192), and
    /// this project's own measurement established that the ADC does <i>not</i> — a correction that
    /// outranks the references and cost real debugging time (docs/protocol.md §6). Whether the QA402
    /// agrees is the second unverified thing about it, and a double swap is silent: a tone simply
    /// comes back on the other channel.
    ///
    /// <para>Drives logical left only and asserts it arrives on left. Mirrors
    /// <c>Loopback_ToneOnLeftOnly_ArrivesOnLeftAndNotOnRight</c>.</para>
    /// </summary>
    [Fact(Skip = SkipReason)]
    public async Task Loopback_ToneOnLeftOnly_ArrivesOnLeftAndNotOnRight()
    {
        const double sampleRateHz = 48_000.0;
        const double toneFreqHz = 1000.0;

        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        await device.ConnectAsync(CancellationToken.None);
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(6.0, CancellationToken.None);
        await device.SetOutputRangeAsync(LoopbackOutputRangeDbv, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(toneFreqHz, LoopbackCommandedDbv)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);

        var config = new AcquisitionConfig(sampleRateHz, ChannelCount: 2, BlockFrameCount: 4096);
        var (leftDbv, rightDbv) = await Qa40xLoopbackMeasurement.StereoRmsDbvAsync(
            device, config, (long)sampleRateHz, CancellationToken.None);

        await device.StopAsync();
        output.WriteLine($"Left {leftDbv:F2} dBV, right {rightDbv:F2} dBV");

        Assert.True(
            leftDbv - rightDbv > 40.0,
            $"Driving logical left only: left {leftDbv:F2} dBV, right {rightDbv:F2} dBV. A right channel "
                + "within a few dB of left means the QA402's RX path swaps too and ProcessRxBuffer needs a "
                + "per-model decision — see docs/protocol.md §6.");
    }

    /// <summary>
    /// A4 — the one refusal that is this app's own policy rather than the device's. 384 kHz is
    /// documented as QA403-only (docs/protocol.md §2.4), so <see cref="Qa40xModel.Qa402"/> does not
    /// offer it and <c>StreamAsync</c> refuses it before any register is written. The assertion is
    /// that it is refused <i>cleanly</i>, with a message naming the model — not that the hardware
    /// cannot do it, which this test deliberately never finds out.
    ///
    /// <para>Safe to run: nothing reaches the wire. It is here rather than beside the unit tests
    /// because only a real QA402 can prove the refusal is wired to a detected model rather than to a
    /// default.</para>
    /// </summary>
    [Fact(Skip = SkipReason)]
    public async Task At384kHz_TheStreamIsRefusedBeforeAnyRegisterIsWritten()
    {
        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance)
        {
            Allow384kHz = () => true,
        };
        await device.ConnectAsync(CancellationToken.None);

        Assert.DoesNotContain(384_000.0, device.Capabilities.SampleRatesHz);

        var config = new AcquisitionConfig(384_000.0, ChannelCount: 2, BlockFrameCount: 4096);
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var block in device.StreamAsync(config, CancellationToken.None))
                block.Dispose();
        });

        Assert.Contains("QA402", ex.Message);
        output.WriteLine(ex.Message);
    }
}
