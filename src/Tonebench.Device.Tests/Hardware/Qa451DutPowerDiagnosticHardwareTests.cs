using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// A <b>diagnostic</b>, not a regression test: it measures what the device does when the DUT switch
/// closes into a real supply, and asserts almost nothing on purpose.
///
/// <para>Why it exists: with nothing wired to S+/S−, closing the switch is immediate and the next
/// poll answers normally (<see cref="Qa451LoadRelayHardwareTests"/>, status 0x041). With a 5 V
/// supply current-limited to 200 mA attached, the first poll after the power-on command timed out —
/// at 1000 ms and then again at 3000 ms. The device recovers: a later run connects and reads
/// normally with the switch open. So the question this answers is which of two things happens, and
/// they need opposite fixes:</para>
///
/// <list type="number">
/// <item>the device keeps its USB handle and merely stops answering for a while — a longer timeout
/// is the whole fix; or</item>
/// <item>the device resets and re-enumerates, leaving this process holding a dead handle — in which
/// case no timeout helps and the driver has to reconnect.</item>
/// </list>
///
/// <para>The discriminator is the second handle: if a fresh open succeeds and reads while the first
/// one is still timing out, the first handle is stale and it is (2).</para>
///
/// <para>Needs the supply wired as <see cref="Qa451LoadCurrentHardwareTests"/> describes. It leaves
/// the switch open through whichever handle still works, and says so if neither does.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa451HardwareCollection))]
public class Qa451DutPowerDiagnosticHardwareTests(ITestOutputHelper output)
{
    private const int PatienceMs = 15_000;
    private const int PerAttemptTimeoutMs = 1000;

    [Fact]
    public async Task AfterTheDutSwitchClosesIntoASupply_HowLongIsTheDeviceSilent()
    {
        using Qa451Load first = new(NullLogger<Qa451Load>.Instance);
        await first.ConnectAsync(resetToKnownState: true);

        LoadReading before = await first.ReadAsync();
        output.WriteLine($"before    : status 0x{before.RawStatusBits:x3}, {before.CurrentA:F3} A");

        bool recovered = false;
        bool freshHandleWorks = false;
        try
        {
            await first.SetDutPowerAsync(on: true);
            output.WriteLine("power-on command written, polling the same handle...");

            var clock = Stopwatch.StartNew();
            int timeouts = 0;
            while (clock.ElapsedMilliseconds < PatienceMs)
            {
                try
                {
                    LoadReading reading = await first.ReadAsync();
                    recovered = true;
                    output.WriteLine(
                        $"answered after {clock.ElapsedMilliseconds} ms and {timeouts} timeouts: "
                            + $"status 0x{reading.RawStatusBits:x3}, {reading.CurrentA:F3} A, "
                            + $"power {reading.DutPowerEngaged}");
                    break;
                }
                catch (TimeoutException)
                {
                    timeouts++;
                }
                catch (Exception ex)
                {
                    output.WriteLine($"the same handle failed with {ex.GetType().Name}: {ex.Message}");
                    break;
                }
            }

            if (!recovered)
            {
                output.WriteLine($"silent for the whole {PatienceMs} ms ({timeouts} timeouts)");

                // The discriminator: a fresh handle, while the first is still being refused.
                try
                {
                    using Qa451Load second = new(NullLogger<Qa451Load>.Instance);
                    await second.ConnectAsync(resetToKnownState: false);
                    LoadReading reading = await second.ReadAsync();
                    freshHandleWorks = true;
                    output.WriteLine(
                        $"a FRESH handle reads fine: status 0x{reading.RawStatusBits:x3}, "
                            + $"{reading.CurrentA:F3} A, power {reading.DutPowerEngaged} — so the first "
                            + "handle is stale and the device re-enumerated");
                    await second.SetSafeStateAsync();
                }
                catch (Exception ex)
                {
                    output.WriteLine($"a fresh handle fails too ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }
        finally
        {
            // Leave the switch open whichever way still works.
            try
            {
                await first.SetSafeStateAsync();
                output.WriteLine("switch opened through the original handle");
            }
            catch (Exception ex)
            {
                output.WriteLine($"could not open the switch through the original handle: {ex.Message}");
                try
                {
                    using Qa451Load rescue = new(NullLogger<Qa451Load>.Instance);
                    await rescue.ConnectAsync(resetToKnownState: true);
                    await rescue.SetSafeStateAsync();
                    output.WriteLine("switch opened through a fresh handle");
                }
                catch (Exception inner)
                {
                    output.WriteLine(
                        $"COULD NOT OPEN THE SWITCH AT ALL ({inner.Message}) — power the supply down "
                            + "and replug the QA451.");
                }
            }
        }

        output.WriteLine(
            recovered
                ? "=> the handle survives: a longer timeout is the fix"
                : freshHandleWorks
                    ? "=> the handle is stale: the driver has to reconnect after a DUT power-on"
                    : "=> neither handle works: something else is going on");
    }
}
