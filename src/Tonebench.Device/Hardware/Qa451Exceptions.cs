namespace Tonebench.Device.Hardware;

/// <summary>
/// Thrown when no QA451 is attached. Public and distinct from every other failure for the same
/// reason <see cref="Qa40xDeviceNotFoundException"/> is: "nothing plugged in" is an ordinary state a
/// bench app has to carry on from, logged without a stack trace because there is nothing to
/// diagnose.
/// </summary>
public sealed class Qa451DeviceNotFoundException(string message) : Exception(message);

/// <summary>
/// Thrown when the device was there and has gone — an OS-level transfer failure, or a handle that
/// no longer refers to anything. Separate from a timeout on purpose, following the QA403's rule:
/// a timeout or a short read is a hiccup on a device that is still present, and tearing a session
/// down over one would be worse than the fault it reports.
/// </summary>
public sealed class Qa451DeviceLostException : Exception
{
    /// <summary>Creates the exception.</summary>
    public Qa451DeviceLostException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception, keeping the OS error that caused it.</summary>
    public Qa451DeviceLostException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Thrown when the device accepted a relay or DUT-power command and then reported that it had not
/// applied it, twice.
///
/// <para>This exists because a state command has <b>no acknowledgement</b> — the device answers
/// nothing — so the only way to know it took effect is to read the relay bits back out of the status
/// word, and two separate hardware behaviours make that necessary rather than fussy:</para>
///
/// <list type="bullet">
/// <item><b>The first output report after an idle period is dropped.</b> Measured 2026-10-13 through
/// the app: after 30 s with no traffic, the first load command reported the load still open, and an
/// immediate repeat took effect. A retry is the whole fix, and it is why this is thrown only after
/// the second attempt.</item>
/// <item><b>DC on a load input makes the device refuse relay control outright</b> — loads and the DUT
/// switch both — with no fault bit set, recovering by itself when the DC goes away. A retry cannot
/// help there, and this exception is how the app says so instead of reporting a load that is not
/// engaged.</item>
/// </list>
/// </summary>
public sealed class Qa451CommandRefusedException(string message) : Exception(message);

/// <summary>
/// Thrown when a reply does not fit the protocol: a wrong report id, a length that cannot hold what
/// was asked for, or a device id that is neither model. Public because the app has to tell the user
/// something more useful than a 500, and because every one of these means an assumption in
/// <c>docs/qa451-protocol.md</c> has broken rather than that a measurement went wrong.
/// </summary>
public sealed class Qa451ProtocolException(string message) : Exception(message);
