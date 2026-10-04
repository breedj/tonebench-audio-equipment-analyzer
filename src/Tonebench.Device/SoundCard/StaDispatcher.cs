using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Tonebench.Device.SoundCard;

/// <summary>
/// A dedicated thread that every PortAudio call is made on, in a <b>single-threaded apartment</b>
/// on Windows.
///
/// <para><b>ASIO is COM, and COM apartments are not a detail here.</b> Measured 2026-09-30: called
/// from an ordinary .NET thread — which is MTA by default — <c>Pa_Initialize</c> reports the ASIO
/// host API with <b>zero devices</b>, although four ASIO drivers are registered on this machine,
/// and a second <c>Pa_Initialize</c> after a <c>Pa_Terminate</c> faults with an access violation
/// that takes the process with it. From an STA thread the same call reports the MOTU M4 as one
/// full-duplex device, 8 in / 4 out, accepting all three of 48/96/192 kHz. The difference is
/// entirely the apartment: PortAudio's ASIO backend calls <c>CoInitialize</c>, which cannot change
/// a thread that is already MTA, and it then quietly enumerates nothing.</para>
///
/// <para>So this is not a convenience wrapper — without it there is no ASIO, and without ASIO there
/// is no full-duplex device on Windows at all. Enumeration, opening, closing and the streaming loop
/// itself all run here. The loop <i>occupying</i> the thread is intended: it is the sound-card
/// analogue of the QA403's dedicated pump thread, and it keeps the rule that nothing but transfer
/// management happens between one transfer and the next.</para>
///
/// <para>On Linux the apartment request is skipped — <see cref="Thread.SetApartmentState"/> throws
/// there, and ALSA and JACK have no equivalent requirement — but the work still runs on this one
/// dedicated thread, because PortAudio's own documentation is clear that a stream belongs to the
/// thread that opened it.</para>
/// </summary>
internal sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _work = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private bool _disposed;

    public StaDispatcher(string name)
    {
        _thread = new Thread(Loop)
        {
            Name = name,

            // Not a background thread: it owns the native library and any open stream, and a
            // process exiting out from under an open ASIO stream is how a driver is left claimed.
            IsBackground = false,

            // **This thread is the sound card's transfer pump**, and it was left at the pool's own
            // priority while the QA403's pump and block assembler were both raised above it — the
            // rule that "a saturated pool must not preempt transfer refill" was written for the
            // USB device and never applied here. It is the whole of the loop, too: PortAudio's
            // blocking duplex stream is read and written from it in lockstep, so descheduling it
            // fails *both* directions at once. Measured on a MOTU M4 at 192 kHz, 131072-point FFT
            // with x8 averaging and nothing else running, the pump was parked long enough to fill
            // the 43 ms input ring and empty the output one twice in 75 seconds — the input
            // overflow and the output underflow logged 0.2 and 0.7 ms apart, which is the
            // signature of a stalled pump rather than of a driver or a clock.
            Priority = ThreadPriority.AboveNormal,
        };

        if (OperatingSystem.IsWindows())
            _thread.SetApartmentState(ApartmentState.STA);

        _thread.Start();
    }

    /// <summary>Whether the caller is already on the dispatcher's thread, so a nested call must not queue and wait for itself.</summary>
    public bool OnDispatcherThread => Thread.CurrentThread == _thread;

    public void Invoke(Action action) => Invoke<object?>(() =>
    {
        action();
        return null;
    });

    /// <summary>
    /// Runs <paramref name="function"/> on the dispatcher thread and waits for it, rethrowing
    /// whatever it threw with its original stack.
    /// </summary>
    public T Invoke<T>(Func<T> function)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (OnDispatcherThread)
            return function();

        using var done = new ManualResetEventSlim(false);
        T result = default!;
        ExceptionDispatchInfo? failure = null;

        _work.Add(() =>
        {
            try
            {
                result = function();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        failure?.Throw();
        return result;
    }

    /// <summary>
    /// Queues long-running work without waiting for it — the streaming loop. It occupies the thread
    /// until it returns, so anything else queued meanwhile waits, which is the intended ordering:
    /// a stream's teardown is queued behind its own loop and therefore runs after it has stopped.
    /// </summary>
    public void Post(Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _work.Add(action);
    }

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    private void Loop()
    {
        // OleInitialize, not just the CoInitializeEx the runtime does for an STA thread. ASIO
        // drivers are COM servers and at least one registered on this bench faults during
        // PortAudio's enumeration without the full OLE apartment -- an access violation inside
        // Pa_Initialize that takes the process with it. PowerShell's STA thread, which does
        // OleInitialize, enumerates the same drivers cleanly, which is what pointed at this.
        if (OperatingSystem.IsWindows())
            OleInitialize(IntPtr.Zero);

        foreach (Action action in _work.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch
            {
                // Invoke captures its own failures; anything reaching here came from Post, whose
                // caller has already gone. Swallowing keeps the thread alive for the teardown that
                // is queued behind it — a dispatcher that dies here would leave the native library
                // initialised and the stream open, with nothing able to close them.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _work.CompleteAdding();

        // Joined rather than abandoned, for the reason the QA403's pump join exists: the thread may
        // be inside a native call that owns a device, and a process that exits around it leaves the
        // driver claimed.
        if (!OnDispatcherThread)
            _thread.Join(TimeSpan.FromSeconds(5));

        _work.Dispose();
    }
}
