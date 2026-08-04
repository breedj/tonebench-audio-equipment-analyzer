namespace Qa403.Device;

/// <summary>
/// Static capabilities of a connected device: supported sample rates and available input/
/// output gain ranges. Reported once after <see cref="IAnalyzerDevice.ConnectAsync"/>.
/// </summary>
public sealed record DeviceCapabilities(
    IReadOnlyList<double> SampleRatesHz,
    IReadOnlyList<double> InputRangesDbv,
    IReadOnlyList<double> OutputRangesDbv,
    int MaxChannelCount)
{
    /// <summary>
    /// The real QA403's ranges, per <c>docs/protocol.md</c> (register 5 = input range, 0..7 ->
    /// {0,6,...,42} dBV in 6 dB steps; register 6 = output range, 0..3 -> {-12,-2,8,18} dBV).
    /// Sample rates are the commonly cited QA402/QA403 rates; the exact negotiation mechanism
    /// is unverified against the reference implementation (see docs/protocol.md) and must be
    /// confirmed before this feeds <c>Qa40xUsbDevice</c> in Phase 6.
    /// </summary>
    public static DeviceCapabilities Qa403 { get; } = new(
        SampleRatesHz: [48_000, 96_000, 192_000],
        InputRangesDbv: [0, 6, 12, 18, 24, 30, 36, 42],
        OutputRangesDbv: [-12, -2, 8, 18],
        MaxChannelCount: 2);
}
