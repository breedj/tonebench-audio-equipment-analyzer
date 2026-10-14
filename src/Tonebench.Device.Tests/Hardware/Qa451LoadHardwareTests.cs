using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// The QA451 driver against a real load. Excluded by default like every other hardware test; run
/// with <c>dotnet test --filter "Category=Hardware&amp;FullyQualifiedName~Qa451"</c>.
///
/// <para><b>Everything here is read-only.</b> No test in this class engages a load or switches the
/// DUT supply, because a QA451 on a bench is wired to an amplifier and this suite has no way of
/// knowing what. The only report written besides the queries is the LED kick
/// (<see cref="Qa451Protocol.KickLed"/>) that <see cref="Qa451Load.ReadAsync"/> sends, which is what
/// the vendor application sends every second anyway. For the same reason the connect is the
/// non-disturbing one: <c>resetToKnownState: false</c> means not even the reset goes out, so the
/// relays are left exactly as they were found.</para>
///
/// <para>A test that exercises the relays would be worth having and is deliberately absent until
/// somebody decides what it may safely do. It is not a gap to be filled casually.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa451HardwareCollection))]
public class Qa451LoadHardwareTests(ITestOutputHelper output)
{
    private static Qa451Load OpenLoad() => new(NullLogger<Qa451Load>.Instance);

    /// <summary>
    /// The whole framing, end to end: three commands whose replies are known shapes. A wrong report
    /// id, a wrong length byte or a byte-order mistake all land here rather than in a measurement
    /// three screens away.
    /// </summary>
    [Fact]
    public async Task Connect_ReadsTheIdentity_WithoutDisturbingTheDevice()
    {
        using Qa451Load load = OpenLoad();

        await load.ConnectAsync(resetToKnownState: false);

        LoadInfo info = Assert.IsType<LoadInfo>(load.Info);
        output.WriteLine($"model          : {info.Model}");
        output.WriteLine($"firmware       : {info.FirmwareVersion} (requires {info.RequiredFirmwareVersion})");
        output.WriteLine($"serial number  : {info.SerialNumber}");
        output.WriteLine($"usb serial     : {info.UsbSerial ?? "unavailable"}");

        Assert.True(load.IsConnected);
        Assert.Contains(info.Model, new[] { "QA451", "QA451B" });
        Assert.InRange(info.FirmwareVersion, 1, 1000);
        Assert.False(string.IsNullOrWhiteSpace(info.UsbSerial));

        // Asserted only to be *something*: measured on this unit, 0xFD answers a literal 12345678,
        // so the product serial is not an identifier. The USB descriptor's serial string is, which
        // is why LoadInfo carries both (docs/qa451-protocol.md).
        Assert.NotEqual(0u, info.SerialNumber);

        // Not reset, so the relay state is genuinely unknown and must be reported as such rather
        // than assumed to be the safe one.
        Assert.Null(load.Impedance);
        Assert.Null(load.DutPowerOn);
    }

    /// <summary>
    /// Three polls a second apart. What is being checked is that the decode produces physically
    /// possible figures from a device sitting on a bench doing nothing: room-temperature heatsinks
    /// and no current. The fault flags are printed rather than asserted, because a trip latched by
    /// some earlier session would fail this test for a reason that has nothing to do with the code.
    /// </summary>
    [Fact]
    public async Task Poll_ReadsPlausibleTemperaturesAndCurrent()
    {
        using Qa451Load load = OpenLoad();
        await load.ConnectAsync(resetToKnownState: false);

        var readings = new List<LoadReading>();
        for (int i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                await Task.Delay(Qa451Protocol.PollIntervalMs);
            }

            LoadReading reading = await load.ReadAsync();
            readings.Add(reading);
            output.WriteLine(
                $"poll {i + 1}: left {reading.LeftTemperatureC} °C, right {reading.RightTemperatureC} °C, "
                    + $"{reading.CurrentA:F3} A, status 0x{reading.RawStatusBits:x3}, faults {reading.Faults}"
                    + (reading.RequiresReplug ? " (latched)" : string.Empty));
        }

        foreach (LoadReading reading in readings)
        {
            // Wider than any room a bench sits in, narrow enough to catch a sign error or the two
            // temperature bytes being read the wrong way round into something impossible.
            Assert.InRange(reading.LeftTemperatureC, -20, 120);
            Assert.InRange(reading.RightTemperatureC, -20, 120);
            Assert.InRange(reading.CurrentA, 0.0, 16.0);
        }

        // Two heatsinks in the same case, with nothing dissipating in either.
        Assert.InRange(Math.Abs(readings[0].LeftTemperatureC - readings[0].RightTemperatureC), 0, 15);

        // A device left alone does not wander: a decode that was reading the wrong byte would.
        Assert.InRange(Math.Abs(readings[^1].LeftTemperatureC - readings[0].LeftTemperatureC), 0, 5);

        Assert.Same(readings[^1], load.LastReading);
    }
}
