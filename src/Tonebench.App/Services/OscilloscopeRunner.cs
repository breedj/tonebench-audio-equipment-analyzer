using Microsoft.Extensions.Logging;
using Tonebench.App.Streaming;
using Tonebench.Device;
using Tonebench.Dsp;

namespace Tonebench.App.Services;

/// <summary>
/// Runs the oscilloscope: takes the session, points the capture engine at the stream, and keeps
/// whatever it catches.
///
/// <para><b>It is a runner, and not a live screen like the FFT.</b> The FFT screen has none because
/// its result <i>is</i> the live display and its configuration is snapshotted off the device. A
/// scope's result is a record — with a trigger, bounds and readings — so a run has a beginning and
/// an end. It owns the session, the pipeline options and the input range while armed, so
/// <see cref="Endpoints.MeasurementRunGuard"/> must refuse other starts over it; and <b>Save reads
/// the server</b>, so the record has to be parked per measurement here rather than in the browser,
/// or two scope measurements sharing this one runner would save each other's waveform.</para>
///
/// <para>The polling loop is deliberately cheap and deliberately not an event: the engine commits
/// records on the device's own consumer thread, which must not be made to run a broadcast or a
/// save. <see cref="ScopeTriggerEngine.TakeRecord"/> hands each one over exactly once, so a poll
/// that misses a tick cannot draw the same capture twice and count it as two.</para>
/// </summary>
public sealed class OscilloscopeRunner(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    ScopeTriggerEngine engine,
    ILogger<OscilloscopeRunner> logger) : IMeasurementRun
{
    /// <summary>How often the run looks for a committed record. Fast enough to feel live, idle enough to cost nothing.</summary>
    private const int PollMs = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    /// <summary>
    /// The records of the measurements switched away from — see <see cref="SwitchOwner"/>. Four,
    /// the bound every other parking runner uses, and here it is also a memory budget: a one-second
    /// stereo record at 192 kS/s is about 4 MB as doubles.
    /// </summary>
    private readonly RunParking<ParkedRun> _parking = new(capacity: 4);

    private CancellationTokenSource? _cts;
    private Task? _task;

    private FrequencyResponsePhase _phase = FrequencyResponsePhase.Idle;
    private string? _error;
    private double _sampleRateHz;
    private int _recordFrames;
    private int _preTriggerFrames;
    private int _recordsTaken;
    private ScopeRecord? _record;
    private OscilloscopeRequest? _lastRequest;

    /// <summary>
    /// The mode the run loop reads, rather than the one the start request carried.
    ///
    /// <para><see cref="Retune"/> can change it under a running loop -- Normal to Single is an
    /// ordinary thing to do mid-capture -- and a loop reading its own captured argument would go on
    /// obeying the mode the user has just moved away from.</para>
    /// </summary>
    private ScopeRunMode _mode;

    /// <summary>
    /// What is done with each record after the first — null under
    /// <see cref="RecordAccumulation.Normal"/>, which is the ordinary case and costs nothing.
    ///
    /// <para><b>In the runner rather than in the engine</b>, and that is where it belongs: the
    /// engine's job is to decide <i>which samples</i> are a record, and this one's is what to do
    /// with the records once they exist. Keeping them apart is what lets the trigger stay a state
    /// machine over the stream while this stays arithmetic over equal-length arrays.</para>
    /// </summary>
    private RecordAccumulator? _accumulator;

    /// <summary>
    /// Armed or capturing. A pure function of the phase field and nothing else — the mechanical
    /// coverage test builds an uninitialized runner and sets that field by reflection, so anything
    /// here that touched another field (or took a lock) would throw rather than answer.
    /// </summary>
    public bool Running =>
        _phase is FrequencyResponsePhase.WaitingForTrigger or FrequencyResponsePhase.Measuring;

    /// <summary>The request this run was started with, for the save path to record what produced the waveform.</summary>
    public OscilloscopeRequest? LastRequest
    {
        get { lock (_stateLock) return _lastRequest; }
    }

    /// <summary>
    /// The record on hand, or null. Kept after the run so it can still be saved.
    ///
    /// <para><b>In Roll it is built here and now</b>, from the newest window the buffer holds — see
    /// <c>ScopeTriggerEngine.NewestWindow</c> for why a rolling display is served on demand rather
    /// than committed on a schedule. The cost is a copy of the record per read, so this is read
    /// once per fetch and <see cref="Status"/> deliberately does not read it at all. A run that has
    /// <i>stopped</i> rolling keeps whatever was last built, so the trace on screen does not vanish
    /// when the run ends.</para>
    /// </summary>
    public ScopeRecord? Record
    {
        get
        {
            lock (_stateLock)
            {
                if (_mode != ScopeRunMode.Roll || !Running)
                    return _record;
            }

            // Outside the lock: the copy is megabytes at a long record, and nothing else here may
            // wait on it. A null answer means the window is not whole yet, which keeps the last
            // one on hand rather than blanking the screen between the two.
            var window = engine.NewestWindow();
            if (window is null)
                return _record;

            lock (_stateLock)
            {
                _record = window;
                return window;
            }
        }
    }

    public OscilloscopeStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                var record = _record;
                var span = record?.Span;

                // Roll answers from arithmetic rather than from a record, because building one
                // costs a copy of the whole window and the status is polled several times a
                // second. The counter is what a client watches to know there is a newer window;
                // the span is the record's by construction -- it ends at "now".
                if (_mode == ScopeRunMode.Roll && Running)
                {
                    bool ready = engine.RollWindowReady;
                    double recordSeconds = _sampleRateHz > 0.0 ? _recordFrames / _sampleRateHz : 0.0;

                    return new OscilloscopeStatus(
                        _phase,
                        _error,
                        _sampleRateHz,
                        _recordFrames,
                        _preTriggerFrames,
                        (int)Math.Min(int.MaxValue, engine.RollWindowsElapsed),
                        HasRecord: ready || record is not null,
                        Spliced: false,
                        Forced: false,
                        TriggerOffsetS: ready ? recordSeconds : null,
                        RecordFromS: ready ? -recordSeconds : null,
                        RecordToS: ready ? 0.0 : null,
                        Rolled: true,
                        // Refused with Roll by Validate, so there is nothing to report.
                        Accumulation: nameof(RecordAccumulation.Normal),
                        AveragesTaken: ready ? 1 : 0,
                        AverageCount: 1);
                }

                return new OscilloscopeStatus(
                    _phase,
                    _error,
                    _sampleRateHz,
                    _recordFrames,
                    _preTriggerFrames,
                    _recordsTaken,
                    HasRecord: record is not null,
                    Spliced: record?.Spliced ?? false,
                    Forced: record?.Forced ?? false,
                    TriggerOffsetS: record?.TriggerOffsetS,
                    RecordFromS: span?.FromS,
                    RecordToS: span?.ToS,
                    Rolled: record?.Rolled ?? false,
                    Accumulation: (_accumulator?.Mode ?? RecordAccumulation.Normal).ToString(),
                    AveragesTaken: record?.AveragesTaken ?? 0,
                    AverageCount: _accumulator?.Count ?? 1);
            }
        }
    }

    public async Task<OscilloscopeStarted> StartAsync(OscilloscopeRequest request, CancellationToken ct)
    {
        request.Validate();

        if (!session.Connected)
            throw new InvalidOperationException("No device is connected.");

        // Synchronously with the caller, so a rate this analyzer does not have is a 409 on this
        // request rather than an exception on the task about to be spawned. After the Connected
        // check, because Capabilities is only valid once the device has connected.
        session.RequireSupportedSampleRate(request.SampleRateHz);

        int recordFrames = request.RecordFramesAt(request.SampleRateHz);
        int preTriggerFrames = request.PreTriggerFramesAt(request.SampleRateHz);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Running)
                throw new InvalidOperationException("An oscilloscope measurement is already running.");

            lock (_stateLock)
            {
                _phase = request.Mode == ScopeRunMode.Roll
                    ? FrequencyResponsePhase.Measuring
                    : FrequencyResponsePhase.WaitingForTrigger;
                _error = null;
                _sampleRateHz = request.SampleRateHz;
                _recordFrames = recordFrames;
                _preTriggerFrames = preTriggerFrames;
                _recordsTaken = 0;
                _record = null;
                _lastRequest = request;
                _mode = request.Mode;
            }

            _cts = new CancellationTokenSource();
            _task = Task.Run(() => RunAsync(request, recordFrames, preTriggerFrames, _cts.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }

        return new OscilloscopeStarted(request.SampleRateHz, recordFrames, preTriggerFrames);
    }

    /// <summary>
    /// Applies new settings to the capture already running, without stopping it.
    ///
    /// <para><b>A scope's controls take effect as they are turned.</b> Dragging the trigger line
    /// <i>is</i> how a level is set, so a level that needed a stop and a start would be a level
    /// nobody could find -- and once one control works that way, every control has to. That is the
    /// whole of why this exists beside <see cref="StartAsync"/>: a <c>/start</c> is refused by
    /// <see cref="Endpoints.MeasurementRunGuard"/> while this runner holds the session, which is
    /// right, and a retune is not a start.</para>
    ///
    /// <para><b>The sample rate is refused by name</b>, and it is the only one. Changing it
    /// re-clocks the analyzer -- on a QA403 that is 15 to 30 seconds of settling, and on a sound
    /// card a re-opened stream -- so it cannot be done to a capture in flight at all. Refusing it
    /// here, rather than silently keeping the old rate, is what lets the screen answer the refusal
    /// by restarting the run instead of showing the user an error they did not cause.</para>
    ///
    /// <para><b>The generator is deliberately not part of this.</b> It is not on the request that
    /// reaches here -- the endpoint refuses one -- because reconfiguring it books a stimulus-latency
    /// and capture-delay discard at <c>ConfigureGeneratorCoreAsync</c>, and a drag of the trigger
    /// line that threw away a record per pointer move would be worse than no live retune at all.
    /// The generator keeps its own apply path when the scope screen grows one.</para>
    /// </summary>
    public OscilloscopeStatus Retune(OscilloscopeRequest request)
    {
        request.Validate();

        lock (_stateLock)
        {
            if (!Running)
                throw new InvalidOperationException("The oscilloscope is not running, so there is nothing to retune.");

            // Compared rather than taken, because the rate decides the frame counts below and a
            // record sized at one rate and captured at another is a record of neither.
            if (Math.Abs(request.SampleRateHz - _sampleRateHz) > 0.5)
            {
                throw new InvalidOperationException(
                    "The sample rate cannot be changed while a capture is running: it re-clocks the analyzer. "
                        + "Stop the run and start it again.");
            }

            int recordFrames = request.RecordFramesAt(request.SampleRateHz);
            int preTriggerFrames = request.PreTriggerFramesAt(request.SampleRateHz);

            // The engine validates and builds the scanner on this thread and applies both on the
            // stream's, so a trigger it cannot honour is this request's error -- see Retune there.
            engine.Retune(BuildSpec(request, recordFrames, preTriggerFrames));

            // Which channels the pipeline analyses is the ranger's business rather than the
            // capture's: the buffer is fed whole blocks either way. It still has to follow, or
            // MaybeStepInputRangeAsync goes on ranging on a channel the user has stopped reading.
            pipeline.Options = pipeline.Options with
            {
                ProcessLeft = request.ProcessLeft,
                ProcessRight = request.ProcessRight,
            };

            // Anything that changes what is being combined starts the accumulation again rather
            // than mixing two measurements into one trace. The record length is in the list for
            // the same reason the accumulator checks the shape itself: position i of a 20 ms
            // record is not position i of a 50 ms one.
            bool accumulationMoved =
                (_accumulator?.Mode ?? RecordAccumulation.Normal) != request.Accumulation
                || (_accumulator is { } current && request.Accumulation != RecordAccumulation.Normal && current.Count != Math.Clamp(request.AverageCount, RecordAccumulator.MinCount, RecordAccumulator.MaxCount))
                || _recordFrames != recordFrames
                || _preTriggerFrames != preTriggerFrames;

            if (accumulationMoved)
            {
                _accumulator = request.Accumulation == RecordAccumulation.Normal
                    ? null
                    : new RecordAccumulator(request.Accumulation, request.AverageCount);
            }

            _recordFrames = recordFrames;
            _preTriggerFrames = preTriggerFrames;
            _mode = request.Mode;

            // The running generator is kept: it is what is actually playing, and the save path
            // reads this request to say what produced the waveform.
            _lastRequest = request with { Generator = _lastRequest?.Generator };

            logger.LogDebug(
                "Oscilloscope retuned: {Mode}, {RecordMs:0.###} ms ({RecordFrames} frames, {PreTriggerFrames} pre-trigger), "
                    + "{Slope} edge at {LevelV:0.######} V, hysteresis {HysteresisV:0.######} V, holdoff {HoldoffMs:0.##} ms, "
                    + "channels L={Left} R={Right}",
                request.Mode, request.RecordSeconds * 1e3, recordFrames, preTriggerFrames,
                request.TriggerSlope, request.TriggerLevelV, request.TriggerHysteresisV,
                request.HoldoffSeconds * 1e3, request.ProcessLeft, request.ProcessRight);

            return Status;
        }
    }

    public async Task CancelAsync()
    {
        var cts = _cts;
        var task = _task;
        if (cts is null || task is null)
            return;

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    /// <summary>
    /// Throws away the record on hand. A deliberate "forget this", so a switch away and back must
    /// not bring it back — which is what <see cref="RunParking{TState}.Switch"/> does with a null
    /// current.
    /// </summary>
    public void Clear()
    {
        if (Running)
            throw new InvalidOperationException("Stop the oscilloscope before clearing it.");

        lock (_stateLock)
        {
            _record = null;
            _recordsTaken = 0;
            _phase = FrequencyResponsePhase.Idle;
            _error = null;
            // So the next run starts its average or its envelope from nothing, rather than from a
            // band swept before the user said to forget what was on screen.
            _accumulator = null;
        }
    }

    /// <summary>
    /// Points the runner at the measurement now on screen, following the same six rules as every
    /// other parking runner. The request is parked with the record because the save path reads it
    /// beside the samples: a restored capture that cannot say what produced it is half a result.
    /// </summary>
    public OscilloscopeStatus SwitchOwner(string? owner)
    {
        lock (_stateLock)
        {
            if (Running)
                return Status;

            var current = _record is null
                ? null
                : new ParkedRun(_phase, _error, _sampleRateHz, _recordFrames, _preTriggerFrames, _recordsTaken, _record, _lastRequest);

            var outcome = _parking.Switch(owner, current);
            if (!outcome.Changed)
                return Status;

            if (outcome.Adopted is { } parked)
            {
                _phase = parked.Phase;
                _error = parked.Error;
                _sampleRateHz = parked.SampleRateHz;
                _recordFrames = parked.RecordFrames;
                _preTriggerFrames = parked.PreTriggerFrames;
                _recordsTaken = parked.RecordsTaken;
                _record = parked.Record;
                _lastRequest = parked.LastRequest;
            }
            else if (outcome.Cleared)
            {
                ResetLiveState();
            }

            return Status;
        }
    }

    /// <summary>The measurement the record on hand belongs to, or null.</summary>
    public string? Owner
    {
        get { lock (_stateLock) return _parking.Owner; }
    }

    /// <summary>
    /// Drops everything held for <paramref name="owner"/>, because that measurement has stopped
    /// existing. A run still going is left alone, owner and all — nothing is parked under the live
    /// owner, so there is nothing to drop.
    /// </summary>
    public void ForgetOwner(string owner)
    {
        lock (_stateLock)
        {
            if (Running && _parking.Owner == owner)
                return;

            if (_parking.Forget(owner))
                ResetLiveState();
        }
    }

    /// <summary>Empties the live slot. A little more than <see cref="Clear"/>, so a measurement that has never run reads back nothing.</summary>
    private void ResetLiveState()
    {
        _record = null;
        _lastRequest = null;
        _phase = FrequencyResponsePhase.Idle;
        _error = null;
        _sampleRateHz = 0.0;
        _recordFrames = 0;
        _preTriggerFrames = 0;
        _recordsTaken = 0;
        _mode = ScopeRunMode.Single;
        _accumulator = null;
    }

    private async Task RunAsync(OscilloscopeRequest request, int recordFrames, int preTriggerFrames, CancellationToken ct)
    {
        var savedOptions = pipeline.Options;
        bool startedAcquisition = false;

        var terminalPhase = FrequencyResponsePhase.Failed;
        string? terminalError = null;

        try
        {
            logger.LogInformation(
                "Oscilloscope starting: {Mode}, {RecordMs:0.###} ms ({RecordFrames} frames, {PreTriggerFrames} pre-trigger) "
                    + "at {SampleRateHz:0} Hz, {Slope} edge at {LevelV:0.####} V, hysteresis {HysteresisV:0.####} V, "
                    + "holdoff {HoldoffMs:0.##} ms, channels L={Left} R={Right}",
                request.Mode, request.RecordSeconds * 1e3, recordFrames, preTriggerFrames, request.SampleRateHz,
                request.TriggerSlope, request.TriggerLevelV, request.TriggerHysteresisV,
                request.HoldoffSeconds * 1e3, request.ProcessLeft, request.ProcessRight);

            // SingleShot for the documented split: DisplayHopFloor stays off a measuring run, and
            // SessionInputRanger suppresses step-down while step-up stays live as the clip backstop.
            // The FFT size is the scope's own small one -- see OscilloscopeRequest.OscilloscopeFftSize
            // for why the pipeline is kept fed rather than switched off -- and it is handed back in
            // the finally, including on a cancelled or refused run.
            pipeline.Options = savedOptions with
            {
                FftSize = OscilloscopeRequest.OscilloscopeFftSize,
                SingleShot = true,
                Averaging = AveragingMode.Off,
                AverageCount = 1,
                ProcessLeft = request.ProcessLeft,
                ProcessRight = request.ProcessRight,
            };

            if (request.Generator is { } generator)
                await session.ConfigureGeneratorAsync(generator, ct, new AcquisitionConfig(request.SampleRateHz, 2));

            if (!session.Acquiring)
            {
                await session.StartAcquisitionAsync(new AcquisitionConfig(request.SampleRateHz, 2), ct);
                startedAcquisition = true;
            }

            // The two counters summed, because only their movement across a record is read. They
            // stay two numbers everywhere they are reported, since one combined figure cannot say
            // which half of the chain fell behind — here the question is only "did anything".
            // Only where a stimulus was actually configured: with no generator in the request
            // nothing about what is being measured has changed, and a settle would cost a few
            // hundred milliseconds before the first trace for nothing. See
            // DeviceSessionService.StimulusSettleFrames for what this is and why the scope has to
            // book it for itself.
            long settleFrames = request.Generator is not null ? session.StimulusSettleFrames : 0;

            lock (_stateLock)
            {
                _accumulator = request.Accumulation == RecordAccumulation.Normal
                    ? null
                    : new RecordAccumulator(request.Accumulation, request.AverageCount);
            }

            engine.Arm(
                BuildSpec(request, recordFrames, preTriggerFrames, settleFrames),
                session.DroppedRxChunks + session.DroppedBlocks);

            while (!ct.IsCancellationRequested)
            {
                if (engine.TakeRecord() is { } record)
                {
                    bool single;
                    lock (_stateLock)
                    {
                        // Folded before it is published, so what the screen holds is what the mode
                        // asked for -- and a spliced record is left out rather than averaged in:
                        // two non-adjacent pieces of the stream are not the same point on the
                        // waveform as everything already in the accumulator, which is the one
                        // assumption the whole accumulation rests on.
                        if (_accumulator is { } accumulator && !record.Spliced)
                        {
                            var (channels, lower) = accumulator.Fold(record.Channels);
                            record = record with
                            {
                                Channels = channels,
                                Lower = lower,
                                AveragesTaken = accumulator.Taken,
                            };
                        }

                        _record = record;
                        _recordsTaken++;

                        // The field, not the request: a retune may have moved the mode since.
                        single = _mode == ScopeRunMode.Single;

                        if (record.Spliced)
                        {
                            logger.LogWarning(
                                "Oscilloscope record {Index} spans a dropped chunk or block and is flagged spliced",
                                _recordsTaken);
                        }
                    }

                    if (single)
                    {
                        terminalPhase = FrequencyResponsePhase.Finished;
                        terminalError = null;
                        return;
                    }
                }

                // Mirror the engine's own state so the screen can tell "waiting for a trigger" from
                // "capturing" -- a level nothing reaches is a setting to fix, and a fill that never
                // completes is a fault.
                var engineState = engine.State;
                lock (_stateLock)
                {
                    _phase = engineState switch
                    {
                        ScopeEngineState.Waiting => FrequencyResponsePhase.WaitingForTrigger,
                        ScopeEngineState.Filling => FrequencyResponsePhase.Measuring,
                        _ => _phase,
                    };
                }

                await Task.Delay(PollMs, ct).ConfigureAwait(false);
            }

            terminalPhase = FrequencyResponsePhase.Cancelled;
            terminalError = null;
        }
        catch (OperationCanceledException)
        {
            terminalPhase = FrequencyResponsePhase.Cancelled;
            terminalError = null;
        }
        catch (Exception ex)
        {
            terminalPhase = FrequencyResponsePhase.Failed;
            terminalError = ex.Message;
            logger.LogError(ex, "Oscilloscope run failed");
        }
        finally
        {
            engine.Disarm();

            if (startedAcquisition)
            {
                try
                {
                    await session.StopAcquisitionAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Stopping the acquisition after the oscilloscope run failed");
                }
            }

            // Handed back whatever happened, the SoundCardCalibrationRunner lesson: a refused or
            // cancelled run that left the screen's record length behind is a setting the user never
            // chose and cannot see.
            pipeline.Options = savedOptions;

            lock (_stateLock)
            {
                _phase = terminalPhase;
                _error = terminalError;
            }
        }
    }

    private static ScopeCaptureSpec BuildSpec(
        OscilloscopeRequest request, int recordFrames, int preTriggerFrames, long settleFrames = 0)
    {
        var trigger = new TriggerSpec(
            request.TriggerKind,
            request.TriggerSource,
            request.TriggerSlope,
            request.TriggerLevelV,
            request.TriggerHysteresisV,
            HoldoffFrames: (int)Math.Round(request.HoldoffSeconds * request.SampleRateHz));

        return new ScopeCaptureSpec(
            trigger,
            request.TriggerChannelIndex,
            recordFrames,
            preTriggerFrames,
            request.Mode,
            request.SampleRateHz,
            AutoTimeoutFrames: (long)Math.Round(request.AutoTimeoutSeconds * request.SampleRateHz),

            // A whole record after a break, because the 24 dB attenuator release leaves about a
            // second of samples reading exactly 24 dB low with only the first block flagged. On a
            // device that does not range itself there is nothing to wait out.
            DiscontinuityHoldoffFrames: recordFrames,

            // Zero on a retune, which is what the default is for: a retune changes no stimulus.
            SettleFrames: settleFrames,

            // How often Roll offers a newer window. A time converted once, here, against the rate
            // the run is streaming at.
            RollStepFrames: request.RollStepFramesAt(request.SampleRateHz));
    }

    private sealed record ParkedRun(
        FrequencyResponsePhase Phase, string? Error, double SampleRateHz, int RecordFrames,
        int PreTriggerFrames, int RecordsTaken, ScopeRecord Record, OscilloscopeRequest? LastRequest);
}
