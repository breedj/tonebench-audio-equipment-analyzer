using Tonebench.Device;
using Tonebench.Device.Hardware;

namespace Tonebench.App.Services;

/// <summary>One reading of a load, as the browser sees it.</summary>
/// <param name="LeftTemperatureC">Left heatsink temperature in whole degrees, as the device reports it.</param>
/// <param name="RightTemperatureC">Right heatsink temperature.</param>
/// <param name="CurrentA">
/// DC current through the sense path. Quantised far more coarsely than its unit suggests — about
/// 48.5 mA on the bench unit — so a client should not print more precision than that implies.
/// </param>
/// <param name="OverTemperature">Whether a heatsink is over temperature (60 °C on a QA451B).</param>
/// <param name="OverCurrent">Whether over-current protection has tripped.</param>
/// <param name="RequiresReplug">
/// Whether a fault has latched in hardware and no command can clear it — the USB cable has to be
/// pulled. True for every over-current trip.
/// </param>
/// <param name="RawStatusBits">
/// The device's nine status bits as it sent them. Carried because only five are understood, so an
/// odd reading is worth having in a log or an issue report rather than reduced to the flags above.
/// </param>
public sealed record LoadReadingResponse(
    int LeftTemperatureC,
    int RightTemperatureC,
    double CurrentA,
    bool OverTemperature,
    bool OverCurrent,
    bool RequiresReplug,
    int RawStatusBits);

/// <summary>
/// Everything the load screens need, in one document, so the status poll is one request.
/// </summary>
/// <param name="Kind">Which load is selected — including <see cref="ProgrammableLoadKind.None"/>.</param>
/// <param name="Connected">Whether that load is open and answering.</param>
/// <param name="Model">What the hardware calls itself ("QA451B"), or null when not connected.</param>
/// <param name="FirmwareVersion">The firmware the device reports, or null.</param>
/// <param name="RequiredFirmwareVersion">What the vendor application expects for that model, or null.</param>
/// <param name="FirmwareSupported">Whether the firmware is at or above that, or null when not connected.</param>
/// <param name="UsbSerial">
/// The USB descriptor's serial string — the only usable identifier, since the device's own serial
/// register answers a placeholder on at least one unit.
/// </param>
/// <param name="SupportedImpedances">The loads this device can present, in ohms, 0 meaning open.</param>
/// <param name="ImpedanceOhms">
/// The load currently engaged, in ohms, or <b>null when that is not known</b> — which is a real
/// state and not a gap: it is what a connect that deliberately skipped the reset leaves behind,
/// until the first poll reads the relays back out of the status word.
/// </param>
/// <param name="DutPowerOn">Whether the DUT supply switch is closed, null when not known.</param>
/// <param name="Reading">The last poll, or null if none has been taken.</param>
/// <param name="Error">
/// Why the load is not connected, when it is not. Carried on the status rather than only thrown at
/// the connect attempt, so a bench that comes up with the load unplugged can say so on screen.
/// </param>
public sealed record LoadStatusResponse(
    ProgrammableLoadKind Kind,
    bool Connected,
    string? Model,
    int? FirmwareVersion,
    int? RequiredFirmwareVersion,
    bool? FirmwareSupported,
    string? UsbSerial,
    int[] SupportedImpedances,
    int? ImpedanceOhms,
    bool? DutPowerOn,
    LoadReadingResponse? Reading,
    string? Error);

/// <summary>
/// Owns the programmable load: which one is selected, its connection, and the one lock every
/// exchange with it goes through. The analyzer's equivalent is <see cref="DeviceSessionService"/>,
/// and this is deliberately a fraction of its size — a load answers one 64-byte question at a time
/// and streams nothing, so there is no pump, no queue and no drop counter to own.
///
/// <para><b>It builds nothing in its constructor.</b> That is the one structural decision worth
/// defending: <see cref="DeviceSessionService"/> creates its first device in a field initializer,
/// which makes resolving it pin <see cref="AnalyzerDeviceFactory.InitialKind"/> and makes the whole
/// order of <see cref="StartupSettingsLoader"/> load-bearing. A service that connects on request
/// cannot reinterpret a setting that is read after it — see <see cref="LoadKindSetting"/>.</para>
///
/// <para><b>Nothing here knows about measurements.</b> Refusing a load change while a sweep is
/// running is a rule about the <i>app</i>, not about the device, so it lives in
/// <see cref="Endpoints.LoadEndpoints"/> where the runners are already in scope. Keeping it out of
/// here is what lets this service be tested against a fake load with no analyzer in sight.</para>
/// </summary>
public sealed class LoadSessionService(
    ProgrammableLoadFactory factory,
    LoadKindSetting kindSetting,
    ILogger<LoadSessionService> logger) : IDisposable
{
    /// <summary>
    /// Least time between two reads of the hardware. The status endpoint is polled by every open
    /// browser tab, and a load has nothing to gain from being asked faster than it changes — the
    /// vendor application polls at 1 s. The same reasoning as the analyzer's liveness ping, which is
    /// throttled for the same reason.
    /// </summary>
    private const int ReadIntervalMs = 500;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private IProgrammableLoad? _load;
    private LoadReading? _reading;

    /// <summary>
    /// When the device was last read, or null for "not since anything changed" — which forces the
    /// next poll to read.
    ///
    /// <para>Nullable rather than a <see cref="long.MinValue"/> sentinel, and not as a matter of
    /// taste: <c>Environment.TickCount64 - long.MinValue</c> overflows and comes back negative, so
    /// the sentinel version never satisfied the "is it stale?" comparison and the first poll after a
    /// connect read nothing at all.</para>
    /// </summary>
    private long? _lastReadTicks;
    private string? _error;
    private bool _disposed;

    /// <summary>Which load is selected. Not necessarily connected — see <see cref="GetStatusAsync"/>.</summary>
    public ProgrammableLoadKind Kind => kindSetting.Kind;

    /// <summary>Whether a load is open and answering.</summary>
    public bool Connected => _load is { IsConnected: true };

    /// <summary>
    /// The last known state, built from what is already in hand: <b>no USB exchange, no lock, no
    /// wait</b>.
    ///
    /// <para>For the save path, which records what the load was doing when a result was stored.
    /// <see cref="GetStatusAsync"/> is the wrong thing there twice over — it is async, and it may
    /// take a reading, which would make saving a measurement a reason to talk to an instrument. A
    /// figure up to a second stale is a better answer than a save that waits behind a soft
    /// start.</para>
    /// </summary>
    public LoadStatusResponse StatusSnapshot => BuildStatus();

    /// <summary>
    /// Opens the selected load and reads its identity. A no-op when the selection is
    /// <see cref="ProgrammableLoadKind.None"/> or a load is already connected — so this is the retry
    /// behind the UI's Connect button as well as what startup calls, exactly like the analyzer's
    /// <c>ConnectAsync</c>.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConnectCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Selects a different load: tears the current one down first, then connects the new one.
    ///
    /// <para>The teardown is unconditional and comes first for the reason the analyzer's switch
    /// documents — a handle released late is a handle the new device cannot claim — and a failure to
    /// connect the new one leaves the selection changed and the error on the status, rather than
    /// silently reverting to a load the user has just said they are not using.</para>
    /// </summary>
    public async Task SwitchKindAsync(ProgrammableLoadKind kind, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TearDown();
            kindSetting.Kind = kind;
            logger.LogInformation("Programmable load switched to {Kind}", kind);
            await ConnectCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The status, taking a fresh reading if the last one is older than <see cref="ReadIntervalMs"/>.
    ///
    /// <para>The lock is taken with a <b>zero timeout</b>: this runs on a poll, and a poll that
    /// blocked behind a command would make the UI appear to hang for as long as the command takes —
    /// which for closing the DUT switch is a second and a half of soft start. A poll that cannot get
    /// in reports the last reading instead, which is at most half a second old.</para>
    /// </summary>
    public async Task<LoadStatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool stale = _lastReadTicks is null || Environment.TickCount64 - _lastReadTicks.Value >= ReadIntervalMs;
        if (Connected && stale && await _lock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            try
            {
                if (_load is { IsConnected: true } load)
                {
                    _reading = await load.ReadAsync(ct).ConfigureAwait(false);
                    _lastReadTicks = Environment.TickCount64;
                    _error = null;
                }
            }
            catch (TimeoutException ex)
            {
                // A hiccup on a device that is still there, following the QA403's rule: report it,
                // keep the session, and let the next poll try again.
                _error = ex.Message;
                _lastReadTicks = Environment.TickCount64;
            }
            catch (Exception ex)
            {
                HandleLoss(ex);
            }
            finally
            {
                _lock.Release();
            }
        }

        return BuildStatus();
    }

    /// <summary>Engages a load, or opens it. Ohms: 0, 4 or 8.</summary>
    public Task SetImpedanceAsync(int ohms, CancellationToken ct = default) =>
        CommandAsync(load => load.SetImpedanceAsync(ToImpedance(ohms), ct), ct);

    /// <summary>Switches the DUT's own supply. Returns once the soft start has run.</summary>
    public Task SetDutPowerAsync(bool on, CancellationToken ct = default) =>
        CommandAsync(load => load.SetDutPowerAsync(on, ct), ct);

    /// <summary>No load, DUT power off.</summary>
    public Task SetSafeStateAsync(CancellationToken ct = default) =>
        CommandAsync(load => load.SetSafeStateAsync(ct), ct);

    /// <summary>
    /// Puts the load away and closes it, for host shutdown.
    ///
    /// <para>The safe state is commanded explicitly even though the user's manual says the hardware
    /// opens its own relays when the application goes away: it is immediate, it is logged, and it
    /// does not depend on firmware behaviour this app has only read about. A failure is swallowed —
    /// a shutdown must not be held up by a load that has already been unplugged.</para>
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_load is { IsConnected: true } load)
            {
                try
                {
                    await load.SetSafeStateAsync(ct).ConfigureAwait(false);
                    logger.LogInformation("Programmable load left open with DUT power off");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not put the programmable load in its safe state");
                }
            }

            TearDown();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task CommandAsync(Func<IProgrammableLoad, Task> command, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IProgrammableLoad load = _load is { IsConnected: true } connected
                ? connected
                : throw new InvalidOperationException("No programmable load is connected.");

            try
            {
                await command(load).ConfigureAwait(false);
            }
            catch (Qa451DeviceLostException ex)
            {
                HandleLoss(ex);
                throw;
            }

            // The command changed the hardware, so the cached reading is stale by definition —
            // force the next poll to take a fresh one rather than show the previous state for up to
            // half a second after the user pressed something.
            _lastReadTicks = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        if (kindSetting.Kind == ProgrammableLoadKind.None)
        {
            _error = null;
            return;
        }

        if (_load is { IsConnected: true })
        {
            return;
        }

        TearDown();

        IProgrammableLoad? load = factory.Create(kindSetting.Kind);
        if (load is null)
        {
            _error = $"No load implementation for {kindSetting.Kind}.";
            return;
        }

        try
        {
            await load.ConnectAsync(ct).ConfigureAwait(false);
            _load = load;
            _error = null;
            _lastReadTicks = null;

            LoadInfo? info = load.Info;
            logger.LogInformation(
                "Programmable load connected: {Kind}, {Model}, firmware {Firmware}",
                kindSetting.Kind,
                info?.Model ?? "unknown",
                info?.FirmwareVersion);
        }
        catch (Exception ex)
        {
            load.Dispose();
            _error = ex.Message;
            throw;
        }
    }

    /// <summary>
    /// The device has gone. Clears the connection rather than only recording the error, so the
    /// status flips to disconnected, commands are refused with a message, and Connect recovers a
    /// replugged device without a restart — the analyzer's <c>OnDeviceLost</c> rule, which exists
    /// because a session holding a dead handle reports a healthy device.
    /// </summary>
    private void HandleLoss(Exception ex)
    {
        logger.LogWarning(ex, "Programmable load lost");
        _error = ex.Message;
        TearDown();
    }

    private void TearDown()
    {
        _load?.Dispose();
        _load = null;
        _reading = null;
        _lastReadTicks = null;
    }

    private LoadStatusResponse BuildStatus()
    {
        IProgrammableLoad? load = _load;
        LoadInfo? info = load?.Info;
        LoadReading? reading = _reading;

        return new LoadStatusResponse(
            kindSetting.Kind,
            Connected,
            info?.Model,
            info?.FirmwareVersion,
            info?.RequiredFirmwareVersion,
            info?.FirmwareIsSupported,
            info?.UsbSerial,
            [.. (load?.SupportedImpedances ?? []).Select(i => (int)i)],
            load?.Impedance is { } impedance ? (int)impedance : null,
            load?.DutPowerOn,
            reading is null
                ? null
                : new LoadReadingResponse(
                    reading.LeftTemperatureC,
                    reading.RightTemperatureC,
                    reading.CurrentA,
                    reading.Faults.HasFlag(LoadFaults.OverTemperature),
                    reading.Faults.HasFlag(LoadFaults.OverCurrent),
                    reading.RequiresReplug,
                    reading.RawStatusBits),
            _error);
    }

    private static LoadImpedance ToImpedance(int ohms) => ohms switch
    {
        0 => LoadImpedance.Open,
        4 => LoadImpedance.FourOhm,
        8 => LoadImpedance.EightOhm,
        _ => throw new ArgumentOutOfRangeException(nameof(ohms), ohms, "A load is 0 (open), 4 or 8 ohms."),
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TearDown();
        _lock.Dispose();
    }
}
