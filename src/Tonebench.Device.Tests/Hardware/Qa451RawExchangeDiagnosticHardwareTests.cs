using System.Diagnostics;
using Tonebench.Device.Hardware;
using Xunit.Abstractions;

namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// A <b>raw-protocol diagnostic</b>: it drives <see cref="IHidTransport"/> directly, one request at
/// a time with its own timeout, so that each exchange after a DUT power-on can be seen separately.
/// Asserts almost nothing.
///
/// <para>Why it exists. A poll issued straight after a power-on command went unanswered through a
/// 1 s ceiling, then a 3 s one, then a 6 s one — but
/// <see cref="Qa451DutPowerDiagnosticHardwareTests"/> saw the device answer at 3010 ms, i.e. on its
/// <i>second attempt</i>, immediately after that attempt's fresh write. Those two observations only
/// fit together one way: the device is not silent for a period, it drops the one request that
/// follows a power command, and a retry is what recovers it. A timeout cannot distinguish those; a
/// per-request log can.</para>
///
/// <para>Needs the supply wired as <see cref="Qa451LoadCurrentHardwareTests"/> describes. It ends by
/// opening the switch and releasing both loads, retrying those writes, and it reports the final
/// status word so the bench state is on the record either way.</para>
/// </summary>
[Trait("Category", "Hardware")]
[Collection(nameof(Qa451HardwareCollection))]
public class Qa451RawExchangeDiagnosticHardwareTests(ITestOutputHelper output)
{
    private const int TimeoutMs = 1000;
    private const int Attempts = 12;

    [Fact]
    public void AfterAPowerCommand_HowManyRequestsAreSwallowed()
    {
        using IHidTransport transport =
            HidTransport.Create(Qa451Protocol.Qa451VendorId, Qa451Protocol.Qa451ProductId);
        transport.Open();

        output.WriteLine($"id 0x{Query(transport, Qa451Protocol.DeviceId):x8}, "
            + $"firmware {Query(transport, Qa451Protocol.FirmwareVersion)}");
        Report(transport, "baseline");

        // Put it in a known state the slow way, one request at a time.
        Send(transport, Qa451Protocol.Reset);
        Report(transport, "after reset");

        try
        {
            // Twice, once plain and once with the LED kick ahead of every query. The driver sends
            // that kick and kept the supply switched for seconds (105 mA on its display) where the
            // plain sequence below dropped the switch within ten milliseconds, so the kick is the
            // one difference worth isolating.
            output.WriteLine(string.Empty);
            output.WriteLine("=== power ON, queries only ===");
            Send(transport, Qa451Protocol.DutPowerOn, Qa451Protocol.DutPowerOnArgument);
            Poll(transport, withKick: false);
            Send(transport, Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument);

            Thread.Sleep(500);

            output.WriteLine(string.Empty);
            output.WriteLine("=== power ON, each query preceded by the LED kick ===");
            Send(transport, Qa451Protocol.DutPowerOn, Qa451Protocol.DutPowerOnArgument);
            Poll(transport, withKick: true);
            Send(transport, Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument);
        }
        finally
        {
            // Both of these are writes, and a write after a power command may be exactly what gets
            // dropped — so send each twice, and read the state back to say what actually happened.
            Send(transport, Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument);
            Send(transport, Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument);
            Send(transport, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument);
            Send(transport, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument);
            output.WriteLine(string.Empty);
            Report(transport, "final state");
        }
    }

    /// <summary>
    /// How long the DUT switch holds once the kicks stop — i.e. the watchdog window, which is what
    /// decides how fast anything holding the supply on has to poll. Establishes the switch is
    /// genuinely closed and passing current first, then stops kicking and times the release.
    /// </summary>
    [Fact]
    public void WithoutTheKick_HowLongDoesTheDutSwitchHold()
    {
        using IHidTransport transport =
            HidTransport.Create(Qa451Protocol.Qa451VendorId, Qa451Protocol.Qa451ProductId);
        transport.Open();
        Send(transport, Qa451Protocol.Reset);

        try
        {
            Send(transport, Qa451Protocol.DutPowerOn, Qa451Protocol.DutPowerOnArgument);

            // Kick through the soft start until the switch is closed and passing current, retrying
            // the request that gets swallowed on the way.
            bool live = false;
            var settling = Stopwatch.StartNew();
            while (settling.ElapsedMilliseconds < 4000 && !live)
            {
                try
                {
                    Send(transport, Qa451Protocol.KickLed);
                    (_, _, ushort status) =
                        Qa451Protocol.DecodeTempsAndStatus(Query(transport, Qa451Protocol.ReadTempsAndStatus));
                    double amps = Qa451Protocol.CurrentAmpsFromRaw(Query(transport, Qa451Protocol.ReadCurrent));
                    (_, bool power) = Qa451Protocol.DecodeRelayState(status);
                    live = power && amps > 0.010;
                    output.WriteLine(
                        $"settling t+{settling.ElapsedMilliseconds,5} ms  status 0x{status:x3}  "
                            + $"power {power,-5}  {amps:F3} A");
                }
                catch (TimeoutException)
                {
                    output.WriteLine($"settling t+{settling.ElapsedMilliseconds,5} ms  TIMEOUT");
                }

                Thread.Sleep(100);
            }

            Assert.True(live, "The switch never closed and passed current, so there is nothing to time.");

            output.WriteLine(string.Empty);
            output.WriteLine("--- kicks stopped; queries only from here ---");
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 5000)
            {
                (_, _, ushort status) =
                    Qa451Protocol.DecodeTempsAndStatus(Query(transport, Qa451Protocol.ReadTempsAndStatus));
                double amps = Qa451Protocol.CurrentAmpsFromRaw(Query(transport, Qa451Protocol.ReadCurrent));
                (_, bool power) = Qa451Protocol.DecodeRelayState(status);
                output.WriteLine(
                    $"  t+{clock.ElapsedMilliseconds,5} ms  status 0x{status:x3}  power {power,-5}  {amps:F3} A");

                if (!power)
                {
                    output.WriteLine(
                        $"=> released {clock.ElapsedMilliseconds} ms after the last kick: the kick is a "
                            + "watchdog that holds the DUT switch");
                    return;
                }

                Thread.Sleep(50);
            }

            output.WriteLine("=> still closed after 5 s with no kicks: the kick is not what holds it");
        }
        finally
        {
            Send(transport, Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument);
            Send(transport, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument);
            output.WriteLine(string.Empty);
            Report(transport, "final state");
        }
    }

    /// <summary>
    /// Whether the LED kick is what holds an engaged load.
    ///
    /// <para>Every run in which a load stayed engaged had a <c>ReadAsync</c> — which sends the kick —
    /// between steps; every run in which loads "would not engage" sent no kick at all. This drives
    /// both cases back to back: engage with kicks, then engage without them.</para>
    ///
    /// <para>If it holds with kicks and releases without, then the kick is a host-liveness watchdog
    /// for the relays, this file's earlier "it is not a watchdog" reading was wrong, and the
    /// DC-refusal explanation for the failed heating test is wrong too — that load dropped out
    /// because nothing kicked within the window, not because 8 V DC was present.</para>
    /// </summary>
    [Fact]
    public void IsTheLedKickWhatHoldsAnEngagedLoad()
    {
        using IHidTransport transport =
            HidTransport.Create(Qa451Protocol.Qa451VendorId, Qa451Protocol.Qa451ProductId);
        transport.Open();
        Send(transport, Qa451Protocol.Reset);

        try
        {
            foreach (bool kicking in new[] { true, false })
            {
                Send(transport, Qa451Protocol.Load8Ohm, Qa451Protocol.Load8OhmArgument);

                var clock = Stopwatch.StartNew();
                int engagedFor = 0;
                for (int i = 0; i < 12; i++)
                {
                    if (kicking)
                    {
                        Send(transport, Qa451Protocol.KickLed);
                    }

                    (_, _, ushort status) =
                        Qa451Protocol.DecodeTempsAndStatus(Query(transport, Qa451Protocol.ReadTempsAndStatus));
                    bool engaged = Qa451Protocol.DecodeRelayState(status).Impedance == LoadImpedance.EightOhm;
                    if (engaged)
                    {
                        engagedFor = (int)clock.ElapsedMilliseconds;
                    }

                    output.WriteLine(
                        $"  {(kicking ? "kicking " : "no kick ")} t+{clock.ElapsedMilliseconds,5} ms  "
                            + $"status 0x{status:x3}  {(engaged ? "engaged" : "open")}");

                    Thread.Sleep(100);
                }

                output.WriteLine($"  => {(kicking ? "with kicks" : "without kicks")}: last seen engaged at t+{engagedFor} ms");
                output.WriteLine(string.Empty);

                Send(transport, Qa451Protocol.KickLed);
                Send(transport, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument);
                Thread.Sleep(300);
            }
        }
        finally
        {
            Send(transport, Qa451Protocol.KickLed);
            Send(transport, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument);
            Send(transport, Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument);
            Report(transport, "final state");
        }
    }

    /// <summary>
    /// What makes a state command stick. Measured through the app on 2026-10-13: after a quiet
    /// period the first load command was reported not applied, a <i>separate</i> request a moment
    /// later worked, and a retry issued a couple of milliseconds later inside the same call did
    /// <b>not</b>. So the loss is not simply "the first write after idle": the gap matters, and this
    /// finds out how much of one is needed.
    ///
    /// <para>Each row goes: idle, write the command, wait <c>gap</c>, read the status, and report
    /// whether the relay moved. Harmless with anything or nothing on the load inputs — it engages
    /// 4 Ω briefly and releases it.</para>
    /// </summary>
    [Fact]
    public void AfterIdle_HowMuchOfAGapDoesAStateCommandNeed()
    {
        using IHidTransport transport =
            HidTransport.Create(Qa451Protocol.Qa451VendorId, Qa451Protocol.Qa451ProductId);
        transport.Open();
        Send(transport, Qa451Protocol.Reset);
        Report(transport, "baseline");

        try
        {
            // Each load, several times, by one method with a generous gap — a reliability table
            // rather than another one-off, because the one-offs have been contradicting each other.
            foreach ((byte opcode, byte argument, LoadImpedance expected) in new[]
            {
                (Qa451Protocol.Load8Ohm, Qa451Protocol.Load8OhmArgument, LoadImpedance.EightOhm),
                (Qa451Protocol.Load4Ohm, Qa451Protocol.Load4OhmArgument, LoadImpedance.FourOhm),
            })
            {
                int applied = 0;
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    Send(transport, opcode, argument);
                    Thread.Sleep(300);
                    (_, _, ushort engagedStatus) =
                        Qa451Protocol.DecodeTempsAndStatus(Query(transport, Qa451Protocol.ReadTempsAndStatus));
                    bool took = Qa451Protocol.DecodeRelayState(engagedStatus).Impedance == expected;
                    applied += took ? 1 : 0;

                    Send(transport, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument);
                    Thread.Sleep(300);
                    (_, _, ushort openStatus) =
                        Qa451Protocol.DecodeTempsAndStatus(Query(transport, Qa451Protocol.ReadTempsAndStatus));
                    bool released = Qa451Protocol.DecodeRelayState(openStatus).Impedance == LoadImpedance.Open;

                    output.WriteLine(
                        $"  {expected,-10} attempt {attempt}: engage 0x{engagedStatus:x3} "
                            + $"{(took ? "APPLIED" : "lost   ")}  release 0x{openStatus:x3} "
                            + $"{(released ? "ok" : "STUCK")}");
                }

                output.WriteLine($"  => {expected}: {applied} of 5 engaged");
                output.WriteLine(string.Empty);
            }
        }
        finally
        {
            Send(transport, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument);
            Thread.Sleep(200);
            Send(transport, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument);
            Send(transport, Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument);
            output.WriteLine(string.Empty);
            Report(transport, "final state");
        }
    }

    /// <summary>
    /// Whether the device drops its DUT switch when the host stops talking to it, or closes its
    /// handle. The user's manual says it does — "if the QA451 application is closed or stops
    /// responding, or if the QA451 is disconnected from the USB, then the QA451 will automatically
    /// open the load relays and disable the load power" — which contradicts this driver's own
    /// <see cref="Qa451Load.Dispose"/> remarks, written from the vendor application's code where
    /// closing only closes the HID stream.
    ///
    /// <para>It also reopens a question this file thought it had settled: an earlier run concluded
    /// the LED kick is not a watchdog, because the switch held through 5 s of status queries with no
    /// kicks. Both can be true — a watchdog fed by <i>any</i> traffic rather than by the kick
    /// specifically — and that is exactly what this measures: first by going silent with the handle
    /// open, then by closing the handle altogether.</para>
    ///
    /// <para>Uses the DUT switch rather than a load relay, so it needs no load wiring at all: the
    /// status word reports the switch whether or not anything is connected to S+/S−.</para>
    /// </summary>
    [Fact]
    public void WhenTheHostGoesQuiet_DoesTheDutSwitchDropOut()
    {
        // Part one: handle open, no traffic at all for several seconds.
        using (IHidTransport quiet =
            HidTransport.Create(Qa451Protocol.Qa451VendorId, Qa451Protocol.Qa451ProductId))
        {
            quiet.Open();
            Send(quiet, Qa451Protocol.Reset);
            Send(quiet, Qa451Protocol.DutPowerOn, Qa451Protocol.DutPowerOnArgument);
            Thread.Sleep(Qa451Protocol.SoftStartSettleMs);
            Report(quiet, "switched on");

            foreach (int seconds in new[] { 2, 5, 10 })
            {
                Thread.Sleep(seconds * 1000);
                Report(quiet, $"after {seconds}s quiet");
            }

            Send(quiet, Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument);
        }

        // Part two: switch it on, then close the handle — "the application is closed".
        using (IHidTransport closing =
            HidTransport.Create(Qa451Protocol.Qa451VendorId, Qa451Protocol.Qa451ProductId))
        {
            closing.Open();
            Send(closing, Qa451Protocol.DutPowerOn, Qa451Protocol.DutPowerOnArgument);
            Thread.Sleep(Qa451Protocol.SoftStartSettleMs);
            Report(closing, "on, about to close");
        }

        Thread.Sleep(3000);

        using IHidTransport reopened =
            HidTransport.Create(Qa451Protocol.Qa451VendorId, Qa451Protocol.Qa451ProductId);
        reopened.Open();
        try
        {
            Report(reopened, "after reopen");
            output.WriteLine(
                "=> compare: if the switch reads off here but stayed on through the quiet periods "
                    + "above, the handle closing is what drops it, not silence");
        }
        finally
        {
            Send(reopened, Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument);
            Send(reopened, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument);
            Report(reopened, "final state");
        }
    }

    /// <summary>
    /// How long a commanded load stays engaged when the load input is fed DC. Needs a supply across
    /// one channel's load input (8 V into 8 Ω here).
    ///
    /// <para>Joost watched the front panel through a temperature-rise attempt and saw the load come
    /// on "for 1 second only", which is why the heating test measured no rise at all: by its first
    /// poll two seconds in, the status word already read 0x040. This times the release.</para>
    /// </summary>
    [Fact]
    public void WithDcOnTheLoadInput_HowLongDoesTheLoadRelayHold()
    {
        using IHidTransport transport =
            HidTransport.Create(Qa451Protocol.Qa451VendorId, Qa451Protocol.Qa451ProductId);
        transport.Open();
        Send(transport, Qa451Protocol.Reset);
        Report(transport, "baseline");

        try
        {
            Send(transport, Qa451Protocol.Load8Ohm, Qa451Protocol.Load8OhmArgument);
            var clock = Stopwatch.StartNew();
            bool wasEngaged = false;

            while (clock.ElapsedMilliseconds < 5000)
            {
                (int left, int right, ushort status) =
                    Qa451Protocol.DecodeTempsAndStatus(Query(transport, Qa451Protocol.ReadTempsAndStatus));
                (LoadImpedance? engaged, _) = Qa451Protocol.DecodeRelayState(status);
                bool nowEngaged = engaged == LoadImpedance.EightOhm;

                output.WriteLine(
                    $"  t+{clock.ElapsedMilliseconds,5} ms  status 0x{status:x3}  "
                        + $"load {engaged?.ToString() ?? "unknown",-10}  {left} °C / {right} °C");

                if (wasEngaged && !nowEngaged)
                {
                    output.WriteLine(
                        $"=> released by {clock.ElapsedMilliseconds} ms, uncommanded: the device drops a "
                            + "load it was told to engage when the input is DC");
                    return;
                }

                wasEngaged |= nowEngaged;
                Thread.Sleep(100);
            }

            output.WriteLine(
                wasEngaged
                    ? "=> still engaged after 5 s"
                    : "=> never engaged at all, so it was refused rather than dropped");
        }
        finally
        {
            Send(transport, Qa451Protocol.LoadOff, Qa451Protocol.LoadOffArgument);
            Send(transport, Qa451Protocol.DutPowerOff, Qa451Protocol.DutPowerOffArgument);
            output.WriteLine(string.Empty);
            Report(transport, "final state");
        }
    }

    /// <summary>
    /// Polls status and current repeatedly, logging every attempt — no early exit, because the
    /// question is how the switch behaves <i>over</i> the next second or two, not whether one
    /// request answers.
    /// </summary>
    private void Poll(IHidTransport transport, bool withKick)
    {
        var clock = Stopwatch.StartNew();
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            long startedAt = clock.ElapsedMilliseconds;
            try
            {
                if (withKick)
                {
                    Send(transport, Qa451Protocol.KickLed);
                }

                (_, _, ushort status) =
                    Qa451Protocol.DecodeTempsAndStatus(Query(transport, Qa451Protocol.ReadTempsAndStatus));
                double amps = Qa451Protocol.CurrentAmpsFromRaw(Query(transport, Qa451Protocol.ReadCurrent));
                (_, bool power) = Qa451Protocol.DecodeRelayState(status);

                output.WriteLine(
                    $"  attempt {attempt,2}: {clock.ElapsedMilliseconds - startedAt,4} ms "
                        + $"(t+{clock.ElapsedMilliseconds,5} ms)  status 0x{status:x3}  power {power,-5}  "
                        + $"{amps:F3} A");
            }
            catch (TimeoutException)
            {
                output.WriteLine(
                    $"  attempt {attempt,2}: TIMEOUT after {clock.ElapsedMilliseconds - startedAt,4} ms "
                        + $"(t+{clock.ElapsedMilliseconds,5} ms)");
            }

            Thread.Sleep(100);
        }
    }

    private void Report(IHidTransport transport, string label)
    {
        try
        {
            (int left, int right, ushort status) =
                Qa451Protocol.DecodeTempsAndStatus(Query(transport, Qa451Protocol.ReadTempsAndStatus));
            double amps = Qa451Protocol.CurrentAmpsFromRaw(Query(transport, Qa451Protocol.ReadCurrent));
            (LoadImpedance? engaged, bool power) = Qa451Protocol.DecodeRelayState(status);
            output.WriteLine(
                $"{label,-14}: status 0x{status:x3}  load {engaged?.ToString() ?? "unknown"}  power {power}  "
                    + $"{amps:F3} A  {left} °C / {right} °C");
        }
        catch (TimeoutException)
        {
            output.WriteLine($"{label,-14}: TIMEOUT");
        }
    }

    private static uint Query(IHidTransport transport, byte opcode, byte argument = 0)
    {
        Span<byte> request = stackalloc byte[Qa451Protocol.ReportLength];
        Qa451Protocol.BuildRequest(request, opcode, argument);
        transport.Write(request);

        Span<byte> reply = stackalloc byte[Qa451Protocol.ReportLength];
        int received = transport.Read(reply, TimeoutMs);
        return received == Qa451Protocol.ReportLength
            ? Qa451Protocol.ParseScalarReply(reply)
            : throw new Qa451ProtocolException($"short reply: {received} bytes");
    }

    private static void Send(IHidTransport transport, byte opcode, byte argument = 0)
    {
        Span<byte> request = stackalloc byte[Qa451Protocol.ReportLength];
        Qa451Protocol.BuildRequest(request, opcode, argument);
        transport.Write(request);
    }
}
