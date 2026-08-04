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
