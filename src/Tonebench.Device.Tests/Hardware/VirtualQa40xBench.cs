using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// Connects to the virtual QA402 served by <c>virtual-qa40x-rs</c> over USB/IP, and refuses to
/// proceed against anything else. See <c>testbench/virtual-qa40x/README.md</c> for the bench.
///
/// <para><b>Why two factors.</b> <see cref="DeviceInfo.IsSimulated"/> is <b>false</b> for the
/// emulator — as far as the app is concerned it is a real USB device, which is the whole point — so
/// nothing in <see cref="DeviceInfo"/> identifies it at the bus level. The pair that does is the
/// product name plus a pinned sentinel serial, and the pair fails closed in every direction: a real
/// QA403 trips factor 1, a real QA402 trips factor 2, and a misconfigured emulator trips factor 2
/// naming the flag.</para>
///
/// <para><b>Why the emulator is a QA402 and never a QA403.</b> <c>Qa40xTransport.Open</c> probes
/// <see cref="Qa40xModel.All"/> with the QA403 <i>first</i>, so a virtual QA403 would be both
/// indistinguishable from a real one and unreachable whenever a real one is plugged in. Running it
/// as a QA402 turns that probe order into the discriminator for free.</para>
///
/// <para><b>These tests fail rather than skip when nothing is attached, deliberately.</b> xunit
/// 2.9.2 has no dynamic skip: the sound-card classes "skip" by returning early, which reports a
/// <i>pass</i>. A green <c>Category=VirtualDevice</c> run with nothing attached would then be
/// indistinguishable from a real green run, which is the worst answer available. You only reach
/// these tests by typing that filter on purpose, so "nothing attached" means the attach step failed
/// and silence would be wrong.</para>
/// </summary>
internal static class VirtualQa40xBench
{
    /// <summary>
    /// The serial <c>bench.ps1</c> passes as <c>--serial 0402_0402</c>. The emulator packs the
    /// string's hex digits into register 0x1D, so 0x04020402 arrives here, and
    /// <c>Qa40xUsbDevice.ConnectAsync</c> reports register 0x1D as a decimal uint32 — hence this
    /// value rather than the hex spelling. Measured against the running emulator, not computed.
    /// This bench's real QA403 reads 1077527945, so the two can never be confused.
    /// </summary>
    public const string SentinelSerial = "67240962";

    /// <summary>What <c>bench.ps1 -Serial</c> passes to produce <see cref="SentinelSerial"/>.</summary>
    public const string SentinelSerialArgument = "0402_0402";

    /// <summary>The emulator is always run as a QA402 — see the class remarks.</summary>
    public const string ProductName = "QA402";

    private const string BenchScript = "testbench/virtual-qa40x/bench.ps1";

    /// <summary>
    /// Connects and verifies that what answered is the emulator. On success the caller owns the
    /// returned device and must dispose it; on failure nothing is left open.
    /// </summary>
    public static async Task<(Qa40xUsbDevice Device, DeviceInfo Info)> ConnectOrFailAsync(
        ITestOutputHelper output,
        CancellationToken ct)
    {
        var device = new Qa40xUsbDevice(NullLogger<Qa40xUsbDevice>.Instance);
        DeviceInfo info;

        try
        {
            info = await device.ConnectAsync(ct);
        }
        catch (Qa40xDeviceNotFoundException ex)
        {
            device.Dispose();
            throw new XunitException(
                $"No QA40x answered on USB, so the virtual device is not attached. Start it with "
                    + $"`powershell -File {BenchScript} -Attach`, then re-run. Original: {ex.Message}");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("verification failed"))
        {
            device.Dispose();
            throw new XunitException(
                "The device was opened but register 0 did not read back. Over USB/IP the first "
                    + "suspect is latency: Qa40xTransport.RegisterTimeoutMs is 20 ms and it applies to "
                    + "both phases of every register read, with the calibration page doing 128 of them. "
                    + $"Check the emulator's --record log for the round-trip times. Original: {ex.Message}");
        }
        catch
        {
            device.Dispose();
            throw;
        }

        // Factor 1 — the model. A real QA403 always wins the probe, so say so rather than letting the
        // serial check below blame the emulator's configuration.
        if (info.ProductName != ProductName)
        {
            device.Dispose();
            throw new XunitException(
                $"Found a {info.ProductName}, not the virtual {ProductName}. Qa40xTransport.Open probes "
                    + "Qa40xModel.All with the QA403 first, so a real QA403 always wins over a virtual "
                    + "QA402 — detaching the virtual device will not help, and neither will re-attaching "
                    + "it. Unplug the QA403, or run `--filter Category=Hardware` instead, which is the "
                    + "suite written for it.");
        }

        // Factor 2 — the sentinel. A real QA402 lands here, and it wants the other file.
        if (info.SerialNumber != SentinelSerial)
        {
            device.Dispose();
            throw new XunitException(
                $"Found a {ProductName} reporting serial {info.SerialNumber}, not the sentinel "
                    + $"{SentinelSerial}. If this is the emulator, start it with "
                    + $"`-Serial {SentinelSerialArgument}` ({BenchScript} does). If this is a real "
                    + "QA402, then these tests are the wrong ones to run: Qa402HardwareTests is the "
                    + "handover suite, and passing it is what licenses marking Qa40xModel.Qa402 "
                    + "Verified — something no emulator can do.");
        }

        output.WriteLine(
            $"virtual {info.ProductName}, serial {info.SerialNumber}, firmware {info.FirmwareVersion}, "
                + $"IsSimulated={info.IsSimulated}");

        return (device, info);
    }
}
