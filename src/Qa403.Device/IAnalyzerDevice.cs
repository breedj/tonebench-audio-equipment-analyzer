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

    /// <summary>
    /// Whether the device is still reachable, checked cheaply enough to run on a UI status poll.
    /// Returns false once it is gone, having raised <see cref="DeviceLost"/> and released whatever
    /// the dead handle still held.
    ///
    /// <para>Exists because nothing else notices an unplug while the app is idle: every other
    /// operation is user-driven, so without a ping the status bar kept claiming "connected" until
    /// something was pressed, and then failed from deep inside a register write. Implementations
    /// that are already watching the device continuously (a running stream) should answer true
    /// without adding traffic of their own.</para>
    ///
    /// <para>Also where <see cref="Telemetry"/> is refreshed, on implementations that have any —
    /// this is the one exchange that already runs on an idle device at a throttled rate.</para>
    /// </summary>
    Task<bool> PingAsync(CancellationToken ct);

    /// <summary>Sample rates and gain ranges the device supports. Valid only after <see cref="ConnectAsync"/> has completed.</summary>
    DeviceCapabilities Capabilities { get; }

    /// <summary>
    /// The analyzer's own housekeeping sensors (USB rail, temperature), or null on a device that
    /// has none — which is the default, so only real hardware implements this.
    ///
    /// <para>A cached <i>last reading</i>, never a live read: taking one costs three register
    /// exchanges on the control endpoint, and a register exchange with data transfers in flight is
    /// the documented way to wedge this device's USB stack. So it is refreshed exactly where the
    /// liveness check already runs — inside <see cref="PingAsync"/>, which is throttled and skipped
    /// while a stream is running — and a reading therefore goes stale for the length of an
    /// acquisition. Callers display it; nothing measures with it.</para>
    /// </summary>
    DeviceTelemetry? Telemetry => null;

    /// <summary>
    /// Whether this device steps its own input range while streaming. False on anything whose range
    /// is chosen for it: the simulator, whose range the browser's own peak watcher steps, and any
    /// device with a single fixed range.
    ///
    /// <para>This exists so <see cref="Qa403.App"/>'s session can stop asking a device-shaped
    /// question by naming one device — three properties there decided "no hardware ranger" with
    /// <c>_device is SimulatedDevice</c>, and a third analyzer falls on the wrong side of all
    /// three.</para>
    ///
    /// <para><b>The default is true, and that is load-bearing.</b> The app's test doubles wrap a
    /// <see cref="Simulation.SimulatedDevice"/> while carrying their own
    /// <see cref="AutoRangeEnabled"/> = true — they stand in for hardware with a ranger, and they
    /// are not <c>SimulatedDevice</c>, so they already bypassed those type tests. Inheriting this
    /// default keeps them doing exactly that. A device that genuinely has no ranger says so
    /// explicitly, the same way <see cref="Telemetry"/> works the other way round.</para>
    /// </summary>
    bool HasInputAutoRanging => true;

    /// <summary>
    /// The lowest frequency this analyzer can actually excite, in Hz. 0 means no floor, which is the
    /// default and is the QA403's answer — it is DC-coupled, so the 2 Hz chirp starts used for
    /// transformer work all reach the DUT.
    ///
    /// <para>A sound card is AC-coupled at <b>both</b> ends, and below its corner nothing fails: the
    /// deconvolution divides by the stimulus it meant to play, so the card's own high-pass lands on
    /// the trace as the DUT's roll-off with normal coherence. A DC-coupled front-end fixes only the
    /// input side, because the card's output coupling still limits the stimulus — which is why this
    /// belongs to the device rather than to the wiring.</para>
    /// </summary>
    double MinStimulusFrequencyHz => 0.0;

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
    /// How long, in milliseconds, a peak must stay continuously under-range before autoranging
    /// steps to a more sensitive range. Settable while connected; ignored (but harmlessly stored)
    /// on devices with no autoranging of their own, e.g. the simulator. See
    /// <see cref="Hardware.Qa40xAutoRanger.StepDownDelayMs"/> for why this is time-based.
    /// </summary>
    double AutoRangeStepDownDelayMs { get; set; }

    /// <summary>
    /// Whether autoranging may step to a *more sensitive* range on its own. Clearing this while
    /// <see cref="AutoRangeEnabled"/> stays set leaves clip protection live but makes an explicit
    /// <see cref="SetInputRangeAsync"/> stick — what a caller that predicts the range itself needs,
    /// since with step-down live any range set above the signal is walked back down. Settable while
    /// connected; ignored (but harmlessly stored) on devices with no autoranging of their own.
    /// See <see cref="Hardware.Qa40xAutoRanger.StepDownEnabled"/>.
    /// </summary>
    bool AutoRangeStepDownEnabled { get; set; }

    /// <summary>
    /// RX chunks discarded because the consumer could not keep up, <b>for the current acquisition</b>
    /// — the count starts again with every stream. Not a cosmetic statistic: a dropped chunk is a
    /// hole in the sample stream, so the next FFT record is spliced — which smears tones into skirts
    /// and lifts the broadband noise floor. Any non-zero value means measured noise numbers are
    /// suspect. Always 0 for the simulator.
    ///
    /// <para>Per-acquisition rather than cumulative because that is the only question these numbers
    /// are ever asked: whether the measurement on screen can be trusted. A running total cannot say —
    /// a stale count from an earlier run is indistinguishable from one that just happened, and the
    /// only way to tell was to note the number, run again, and subtract.</para>
    /// </summary>
    long DroppedRxChunks { get; }

    /// <summary>
    /// Assembled PCM blocks discarded because whoever consumes <see cref="StreamAsync"/> could not
    /// keep up with calibration, <b>for the current acquisition</b> — reset with every stream, on the
    /// same terms as <see cref="DroppedRxChunks"/>. Always 0 for the simulator.
    ///
    /// <para>Deliberately separate from <see cref="DroppedRxChunks"/> rather than summed into it:
    /// the two name different halves of the pipeline. RX chunks drop when the *assembler* fell
    /// behind the transfer pump; blocks drop when the *consumer* fell behind the assembler. One
    /// combined number says "something fell behind" and leaves the next person to guess which.</para>
    ///
    /// <para>Separate, but it must still be <i>reported</i>. This existed and was logged for weeks
    /// while only <see cref="DroppedRxChunks"/> reached the status endpoint, so the UI and the API
    /// both said 0 through runs that dropped 14, 74 and even 439 blocks — 439 being 1.8 million
    /// frames, over 9 seconds of audio missing from a single run. A hole this size splices every FFT
    /// record spanning it, and two separate investigations were sent down the wrong path by a status
    /// field that read clean. A counter that is not surfaced is not instrumentation.</para>
    /// </summary>
    long DroppedBlocks { get; }

    /// <summary>
    /// Frames of audio that are already committed to the hardware when a stimulus change
    /// (<see cref="ConfigureGeneratorAsync"/>, <see cref="SetInputRangeAsync"/>,
    /// <see cref="SetOutputRangeAsync"/>) returns, and which therefore still carry the *old*
    /// stimulus when they arrive on <see cref="StreamAsync"/>.
    ///
    /// <para>Anything that empties a buffer in response to such a change has to discard this many
    /// frames as well, or it re-fills with pre-change audio and the discontinuity ends up inside the
    /// very record the flush existed to protect. Measured on a QA403 at 48/96/192 kHz: 3200/6272/12416
    /// frames, i.e. the DAC's committed-but-unplayed TX queue and nothing else (~65 ms at every rate,
    /// by design — see <c>Qa40xUsbDevice.TargetInFlightSeconds</c>).</para>
    ///
    /// <para>One block for the simulator: it synthesizes each block from the configuration as it
    /// stood when the block started, so the block in progress when a change returns is still the
    /// old stimulus. It claimed zero until 2026-09-11, and a THD sweep whose records were one block
    /// long read the previous point's tone about one time in five.</para>
    /// </summary>
    int StimulusLatencyFrames(double sampleRateHz);

    /// <summary>
    /// How many frames to throw away after a stream has been <b>opened</b>, before anything is
    /// analysed — the device's own settling, as distinct from the DUT's (the capture delay) and
    /// from audio already committed to the DAC (<see cref="StimulusLatencyFrames"/>).
    ///
    /// <para><b>Zero by default, and that is right for everything with a fixed clock.</b> A QA403
    /// is running at its rate the moment the interface is claimed. A sound card is not: an ASIO
    /// driver re-clocks the hardware for a rate it is not already running at, and it is audibly
    /// unsettled afterwards — a card shared with Windows playback takes that playback down with it.
    /// Measured on a MOTU M4 (2026-10-01), the first records after an open read THD 45–90 % before
    /// settling at 0.0004 %.</para>
    ///
    /// <para>Only on an <i>open</i>: a start that reuses a running stream has nothing to settle, and
    /// paying this on every RUN would be two seconds of nothing for no reason.</para>
    /// </summary>
    int StreamStartSettleFrames(double sampleRateHz) => 0;

    /// <summary>
    /// The highest sample rate at which this analyzer's <i>generator</i> may be driven, when that is
    /// lower than the highest rate it can <i>capture</i> at. <see cref="double.PositiveInfinity"/>
    /// means no such limit, which is true of every implementation but the QA403.
    ///
    /// <para>A default member, like <see cref="Telemetry"/> and
    /// <see cref="StreamStartSettleFrames"/>, so that only the one implementation with the
    /// limitation has to mention it and the test doubles need no edit.</para>
    ///
    /// <para><b>Why this exists.</b> The QA403 captures correctly at 384 kHz and emits garbage from
    /// its DAC there — see <see cref="DeviceCapabilities.Qa403Experimental"/> for the measurements.
    /// That garbage carries enough near-DC content to cycle the device's own output protection relay,
    /// and a <c>StreamingStop</c> did not clear it, so the limit is a hardware-protection rule rather
    /// than a measurement-quality one. A caller must refuse to configure a live generator above this
    /// rate; it is deliberately not silently clamped, because quietly playing a different stimulus
    /// than the one asked for is its own class of wrong answer.</para>
    /// </summary>
    double MaxGeneratorSampleRateHz => double.PositiveInfinity;

    /// <summary>
    /// Where this device copies the samples it hands its DAC, or null -- the default -- for no
    /// copying at all. Set by the session while the stream recorder is armed, cleared when it is
    /// not; see <see cref="IStimulusTap"/> for what the recording can and cannot answer, and for why
    /// an implementation may do nothing but copy the span.
    ///
    /// <para>A default member with an inert setter, the way <see cref="Telemetry"/> is a default
    /// getter: a device that synthesizes nothing (and every test double that wraps one) needs no
    /// edit, and a device that does implements the property for real. Assigning to one that has not
    /// is silently a no-op, which is the correct behaviour for a diagnostic -- a recording of a
    /// device that cannot be recorded is an empty output file, not a failed acquisition.</para>
    /// </summary>
    IStimulusTap? StimulusTap
    {
        get => null;
        set { }
    }

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
