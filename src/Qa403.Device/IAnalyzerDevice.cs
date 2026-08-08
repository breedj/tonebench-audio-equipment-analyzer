namespace Qa403.Device;

/// <summary>
/// Abstraction over a QA403-class audio analyzer: connect, configure input/output ranges and
/// the generator, then stream calibrated PCM. Implementations apply calibration (raw ADC
/// counts -> volts) internally, exactly once — every <see cref="PcmBlock"/> handed to a caller
/// is already in volts. <c>Qa403.Dsp</c> must never see a raw count or a gain constant.
/// </summary>
public interface IAnalyzerDevice
{
    Task<DeviceInfo> ConnectAsync(CancellationToken ct);

    /// <summary>Sample rates and gain ranges the device supports. Valid only after <see cref="ConnectAsync"/> has completed.</summary>
    DeviceCapabilities Capabilities { get; }

    /// <summary>
    /// Raised when the transport detects the physical device is gone (e.g. unplugged mid-stream).
    /// Implementations must never throw this condition into <see cref="StreamAsync"/>'s
    /// <c>await foreach</c> — they end the enumeration cleanly and raise this event instead, so
    /// callers can surface a device-lost state to the UI. <see cref="Simulation.SimulatedDevice"/>
    /// never raises it; real hardware has no equivalent for the simulator.
    /// </summary>
    event EventHandler? DeviceLost;

    /// <summary>
    /// The input range the hardware is actually on right now. Read this rather than remembering
    /// whatever was last passed to <see cref="SetInputRangeAsync"/>: autoranging moves the range on
    /// its own, and a caller-side cache silently becomes a lie the moment it does — which is exactly
    /// how the UI came to report a range the device was not using.
    /// </summary>
    double InputRangeDbv { get; }

    /// <summary>
    /// Whether the device may step <see cref="InputRangeDbv"/> on its own. Settable while connected;
    /// <c>false</c> means an explicit <see cref="SetInputRangeAsync"/> stays put. Always false (and
    /// ignored) on devices with no autoranging of their own, e.g. the simulator.
    /// </summary>
    bool AutoRangeEnabled { get; set; }

    /// <summary>
    /// RX chunks discarded since connect because the consumer could not keep up, cumulative across
    /// streams. Not a cosmetic statistic: a dropped chunk is a hole in the sample stream, so the next
    /// FFT record is spliced — which smears tones into skirts and lifts the broadband noise floor.
    /// Any non-zero value means measured noise numbers are suspect. Always 0 for the simulator.
    /// </summary>
    long DroppedRxChunks { get; }

    /// <summary><paramref name="dbv"/> must be one of <see cref="DeviceCapabilities.InputRangesDbv"/>.</summary>
    Task SetInputRangeAsync(double dbv, CancellationToken ct);

    /// <summary><paramref name="dbv"/> must be one of <see cref="DeviceCapabilities.OutputRangesDbv"/>.</summary>
    Task SetOutputRangeAsync(double dbv, CancellationToken ct);

    Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct);

    /// <summary>
    /// Streams calibrated PCM blocks at real-time cadence for the configured sample rate,
    /// until <paramref name="ct"/> is cancelled or <see cref="StopAsync"/> is called. Each
    /// yielded <see cref="PcmBlock"/> is owned by the caller — see its XML doc for the
    /// disposal contract.
    /// </summary>
    IAsyncEnumerable<PcmBlock> StreamAsync(AcquisitionConfig config, CancellationToken ct);

    Task StopAsync();
}
