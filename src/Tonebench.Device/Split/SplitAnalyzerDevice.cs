using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Tonebench.Device.SoundCard;

namespace Tonebench.Device.Split;

/// <summary>
/// Thrown when one half of a composite analyzer will not connect. Carries which half, so the status
/// bar can say "the generator side" rather than leaving the user to guess which of two devices the
/// message is about — see <c>ConnectFailure.MessageFor</c>, which unwraps it.
/// </summary>
public sealed class SplitSideException(string side, string label, Exception inner)
    : Exception($"The {side} ({label}) did not connect: {inner.Message}", inner)
{
    /// <summary>"generator side" or "capture side", already worded for a sentence.</summary>
    public string Side { get; } = side;

    /// <summary>What that half is called, e.g. <c>Topping D50 III</c>.</summary>
    public string Label { get; } = label;
}

/// <summary>
/// Two devices as one analyzer: one supplies the DAC, the other the ADC.
///
/// <para>The benches it exists for are a USB DAC playing while a QA403 captures (DAC measurements),
/// and a QA403 playing into a capture-only ADC such as an E1DA Cosmos. Both are the same shape, and
/// neither is reachable with one <see cref="IAnalyzerDevice"/>.</para>
///
/// <para><b>Why the composition is here and not in the session.</b> Every member of
/// <see cref="IAnalyzerDevice"/> already belongs to one side of a bench — its own doc comments say
/// which — so routing them is the whole of the work, and <c>DeviceSessionService</c> stays a
/// single-device consumer: its one stream lifecycle, its flushes, its ranging and its teardown are
/// untouched. The two members that are structurally duplex, <see cref="StreamAsync"/> and
/// <see cref="StimulusLatencyFrames"/>, are resolved in here where both rates are known.</para>
///
/// <para><b>Two clocks, and the app says so elsewhere.</b> Two devices are two crystals, so the
/// capture is a time-scaled copy of what was played. Tone measurements — level, THD, IMD, stepped
/// sweeps — are unaffected, because every one of them peak-searches. A chirp's phase, group delay
/// and impulse response are not, and the clock-drift correction is what answers that; the one case
/// that escapes entirely is a measured reference channel, where <c>Gain = L/R</c> is two channels
/// of one capture and the dilation cancels exactly. None of that is this type's business — it only
/// has to make the bench openable and report honestly which device each figure came from.</para>
///
/// <para><b>Independent rates cost nothing in transport here</b>, which is not obvious: the feared
/// rewrite is one <see cref="SoundCardDevice"/> opening two one-direction streams against two
/// clocks in its own lockstep loop. Two separate devices already each pump one stream on a thread
/// of their own, so each simply gets its own <see cref="AcquisitionConfig"/> and no stream ever
/// carries two rates.</para>
/// </summary>
public sealed class SplitAnalyzerDevice : IAnalyzerDevice, IDisposable
{
    /// <summary>How long to wait for the generator side's drain to finish on teardown. <see cref="SoundCardDevice"/>'s own figure.</summary>
    private static readonly TimeSpan DrainJoinTimeout = TimeSpan.FromSeconds(2);

    private readonly IAnalyzerDevice _input;
    private readonly IAnalyzerDevice _output;
    private readonly SplitDeviceSettings _settings;
    private readonly ILogger<SplitAnalyzerDevice> _logger;

    private int _deviceLostRaised;
    private volatile bool _outputFaulted;

    /// <summary>
    /// The rate the capturing half was last streamed at, which <see cref="AcrossTheWire"/> needs to
    /// know how many of its frames a played period lasts. Zero until the first stream.
    /// </summary>
    private volatile int _lastInputRateHz;

    public SplitAnalyzerDevice(
        IAnalyzerDevice input,
        IAnalyzerDevice output,
        SplitDeviceSettings settings,
        ILogger<SplitAnalyzerDevice> logger)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        // **A sound card on both sides used to be refused here, and is not any more.**
        // The refusal was real while every PortAudio call ran on one StaDispatcher thread that
        // SoundCardDevice.StreamAsync *occupies* for a stream's whole life: the second card's pump
        // queued behind the first and never ran — no exception, no log line, just an acquisition
        // that produced nothing, which is worth refusing rather than hanging. A card built as half
        // of a two-card bench now takes a transfer thread of its own (SoundCardDevice's
        // ownStreamThread), so the two free-run on their own clocks, which is the whole point: one
        // loop blocking on both directions would couple them exactly as one duplex stream spanning
        // two devices does, and slip a ring on the same schedule.
        //
        // What survives is narrower and cannot be judged from here — two ASIO cards, which the SDK
        // refuses one process-wide — so it arrives as SplitDeviceSettings.CombinationProblem and is
        // reported at connect. See that property for why not here.
        _input = input;
        _output = output;
        _settings = settings;
        _logger = logger;

        // Subscribed once, here rather than at connect, because a loss raised between connects
        // still has to reach the session. RaiseDeviceLost is idempotent per connection.
        _input.DeviceLost += OnInputLost;
        _output.DeviceLost += OnOutputLost;
    }

    /// <summary>The capturing half. Public so the App layer can report what each side is.</summary>
    public IAnalyzerDevice Input => _input;

    /// <summary>The generating half.</summary>
    public IAnalyzerDevice Output => _output;

    /// <summary>
    /// Why this pairing cannot be opened, or null — see
    /// <see cref="SplitDeviceSettings.CombinationProblem"/>. Public so the App layer can report it
    /// without opening anything, and so a test can say the ordinary case carries nothing.
    /// </summary>
    public string? CombinationProblem => _settings.CombinationProblem;

    /// <summary>What the capturing half called itself, or null before <see cref="ConnectAsync"/>.</summary>
    public DeviceInfo? InputInfo { get; private set; }

    /// <summary>What the generating half called itself, or null before <see cref="ConnectAsync"/>.</summary>
    public DeviceInfo? OutputInfo { get; private set; }

    public event EventHandler? DeviceLost;

    // ---- output side ----

    /// <summary>
    /// The stimulus, to the generating half — and to a <i>simulated</i> capturing half as well,
    /// which is the cable between them.
    ///
    /// <para><b>A simulator models what arrives at its input by synthesizing its own generator
    /// config</b>, so two simulators paired as a split are two devices with nothing wired between
    /// them: the one that plays plays into nothing, the one that captures hears its own silenced
    /// generator, and the screen reads a bare noise floor with THD in the hundreds of per cent.
    /// Measured, first time it was run. Mirroring the config onto a simulated capture half is the
    /// wire, and it costs nothing physical — that half has no DAC to drive anything with.</para>
    ///
    /// <para>A <b>real</b> capturing half is silenced instead and never handed a voiced config: a
    /// QA403 used as the input still has a DAC, and a tone left configured by an earlier session
    /// would drive whatever is wired to its output with nothing on screen naming it. So the test is
    /// <see cref="DeviceInfo.IsSimulated"/> rather than "which half" — the same flag the status pill
    /// reads.</para>
    /// </summary>
    public async Task ConfigureGeneratorAsync(GeneratorConfig config, CancellationToken ct)
    {
        await _output.ConfigureGeneratorAsync(config, ct).ConfigureAwait(false);

        if (InputInfo?.IsSimulated == true)
            await _input.ConfigureGeneratorAsync(AcrossTheWire(config), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The stimulus as the capturing half will <i>hear</i> it, which on a bench whose two rates
    /// differ is not the same record as the one played.
    ///
    /// <para><b>A wire preserves seconds, not frames.</b> Every periodic stimulus here states its
    /// period as a frame count, and a simulated capture half synthesizes from that count at its own
    /// rate — so handing it the generator's figure unchanged would have it re-create a sweep of a
    /// quarter the duration at a 1:4 pairing and call it the same waveform. Real hardware has no
    /// such problem: the ADC resamples a continuous signal and the frame count never crosses the
    /// cable. This is the simulator's end of that, and it is the only thing in this type that exists
    /// for a modelled bench rather than a real one.</para>
    /// </summary>
    private GeneratorConfig AcrossTheWire(GeneratorConfig config)
    {
        if (GeneratorSampleRateHz is not { } fsOut || _lastInputRateHz <= 0 || Math.Abs(fsOut - _lastInputRateHz) < 1e-9)
            return config;

        double ratio = _lastInputRateHz / fsOut;
        return config with { Left = Heard(config.Left), Right = Heard(config.Right) };

        ChannelGeneratorConfig Heard(ChannelGeneratorConfig channel)
        {
            if (channel.Chirp is { } chirp)
                channel = channel with { Chirp = chirp with { RepeatFrames = Scaled(chirp.RepeatFrames) } };

            if (channel.Noise is { } noise && noise.PeriodFrames > 0)
                channel = channel with { Noise = noise with { PeriodFrames = Scaled(noise.PeriodFrames) } };

            return channel;
        }

        int Scaled(int frames) => Math.Max(1, (int)Math.Round(frames * ratio));
    }

    public Task SetOutputRangeAsync(double dbv, CancellationToken ct) =>
        _output.SetOutputRangeAsync(dbv, ct);

    public double MaxGeneratorSampleRateHz => _output.MaxGeneratorSampleRateHz;

    public IStimulusTap? StimulusTap
    {
        get => _output.StimulusTap;
        set => _output.StimulusTap = value;
    }

    /// <summary>
    /// The generator's rate, or null when it follows the analysis rate — see
    /// <see cref="SplitDeviceSettings.GeneratorSampleRateHz"/>.
    /// </summary>
    public double? GeneratorSampleRateHz =>
        _settings.GeneratorSampleRateHz > 0 ? _settings.GeneratorSampleRateHz : null;

    // ---- input side ----

    public double InputRangeDbv => _input.InputRangeDbv;

    public Task SetInputRangeAsync(double dbv, CancellationToken ct) =>
        _input.SetInputRangeAsync(dbv, ct);

    public bool HasInputAutoRanging => _input.HasInputAutoRanging;

    public bool AutoRangeEnabled
    {
        get => _input.AutoRangeEnabled;
        set => _input.AutoRangeEnabled = value;
    }

    public bool AutoRangeStepDownEnabled
    {
        get => _input.AutoRangeStepDownEnabled;
        set => _input.AutoRangeStepDownEnabled = value;
    }

    /// <summary>
    /// The input side's figure, written to <b>both</b> — the factory pushes this into every device
    /// it builds "so a device that grows a ranger cannot forget it", and a half that is handed it
    /// anyway keeps that true.
    /// </summary>
    public double AutoRangeStepDownDelayMs
    {
        get => _input.AutoRangeStepDownDelayMs;
        set
        {
            _input.AutoRangeStepDownDelayMs = value;
            _output.AutoRangeStepDownDelayMs = value;
        }
    }

    public long DroppedRxChunks => _input.DroppedRxChunks;

    public long DroppedBlocks => _input.DroppedBlocks;

    // ---- both ----

    /// <summary>
    /// The measuring device's sensors. On both benches this feature exists for, exactly one side has
    /// any — and a measurement is judged by the device that took it, so the input side wins a tie.
    /// </summary>
    public DeviceTelemetry? Telemetry => _input.Telemetry ?? _output.Telemetry;

    /// <summary>
    /// The more restrictive of the two corners. It is documented as the output's property — a card's
    /// output coupling limits the stimulus, and a DC-coupled front end does not fix that — but an
    /// AC-coupled <i>input</i> limits the measurement by the same mechanism, so on a mixed bench the
    /// honest answer is whichever corner is higher.
    /// </summary>
    public double MinStimulusFrequencyHz =>
        Math.Max(_input.MinStimulusFrequencyHz, _output.MinStimulusFrequencyHz);

    /// <summary>
    /// Merged, and <b>not intersected</b>: <see cref="DeviceCapabilities.SampleRatesHz"/> is the
    /// analysis ladder and belongs to the capturing half, while the generator's own ladder rides
    /// along separately. Intersecting would delete the one configuration that makes the QA403's
    /// 384 kHz input a real measurement — that rate against a 192 kHz DAC.
    ///
    /// <para>The generator's ladder is filtered by its own
    /// <see cref="IAnalyzerDevice.MaxGeneratorSampleRateHz"/>, so a QA403 used as the <i>output</i>
    /// never offers 384 kHz: that ceiling is a hardware-protection rule, its DAC having tripped the
    /// output protection relay into cycling until the device needed a power cycle. Offering a rate
    /// and then refusing it would be the worse of the two answers.</para>
    /// </summary>
    public DeviceCapabilities Capabilities
    {
        get
        {
            DeviceCapabilities input = _input.Capabilities;
            DeviceCapabilities output = _output.Capabilities;

            double ceiling = _output.MaxGeneratorSampleRateHz;
            double[] outputRates = [.. output.OutputSampleRatesHz.Where(rate => rate <= ceiling)];

            return new DeviceCapabilities(
                SampleRatesHz: input.SampleRatesHz,
                InputRangesDbv: input.InputRangesDbv,
                OutputRangesDbv: output.OutputRangesDbv,
                MaxChannelCount: Math.Min(input.MaxChannelCount, output.MaxChannelCount),
                DeclaredOutputSampleRatesHz: outputRates.Length > 0 ? outputRates : output.OutputSampleRatesHz);
        }
    }

    /// <summary>
    /// The generator side's committed-but-unplayed audio, expressed in <i>input</i> frames — which
    /// is what every caller wants, because the discard it books is applied to the input pipeline.
    /// The conversion lives here, where both rates are known, so
    /// <c>DeviceSessionService.FlushAfterStimulusChange</c> needs no edit at all.
    /// </summary>
    public int StimulusLatencyFrames(double sampleRateHz)
    {
        double fsOut = GeneratorSampleRateHz ?? sampleRateHz;
        return ToInputFrames(_output.StimulusLatencyFrames(fsOut), sampleRateHz, fsOut);
    }

    /// <summary>
    /// What this bench withholds after a stream is opened: the greater of the two sides' own
    /// settling, the generator's converted into input frames.
    ///
    /// <para><b>The generator side's settle is the new hazard, and it is why this is a max rather
    /// than either side's figure.</b> A sound card re-clocks its hardware for a rate it is not
    /// already running at and is audibly unsettled for up to 1.5 s afterwards; it discards that
    /// inside its own pump, "and nowhere else", because a flush booked downstream is overwritten by
    /// the next one. On a split the card is the <i>generator</i>: its transient is on the wire and
    /// the other device is capturing it, and a device cannot withhold frames it does not produce. So
    /// <see cref="StreamAsync"/> withholds them from the input's enumeration instead, and this
    /// reports the total so every runner's capture timeout still allows for it.</para>
    ///
    /// <para>The two never overlap in practice: a card on both sides is refused, so at most one half
    /// has a non-zero figure.</para>
    /// </summary>
    public int StreamStartSettleFrames(double sampleRateHz)
    {
        double fsOut = GeneratorSampleRateHz ?? sampleRateHz;
        return Math.Max(
            _input.StreamStartSettleFrames(sampleRateHz),
            ToInputFrames(_output.StreamStartSettleFrames(fsOut), sampleRateHz, fsOut));
    }

    public async Task<DeviceInfo> ConnectAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _deviceLostRaised, 0);
        _outputFaulted = false;

        // Before either half is opened, so a pairing that cannot work says why instead of failing
        // halfway through with whatever the native layer called it.
        if (_settings.CombinationProblem is { Length: > 0 } problem)
            throw new InvalidOperationException(problem);

        DeviceInfo output;
        try
        {
            output = await _output.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SplitSideException("generator side", _settings.OutputLabel, ex);
        }

        DeviceInfo input;
        try
        {
            input = await _input.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Whichever half did open has to be let go of again, or a retry finds it claimed —
            // which on an exclusive-mode card or a libusb interface reads as hardware that needs a
            // power cycle.
            await SafeStopAsync(_output).ConfigureAwait(false);
            throw ex is OperationCanceledException
                ? ex
                : new SplitSideException("capture side", _settings.InputLabel, ex);
        }

        InputInfo = input;
        OutputInfo = output;

        // **A real capturing half's own generator is silenced, and never handed a voiced config.**
        // A QA403 used as the input still has a DAC, and a tone left configured by an earlier
        // session would drive whatever is wired to its output with nothing on screen naming it.
        // Swallowed rather than fatal, like the front-panel I2S stop at the QA403's own connect: a
        // device that cannot be silenced must not be one the app refuses to open. A *simulated*
        // capture half is the opposite case and is left alone -- see ConfigureGeneratorAsync, where
        // its generator config is the wire between the two halves.
        if (!input.IsSimulated)
        {
            try
            {
                await _input.ConfigureGeneratorAsync(GeneratorConfig.Off, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not silence the capture side's own generator.");
            }
        }

        _logger.LogInformation(
            "Split analyzer connected: {Output} generates at {GenRate}, {Input} captures. Two clocks — "
                + "tone measurements are unaffected; a chirp's phase, group delay and impulse response "
                + "need the clock-drift correction or a measured reference channel.",
            output.ProductName,
            GeneratorSampleRateHz is { } rate ? $"{rate / 1000.0:0.###} kHz" : "the analysis rate",
            input.ProductName);

        // Signal order — what plays, then what measures it.
        return new DeviceInfo(
            ProductName: $"{output.ProductName} → {input.ProductName}",
            SerialNumber: $"out {output.SerialNumber} / in {input.SerialNumber}",
            FirmwareVersion: $"out {output.FirmwareVersion} / in {input.FirmwareVersion}",
            // A half-simulated bench is not a simulation: this flag is what the status pill reads,
            // and real hardware is in the path.
            IsSimulated: input.IsSimulated && output.IsSimulated);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        // Both, in one throttled window — this is also the only place either side's telemetry is
        // refreshed. Named so a log reader knows which half went away.
        bool outputAlive = await _output.PingAsync(ct).ConfigureAwait(false);
        bool inputAlive = await _input.PingAsync(ct).ConfigureAwait(false);

        if (!outputAlive)
            _logger.LogWarning("The split's generator side ({Output}) stopped answering.", _settings.OutputLabel);
        if (!inputAlive)
            _logger.LogWarning("The split's capture side ({Input}) stopped answering.", _settings.InputLabel);

        return inputAlive && outputAlive;
    }

    /// <summary>
    /// The capturing half's blocks, with the generator side streaming beside them.
    ///
    /// <para><b>Three things here are decisions.</b> The generator side is started <i>first</i>, so
    /// the withheld window covers its settling transient rather than beginning after it. Its blocks
    /// are <b>drained and disposed</b> — a card running output-only yields none at all, but a QA403
    /// or the simulator used as the generator yields them, they carry pooled buffers, and for a
    /// QA403 that continuous reader is not bookkeeping: the device needs one on EP2 to keep the DAC
    /// clocking at all. And the teardown is in a <c>finally</c>, because consumers routinely break
    /// out of the loop and disposal runs finally handlers and nothing else.</para>
    /// </summary>
    public async IAsyncEnumerable<PcmBlock> StreamAsync(
        AcquisitionConfig config, [EnumeratorCancellation] CancellationToken ct)
    {
        double fsIn = config.SampleRateHz;
        _lastInputRateHz = (int)Math.Round(fsIn);
        AcquisitionConfig outputConfig = OutputConfigFor(config);

        _outputFaulted = false;

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task drain = Task.Run(() => DrainOutputAsync(outputConfig, drainCts.Token), CancellationToken.None);

        long withhold = ToInputFrames(
            _output.StreamStartSettleFrames(outputConfig.SampleRateHz), fsIn, outputConfig.SampleRateHz);
        long withheld = 0;

        try
        {
            await foreach (PcmBlock block in _input.StreamAsync(config, ct).ConfigureAwait(false))
            {
                // A generator that has died ends the acquisition cleanly rather than throwing into
                // the caller's await foreach, which is the interface's own contract: the loss is an
                // event, and the enumeration simply stops.
                if (_outputFaulted)
                {
                    block.Dispose();
                    break;
                }

                // Whole blocks: re-slicing one would mean a copy, and over-discarding by at most a
                // block is the safe side of a window that exists to keep a transient out of a
                // measurement.
                if (withheld < withhold)
                {
                    withheld += block.FrameCount;
                    block.Dispose();
                    continue;
                }

                yield return block;
            }
        }
        finally
        {
            drainCts.Cancel();
            try
            {
                await drain.WaitAsync(DrainJoinTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "The split's generator side ({Output}) did not stop streaming within {Timeout}.",
                    _settings.OutputLabel, DrainJoinTimeout);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async Task StopAsync()
    {
        await SafeStopAsync(_input).ConfigureAwait(false);
        await SafeStopAsync(_output).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _input.DeviceLost -= OnInputLost;
        _output.DeviceLost -= OnOutputLost;

        (_input as IDisposable)?.Dispose();
        (_output as IDisposable)?.Dispose();
    }

    /// <summary>The generator side's stream config: the same block size and channel count, its own rate.</summary>
    internal AcquisitionConfig OutputConfigFor(AcquisitionConfig input) =>
        GeneratorSampleRateHz is { } fsOut ? input with { SampleRateHz = fsOut } : input;

    private async Task DrainOutputAsync(AcquisitionConfig config, CancellationToken ct)
    {
        try
        {
            await foreach (PcmBlock block in _output.StreamAsync(config, ct).ConfigureAwait(false))
                block.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "The split's generator side ({Output}) stopped streaming; ending the acquisition.",
                _settings.OutputLabel);
            _outputFaulted = true;
            RaiseDeviceLost();
        }
    }

    /// <summary>
    /// A frame count on the generator's clock, expressed on the input's. Rounded <i>up</i>: every
    /// caller is booking a discard, and discarding a frame too many costs nothing where discarding
    /// one too few puts pre-change audio inside the record the discard exists to protect.
    /// </summary>
    private static int ToInputFrames(int outputFrames, double fsIn, double fsOut)
    {
        if (outputFrames <= 0 || fsOut <= 0 || fsIn <= 0 || Math.Abs(fsIn - fsOut) < 1e-9)
            return Math.Max(0, outputFrames);

        return (int)Math.Ceiling(outputFrames * fsIn / fsOut);
    }

    private static async Task SafeStopAsync(IAnalyzerDevice device)
    {
        try
        {
            await device.StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Teardown of a half that may already be gone; the caller is reporting a failure.
        }
    }

    private void OnInputLost(object? sender, EventArgs e) => RaiseDeviceLost();

    private void OnOutputLost(object? sender, EventArgs e)
    {
        _outputFaulted = true;
        RaiseDeviceLost();
    }

    /// <summary>At most once per connection — the session tears the whole bench down on the first one.</summary>
    private void RaiseDeviceLost()
    {
        if (Interlocked.Exchange(ref _deviceLostRaised, 1) == 0)
            DeviceLost?.Invoke(this, EventArgs.Empty);
    }
}
