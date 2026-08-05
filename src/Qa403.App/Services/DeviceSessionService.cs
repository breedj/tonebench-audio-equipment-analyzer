using Qa403.App.Endpoints;
using Qa403.App.Streaming;
using Qa403.Device;

namespace Qa403.App.Services;

/// <summary>
/// Owns the single <see cref="IAnalyzerDevice"/> for the app's lifetime: connects once at
/// startup, tracks the range/generator/acquisition state the device itself doesn't expose
/// getters for, and drains acquisition blocks into the <see cref="SpectrumPipeline"/> for the
/// duration of each acquisition run.
/// </summary>
public sealed class DeviceSessionService(IAnalyzerDevice device, SpectrumPipeline spectrumPipeline, ILogger<DeviceSessionService> logger)
{
    /// <summary>Real QA403 hardware queue depth (docs/qa403-app-stack-spec.md §2.1) — also the floor enforced on <see cref="StartAcquisitionAsync"/>'s <c>BlockFrameCount</c>.</summary>
    public const int MinBlockFrameCount = 1024;

    private readonly object _acquisitionLock = new();

    private DeviceInfo? _info;
    private double _inputRangeDbv;
    private double _outputRangeDbv;
    private GeneratorConfig _generatorConfig = GeneratorConfig.Off;
    private CancellationTokenSource? _acquisitionCts;
    private Task? _acquisitionTask;

    public DeviceCapabilities Capabilities => device.Capabilities;

    public bool Acquiring => _acquisitionTask is { IsCompleted: false };

    public async Task ConnectAsync(CancellationToken ct)
    {
        _info = await device.ConnectAsync(ct);
        _inputRangeDbv = Capabilities.InputRangesDbv[0];
        _outputRangeDbv = Capabilities.OutputRangesDbv[0];
        logger.LogInformation(
            "Connected to {Product} (serial {Serial}, fw {Firmware}, simulated={Simulated})",
            _info.ProductName, _info.SerialNumber, _info.FirmwareVersion, _info.IsSimulated);
    }

    public DeviceStatusResponse GetStatus()
    {
        var left = ToResponse(_generatorConfig.Left);
        var right = ToResponse(_generatorConfig.Right);

        if (_info is not { } info)
            return new DeviceStatusResponse(false, "", "", "", false, 0, 0, false, left, right);

        return new DeviceStatusResponse(
            true, info.ProductName, info.SerialNumber, info.FirmwareVersion, info.IsSimulated,
            _inputRangeDbv, _outputRangeDbv, Acquiring, left, right);
    }

    private static GeneratorChannelResponse ToResponse(ChannelGeneratorConfig config)
    {
        var tones = config.Tones.Select(t => new GeneratorToneResponse(t.FrequencyHz, t.AmplitudeDbv)).ToArray();
        return new GeneratorChannelResponse(config.Enabled, tones);
    }

    public async Task SetInputRangeAsync(double dbv, CancellationToken ct)
    {
        await device.SetInputRangeAsync(dbv, ct);
        _inputRangeDbv = dbv;
    }

    public async Task SetOutputRangeAsync(double dbv, CancellationToken ct)
    {
        await device.SetOutputRangeAsync(dbv, ct);
        _outputRangeDbv = dbv;
    }

    public async Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct)
    {
        await device.ConfigureGeneratorAsync(config, ct);
        _generatorConfig = config;
    }

    public Task StartAcquisitionAsync(AcquisitionConfig config, CancellationToken ct)
    {
        if (config.SampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "SampleRateHz must be positive.");
        if (config.ChannelCount < 1 || config.ChannelCount > Capabilities.MaxChannelCount)
            throw new ArgumentOutOfRangeException(nameof(config), $"ChannelCount must be between 1 and {Capabilities.MaxChannelCount}.");
        if (config.BlockFrameCount < MinBlockFrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                $"BlockFrameCount must be at least {MinBlockFrameCount} (the real QA403's hardware queue depth, "
                    + "per docs/qa403-app-stack-spec.md §2.1) — smaller values turn the acquisition loop into a "
                    + "near-tight spin at the simulated sample rate and can starve the app under load.");
        }

        lock (_acquisitionLock)
        {
            if (Acquiring)
                throw new InvalidOperationException("Acquisition already running.");

            spectrumPipeline.Start(config.SampleRateHz, config.ChannelCount);
            _acquisitionCts = new CancellationTokenSource();

            // Capture the token by value into a local, not the field, before handing it to
            // Task.Run: the lambda doesn't actually run until the thread pool gets to it, and if
            // StopAcquisitionAsync races in first and nulls _acquisitionCts (see below), reading
            // the field lazily inside the lambda throws a NullReferenceException instead of
            // observing an already-cancelled token like it should.
            var drainCt = _acquisitionCts.Token;
            _acquisitionTask = Task.Run(() => DrainAsync(config, drainCt), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private async Task DrainAsync(AcquisitionConfig config, CancellationToken ct)
    {
        try
        {
            await foreach (var block in device.StreamAsync(config, ct).WithCancellation(ct))
                spectrumPipeline.Enqueue(block); // takes ownership — disposes the block itself
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Acquisition stream faulted");
        }
    }

    public async Task StopAcquisitionAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_acquisitionLock)
        {
            cts = _acquisitionCts;
            task = _acquisitionTask;
            _acquisitionCts = null;
            _acquisitionTask = null;
        }

        if (cts is null)
            return;

        cts.Cancel();
        await device.StopAsync();
        if (task is not null)
            await task;
        cts.Dispose();
        await spectrumPipeline.StopAsync();
    }
}
