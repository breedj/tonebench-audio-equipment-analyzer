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
    /// stream is not. Measured, so the trade is a number rather than an assertion: a 73-point
    /// THD-vs-frequency sweep at 192 kHz went from 273.8 s to 281 s, <b>+2.6 %</b>, with every
    /// point still on its own grid frequency and the loopback still reading 0.005 % THD and 76 dB
    /// SNR — i.e. the longer discard is spent waiting, not on stale audio.</para>
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

    /// <summary>
    /// How much of PortAudio's output ring the priming may occupy. The rest is the room the next
    /// blocking <c>Pa_WriteStream</c> needs: fill the ring and the write waits for the DAC to free
    /// a transfer's worth, which on a stream that is already late is how a hiccup becomes the
    /// "Wait timed out" that kills it.
    /// </summary>
    private const double OutputRingFraction = 0.6;

    /// <summary>
    /// How many whole transfers of stimulus to hold ahead of the DAC, given the ring PortAudio
    /// actually allocated. One place, because two things need the same answer and must not drift:
    /// the pump, which does the priming, and <see cref="StimulusLatencyFrames"/>, which has to say
    /// how much committed audio a stimulus change must outrun. They disagreed — the pump primed
    /// this, while the latency reported the ring's whole <i>capacity</i>, which is the figure that
    /// method's own remarks say it is not. Harmless while the two were close; a 4x discard on
    /// every tone of every sweep once the ring is asked for in time, and 2.7 s a tone on WASAPI
    /// exclusive, which reports 131072 frames whatever is asked of it.
    /// </summary>
    private static int PrimeTransfersFor(double outputLatencyS, double sampleRateHz)
    {
        int ringFrames = (int)(outputLatencyS * sampleRateHz);
        double primeSeconds = Math.Min(
            TargetInFlightSeconds, ringFrames * OutputRingFraction / sampleRateHz);
        return Math.Max(1, (int)(primeSeconds * sampleRateHz / TransferFrames));
    }

    private readonly IPortAudio _portAudio;
    private readonly ILogger<SoundCardDevice> _logger;
    private readonly Lock _stateGate = new();

    private SoundCardSettings _settings;
    private SoundCardCalibration _calibration;
    private DeviceCapabilities _capabilities = DeviceCapabilities.Qa403;
    private GeneratorConfig _generator = new(ChannelGeneratorConfig.Off, ChannelGeneratorConfig.Off);

    private volatile bool _connected;
    private volatile int _stimulusLatencyFrames;
    /// <inheritdoc />
    public int StreamStartSettleFrames(double sampleRateHz) =>
        (int)Math.Round(Math.Max(0.0, _settings.StreamStartSettleMs) / 1000.0 * sampleRateHz);

    /// <summary>The rate the last stream was opened at, so a reopen at the same rate costs nothing. 0 until one has been.</summary>
    private double _lastOpenedSampleRateHz;

    private long _droppedRxChunks;
    private long _droppedBlocks;
    private volatile bool _stopRequested;

    /// <summary>
    /// Where each output block is copied while the stream recorder is armed, or null. Volatile and
    /// read per block, so arming takes effect on a running stream -- see <see cref="IStimulusTap"/>.
    /// </summary>
    private volatile IStimulusTap? _stimulusTap;

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
    public IStimulusTap? StimulusTap
    {
        get => _stimulusTap;
        set => _stimulusTap = value;
    }

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
            config.SampleRateHz, Exclusive: true)
        {
            // **The rings are asked for in time, and this field is what makes TargetBufferedSeconds
            // real rather than documentation.** Left null, PortAudio sizes both from the driver's
            // own defaultLow*Latency, which an ASIO driver reports as a frame count: on a MOTU M4
            // that is 43 ms in and 30 ms out at 192 kHz, and half of each at 384. See the glitch
            // measurements on TargetBufferedSeconds for why a frame count is the wrong unit here.
            SuggestedLatencyS = TargetBufferedSeconds,
        };

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

        bool reclocked = ReclockIfNeeded(request, config.SampleRateHz);

        // **The re-clock settle is discarded here, inside the device, and nowhere else.**
        // It used to be a Flush booked by DeviceSessionService.StartAcquisitionCoreAsync, which
        // could not hold: every later Flush *overwrites* the pending discard rather than extending
        // it, so the first tone a runner configured replaced the 3 s settle with the 300 ms capture
        // delay and the transient landed in the measurement. Measured on a MOTU M4 in loopback,
        // entering each run from a different sample rate, 100 % reproducible: a THD-vs-frequency
        // point 1 of THD 4.3 % / SNR 20 dB against 0.0006 % / 85 dB from point 2 on; a THD-vs-
        // amplitude point 1 of THD+N 1.93 % / SNR 34 dB against 72.7 dB; and a frequency-response
        // trace spread over **91 dB with coherence 0.17**, against 0.51 dB and 1.0000 on the very
        // next identical run — the chirp averages the bad record in, so the whole curve goes, not
        // one point. Frames the device never hands out cannot be un-discarded by anything
        // downstream, which is the whole reason it lives here.
        //
        // Only after a re-clock. A same-rate reopen has nothing to settle — measured, and it is
        // also why STOP then RUN at one rate was always clean — so an unconditional settle would
        // charge every run three seconds for nothing.
        int settleFrames = reclocked ? StreamStartSettleFrames(config.SampleRateHz) : 0;

        IPortAudioStream stream = _portAudio.OpenStream(request, TransferFrames);
        // The stream's own lifecycle, at Debug: a reopened stream and a reused one behave
        // differently and nothing else in the log distinguishes them.
        // Information, not Debug: these two figures are the size of PortAudio's own ring buffers,
        // which is what decides how much jitter the lockstep read/write loop can absorb before it
        // glitches. A glitch report that does not say what the margin was cannot be acted on.
        _logger.LogInformation(
            "Sound card stream opened at {Rate:0} Hz, input latency {In:0.000} s ({InFrames:0} frames), output latency {Out:0.000} s ({OutFrames:0} frames), transfer {Transfer} frames",
            config.SampleRateHz,
            stream.InputLatencyS, stream.InputLatencyS * config.SampleRateHz,
            stream.OutputLatencyS, stream.OutputLatencyS * config.SampleRateHz,
            TransferFrames);
        var pumpFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // What the stream actually holds: the queue the pump is about to prime, plus the transfer
        // it will have in hand on top of it. Not PortAudio's reported latency — that is the ring's
        // capacity, and the pump deliberately fills only OutputRingFraction of it.
        _stimulusLatencyFrames =
            (PrimeTransfersFor(stream.OutputLatencyS, config.SampleRateHz) + 1) * TransferFrames;

        // The loop runs on the dispatcher's own thread -- the apartment ASIO needs, and a dedicated
        // thread rather than the pool, which is what the DAC-gap rule asks for.
        _portAudio.Dispatcher.Post(() =>
        {
            try
            {
                Pump(stream, blocks.Writer, config, settleFrames, ct);
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
        IPortAudioStream stream, ChannelWriter<PcmBlock> writer, AcquisitionConfig config,
        int settleFrames, CancellationToken ct)
    {
        int frames = TransferFrames;
        var waveform = new Qa40xGeneratorWaveform(new Random(), config.SampleRateHz);

        float[] inBuffer = ArrayPool<float>.Shared.Rent(frames * InputChannelCount);
        float[] outBuffer = ArrayPool<float>.Shared.Rent(frames * OutputChannelCount);

        long frameIndex = 0;
        long sequence = 0;
        bool discontinuity = false;
        int settleRemaining = settleFrames;

        // Diagnosis of a glitch, and the only thing that can tell its four causes apart. The pump
        // sets one flag -- PcmBlock.StartsAfterDiscontinuity -- and DeviceSessionService logs that
        // flag with the QA403's wording ("input range stepped by autoranging"), which on a card
        // with no ranger at all names the one cause it cannot be. Read timeout, input overflow,
        // output underflow and write timeout are four different faults with four different fixes,
        // so each is counted and the first few of each are named.
        var sinceLast = Stopwatch.StartNew();
        int readTimeouts = 0, inputOverflows = 0, outputUnderflows = 0, writeTimeouts = 0;

        // **Attribution, and the reason it is here rather than inferred afterwards.** Every one of
        // the four causes above is the *symptom* of the same thing — this thread was not running
        // when the hardware needed it — and a log that says only which ring emptied cannot say
        // whether to raise a priority, enlarge a buffer or stop allocating. So each iteration
        // records how long it ran, read return to read return (nominally one transfer: 5.3 ms at
        // 192 kHz), and how much of that the runtime spent with every managed thread suspended. The same question
        // TransferPumpDiagnostics answers for the QA403, and the same two answers: a stall with GC
        // flat in it is scheduling, which priority and buffering address; a stall that *is* the GC
        // pause is not something thread priority can help with at all, because a blocking
        // collection suspends an AboveNormal thread exactly as readily as any other.
        var loopWatch = Stopwatch.StartNew();
        var lastGcPause = GC.GetTotalPauseDuration();
        double loopMs = 0.0;
        double loopGcMs = 0.0;

        // The worst of those, kept whether or not anything glitched — which is the point. A run
        // with no glitch in it says only that no margin was *exceeded*; this says how much of it
        // was used, so "the fix works" can be a measurement rather than an absence. It is the
        // sound-card counterpart of the QA403's `TX summary` slack floor, and unlike that one it
        // is a true worst case rather than a drifting metric: both ends come from one Stopwatch.
        double worstLoopMs = 0.0;
        double worstLoopGcMs = 0.0;
        long worstLoopBlock = 0;

        // Out here rather than beside the priming loop, because the margin line in the finally
        // has to report what actually went in -- including when the stream died before it was
        // full, which is exactly the run whose margin someone will want to read.
        int primed = 0;

        void NoteIteration()
        {
            loopMs = loopWatch.Elapsed.TotalMilliseconds;
            loopWatch.Restart();

            var pause = GC.GetTotalPauseDuration();
            loopGcMs = (pause - lastGcPause).TotalMilliseconds;
            lastGcPause = pause;

            // Skipped while sequence is 0: the first iterations are the stream spinning up, where
            // a read waits hundreds of milliseconds for the driver's first buffer and would stand
            // as the worst case for ever. Nothing was at risk then -- there is no audio to lose
            // before the first block, which is the same judgement the startup read timeouts get.
            if (sequence > 0 && loopMs > worstLoopMs)
            {
                worstLoopMs = loopMs;
                worstLoopGcMs = loopGcMs;
                worstLoopBlock = sequence;
            }
        }

        void Glitch(string cause, int count)
        {
            // Every one for the first handful, then only at Debug: a card glitching steadily must
            // not turn the log into the glitch's own biggest cost.
            double ms = sinceLast.Elapsed.TotalMilliseconds;
            sinceLast.Restart();
            if (count <= GlitchesLoggedPerCause)
            {
                _logger.LogInformation(
                    "Sound card stream glitch: {Cause} at block {Sequence}, {Ms:0.0} ms since the previous glitch ({Count} so far this acquisition); "
                        + "this iteration took {LoopMs:0.0} ms of which {GcMs:0.0} ms was GC",
                    cause, sequence, ms, count, loopMs, loopGcMs);
            }
            else
            {
                _logger.LogDebug(
                    "Sound card stream glitch: {Cause} at block {Sequence}, {Ms:0.0} ms since the previous ({Count}); "
                        + "iteration {LoopMs:0.0} ms, GC {GcMs:0.0} ms",
                    cause, sequence, ms, count, loopMs, loopGcMs);
            }
        }

        try
        {
            stream.Start();

            // Priming: as much audio ahead of the DAC as the ring PortAudio actually allocated will
            // hold, in whole transfers.
            //
            // A duplex blocking stream has to be read and written in lockstep: both directions are
            // serviced by one callback, so writing several transfers ahead without reading backs the
            // input up until the callback stalls and the write times out. Measured on a MOTU M4 over
            // ASIO, priming a 64 ms queue killed the stream about a second in with
            // "Wait timed out" from Pa_WriteStream -- **because 64 ms does not fit**: the ring is
            // sized from the device's own defaultLowOutputLatency, and on that card at 192 kHz it is
            // 5943 frames, i.e. 31 ms. A blocking write waits for room that only frees at the DAC's
            // own rate, so asking for twice the ring waits for ever. That measurement was read as
            // "lockstep allows only one transfer", and it does not: it allows anything the ring
            // holds with a transfer's room to spare.
            //
            // One transfer was 5.3 ms at 192 kHz -- less than a scheduler quantum, against a ring
            // that could hold six times it -- so any stall at all emptied the DAC. Bounded by
            // OutputRingFraction so the next Write never blocks on a full ring, and by the queue
            // this device would choose for itself; see the glitch measurements in StaDispatcher.
            int ringFrames = (int)(stream.OutputLatencyS * config.SampleRateHz);
            int primeTransfers = PrimeTransfersFor(stream.OutputLatencyS, config.SampleRateHz);

            // **A priming write may time out, and that must not be fatal.** This call was the one
            // write in the pump with no timeout handling, which was survivable only while it was
            // made once; asking for three turned a latent fault into a reproducible one. Right
            // after `ReclockIfNeeded` has opened and closed a throwaway stream, the driver is not
            // yet consuming, so `Pa_WriteStream` blocks on a ring that nothing is draining and
            // comes back `paTimedOut` — measured twice on a MOTU M4 on a 48 → 96 and a 96 → 192 kHz
            // change, ~230 ms after the priming began, and the escaping exception took the whole
            // device with it: "The sound card's transfer loop stopped", then DeviceLost, then every
            // later measurement silently running against a session with no device. Nothing else in
            // the log said the stream never opened.
            //
            // **Only one transfer fits before the first read, and the rest go in after it.**
            // Measured with a stopwatch around each write, across a 48 → 96 and a 96 → 192 kHz
            // change: write 0 returns in 0.0–0.8 ms, write 1 then blocks for the stream's whole
            // write timeout (95.5 ms at 96 kHz, 49.7 at 192) and is refused — there is no room
            // because nothing is draining the ring yet. After one read has returned, which is proof
            // the driver is running, the remaining writes return in **0.0 ms**. So the priming is
            // split in two, and a refusal before the first read must not be taken as final: taking
            // it as final leaves every stream that follows a rate change on the 5.3 ms margin this
            // whole change exists to end, which is exactly what an adaptive sweep arranges for
            // itself at every resolution step.
            bool primingFinished = false;

            // Writes until the queue is as deep as asked for, or until the ring refuses more.
            // Called twice: once before the first read, and once after it — see below.
            void Prime(bool running)
            {
                while (!primingFinished && primed < primeTransfers)
                {
                    Synthesize(outBuffer, frames, waveform, frameIndex, config.SampleRateHz);
                    frameIndex += frames;

                    try
                    {
                        stream.Write(outBuffer, frames);
                    }
                    catch (PortAudioException ex) when (ex.ErrorCode == PaError.TimedOut)
                    {
                        // A refusal *before* the first read says only that the driver is not
                        // consuming yet -- there is no room because nothing has drained -- so the
                        // top-up after the first read gets its turn. A refusal once the stream is
                        // demonstrably running is the ring genuinely full, and that is the end of it.
                        primingFinished = running;
                        return;
                    }

                    primed++;
                }

                primingFinished = true;
            }

            Prime(running: false);

            // How deep the queue ended up, after the top-up below has had its turn. Logged once,
            // reporting what went in rather than what was asked for, so it can never overstate the
            // margin the stream actually has -- and it is the margin a glitch report has to quote.
            bool marginReported = false;

            int consecutiveTimeouts = 0;

            while (!_stopRequested && !ct.IsCancellationRequested)
            {
                bool overflowed;
                try
                {
                    overflowed = stream.Read(inBuffer, frames);
                    // Measured here, as the read returns, and not at the top of the loop: that is
                    // the far end of the window a stall lives in. Both glitches this has to
                    // explain are then describing the same window -- an input overflow is noticed
                    // by the read that has just returned late, and an output underflow by the
                    // write a few hundred microseconds after it.
                    NoteIteration();
                    consecutiveTimeouts = 0;
                }
                catch (PortAudioException ex) when (ex.ErrorCode == PaError.TimedOut)
                {
                    NoteIteration();
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
                    Glitch("read timed out", ++readTimeouts);
                    discontinuity = true;
                    continue;
                }

                if (overflowed)
                {
                    // Samples the driver discarded before this transfer: a hole in the stream, not
                    // a skipped display frame. The next block must say so, or a record spanning the
                    // splice reads as a tone smeared into a skirt with the whole floor lifted.
                    Interlocked.Increment(ref _droppedRxChunks);
                    Glitch("input overflowed", ++inputOverflows);
                    discontinuity = true;
                }

                // **The top-up, and it is where the priming usually happens after a rate change.**
                // A read that returned is proof the driver is running and draining the output ring;
                // before that it may be doing neither, which is why the priming ahead of the first
                // read can be refused outright. Measured on a MOTU M4 immediately after
                // ReclockIfNeeded: 1 of 2 transfers accepted at 96 kHz and 1 of 3 at 192 kHz, i.e.
                // exactly the 5.3 ms margin the glitch fix exists to end, reappearing on every
                // stream that follows a rate change -- which an adaptive sweep makes on its own.
                // Writing the shortfall here costs one iteration and nothing afterwards: Prime()
                // stops for good at the first refusal, so a ring that is simply full is asked once.
                if (!primingFinished)
                    Prime(running: true);

                if (!marginReported)
                {
                    marginReported = true;
                    _logger.LogInformation(
                        "Primed the sound card's output with {Transfers} transfers ({Frames} frames, {Ms:0.0} ms) of a {RingFrames}-frame ring",
                        primed, primed * frames,
                        primed * frames * 1000.0 / config.SampleRateHz, ringFrames);
                }

                // **The re-clock settle: these frames are thrown away here and never handed out.**
                // The driver is still bringing the hardware to the new rate, and what it returns
                // meanwhile is partly repeated and partly silence -- the analyzer reads it as a
                // chopped tone, which is where THD 4.3 % and a coherence of 0.17 came from. Still
                // read, so the stream stays paced by the device clock; still written to, so the
                // stimulus is playing throughout and the DUT settles on it rather than on the
                // first measured record; counted against *frames*, not the clock, so it is exact.
                //
                // Not a dropped chunk and not a glitch: nothing was lost, because nothing here was
                // ever going to be measured -- the same judgement as the startup read timeouts
                // above, and for the same reason, since DroppedRxChunks is the number a user
                // checks to decide whether to believe the screen.
                if (settleRemaining > 0)
                {
                    settleRemaining -= frames;
                    discontinuity = true;

                    Synthesize(outBuffer, frames, waveform, frameIndex, config.SampleRateHz);
                    frameIndex += frames;

                    try
                    {
                        stream.Write(outBuffer, frames);
                    }
                    catch (PortAudioException ex) when (ex.ErrorCode == PaError.TimedOut)
                    {
                        if (++consecutiveTimeouts > MaxConsecutiveTimeouts)
                            throw;
                    }

                    if (settleRemaining <= 0)
                    {
                        _logger.LogInformation(
                            "Discarded {Ms:0} ms of the sound card's re-clock settle at {Rate:0} Hz; measuring from here",
                            settleFrames * 1000.0 / config.SampleRateHz, config.SampleRateHz);
                    }

                    continue;
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

                Synthesize(outBuffer, frames, waveform, frameIndex, config.SampleRateHz);
                frameIndex += frames;

                try
                {
                    // The bool was discarded here, and it is the DAC's own gap: paOutputUnderflowed
                    // says the callback found the ring empty and played silence. That is an audible
                    // hole in the stimulus and a splice in what comes back, and it was invisible --
                    // not counted, not flagged, not logged.
                    if (stream.Write(outBuffer, frames))
                    {
                        Glitch("output underflowed", ++outputUnderflows);
                        discontinuity = true;
                    }
                }
                catch (PortAudioException ex) when (ex.ErrorCode == PaError.TimedOut)
                {
                    // As above, and the more common of the two: the output side is the one that
                    // backs up. The audio for this transfer is lost, which is a gap in the stimulus,
                    // so the next block says the stream broke.
                    if (++consecutiveTimeouts > MaxConsecutiveTimeouts)
                        throw;

                    Glitch("write timed out", ++writeTimeouts);
                    discontinuity = true;
                }
            }
        }
        finally
        {
            stream.Stop();
            ArrayPool<float>.Shared.Return(inBuffer);
            ArrayPool<float>.Shared.Return(outBuffer);

            // Logged whether or not anything went wrong, because the worst iteration is the
            // number that says how close the stream came -- and a clean run that came within a
            // millisecond of the margin is a report worth having before the next one does not.
            _logger.LogInformation(
                "Sound card stream margin over {Blocks} blocks at {Rate:0} Hz: worst iteration {WorstMs:0.0} ms "
                    + "(GC {WorstGcMs:0.0} ms) at block {WorstBlock}, against {PrimeMs:0.0} ms primed ahead of the DAC "
                    + "and {InputMs:0.0} ms of input ring",
                sequence, config.SampleRateHz, worstLoopMs, worstLoopGcMs, worstLoopBlock,
                primed * frames * 1000.0 / config.SampleRateHz,
                stream.InputLatencyS * 1000.0);

            if (readTimeouts + inputOverflows + outputUnderflows + writeTimeouts > 0)
            {
                _logger.LogInformation(
                    "Sound card stream summary over {Blocks} blocks at {Rate:0} Hz: {ReadTimeouts} read timeouts, "
                        + "{InputOverflows} input overflows, {OutputUnderflows} output underflows, {WriteTimeouts} write timeouts",
                    sequence, config.SampleRateHz, readTimeouts, inputOverflows, outputUnderflows, writeTimeouts);
            }
        }
    }

    /// <summary>How many of each glitch cause are logged at Information before the rest drop to Debug.</summary>
    private const int GlitchesLoggedPerCause = 10;

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
    private void Synthesize(
        float[] buffer, int frames, Qa40xGeneratorWaveform waveform, long frameIndex, double sampleRateHz)
    {
        GeneratorConfig generator;
        lock (_stateGate)
        {
            generator = _generator;
        }

        Array.Clear(buffer, 0, frames * OutputChannelCount);

        // Null unless the stream recorder is armed. Rented per block for the reason the QA403's
        // pump rents per chunk: this is a diagnostic that is normally off, and it must not cost a
        // live buffer for the life of every stream.
        var tap = _stimulusTap;
        float[]? tapVolts = tap is null ? null : ArrayPool<float>.Shared.Rent(frames * 2);

        for (int f = 0; f < frames; f++)
        {
            long index = frameIndex + f;
            int dst = (f * OutputChannelCount) + _settings.OutputChannelOffset;
            buffer[dst] = _calibration.VoltsToSample(waveform.ComputeSample(generator.Left, index, 0));
            buffer[dst + 1] = _calibration.VoltsToSample(waveform.ComputeSample(generator.Right, index, 1));

            if (tapVolts is null)
                continue;

            // From the samples, not from the volts, so the clamp in VoltsToSample -- the card's own
            // clipping -- is in the recording. OutputFullScaleV is its exact inverse.
            tapVolts[f * 2] = (float)(buffer[dst] * _calibration.OutputFullScaleV);
            tapVolts[(f * 2) + 1] = (float)(buffer[dst + 1] * _calibration.OutputFullScaleV);
        }

        if (tap is not null && tapVolts is not null)
        {
            tap.WriteStimulus(tapVolts.AsSpan(0, frames * 2), channelCount: 2, sampleRateHz, frameIndex);
            ArrayPool<float>.Shared.Return(tapVolts);
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

    /// <summary>
    /// Opens the stream once and throws it away before opening the one that will be used, whenever
    /// the sample rate has changed since the last open.
    ///
    /// <para><b>This is a workaround for the driver, and it is doing real work.</b> An ASIO driver
    /// re-clocks the hardware when a stream asks for a rate it is not already running at, and the
    /// MOTU M4's does not have the new rate ready by the time that first stream starts: the reads
    /// come back <i>partially filled</i>, and the unfilled part is handed to the analyzer as audio.
    /// Measured 2026-10-01 from the diagnostic recorder's held-constant detector, 96 → 48 kHz while
    /// running: <b>64 frozen runs of exactly 512 frames, 50.0 % of a 65536-point record</b> — every
    /// 1024-frame read half real and half repeated. The analyzer measured a 1 kHz tone chopped to a
    /// 50 % duty cycle: THD 2.32 %, SNR 7.6 dB, and a "fundamental" of 952.9 Hz against the 999.76 Hz
    /// actually playing. At 96 kHz the same fault was ~3.3 % of the record and still ruinous.</para>
    ///
    /// <para>It persisted for the life of the stream and cleared only on a stop/start — because a
    /// stop/start at the same rate <i>reuses</i> the stream rather than reopening it, which is what
    /// made it read as "the first run after a rate change always fails". The first open is what
    /// re-clocks the card; the second finds it already there.</para>
    ///
    /// <para>Only when the rate has actually changed, so an ordinary RUN pays nothing. Any failure
    /// is swallowed: this is a warm-up, and the open that matters is the next one, which reports
    /// its own errors.</para>
    /// </summary>
    private bool ReclockIfNeeded(SoundCardStreamRequest request, double sampleRateHz)
    {
        if (_lastOpenedSampleRateHz == sampleRateHz)
            return false;

        try
        {
            using (IPortAudioStream warmUp = _portAudio.OpenStream(request, TransferFrames))
            {
                // Started, not merely opened: opening negotiates the rate, starting is what makes
                // the driver actually run at it, and it is the running that has to settle.
                warmUp.Start();
                Thread.Sleep(ReclockSettle);
                warmUp.Stop();
            }

            _logger.LogDebug(
                "Sound card re-clocked to {Rate:0} Hz with a throwaway stream before opening the real one.",
                sampleRateHz);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The sound card's re-clocking open failed; opening the real stream anyway.");
        }

        _lastOpenedSampleRateHz = sampleRateHz;
        return true;
    }

    /// <summary>How long the throwaway stream is left running for the driver to settle on the new rate.</summary>
    private static readonly TimeSpan ReclockSettle = TimeSpan.FromMilliseconds(300);

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
