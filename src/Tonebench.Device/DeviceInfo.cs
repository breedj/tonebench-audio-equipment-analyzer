namespace Tonebench.Device;

/// <summary>Identity of a connected device, as returned by <see cref="IAnalyzerDevice.ConnectAsync"/>.</summary>
public sealed record DeviceInfo(string ProductName, string SerialNumber, string FirmwareVersion, bool IsSimulated);
