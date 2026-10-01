using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Qa403.Device.Hardware;

namespace Qa403.Device.SoundCard;

/// <summary>Thrown when the configured card is not there — the analyzer's <c>Qa40xDeviceNotFoundException</c>.</summary>
public sealed class SoundCardNotFoundException(string message) : Exception(message);

/// <summary>
/// A sound card as a calibrated analyzer, over PortAudio.
///
/// <para>Three things about it differ from the QA403 and all three are visible from outside.
/// It has <b>one input range and one output range</b> — the card's own full scale, as
/// <see cref="SoundCardCalibration"/> measured it — so <see cref="HasInputAutoRanging"/> is false
/// and no runner tries to range it. It declares a <b>minimum stimulus frequency</b>, because a
/// card is AC-coupled at both ends and below that corner a sweep reports the card's own high-pass
/// as DUT response. And its <b>sample rates are probed</b>, not assumed: only those of
/// 48/96/192 kHz the hardware actually accepts are offered.</para>
///
/// <para>Everything native happens on <see cref="StaDispatcher"/>'s thread, which on Windows is a
/// single-threaded apartment. That is not tidiness — ASIO is COM, and from an MTA thread PortAudio
/// enumerates no ASIO devices at all. It also gives the streaming loop the dedicated thread the
/// project's rules ask for, off the thread pool entirely.</para>
/// </summary>
public sealed class SoundCardDevice : IAnalyzerDevice, IDisposable
{
    /// <summary>
    /// Frames per PortAudio transfer. Small enough that the read paces the loop tightly, large
    /// enough not to spend the thread in transfer overhead — the same tradeoff as the QA403's
    /// 512-frame chunks, one step larger because a sound card's own buffer is coarser.
    /// </summary>
    private const int TransferFrames = 1024;

    /// <summary>
    /// How much audio PortAudio is asked to buffer each way, <b>in seconds</b>.
    ///
    /// <para>A time and not a frame count, for the reason the QA403's <c>TargetInFlightSeconds</c>
    /// is one: a buffer fixed in frames holds half as long at twice the rate. Inheriting the
    /// device's own <c>defaultLowInputLatency</c> — which an ASIO driver reports as its buffer
    /// size, a frame count — did exactly that, and it was measurable. Starting a stream on a
    /// MOTU M4 and leaving it running, the dropped-chunk count settled at <b>0 at 48 kHz, 3 at
    /// 96 kHz and 7 at 192 kHz</b> and then never moved again: a fixed ~35 ms of audio accumulating
    /// while the stream starts, against a ring that was 2.73 s at 48 kHz and a quarter of that at
    /// 192 kHz. Reported as "a lot of dropped when using the soundcard, especially after I change
    /// the sample rate" — a rate change opens a new stream, so it brings a fresh batch.</para>
    ///
    /// <para>It is the <i>output</i> side of this that <see cref="StimulusLatencyFrames"/> reads,
    /// so raising it lengthens the discard after every stimulus change by the same amount. That is
    /// the correct trade and not a cost worth minimising: a discard is exact, and a hole in the
    /// stream is not.</para>
    /// </summary>
    private const double TargetBufferedSeconds = 0.128;

    /// <summary>
    /// How much audio is kept queued ahead of the DAC, as a time budget rather than a transfer
    /// count, for the reason <c>Qa40xUsbDevice.QueueDepthFor</c> gives: a fixed count shrinks the
    /// buffered time as the rate rises, which is exactly how DAC dropouts came to be "worse at
    /// higher sample rate".
    ///
    /// <para>This is what <see cref="StimulusLatencyFrames"/> reports, and it is ours to choose:
    /// PortAudio's own buffer is only a capacity, and a duplex stream is paced by its reads, so the
    /// queue sits wherever it was primed.</para>
    /// </summary>
    private const double TargetInFlightSeconds = 0.064;

    private readonly IPortAudio _portAudio;
    private readonly ILogger<SoundCardDevice> _logger;
    private readonly Lock _stateGate = new();

    private SoundCardSettings _settings;
    private SoundCardCalibration _calibration;
    private DeviceCapabilities _capabilities = DeviceCapabilities.Qa403;
    private GeneratorConfig _generator = new(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off);

    private volatile bool _connected;
    private volatile int _stimulusLatencyFrames;
    private long _droppedRxChunks;
    private long _droppedBlocks;
    private volatile bool _stopRequested;

    public SoundCardDevice(
        SoundCardSettings settings,
        SoundCardCalibration calibration,
        ILogger<SoundCardDevice> logger)
        : this(settings, calibration, logger, PortAudioNative.Shared)
    {
    }

    /// <summary>The seam the tests use: a fake PortAudio, so everything above the interop runs without a card.</summary>
    internal SoundCardDevice(
        SoundCardSettings settings,
        SoundCardCalibration calibration,
        ILogger<SoundCardDevice> logger,
        IPortAudio portAudio)
    {
        _settings = settings;
        _calibration = calibration;
        _logger = logger;
        _portAudio = portAudio;
    }

    public DeviceCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
    /// <remarks>One fixed range, so there is nothing to step and no runner should try.</remarks>
    public bool HasInputAutoRanging => false;

    /// <inheritdoc />
    /// <remarks>Declared per card, because a front-end changes where the corner is.</remarks>
    public double MinStimulusFrequencyHz => _settings.MinStimulusFrequencyHz;

    public double InputRangeDbv => _calibration.InputRangeDbv;

    public bool AutoRangeEnabled { get => false; set { } }

    public double AutoRangeStepDownDelayMs { get; set; } = Qa40xAutoRanger.DefaultStepDownDelayMs;

    public bool AutoRangeStepDownEnabled { get; set; } = true;

    public long DroppedRxChunks => Interlocked.Read(ref _droppedRxChunks);

    public long DroppedBlocks => Interlocked.Read(ref _droppedBlocks);

    /// <summary>Never raised by polling: a card that disappears surfaces as a failed transfer in the stream.</summary>
    public event EventHandler? DeviceLost;

    /// <inheritdoc />
    /// <remarks>
    /// The audio already queued ahead of the DAC when a stimulus change returns. It is the queue
    /// this device primes and holds, not PortAudio's reported latency — that figure is the buffer's
    /// capacity, and on WASAPI exclusive it is 131072 frames whatever is asked for.
    /// </remarks>
    public int StimulusLatencyFrames(double sampleRateHz)
    {
        int running = _stimulusLatencyFrames;
        return running > 0 ? running : QueueFramesFor(sampleRateHz) + TransferFrames;
    }

    public Task<DeviceInfo> ConnectAsync(CancellationToken ct)
    {
        if (!_settings.IsConfigured)
            throw new SoundCardNotFoundException("No sound card has been chosen yet. Pick one in the sound-card settings.");

        var enumerator = new SoundCardEnumerator(_portAudio);

        var input = enumerator.Find(_settings.InputHostApi, _settings.InputDeviceName)
            ?? throw new SoundCardNotFoundException(
                $"The input '{_settings.InputDeviceName}' ({_settings.InputHostApi}) is not there. Plug the card in, or pick another in the sound-card settings.");

        var output = enumerator.Find(_settings.OutputHostApi, _settings.OutputDeviceName)
            ?? throw new SoundCardNotFoundException(
                $"The output '{_settings.OutputDeviceName}' ({_settings.OutputHostApi}) is not there. Plug the card in, or pick another in the sound-card settings.");

        RequireChannels(input, output);

        var rates = enumerator.SupportedRatesHz(
            input, output, exclusive: true, InputChannelCount, OutputChannelCount);

        if (rates.Count == 0)
        {
            throw new SoundCardNotFoundException(
                $"'{input.Name}' accepts none of 48, 96 or 192 kHz, which are the rates this analyzer measures at.");
        }

        lock (_stateGate)
        {
            _capabilities = new DeviceCapabilities(
                SampleRatesHz: rates,
                InputRangesDbv: [_calibration.InputRangeDbv],
                OutputRangesDbv: [_calibration.OutputRangeDbv],
                MaxChannelCount: 2);
        }

        _connected = true;

        var pairing = SoundCardEnumerator.Pairing(input, output);
        if (pairing == SoundCardPairing.Unknown)
        {
            _logger.LogWarning(
                "Sound card input '{Input}' and output '{Output}' may not be the same interface. Two clocks drift, and the drift is reported as DUT response.",
                input.Name, output.Name);
        }

        _logger.LogInformation(
            "Sound card connected: {HostApi} / {Input} + {Output}, rates {Rates}, input full scale {InputDbv:0.00} dBV{Calibrated}",
            input.HostApiName, input.Name, output.Name, string.Join("/", rates.Select(r => r / 1000)),
            _calibration.InputRangeDbv,
            _calibration.IsInputCalibrated && _calibration.IsOutputCalibrated ? string.Empty : " (UNCALIBRATED — levels are dBFS)");

        string label = pairing == SoundCardPairing.FullDuplexDevice
            ? input.Name
            : $"{input.Name} + {output.Name}";

        return Task.FromResult(new DeviceInfo(
            ProductName: $"{label} ({input.HostApiName})",
            SerialNumber: "—",
            FirmwareVersion: _portAudio.VersionText,
            IsSimulated: false));
    }

    /// <summary>There is nothing to poll: a card that goes away shows up as a failed transfer in the stream.</summary>
    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(_connected);

    /// <summary>
    /// One rung, so this only ever confirms the one value — but it still validates, because a
    /// caller asking for a range this card does not have has miscalculated something.
    /// </summary>
    public Task SetInputRangeAsync(double dbv, CancellationToken ct)
    {
        RequireSupportedRange(_capabilities.InputRangesDbv, dbv);
        return Task.CompletedTask;
    }

    public Task SetOutputRangeAsync(double dbv, CancellationToken ct)
    {
        RequireSupportedRange(_capabilities.OutputRangesDbv, dbv);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Publishes the new stimulus. The pump reads it once at the top of each transfer, so a change
    /// landing mid-transfer is in the next one — which is what <see cref="StimulusLatencyFrames"/>
    /// describes, and why it counts the queue as well.
    /// </summary>
    public Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_stateGate)
        {
            _generator = config;
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<PcmBlock> StreamAsync(
        AcquisitionConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_connected)
            throw new InvalidOperationException("Connect the sound card before streaming from it.");

        if (!_capabilities.SampleRatesHz.Contains(config.SampleRateHz))
        {
            throw new ArgumentOutOfRangeException(
                nameof(config), config.SampleRateHz,
                $"This card accepts {string.Join(", ", _capabilities.SampleRatesHz)} Hz.");
        }

        // Per-acquisition, zeroed here rather than at connect: the only question these numbers are
        // ever asked is whether the measurement on screen can be trusted, and a running total
        // cannot answer it.
        Interlocked.Exchange(ref _droppedRxChunks, 0);
        Interlocked.Exchange(ref _droppedBlocks, 0);
        _stopRequested = false;

        var enumerator = new SoundCardEnumerator(_portAudio);
        var input = enumerator.Find(_settings.InputHostApi, _settings.InputDeviceName)
            ?? throw new SoundCardNotFoundException("The input device is no longer there.");
        var output = enumerator.Find(_settings.OutputHostApi, _settings.OutputDeviceName)
            ?? throw new SoundCardNotFoundException("The output device is no longer there.");

        var request = new SoundCardStreamRequest(
            input.Index, InputChannelCount, output.Index, OutputChannelCount,
            config.SampleRateHz, Exclusive: true);

        // Drop-oldest, so a slow consumer never stalls acquisition -- the same contract the QA403's
        // block channel has, and the reason DroppedBlocks exists to report it.
        var blocks = Channel.CreateBounded<PcmBlock>(
            new BoundedChannelOptions(BlockChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            dropped =>
            {
                Interlocked.Increment(ref _droppedBlocks);
                dropped.Dispose();
            });

        IPortAudioStream stream = _portAudio.OpenStream(request, TransferFrames);
        // The stream's own lifecycle, at Debug: a reopened stream and a reused one behave
        // differently and nothing else in the log distinguishes them.
        _logger.LogDebug(
            "Sound card stream opened at {Rate:0} Hz, output latency {Lat:0.000} s",
            config.SampleRateHz, stream.OutputLatencyS);
        var pumpFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // What the stream actually holds, read from it rather than chosen: with one transfer of
        // priming the committed audio is PortAudio's own output latency plus the transfer in hand.
        _stimulusLatencyFrames =
            (int)Math.Ceiling(stream.OutputLatencyS * config.SampleRateHz) + TransferFrames;

        // The loop runs on the dispatcher's own thread -- the apartment ASIO needs, and a dedicated
        // thread rather than the pool, which is what the DAC-gap rule asks for.
        _portAudio.Dispatcher.Post(() =>
        {
            try
            {
                Pump(stream, blocks.Writer, config, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The sound card's transfer loop stopped.");
                _connected = false;
                DeviceLost?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _logger.LogDebug("Sound card transfer loop finished at {Rate:0} Hz", config.SampleRateHz);
                blocks.Writer.TryComplete();
                pumpFinished.TrySetResult();
            }
        });

        try
        {
            await foreach (PcmBlock block in blocks.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return block;
        }
        finally
        {
            // In a finally, and this is the rule the whole type hangs on: consumers routinely break
            // out of the await foreach, and disposal runs finally handlers and nothing else. A
            // stream left open holds the card -- with ASIO, exclusively -- which reads to the user
            // as hardware that needs a power cycle.
            _stopRequested = true;
            await pumpFinished.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            stream.Dispose();
            _stimulusLatencyFrames = 0;

            while (blocks.Reader.TryRead(out PcmBlock? leftover))
                leftover.Dispose();
        }
    }

    public Task StopAsync()
    {
        _stopRequested = true;
        return Task.CompletedTask;
    }

    public void Dispose() => _stopRequested = true;

    /// <summary>
    /// Read, convert, hand on, synthesize, write — in that order, every iteration.
    ///
    /// <para>The read paces the loop: it blocks until the card has a transfer's worth, which is the
    /// device's own clock. Reading before writing is also the documented way not to deadlock a
    /// duplex stream.</para>
    /// </summary>
    private void Pump(
        IPortAudioStream stream, ChannelWriter<PcmBlock> writer, AcquisitionConfig config, CancellationToken ct)
    {
        int frames = TransferFrames;
        var waveform = new Qa40xGeneratorWaveform(new Random(), config.SampleRateHz);

        float[] inBuffer = ArrayPool<float>.Shared.Rent(frames * InputChannelCount);
        float[] outBuffer = ArrayPool<float>.Shared.Rent(frames * OutputChannelCount);

        long frameIndex = 0;
        long sequence = 0;
        bool discontinuity = false;

        try
        {
            stream.Start();

            // Exactly one transfer of priming, so the DAC has something before the first read
            // returns -- and no more than one.
            //
            // A duplex blocking stream has to be read and written in lockstep: both directions are
            // serviced by one callback, so writing several transfers ahead without reading backs the
            // input up until the callback stalls and the write times out. Measured on a MOTU M4 over
            // ASIO, priming a 64 ms queue killed the stream about a second in with
            // "Wait timed out" from Pa_WriteStream. The queue depth this device reports is
            // PortAudio's own output latency instead -- what it actually holds, rather than what we
            // would like it to.
            Synthesize(outBuffer, frames, waveform, frameIndex);
            frameIndex += frames;
            stream.Write(outBuffer, frames);

            int consecutiveTimeouts = 0;

            while (!_stopRequested && !ct.IsCancellationRequested)
            {
                bool overflowed;
                try
                {
                    overflowed = stream.Read(inBuffer, frames);
                    consecutiveTimeouts = 0;
                }
                catch (PortAudioException ex) when (ex.ErrorCode == PaError.TimedOut)
                {
                    // The device is still there and the next transfer usually succeeds. Tearing the
                    // session down over one would be worse than the hiccup -- the same judgement the
                    // QA403's liveness ping makes about a timeout or a short read. Only a run of
                    // them means the card has actually stopped.
                    if (++consecutiveTimeouts > MaxConsecutiveTimeouts)
                        throw;

                    // **A timeout before the first block has arrived is not a drop**, and counting
                    // it as one was reported as "a lot of dropped when using the soundcard,
                    // especially after I change the sample rate". An ASIO stream takes a few
                    // hundred milliseconds to deliver its first buffer, and every read until then
                    // times out: measured on a MOTU M4, 3 timeouts at 96 kHz and 7 at 192 kHz, all
                    // before block 0, with not a single one afterwards. The count scaled with the
                    // sample rate only because the stream's own read timeout is shorter there --
                    // ~90 ms against ~47 ms -- so more of them fit into the same wait.
                    //
                    // Nothing was lost: there were no samples yet to lose. DroppedRxChunks means a
                    // *hole* in the stream, which per its own rule invalidates a noise measurement,
                    // so reporting a startup wait as one is worse than useless -- it is the number
                    // a user checks to decide whether to believe the screen.
                    if (sequence == 0)
                    {
                        _logger.LogDebug("Waiting for the sound card's first buffer.");
                        continue;
                    }

                    Interlocked.Increment(ref _droppedRxChunks);
                    discontinuity = true;
                    continue;
                }

                if (overflowed)
                {
                    // Samples the driver discarded before this transfer: a hole in the stream, not
                    // a skipped display frame. The next block must say so, or a record spanning the
                    // splice reads as a tone smeared into a skirt with the whole floor lifted.
                    Interlocked.Increment(ref _droppedRxChunks);
                    discontinuity = true;
                }

                float[] blockBuffer = ArrayPool<float>.Shared.Rent(frames * 2);
                for (int f = 0; f < frames; f++)
                {
                    int src = (f * InputChannelCount) + _settings.InputChannelOffset;
                    blockBuffer[f * 2] = (float)_calibration.SampleToVolts(inBuffer[src], 0);
                    blockBuffer[(f * 2) + 1] = (float)_calibration.SampleToVolts(inBuffer[src + 1], 1);
                }

                var block = new PcmBlock(
                    blockBuffer, frames * 2, config.SampleRateHz, 2, sequence++,
                    Stopwatch.GetTimestamp(), discontinuity);
                discontinuity = false;

                if (!writer.TryWrite(block))
                {
                    block.Dispose();
                    Interlocked.Increment(ref _droppedBlocks);
                }

                Synthesize(outBuffer, frames, waveform, frameIndex);
                frameIndex += frames;

                try
                {
                    stream.Write(outBuffer, frames);
                }
                catch (PortAudioException ex) when (ex.ErrorCode == PaError.TimedOut)
                {
                    // As above, and the more common of the two: the output side is the one that
                    // backs up. The audio for this transfer is lost, which is a gap in the stimulus,
                    // so the next block says the stream broke.
                    if (++consecutiveTimeouts > MaxConsecutiveTimeouts)
                        throw;

                    discontinuity = true;
                }
            }
        }
        finally
        {
            stream.Stop();
            ArrayPool<float>.Shared.Return(inBuffer);
            ArrayPool<float>.Shared.Return(outBuffer);
        }
    }

    /// <summary>
    /// Fills one transfer from <see cref="Qa40xGeneratorWaveform"/> — reused rather than
    /// reimplemented so the played chirp stays pinned to <c>Qa403.Dsp.ExponentialChirp</c> by
    /// <c>ChirpFormulaAgreementTests</c>.
    ///
    /// <para><paramref name="frameIndex"/> is monotonic for the life of the stream and never reset:
    /// the chirp wraps by integer modulo on it, and a per-transfer reset would put a phase step at
    /// every seam. The config is read once per transfer, as the simulator reads its profile once
    /// per block and for the same reason. No L/R swap — that is a QA403 DAC quirk.</para>
    /// </summary>
    private void Synthesize(float[] buffer, int frames, Qa40xGeneratorWaveform waveform, long frameIndex)
    {
        GeneratorConfig generator;
        lock (_stateGate)
        {
            generator = _generator;
        }

        Array.Clear(buffer, 0, frames * OutputChannelCount);

        for (int f = 0; f < frames; f++)
        {
            long index = frameIndex + f;
            int dst = (f * OutputChannelCount) + _settings.OutputChannelOffset;
            buffer[dst] = _calibration.VoltsToSample(waveform.ComputeSample(generator.Left, index, 0));
            buffer[dst + 1] = _calibration.VoltsToSample(waveform.ComputeSample(generator.Right, index, 1));
        }
    }

    /// <summary>Updates the card and its calibration. The session reconnects afterwards, because the two ranges are derived from the calibration.</summary>
    public void Apply(SoundCardSettings settings, SoundCardCalibration calibration)
    {
        lock (_stateGate)
        {
            _settings = settings;
            _calibration = calibration;
        }
    }

    private int InputChannelCount => _settings.InputChannelOffset + 2;

    private int OutputChannelCount => _settings.OutputChannelOffset + 2;

    private static int QueueFramesFor(double sampleRateHz)
    {
        int frames = (int)Math.Round(TargetInFlightSeconds * sampleRateHz / TransferFrames) * TransferFrames;
        return Math.Max(TransferFrames * 2, frames);
    }

    private const int BlockChannelCapacity = 64;

    /// <summary>
    /// How many transfers in a row may time out before the card is treated as gone. One is a
    /// hiccup; a run of them is a device that has stopped answering.
    /// </summary>
    private const int MaxConsecutiveTimeouts = 20;

    private void RequireChannels(SoundCardDeviceDescriptor input, SoundCardDeviceDescriptor output)
    {
        if (input.MaxInputChannels < InputChannelCount)
        {
            throw new SoundCardNotFoundException(
                $"'{input.Name}' has {input.MaxInputChannels} input channels, and the analyzer was asked for channels {_settings.InputChannelOffset + 1}–{_settings.InputChannelOffset + 2}.");
        }

        if (output.MaxOutputChannels < OutputChannelCount)
        {
            throw new SoundCardNotFoundException(
                $"'{output.Name}' has {output.MaxOutputChannels} output channels, and the analyzer was asked for channels {_settings.OutputChannelOffset + 1}–{_settings.OutputChannelOffset + 2}.");
        }
    }

    /// <summary>
    /// A millidecibel, which is the tolerance a rung is matched to.
    ///
    /// <para>Not bit equality, which is what this was and which produced a 400 on the bench:
    /// "the only range is 16.278229126255518 dBV, actual value was 16.278233778585854" — five
    /// <i>micro</i>decibels apart, because the caller's ladder had been derived from a
    /// calibration a few tenths of a millivolt different from the current one. The rungs of a
    /// real ladder are six decibels apart and a sound card has exactly one, so nothing within a
    /// millidecibel of a rung can be anything but that rung; a caller that is further out than
    /// this is holding a genuinely stale ladder and should be told so.</para>
    /// </summary>
    private const double RangeMatchToleranceDb = 1e-3;

    private static void RequireSupportedRange(IReadOnlyList<double> ranges, double dbv)
    {
        foreach (double supported in ranges)
        {
            if (Math.Abs(supported - dbv) < RangeMatchToleranceDb)
                return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(dbv), dbv,
            $"This card's only range is {string.Join(", ", ranges)} dBV. If that is not what you were offered, the card has been recalibrated since — reload the page.");
    }
}
