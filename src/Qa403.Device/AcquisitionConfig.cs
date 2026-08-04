namespace Qa403.Device;

/// <summary>
/// What to stream. FFT size, window and averaging are <c>Qa403.Dsp</c> pipeline concerns, not
/// device concerns, and deliberately do not appear here.
/// </summary>
/// <param name="SampleRateHz">Must be one of <see cref="DeviceCapabilities.SampleRatesHz"/>.</param>
/// <param name="ChannelCount">Must not exceed <see cref="DeviceCapabilities.MaxChannelCount"/>.</param>
/// <param name="BlockFrameCount">
/// Frames per yielded <see cref="PcmBlock"/>. Controls streaming granularity/latency and, for
/// <c>SimulatedDevice</c>, the block emission cadence — smaller values exercise the streaming
/// pipeline's backpressure behaviour more aggressively.
/// </param>
public readonly record struct AcquisitionConfig(double SampleRateHz, int ChannelCount, int BlockFrameCount = 4096);
