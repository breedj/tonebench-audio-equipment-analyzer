using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// The QA402 code path, exercised against <c>virtual-qa40x-rs</c> attached over USB/IP. Bench setup:
/// <c>testbench/virtual-qa40x/README.md</c>.
///
/// <para><b>Nothing in this file can make <see cref="Qa40xModel.Qa402"/>'s <c>Verified</c> flag
/// true. It asserts the opposite</b> — see
/// <see cref="TheVirtualQa402_IsStillReportedAsUnverified"/>. The measurement that could set that
/// flag lives in <see cref="Qa402HardwareTests"/>, which is skipped and must stay skipped until a
/// real QA402 is attached. An emulator confirms that this app matches <i>our model</i> of a QA402,
/// built from the same four references the support was built from; it cannot confirm the hardware,
/// and the calibration page in particular rests on one third-party sentence plus an emulator page
/// read from somebody else's unit.</para>
///
/// <para><b>What this is genuinely worth.</b> Everything in <c>Qa40xUsbDevice</c> and
/// <c>Qa40xTransport</c> below <see cref="IAnalyzerDevice"/> had no coverage at all without real
/// hardware — the connect sequence and its StreamingStop/EP2-drain recovery, <c>TryVerifyAsync</c>,
/// the 128-read calibration page, <c>TransferPump</c>, <c>BlockAssembler</c>, teardown. There is no
/// transport-level double for the analyzer (the QA451 has one only because <c>IHidTransport</c>
/// exists). This class reaches all of it, and it reaches the QA402 product id, which nothing
/// else here can.</para>
///
/// <para>In <see cref="Qa403HardwareCollection"/> because it opens a QA40x and there is no way to
/// ask for one rather than another — see that file.</para>
/// </summary>
[Trait("Category", "VirtualDevice")]
[Collection(nameof(Qa403HardwareCollection))]
public class Qa402VirtualDeviceTests(ITestOutputHelper output)
{
    /// <summary>Matches <see cref="Qa402HardwareTests"/>'s bench, so the figures are comparable.</summary>
    private const double LoopbackCommandedDbv = -6.0;

    private const double LoopbackOutputRangeDbv = 8.0;
    private const double SampleRateHz = 48_000.0;
    private const double ToneFreqHz = 1000.0;

    /// <summary>
    /// The guard that makes the <c>Verified</c> confusion structurally impossible rather than merely
    /// discouraged: a green run of this suite <i>includes</i> "the amber Unverified badge is still
    /// there". Setting <c>Verified = true</c> because this file passed would break this test and
    /// <see cref="Qa40xModelTests"/>'s own check at the same moment.
    /// </summary>
    [Fact]
    public async Task TheVirtualQa402_IsStillReportedAsUnverified()
    {
        var (device, _) = await VirtualQa40xBench.ConnectOrFailAsync(output, CancellationToken.None);
        using (device)
        {
            Assert.True(
                device.ModelUnverified,
                "The QA402 must keep reporting itself unverified. An emulator cannot license that "
                    + "flag — only Qa402HardwareTests against real hardware can.");
            Assert.False(
                Qa40xModel.Qa402.Verified,
                "Qa40xModel.Qa402.Verified must stay false until a real QA402 has been measured.");
        }
    }

    /// <summary>
    /// A1 — detection. Exercises the product-id list and the probe order against a device answering
    /// as 0x4e37, which no other test in this solution can do. The one check here that carries over
    /// unchanged from the hardware suite.
    /// </summary>
    [Fact]
    public async Task EnumerateAndConnect_FindsTheQa402_AndNamesItAsOne()
    {
        var (device, info) = await VirtualQa40xBench.ConnectOrFailAsync(output, CancellationToken.None);
        using (device)
        {
            // False even for the emulator: it is a real USB device as far as the app is concerned,
            // which is what makes this suite worth having at all.
            Assert.False(info.IsSimulated);
            Assert.Equal("QA402", info.ProductName);
            Assert.False(string.IsNullOrWhiteSpace(info.SerialNumber));
            Assert.False(string.IsNullOrWhiteSpace(info.FirmwareVersion));
        }
    }

    /// <summary>
    /// A2's mechanics, renamed so it cannot be read as A2's <i>finding</i>. The emulator has no
    /// analog front end and no 24 dB relay, so this does not establish that a real QA402's
    /// calibration page is laid out like a QA403's — the thing A2 exists to settle. What it does
    /// establish is that <see cref="Qa40xCalibration"/> parses a real 512-byte factory page served
    /// over 128 reads of register 0x19 and applies the per-range scalars self-consistently, at every
    /// rung of the ladder.
    ///
    /// <para>Measured 2026-11-05: the emulator does model register 5, and its noise floor steps
    /// 6.00 dB per rung across all eight ranges, so the ladder is genuinely exercised rather than
    /// ignored.</para>
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(6.0)]
    [InlineData(12.0)]
    [InlineData(18.0)]
    [InlineData(24.0)]
    [InlineData(30.0)]
    [InlineData(36.0)]
    [InlineData(42.0)]
    public async Task CalibrationPage_DecodesAndScalesConsistently_AtEveryInputRange(double inputRangeDbv)
    {
        const double captureSeconds = 2.0;

        var (device, _) = await VirtualQa40xBench.ConnectOrFailAsync(output, CancellationToken.None);
        using (device)
        {
            await ConfigureLoopbackAsync(device, inputRangeDbv);

            var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 1, BlockFrameCount: 4096);
            double measuredDbv = await Qa40xLoopbackMeasurement.SingleChannelRmsDbvAsync(
                device, config, captureSeconds, CancellationToken.None);
            await device.StopAsync();

            output.WriteLine($"Input range {inputRangeDbv,5} dBV: measured {measuredDbv:F3} dBV");

            Assert.True(
                Math.Abs(measuredDbv - LoopbackCommandedDbv) < 0.1,
                $"Input range {inputRangeDbv} dBV: commanded {LoopbackCommandedDbv} dBV, measured "
                    + $"{measuredDbv} dBV. Failing at every range means Qa40xCalibration mis-parses the "
                    + "served page; failing at one range means the per-range scalar for that rung.");
        }
    }

    /// <summary>
    /// A3's mechanics — and this one needs a <b>patched</b> emulator, which is worth knowing before
    /// it fails.
    ///
    /// <para>The L/R swap is the DAC's alone: driving wire slot 1 alone returns on RX slot 0, measured
    /// on this bench's QA403 (CLAUDE.md, "The L/R swap is on the DAC only"). <c>virtual-qa40x-rs</c>
    /// v0.5.0 swaps on <i>both</i> directions, inherited from qa40x-rs and from QA40x_BareMetal — the
    /// one place a vendored reference is known to be wrong — so it double-swaps and crosses the
    /// channels. Measured against the unpatched build on 2026-11-05: Tonebench put the tone on TX
    /// slot 1 and it came back on RX slot 1 at −5.81 dBFS while slot 0 held only the −140 dBFS noise
    /// floor, so this test read the floor and failed by ~125 dB.</para>
    ///
    /// <para>The fix is one line in the emulator's ADC encode (emit left first), not in
    /// <c>ProcessRxBuffer</c> — CLAUDE.md forbids that explicitly. Patched, the same capture reads
    /// the tone on slot 0 at −5.81 dBFS against the measured −6.00 dBFS. If this test fails with left
    /// and right swapped, the bench is running an unpatched emulator.</para>
    /// </summary>
    [Fact]
    public async Task Loopback_ToneOnLeftOnly_ArrivesOnLeftAndNotOnRight()
    {
        var (device, _) = await VirtualQa40xBench.ConnectOrFailAsync(output, CancellationToken.None);
        using (device)
        {
            await ConfigureLoopbackAsync(device, 6.0);

            var config = new AcquisitionConfig(SampleRateHz, ChannelCount: 2, BlockFrameCount: 4096);
            var (leftDbv, rightDbv) = await Qa40xLoopbackMeasurement.StereoRmsDbvAsync(
                device, config, (long)SampleRateHz, CancellationToken.None);
            await device.StopAsync();

            output.WriteLine($"Left {leftDbv:F2} dBV, right {rightDbv:F2} dBV");

            Assert.True(
                leftDbv - rightDbv > 40.0,
                $"Driving logical left only: left {leftDbv:F2} dBV, right {rightDbv:F2} dBV. If right "
                    + "carries the tone and left is at the noise floor, the emulator is unpatched and "
                    + "swaps on receive as well as on transmit — fix the emulator, not ProcessRxBuffer "
                    + "(see this test's remarks and CLAUDE.md).");
        }
    }

    /// <summary>
    /// A4 — this app's own refusal, and a real win here. 384 kHz is QA403-only, so
    /// <see cref="Qa40xModel.Qa402"/> does not offer it and <c>StreamAsync</c> refuses it before any
    /// register reaches the wire. <see cref="Qa40xModelTests"/> already covers the capability table
    /// with no device at all; what this adds is that the refusal is wired to a <i>detected</i> model
    /// rather than to a default, which only a device answering as a QA402 can show.
    /// </summary>
    [Fact]
    public async Task At384kHz_TheStreamIsRefusedBeforeAnyRegisterIsWritten()
    {
        // Built by hand rather than through the bench helper: this one needs Allow384kHz set at
        // construction, and the point is that the refusal survives the setting being on.
        using var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance)
        {
            Allow384kHz = () => true,
        };
        DeviceInfo info = await device.ConnectAsync(CancellationToken.None);
        Assert.Equal(VirtualQa40xBench.ProductName, info.ProductName);
        Assert.Equal(VirtualQa40xBench.SentinelSerial, info.SerialNumber);

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

    /// <summary>
    /// The loopback setup every capture here shares. Manual ranging throughout: the point is to pin a
    /// rung and read it, not to watch a ranger choose one.
    /// </summary>
    private static async Task ConfigureLoopbackAsync(Qa40xUsbDevice device, double inputRangeDbv)
    {
        device.AutoRangeEnabled = false;
        await device.SetInputRangeAsync(inputRangeDbv, CancellationToken.None);
        await device.SetOutputRangeAsync(LoopbackOutputRangeDbv, CancellationToken.None);
        await device.ConfigureGeneratorAsync(
            new GeneratorConfig(
                new ChannelGeneratorConfig(true, [new GeneratorTone(ToneFreqHz, LoopbackCommandedDbv)]),
                ChannelGeneratorConfig.Off),
            CancellationToken.None);
    }
}
