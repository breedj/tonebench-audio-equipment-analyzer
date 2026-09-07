// Hand-written client for the endpoints in docs/qa403-app-stack-spec.md §4.1.
// Wire shapes must stay in sync with Qa403.App/Endpoints/*.cs by hand — no codegen.

export interface DeviceStatus {
  connected: boolean
  productName: string
  serialNumber: string
  firmwareVersion: string
  isSimulated: boolean
  inputRangeDbv: number
  outputRangeDbv: number
  acquiring: boolean
  generatorLeft: GeneratorChannelState
  generatorRight: GeneratorChannelState
  /** Simulated DUT noise floor on/off; null when the device isn't the simulator. */
  simulatedNoiseEnabled: boolean | null
  /** Simulated DUT mains hum on/off; same null convention. */
  simulatedHumEnabled: boolean | null
  /**
   * Which simulated device under test is patched in; null when the device isn't the simulator.
   * Unlike the two switches above this changes the DUT's response rather than what is added to it.
   */
  simulatedDut: SimulatedDutModel | null
  /**
   * Which analyzer the session is talking to. Separate from `isSimulated`, which describes a
   * *connected* device and so reads false while the simulator is selected but not yet up.
   */
  deviceKind: DeviceKindName
  /** Input autoranging on/off; null when the device has no autoranging (the simulator). */
  autoRangeEnabled: boolean | null
  /**
   * How long, in ms, a peak must stay under-range before autoranging steps to a more sensitive
   * range. Same null-means-no-autoranging convention as autoRangeEnabled.
   */
  autoRangeStepDownDelayMs: number | null
  /** Whether generator tones are snapped to the nearest coherent FFT bin. generatorLeft/Right Tones are the snapped values. */
  generatorSnapToBin: boolean
  /**
   * True once a device the app *was* connected to went away — an unplugged cable, mid-session.
   * Comes with connected=false: the server releases the dead handle, so this is the reason for the
   * disconnect rather than a state of its own, and Connect brings the device back once it returns.
   */
  deviceLost: boolean
  /**
   * RX chunks dropped since connect because the consumer fell behind. Each one is a hole in the
   * sample stream, so the FFT records around it are spliced — non-zero means noise-floor readings
   * are suspect, not just that a display frame was skipped.
   */
  droppedRxChunks: number
  droppedBlocks: number
}

/**
 * What the simulator pretends is patched between its generator and its input. A straight wire has
 * no response and no memory, which is enough for a spectrum and useless for anything whose subject
 * is what a DUT does over time — an impulse response, a gated magnitude, either decay surface.
 */
export type SimulatedDutModel =
  | 'StraightWire'
  | 'TubeAmplifier'
  | 'Loudspeaker'
  | 'BandpassFilter'
  | 'MarginallyStable'
  | 'Oscillating'

/** Real QA403 over USB, or the synthetic device. Switchable at runtime — no restart, no flag. */
export type DeviceKindName = 'Hardware' | 'Simulated'

/** Patch for the simulated DUT's signal profile — omitted fields are left alone. */
export interface SimulatedProfileRequest {
  noiseEnabled?: boolean
  humEnabled?: boolean
  dut?: SimulatedDutModel
}

/** Mirrors Qa403.Device.GeneratorKind — exactly one stimulus is active per channel. */
export type GeneratorKind = 'Off' | 'Tones' | 'Multitone' | 'WhiteNoise' | 'EchoChirp'

/** Mirrors Qa403.Device.SmoothingOctaves. Stored and round-tripped only; the FR measurement that consumes it is a later phase. */
export type SmoothingOctaves =
  | 'None'
  | 'OneOver96'
  | 'OneOver48'
  | 'OneOver24'
  | 'OneOver12'
  | 'OneOver6'
  | 'OneOver3'

export interface MultitoneSettings {
  tonesPerOctave: number
  /** Combined RMS of the comb, dBV — exact only at 0 dB/octave rolloff. */
  totalRmsDbv: number
  rolloffDbPerOctave: number
}

export interface WhiteNoiseSettings {
  /** Broadband RMS, dBV. */
  levelDbv: number
}

export interface EchoChirpSettings {
  amplitudeDbv: number
  startFreqHz: number
  endFreqHz: number
  smoothing: SmoothingOctaves
}

export interface GeneratorChannelState {
  enabled: boolean
  /** The effective tone list — for kind 'Multitone' this is the derived comb, not the parameters. */
  tones: GeneratorTone[]
  kind: GeneratorKind
  multitone: MultitoneSettings
  noise: WhiteNoiseSettings
  chirp: EchoChirpSettings
}


// ---------------------------------------------------------------------------
// Frequency response (chirp)
// ---------------------------------------------------------------------------

/** Mirrors Qa403.App.Services.FrequencyResponseTraceKind. */
export type ResponseTraceKindName = 'Left' | 'Right' | 'Gain'

/**
 * Mirrors Qa403.App.Services.ResponsePhaseKind — what phase a run measured, and so what the UI may
 * draw and what it should call it. There is only one phase quantity in these measurements,
 * `arg(L/R)`; what changes is whether it exists and what it means.
 */
export type ResponsePhaseKindName = 'None' | 'PerTrace' | 'DutResponse' | 'ChannelDifference'

/** Mirrors Qa403.App.Services.FrequencyResponsePhase. */
export type ResponsePhaseName =
  | 'Idle'
  | 'DeterminingLevel'
  | 'DeterminingRange'
  | 'Measuring'
  /** The stepped-sine Bode plot's counterpart of Measuring. */
  | 'Sweeping'
  | 'Finished'
  | 'Cancelled'
  | 'Failed'

/** The three spans the UI offers, in Hz. These are Nyquist values — the sample rate is twice each. */
export const RESPONSE_SPANS_HZ = [24000, 48000, 96000] as const

/**
 * FFT-size multipliers and the bin width each produces. The base size scales with the sample rate,
 * so the same three resolutions are available at every span.
 */
export const RESPONSE_RESOLUTIONS: { multiplier: number; label: string }[] = [
  { multiplier: 1, label: '1.46' },
  { multiplier: 2, label: '0.73' },
  { multiplier: 4, label: '0.37' },
]

/** Smoothing widths, as the N in 1/N octave. 0 is none; 69 is the narrowest and 3 the widest. */
export const RESPONSE_SMOOTHING: { denominator: number; label: string }[] = [
  { denominator: 0, label: 'None' },
  { denominator: 69, label: '1/69 (min)' },
  { denominator: 48, label: '1/48' },
  { denominator: 24, label: '1/24' },
  { denominator: 12, label: '1/12' },
  { denominator: 6, label: '1/6' },
  { denominator: 3, label: '1/3 (max)' },
]

export interface FrequencyResponseStartRequest {
  frequencySpanHz: number
  resolutionMultiplier: number
  /** GeneratorVoltage is the UI's "Input voltage" — the level fed to the DUT. */
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  /** The load a `Watt` level is defined against. Only read by the OutputPower mode; V = sqrt(P·R). */
  loadOhms: number
  rightChannelIsReference: boolean
  smoothingDenominator: number
  averageCount: number
  /**
   * Where the sweep starts, in Hz. Below ~10 Hz a transformer-coupled DUT sees several times the
   * core flux it does in the band anyone reads (flux goes as V/f), so the bottom of the sweep is not
   * free — raising this is how you find out whether it is disturbing the measurement above it.
   */
  chirpStartHz: number
  /** RUN sets this and keeps measuring until stopped; SINGLE leaves it false. */
  continuous: boolean
}

/**
 * A frequency-response measurement type's stored settings. The `chart*` fields are display state
 * and all optional, so a type saved before one of them existed still loads.
 */
export interface FrequencyResponseParameters {
  frequencySpanHz: number
  resolutionMultiplier: number
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  rightChannelIsReference: boolean
  smoothingDenominator: number
  averageCount: number
  /** Absent on a type saved before the chirp start was configurable. */
  chirpStartHz?: number
  /** The load an OutputPower level was defined against. Absent on a type saved before that mode existed. */
  loadOhms?: number | null
  generatorAmpDbv?: number | null
  chartView?: ResponseView | null
  chartShowLeft?: boolean | null
  chartShowRight?: boolean | null
  chartShowPhase?: boolean | null
  chartShowCoherence?: boolean | null
  chartShowOneDb?: boolean | null
  chartShowThreeDb?: boolean | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  chartYTopDb?: number | null
  chartYBottomDb?: number | null
  chartXFromHz?: number | null
  chartXToHz?: number | null
  /** Absent on a type saved before the phase axis was independently rangeable; the client falls back to ±180°. */
  chartPhaseTopDeg?: number | null
  chartPhaseBottomDeg?: number | null
  /** The FR (dBV) view's own y window — chartYTopDb/chartYBottomDb (despite the name) is the Gain (dB) view's window only. Absent on a type saved before the two views had separate ranges. */
  chartYTopDbv?: number | null
  chartYBottomDbv?: number | null
  /** Off unless the user opts in — see FrequencyResponseDisplayPanel.vue's "Autofit when measurement done" checkbox. */
  chartAutofitOnDone?: boolean | null
  /**
   * Which projection of the response is on screen. Absent on a type saved before the polar views
   * existed, where 'Bode' is the behaviour that build had.
   */
  chartProjection?: ResponseProjection | null
  chartNyquistRadius?: NyquistRadiusName | null
  chartNyquistRadiusFloorDb?: number | null
  chartInvertLoopSign?: boolean | null
  chartRestoreDelay?: boolean | null
}

/** Which y quantity the chart is showing — the old app's "FR (dBV)" / "Gain (dB)" buttons. */
export type ResponseView = 'Dbv' | 'Gain'

/**
 * Which projection of the response the chart draws.
 *
 * Deliberately separate from `ResponseView` rather than added to it: that type means *which y
 * quantity the Bode chart shows*, and everything branching on it carries that meaning. Widening it
 * would add a silent third case to each of those.
 */
export type ResponseProjection = 'Bode' | 'Nyquist' | 'Nichols'

/** How a Nyquist plot maps magnitude onto radius. Mirrors NyquistRadiusMode in response/projection.ts. */
export type NyquistRadiusName = 'Linear' | 'Db'

export interface FrequencyResponseStarted {
  sampleRateHz: number
  fftSize: number
  binHz: number
  averageCount: number
  recordSeconds: number
  continuous: boolean
}

/**
 * Starting a stepped-sine Bode plot. The stimulus half mirrors ThdSweepStartRequest — same grid,
 * same level modes — and `rightChannelIsReference` comes from the chirp measurement, because that
 * is what makes the gain trace a real transfer function and gives it a phase.
 *
 * The analysis preset (`fftSize`, `window`, `sampleRateHz`) is not offered in the panel and is only
 * here so the wire record can carry it: with `adaptiveResolution` on — the default, since there are
 * no harmonics to keep in band — the server picks both from the sweep's own range instead.
 */
export interface SteppedResponseStartRequest {
  startFreqHz: number
  endFreqHz: number
  stepsPerOctave: number
  /** GeneratorVoltage is the UI's "Input voltage" — the level fed to the DUT. */
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  /** The load a `Watt` level is defined against. Only read by the OutputPower mode; V = sqrt(P·R). */
  loadOhms: number
  /** Which generator channels play the tone. Both inputs are analyzed regardless — the gain trace is a ratio between them. */
  generateLeft: boolean
  generateRight: boolean
  averageCount: number
  rightChannelIsReference: boolean
  adaptiveResolution: boolean
}

export interface SteppedResponseStarted {
  pointCount: number
  frequenciesHz: number[]
  sampleRateHz: number
  averageCount: number
  /** What the run should take, in seconds. The level probe is not counted; it is a couple of short captures. */
  estimatedSeconds: number
}

/**
 * A stepped-sine response type's stored settings. Its own shape rather than more optional fields on
 * FrequencyResponseParameters, for the same reason the two sweep kinds have separate records: a
 * chirp is described by a span and a resolution multiplier, a stepped sweep by a frequency range and
 * a step density, and neither set means anything to the other. The `chart*` half is shared, and all
 * optional so a type saved before one of them existed still loads.
 */
export interface SteppedResponseParameters {
  startFreqHz: number
  endFreqHz: number
  stepsPerOctave: number
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  loadOhms: number
  generateLeft: boolean
  generateRight: boolean
  fftSize: number
  window: WindowType
  averageCount: number
  sampleRateHz: number
  adaptiveResolution: boolean
  rightChannelIsReference: boolean
  generatorAmpDbv?: number | null
  chartView?: ResponseView | null
  chartShowLeft?: boolean | null
  chartShowRight?: boolean | null
  chartShowPhase?: boolean | null
  chartShowCoherence?: boolean | null
  chartShowOneDb?: boolean | null
  chartShowThreeDb?: boolean | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  chartYTopDb?: number | null
  chartYBottomDb?: number | null
  chartXFromHz?: number | null
  chartXToHz?: number | null
  chartPhaseTopDeg?: number | null
  chartPhaseBottomDeg?: number | null
  /** The FR (dBV) view's own y window — see FrequencyResponseParameters.chartYTopDbv. */
  chartYTopDbv?: number | null
  chartYBottomDbv?: number | null
  /** Off unless the user opts in — see FrequencyResponseDisplayPanel.vue's "Autofit when measurement done" checkbox. */
  chartAutofitOnDone?: boolean | null
  /**
   * Which projection of the response is on screen. Absent on a type saved before the polar views
   * existed, where 'Bode' is the behaviour that build had.
   */
  chartProjection?: ResponseProjection | null
  chartNyquistRadius?: NyquistRadiusName | null
  chartNyquistRadiusFloorDb?: number | null
  chartInvertLoopSign?: boolean | null
  chartRestoreDelay?: boolean | null
}

/** A -1 dB or -3 dB reading. A clamped end means the curve never got there, not that the corner is at that frequency. */
export interface ResponseBandwidthJson {
  dropDb: number
  lowerHz: number
  upperHz: number
  bandwidthHz: number
  lowerClamped: boolean
  upperClamped: boolean
}

export interface FrequencyResponseTraceJson {
  kind: ResponseTraceKindName
  startFreqHz: number
  endFreqHz: number
  freqHz: number[]
  magnitudeMinDb: number[]
  magnitudeMaxDb: number[]
  phaseDeg: number[]
  coherence: number[]
  oneDb: ResponseBandwidthJson
  threeDb: ResponseBandwidthJson
  peakDb: number
  peakFreqHz: number
}

export interface FrequencyResponseStatus {
  running: boolean
  phase: ResponsePhaseName
  continuous: boolean
  passesCompleted: number
  averagesTaken: number
  averageCount: number
  generatorAmpDbv: number
  binHz: number
  sampleRateHz: number
  fftSize: number
  /** DUT propagation delay in samples, or null when the run had no measured reference. */
  latencySamples: number | null
  error: string | null
  traces: FrequencyResponseTraceJson[]
  /**
   * Points measured, and points to measure, for a stepped-sine run. Both 0 for a chirp, whose
   * progress is `averagesTaken` records — a stepped run has both an average count and a point count,
   * so neither can stand in for the other.
   */
  pointsMeasured: number
  pointCount: number
  /** Absent on a status from a build that predates the field, where 'PerTrace' is the behaviour that build had. */
  phaseKind?: ResponsePhaseKindName
  /** The right input was a yardstick rather than a measurement, so it is not offered as a trace. */
  rightChannelIsReference?: boolean
}

/** What an impulse-response run divides the captured response by. Mirrors `ImpulseReferenceMode`. */
export type ImpulseReferenceModeName = 'None' | 'RightChannel' | 'StoredLoopback'

/** Gate windows the impulse-response panel offers. Rectangular is a real choice: a bare cut isolates a reflection exactly. */
export const IMPULSE_GATE_WINDOWS: { value: WindowType; label: string }[] = [
  { value: 'Rectangular', label: 'Rectangular (bare cut)' },
  { value: 'Hann', label: 'Hann' },
  { value: 'BlackmanHarris', label: 'Blackman-Harris' },
]

export const IMPULSE_REFERENCE_MODES: { value: ImpulseReferenceModeName; label: string }[] = [
  { value: 'None', label: 'None (the generated sweep)' },
  { value: 'RightChannel', label: 'Right input channel' },
  { value: 'StoredLoopback', label: 'Stored loopback calibration' },
]

export interface ImpulseResponseStartRequest {
  frequencySpanHz: number
  resolutionMultiplier: number
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  smoothingDenominator: number
  averageCount: number
  referenceMode: ImpulseReferenceModeName
  referenceCalibrationId: number | null
  /** The *display* gate, in ms either side of the impulse — not the sweep's own anti-distortion gate. */
  gatePreMs: number
  gatePostMs: number
  gateWindow: WindowType
  extractHarmonics: boolean
  maxHarmonicOrder: number
  continuous: boolean
  chirpStartHz: number
  loadOhms: number
}

export interface ImpulseResponseStarted {
  sampleRateHz: number
  fftSize: number
  binHz: number
  averageCount: number
  recordSeconds: number
  continuous: boolean
  gateResolutionHz: number
}

/** One decimated impulse response. Time is relative to the impulse, so index 0 is `-gatePreMs`. */
export interface ImpulseTraceJson {
  kind: ResponseTraceKindName
  timeS: number[]
  minVolts: number[]
  maxVolts: number[]
  peakVolts: number
  sampleRateHz: number
  fullSampleCount: number
  gatePreMs: number
  gatePostMs: number
  /** 1 / gate length. Below this the gated magnitude describes the window, not the DUT. */
  resolutionLimitHz: number
}

/** One harmonic order's distortion against the *fundamental* that produced it. */
export interface HarmonicCurveJson {
  order: number
  fundamentalHz: number[]
  levelDbc: number[]
  /** Where this curve has to stop: the k-th harmonic is only observable up to the sweep's end over k. */
  upperValidHz: number
}

export interface ImpulseResponseStatus {
  running: boolean
  phase: ResponsePhaseName
  continuous: boolean
  passesCompleted: number
  averagesTaken: number
  averageCount: number
  generatorAmpDbv: number
  binHz: number
  sampleRateHz: number
  fftSize: number
  latencySamples: number | null
  error: string | null
  /** The gated magnitude and phase — the same shape a frequency response produces, drawn by the same chart. */
  traces: FrequencyResponseTraceJson[]
  impulses: ImpulseTraceJson[]
  harmonics: HarmonicCurveJson[]
  referenceMode: ImpulseReferenceModeName
  referenceCalibrationName: string | null
  gateResolutionHz: number
  gatePreMs: number
  gatePostMs: number
}

/** A stored loopback calibration, as the picker lists it. */
export interface LoopbackReferenceInfo {
  id: number
  name: string
  takenUtc: string
  sampleRateHz: number
  chirpStartHz: number
  chirpEndHz: number
  /** The analyzer's own round-trip delay, in samples. Informational — it is not reapplied. */
  removedDelaySamples: number
  irSamples: number
  deviceSerial: string | null
  isSimulated: boolean
}

export interface ImpulseReferenceCaptureRequest {
  name: string
  frequencySpanHz: number
  resolutionMultiplier: number
  generatorLevelDbv: number
  averageCount: number
  chirpStartHz: number
}

/**
 * An impulse-response type's stored settings. Its own shape rather than more optional fields on
 * `FrequencyResponseParameters`: the two share a stimulus but not a configuration, and the gate,
 * the reference mode and the harmonic settings mean nothing to a frequency response.
 */
export interface ImpulseResponseParameters {
  frequencySpanHz: number
  resolutionMultiplier: number
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  smoothingDenominator: number
  averageCount: number
  referenceMode: ImpulseReferenceModeName
  referenceCalibrationId?: number | null
  gatePreMs: number
  gatePostMs: number
  gateWindow: WindowType
  extractHarmonics: boolean
  maxHarmonicOrder: number
  generatorAmpDbv?: number | null
  chirpStartHz: number
  loadOhms: number
  chartView?: ImpulseView | null
  /** The impulse view drawn as dB below its own peak rather than volts. */
  chartNormalisedImpulse?: boolean | null
  chartShowPhase?: boolean | null
  chartShowCoherence?: boolean | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  /** The impulse view's own level window; the other two views keep their own, below. */
  chartYTopDb?: number | null
  chartYBottomDb?: number | null
  chartResponseYTopDb?: number | null
  chartResponseYBottomDb?: number | null
  chartHarmonicsYTopDb?: number | null
  chartHarmonicsYBottomDb?: number | null
  chartPhaseTopDeg?: number | null
  chartPhaseBottomDeg?: number | null
  chartTimeFromMs?: number | null
  chartTimeToMs?: number | null
  chartFreqFromHz?: number | null
  chartFreqToHz?: number | null
  /** How the two decay surfaces are drawn, and in what colours. Shared by both of them. */
  chartDecayMode?: DecayChartMode | null
  chartDecayPalette?: DecayPalette | null
  chartDecayColoredCurves?: boolean | null
  chartDecayRangeDb?: number | null
  /** The cumulative spectral decay's analysis. Display settings, but a surface is unreadable without them. */
  chartCsdFftLength?: number | null
  chartCsdBlockShiftSamples?: number | null
  chartCsdBlockCount?: number | null
  chartCsdWindowMode?: CsdWindowModeName | null
  chartCsdRiseTimeMs?: number | null
  /** The burst decay's, on the same footing. */
  chartBurstMaxPeriods?: number | null
  chartBurstPointsPerPeriod?: number | null
  chartBurstResolution?: BurstDecayResolutionName | null
}

/** Stacked curves in oblique projection, or the same surface as a flat colour map. */
/**
 * `Waterfall` is ARTA's Waterfall_F — frequency across the curves, the stack receding in time.
 * `WaterfallPeriods` is its Waterfall_P — time across, the stack receding in frequency. The wire
 * value of the first stays `Waterfall` so that stored measurements keep opening as they were saved.
 */
export type DecayChartMode = 'Waterfall' | 'WaterfallPeriods' | 'Sonogram'

export type DecayPalette = 'Jet' | 'Hot' | 'Grey'

/** Which of the things an impulse-response measurement can be read as is on screen. */
export type ImpulseView = 'Impulse' | 'Response' | 'Harmonics' | 'Csd' | 'BurstDecay'

export type DecayAxisName = 'Milliseconds' | 'Periods'
export type CsdWindowModeName = 'Apodizing' | 'Hann'
export type BurstDecayResolutionName = 'PreferTime' | 'PreferFrequency'

/**
 * One row of a decay surface. `levelDb` starts at `firstValidColumn`, not at column 0 — a burst
 * decay's deep slices run off the end of the record at the low-frequency end, and the alternative
 * to saying so is drawing the zeros the gate left behind as a perfect decay.
 */
export interface DecaySlice {
  /** Milliseconds or periods, per the surface's `sliceAxis`. */
  value: number
  firstValidColumn: number
  levelDb: number[]
}

/**
 * A time-frequency decay surface. 0 dB means different things in the two measurements: a
 * cumulative spectral decay is normalised once for the whole surface (so the front slice keeps the
 * DUT's frequency response), a burst decay per frequency (so it does not).
 */
export interface DecaySurface {
  sliceAxis: DecayAxisName
  freqHz: number[]
  slices: DecaySlice[]
  /** Below this the surface describes the analysis rather than the DUT. Zero when there is no single such limit. */
  resolutionLimitHz: number
}

/** The cumulative spectral decay's analysis settings — all display, never a reason to re-measure. */
export interface ImpulseCsdRequest {
  fftLength: number
  blockShiftSamples: number
  blockCount: number
  windowMode: CsdWindowModeName
  riseTimeMs: number
  lowHz: number
  highHz: number
  displayColumns: number
}

/** The burst decay's, likewise. */
export interface ImpulseBurstDecayRequest {
  lowHz: number
  highHz: number
  maxPeriods: number
  pointsPerPeriod: number
  resolution: BurstDecayResolutionName
  frequencyColumns: number
}

/** One stored impulse response, as `GET /api/measurements/{id}/impulse` returns it. */
export interface StoredImpulseTrace {
  kind: 'left' | 'gain'
  sampleRateHz: number
  peakIndex: number
  peakVolts: number
  gatePreMs: number
  gatePostMs: number
  gateWindow: string
  resolutionLimitHz: number
  referenceMode: string
  referenceName: string | null
  latencySamples: number | null
  /** Full resolution and unquantized, unlike every curve beside it — a decimated one could not be re-gated. */
  ir: number[]
}

export interface StoredHarmonicCurve {
  order: number
  upperValidHz: number
  fundamentalHz: number[]
  levelDbc: number[]
}

export interface StoredImpulseResult {
  impulses: StoredImpulseTrace[]
  harmonics: StoredHarmonicCurve[]
}

/** One stored curve, as `GET /api/measurements/{id}/response-traces` returns it. */
export interface StoredResponseTrace {
  kind: 'left' | 'right' | 'gain'
  startHz: number
  endHz: number
  peakDb: number
  peakHz: number
  oneDb: { lowerHz: number; upperHz: number; lowerClamped: boolean; upperClamped: boolean }
  threeDb: { lowerHz: number; upperHz: number; lowerClamped: boolean; upperClamped: boolean }
  freqHz: number[]
  magnitudeMinDb: number[]
  magnitudeMaxDb: number[]
  phaseDeg: number[]
  coherence: number[]
}

export interface StoredResponseTracesResponse {
  traces: StoredResponseTrace[]
}

export interface DeviceRanges {
  sampleRatesHz: number[]
  inputRangesDbv: number[]
  outputRangesDbv: number[]
  maxChannelCount: number
}

export interface GeneratorTone {
  freqHz: number
  ampDbv: number
}

/** Only the block matching `kind` needs to be sent; the server fills the rest from its own defaults. */
export interface GeneratorChannelRequest {
  enabled: boolean
  tones: GeneratorTone[]
  kind?: GeneratorKind
  multitone?: Partial<MultitoneSettings>
  noise?: Partial<WhiteNoiseSettings>
  chirp?: Partial<EchoChirpSettings>
}

export interface GeneratorRequest {
  left: GeneratorChannelRequest
  right: GeneratorChannelRequest
  /**
   * Hints the sample rate/channel/block-size to stream at if this call needs to bring up a
   * generator-only stream (no acquisition running yet). Omit to leave the current stream state
   * alone. See DeviceSessionService.ConfigureGeneratorAsync.
   */
  sampleRateHz?: number
  channelCount?: number
  blockFrameCount?: number
  /**
   * Snap every tone to the nearest coherent FFT bin before it reaches the DAC. Defaults to true
   * server-side. Off-bin tones leak across neighbouring bins no matter which window is used.
   */
  snapToBin?: boolean
}

export interface AcquisitionStartRequest {
  sampleRateHz: number
  channelCount: number
  blockFrameCount?: number
}

export type WindowType = 'Rectangular' | 'Hann' | 'Hamming' | 'BlackmanHarris' | 'FlatTop'
export type AveragingMode = 'Off' | 'Linear' | 'Exponential'

// Mirrors Qa403.App.Streaming.SpectrumPipelineOptions. Server serializes/accepts WindowType and
// AveragingMode as strings (JsonStringEnumConverter, configured in Program.cs) — same
// representation in both directions, no int<->name mapping needed here.
export interface SpectrumConfig {
  fftSize: number
  window: WindowType
  averaging: AveragingMode
  averageCount: number
  displayColumns: number
  displayLoFreqHz: number
  targetFrameRateHz: number
  harmonicCount: number
  fundamentalMinSearchHz: number
  otherPeaksCount: number
  /** Whether the left/right channel is processed (FFT + meters) and broadcast at all — see SpectrumConfigPanel's L/R checkboxes. */
  processLeft: boolean
  processRight: boolean
  /**
   * Measurement bandwidth THD+N, SNR and the noise floor are integrated over. Not a display
   * setting — those figures are meaningless without it, and it is what keeps a DC-coupled input's
   * offset (bin 0) out of the noise sum. Defaults to the 20 Hz - 20 kHz audio band.
   */
  measurementLoFreqHz: number
  measurementHiFreqHz: number
}

export interface SpectrumConfigRequest {
  fftSize?: number
  window?: WindowType
  averaging?: AveragingMode
  averageCount?: number
  targetFrameRateHz?: number
  otherPeaksCount?: number
  processLeft?: boolean
  processRight?: boolean
  measurementLoFreqHz?: number
  measurementHiFreqHz?: number
}

// Projects / measurement-types / measurements — mirrors Qa403.App/Endpoints/ProjectsEndpoints.cs
// and MeasurementsEndpoints.cs. A project holds several configured measurement-types (only
// 'FFTSpectrum' exists today); saving a measurement snapshots the type's parameters as-run.

export interface Project {
  id: number
  name: string
  notes: string | null
  createdUtc: string
}

export interface SaveProjectRequest {
  name: string
  notes?: string | null
}

export interface MeasurementType {
  id: number
  projectId: number
  kind: string
  name: string
  notes: string | null
  /** The configured parameters (FFT size, window, band, generator, ...), JSON-encoded. Re-appliable via api.applyMeasurementType. */
  paramsJson: string
  /** The user's own sidebar position, set by dragging — listMeasurementTypes already returns the list in this order. */
  sortOrder: number
  createdUtc: string
}

/** What a measurement type's stored generator/acquisition config actually is — same shape as DeviceStatus's generatorLeft/Right, so the UI's existing form-sync logic works from either source. */
export interface MeasurementTypeAppliedResponse {
  generatorLeft: GeneratorChannelState
  generatorRight: GeneratorChannelState
  snapToBin: boolean
  sampleRateHz: number
  channelCount: number
  blockFrameCount: number
  inputRangeDbv: number
  outputRangeDbv: number
  autoRangeEnabled: boolean | null
  /** This type's stored metric-picker selection — null for a type saved before the feature existed, or never customized. See useMetricSelection.ts. */
  selectedMetrics?: string[] | null
  /** This type's stored oscilloscope period count — null for a type saved before that feature existed. See useScopeDisplay.ts. */
  scopePeriods?: number | null
  /** This type's stored spectrum-chart axis bounds and line-style toggles — null for a type saved before the feature existed, or never customized. See useChartDisplay.ts. */
  chartYTopDbv?: number | null
  chartYBottomDbv?: number | null
  chartXFromHz?: number | null
  chartXToHz?: number | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  chartShowMinMax?: boolean | null
  chartShowPeakHold?: boolean | null
  /** Off (null/false) unless the user opts in — see ChartDisplayPanel.vue's "Autofit when measurement done" checkbox. */
  chartAutofitOnDone?: boolean | null
  /** Which of the FFT screen's secondary panels are drawn — null (a type saved before these existed) means all four. */
  chartShowWaterfall?: boolean | null
  chartShowOscilloscope?: boolean | null
  chartShowHarmonics?: boolean | null
  chartShowOtherPeaks?: boolean | null
}

/**
 * The Acquisition/Generator panels' current staged (possibly not-yet-applied-to-the-device)
 * values — sent along with a save so params_json reflects what's on screen rather than whatever
 * the device last actually ran. Mirrors ProjectsEndpoints.cs's StagedMeasurementConfig.
 */
export interface StagedMeasurementConfig {
  sampleRateHz: number
  channelCount: number
  inputRangeDbv: number
  autoRangeEnabled: boolean | null
  generatorLeft: GeneratorChannelRequest
  generatorRight: GeneratorChannelRequest
  snapToBin?: boolean
  /** The metrics-picker's current selection, staged the same way as any other on-screen setting. */
  selectedMetrics?: string[] | null
  /** The oscilloscope chart's period count, staged the same way as any other on-screen setting. */
  scopePeriods?: number | null
  /** The spectrum chart's axis bounds and line-style toggles, staged the same way as any other on-screen setting. See useChartDisplay.ts. */
  chartYTopDbv?: number | null
  chartYBottomDbv?: number | null
  chartXFromHz?: number | null
  chartXToHz?: number | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  chartShowMinMax?: boolean | null
  chartShowPeakHold?: boolean | null
  /** Off (null/false) unless the user opts in — see ChartDisplayPanel.vue's "Autofit when measurement done" checkbox. */
  chartAutofitOnDone?: boolean | null
  /** Which of the FFT screen's secondary panels are drawn — null (a type saved before these existed) means all four. */
  chartShowWaterfall?: boolean | null
  chartShowOscilloscope?: boolean | null
  chartShowHarmonics?: boolean | null
  chartShowOtherPeaks?: boolean | null
}

export interface SaveMeasurementTypeRequest {
  kind: string
  name: string
  notes?: string | null
  paramsJson?: string | null
  staged?: StagedMeasurementConfig | null
}

export interface ChannelHeadline {
  thdPercent: number
  thdNPercent: number
  snrDb: number
}

export interface MeasurementSummary {
  resultId: number
  projectName: string
  measurementTypeName: string
  name: string
  notes: string | null
  timestampUtc: string
  sampleRateHz: number
  fftSize: number
  channels: number
  /** Keyed by channel index as a string ("0"/"1") — System.Text.Json's default int-key dictionary shape. */
  channelHeadlines: Record<string, ChannelHeadline>
}

export interface MeasurementListResponse {
  items: MeasurementSummary[]
}

export interface MeasurementFilter {
  projectId?: number
  /**
   * Named to match the query parameter and the save requests, not shortened. See
   * `measurementsQuery` for why the spelling is load-bearing.
   */
  measurementTypeId?: number
  /** ISO-8601 UTC, e.g. new Date().toISOString(). */
  from?: string
  to?: string
  skip?: number
  take?: number
}

/** One decimated display column of a stored trace — same (freqHz, min, max) shape as the live chart's columns, just JSON instead of the binary wire format. */
export interface MeasurementColumn {
  freqHz: number
  minDbv: number
  maxDbv: number
}

export interface MeasurementChannel {
  channel: number
  columns: MeasurementColumn[]
  /** Metric name -> value, e.g. "thd_percent", "snr_db", "h2_hz", "h2_dbc". */
  metrics: Record<string, number>
}

export interface MeasurementDetail {
  resultId: number
  measurementTypeId: number
  projectName: string
  measurementTypeName: string
  /** The owning type's kind. A 'ThdVsFrequency' result has an empty `channelsData` — its data comes from api.getStoredSweep instead. */
  kind: string
  name: string
  notes: string | null
  timestampUtc: string
  /** The parameters this specific result actually ran with — independent of the type's current (possibly since-edited) params. */
  paramsJson: string
  sampleRateHz: number
  fftSize: number
  channels: number
  inRangeDbv: number | null
  outRangeDbv: number | null
  genJson: string | null
  channelsData: MeasurementChannel[]
}

export interface SaveMeasurementRequest {
  measurementTypeId: number
  name: string
  notes?: string | null
}

export interface UpdateMeasurementRequest {
  name: string
  notes?: string | null
}

/** Mirrors Qa403.App.Services.UpdateCheckResult. latestVersion/latestUrl are null when the check failed or found nothing newer. */
export interface UpdateStatus {
  currentVersion: string
  latestVersion: string | null
  latestUrl: string | null
  updateAvailable: boolean
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  })

  if (!response.ok) {
    const body = await response.text()
    throw new Error(`${init?.method ?? 'GET'} /api${path} failed: ${response.status} ${body}`)
  }

  // Every endpoint until now always returned a full JSON body. The project/measurement CRUD
  // endpoints introduced 204 No Content (delete) and empty 200 (update) responses, so `.json()`
  // unconditionally would throw "Unexpected end of JSON input" on those — read as text first and
  // only parse when there's something to parse.
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

function toQuery(params: Record<string, string | number | undefined>): string {
  const entries = Object.entries(params).filter((entry): entry is [string, string | number] => entry[1] !== undefined)
  if (entries.length === 0) return ''
  return '?' + entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join('&')
}

/**
 * The path `listMeasurements` fetches, including its filter query.
 *
 * Exported and pure so the parameter <em>names</em> can be pinned by a test, which they need to be:
 * the server binds them by name and silently ignores one it does not recognise, so a misspelling
 * here does not fail — it returns the whole unfiltered list. A caller that trusts the filter and
 * acts on every row then does so believing it was scoped to one measurement type.
 */
export function measurementsQuery(filter: MeasurementFilter = {}): string {
  return `/measurements${toQuery({
    projectId: filter.projectId,
    measurementTypeId: filter.measurementTypeId,
    from: filter.from,
    to: filter.to,
    skip: filter.skip,
    take: filter.take,
  })}`
}

// ---------------------------------------------------------------------------
// THD vs frequency sweep
// ---------------------------------------------------------------------------

/** Mirrors Qa403.App.Services.ThdSweepLevelMode. */
export type SweepLevelMode = 'GeneratorVoltage' | 'OutputVoltage' | 'OutputPower'

/** Mirrors Qa403.App.Services.ThdSweepLevelUnit. 'Watt' is only valid with the OutputPower mode. */
export type SweepLevelUnit = 'Dbv' | 'Volt' | 'MilliVolt' | 'Watt'

/** Mirrors Qa403.App.Services.ThdSweepPhase. */
export type SweepPhaseName =
  | 'Idle'
  | 'DeterminingLevel'
  | 'NoiseFloor'
  | 'Sweeping'
  | 'Finished'
  | 'Cancelled'
  | 'Failed'

export interface ThdSweepStartRequest {
  startFreqHz: number
  endFreqHz: number
  stepsPerOctave: number
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  loadOhms: number
  processLeft: boolean
  processRight: boolean
  fftSize: number
  window: WindowType
  averageCount: number
  sampleRateHz: number
  measureNoiseFloor: boolean
  /**
   * Size the FFT per point and pick the sample rate from the sweep's own range, instead of holding
   * both fixed across every point — see ThdSweepRequest.AdaptiveResolution server-side. When on,
   * `fftSize` and `sampleRateHz` above are ignored.
   */
  adaptiveResolution: boolean
}

/**
 * Mirrors Qa403.Data.ThdVsFrequencyParameters — what a ThdVsFrequency measurement type stores in
 * `params_json`, and what a saved sweep result records as its as-run settings. Identical to
 * ThdSweepStartRequest plus `generatorAmpDbv`, but deliberately its own type: the request is what
 * the client asks for, this is what was configured or actually run.
 */
export interface ThdVsFrequencyParameters extends ThdSweepStartRequest {
  /** The generator amplitude a run actually used — probed for the output-referred level modes, so only known after the fact. 0 on a configured type that has not been run. */
  generatorAmpDbv: number
  /** The sweep chart's axis bounds, per-trace visibility and line-style toggles — null for a type saved before the feature existed, or never customized. See SweepDisplayPanel.vue. */
  chartUnit?: 'percent' | 'db' | null
  chartShowNoiseFloor?: boolean | null
  chartShowMagnitude?: boolean | null
  chartShowThd?: boolean | null
  chartShowD2?: boolean | null
  chartShowD3?: boolean | null
  chartShowD4?: boolean | null
  chartShowD5?: boolean | null
  chartShowD6Plus?: boolean | null
  chartShowLeft?: boolean | null
  chartShowRight?: boolean | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  chartYMaxPercent?: number | null
  chartYMinPercent?: number | null
  chartXFromHz?: number | null
  chartXToHz?: number | null
  chartYTopDb?: number | null
  chartYBottomDb?: number | null
  /** Off unless the user opts in — see SweepDisplayPanel.vue's "Autofit when measurement done" checkbox. */
  chartAutofitOnDone?: boolean | null
}

/**
 * Mirrors Qa403.App.Endpoints.ThdAmplitudeSweepStartRequest. Much smaller than its frequency
 * counterpart because most of that request has no meaning on this axis: the level *is* the swept
 * variable, so there is no level mode, no adaptive resolution and no noise-floor pass, and the
 * window / FFT size / sample rate are fixed server-side.
 */
export interface ThdAmplitudeSweepStartRequest {
  /** The one frequency every point is measured at. */
  fixedFreqHz: number
  /** Generator amplitude, in volts RMS. At least 0.001 V. */
  startAmplitudeVolt: number
  /** Generator amplitude, in volts RMS. At most 7.9 V — the ceiling of the widest output range. */
  endAmplitudeVolt: number
  stepsPerOctave: number
  loadOhms: number
  processLeft: boolean
  processRight: boolean
  averageCount: number
  /** Capture one generator-off record before the sweep, for the residual the distortion traces sit above. */
  measureNoiseFloor: boolean
}

/**
 * Mirrors Qa403.Data.ThdVsAmplitudeParameters — what a ThdVsAmplitude measurement type stores in
 * `params_json`. Its own type rather than an extension of the request for the same reason the
 * frequency sweep's is: the request is what the client asks for, this is what was configured, and
 * this one also carries the chart's display state.
 */
export interface ThdVsAmplitudeParameters {
  fixedFreqHz: number
  startAmplitudeVolt: number
  endAmplitudeVolt: number
  stepsPerOctave: number
  loadOhms: number
  processLeft: boolean
  processRight: boolean
  fftSize: number
  window: WindowType
  averageCount: number
  sampleRateHz: number
  measureNoiseFloor: boolean
  /** Which quantity the x axis plots. Absent on a type saved before the field existed. */
  chartXAxis?: SweepXAxisName | null
  chartUnit?: 'percent' | 'db' | null
  chartShowNoiseFloor?: boolean | null
  chartShowMagnitude?: boolean | null
  chartShowThd?: boolean | null
  chartShowD2?: boolean | null
  chartShowD3?: boolean | null
  chartShowD4?: boolean | null
  chartShowD5?: boolean | null
  chartShowD6Plus?: boolean | null
  chartShowLeft?: boolean | null
  chartShowRight?: boolean | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  chartYMaxPercent?: number | null
  chartYMinPercent?: number | null
  /** X bounds in the selected axis' own unit — volts or watts. */
  chartXFrom?: number | null
  chartXTo?: number | null
  chartYTopDb?: number | null
  chartYBottomDb?: number | null
  /** Off unless the user opts in — see SweepDisplayPanel.vue's "Autofit when measurement done" checkbox. */
  chartAutofitOnDone?: boolean | null
}

/** The three x-axis choices a THD-vs-amplitude chart offers. Mirrors the old app's E_X_AxisType. */
export type SweepXAxisName = 'outputVolt' | 'outputWatt' | 'inputVolt'

export interface ThdSweepStarted {
  pointCount: number
  /** The coherent frequencies the sweep will actually step through, ascending. One repeated value on the amplitude axis. */
  frequenciesHz: number[]
  /** The generator levels an amplitude sweep will step through, in volts, ascending. Empty on the frequency axis. */
  amplitudesVolt: number[]
}

/** One decimated (x, min, max) trace — mirrors the streaming protocol's SweepPreviewSeries and Qa403.Data.StoredPreviewSeries. */
export interface SweepPreviewSeries {
  /** Frequency (Hz) for an FFT preview, elapsed time (s) for a scope preview. */
  x: number[]
  min: number[]
  max: number[]
}

export interface ThdSweepPoint {
  freqHz: number
  /** 0 = left, 1 = right. */
  channel: number
  fundamentalDbv: number
  gainDb: number
  powerWatt: number
  thdPercent: number
  thdNPercent: number
  snrDb: number
  /** D2..D5 as a % of the fundamental, index 0 = 2nd harmonic. */
  harmonicPercent: number[]
  d6PlusPercent: number
  noisePercent: number
  /**
   * The generator amplitude this point was played at. Constant across a frequency sweep; the swept
   * variable — and the "input voltage" x axis — on an amplitude one.
   */
  generatorDbv: number
  /** A small decimated spectrum captured alongside this point, in dBV. Null if the pipeline wasn't asked to capture one. */
  previewFft: SweepPreviewSeries | null
  /** A small decimated oscilloscope trace, in volts. Null if not captured. */
  previewScope: SweepPreviewSeries | null
}

export interface StoredSweepPoint {
  seqIndex: number
  freqHz: number
  /** Metric name -> value, keyed by channel index (0 = left, 1 = right). Names match the FFT save's convention. */
  metricsByChannel: Record<number, Record<string, number>>
  /** kind ('fft' | 'scope') -> preview, keyed by channel index. Absent for a sweep saved without previews. */
  previewsByChannel: Record<number, Record<string, SweepPreviewSeries>> | null
}

export interface StoredSweepResponse {
  points: StoredSweepPoint[]
}

export interface ThdSweepStatus {
  running: boolean
  phase: SweepPhaseName
  index: number
  total: number
  currentFreqHz: number
  /** The generator amplitude actually used — solved by probing for the output-referred modes, or the level of the point being measured on the amplitude axis. */
  generatorAmpDbv: number
  /** Which quantity the running sweep steps. Mirrors Qa403.App.Services.SweepAxis. */
  axis: 'Frequency' | 'Amplitude'
  /** The generator amplitude of the point being measured, in volts. Only moves on the amplitude axis. */
  currentAmplitudeVolt: number
  error: string | null
  /** Every point measured so far. Present on a finished, cancelled or still-running sweep alike. */
  points: ThdSweepPoint[]
}

export const api = {
  getDevice: () => request<DeviceStatus>('/device'),

  /**
   * Connects a QA403 that wasn't attached when the app started. The app comes up device-less
   * rather than failing to start, so this is the only way back to a connected session short of a
   * restart. Rejects with the server's message (503) when there's still nothing to connect to.
   */
  connectDevice: () => request<DeviceStatus>('/device/connect', { method: 'POST' }),

  getDeviceRanges: () => request<DeviceRanges>('/device/ranges'),

  setInputRange: (dbv: number) =>
    request<DeviceStatus>('/device/input-range', { method: 'POST', body: JSON.stringify({ dbv }) }),

  setOutputRange: (dbv: number) =>
    request<DeviceStatus>('/device/output-range', { method: 'POST', body: JSON.stringify({ dbv }) }),

  setAutoRange: (enabled: boolean) =>
    request<DeviceStatus>('/device/auto-range', { method: 'POST', body: JSON.stringify({ enabled }) }),

  setAutoRangeStepDownDelay: (delayMs: number) =>
    request<DeviceStatus>('/device/auto-range-step-down-delay', { method: 'POST', body: JSON.stringify({ delayMs }) }),

  setDeviceKind: (kind: DeviceKindName) =>
    request<DeviceStatus>('/device/kind', { method: 'POST', body: JSON.stringify({ kind }) }),

  setSimulatedProfile: (patch: SimulatedProfileRequest) =>
    request<DeviceStatus>('/device/sim-profile', { method: 'POST', body: JSON.stringify(patch) }),

  setGenerator: (config: GeneratorRequest) =>
    request<DeviceStatus>('/generator', { method: 'POST', body: JSON.stringify(config) }),

  startAcquisition: (config: AcquisitionStartRequest) =>
    request<DeviceStatus>('/acquisition/start', { method: 'POST', body: JSON.stringify(config) }),

  stopAcquisition: () => request<DeviceStatus>('/acquisition/stop', { method: 'POST' }),

  getSpectrumConfig: () => request<SpectrumConfig>('/acquisition/spectrum-config'),

  setSpectrumConfig: (config: SpectrumConfigRequest) =>
    request<SpectrumConfig>('/acquisition/spectrum-config', { method: 'POST', body: JSON.stringify(config) }),

  /** Backs the "Single" button: turns on SpectrumPipelineOptions.SingleShot and arms a fresh capture — see the server endpoint's remarks. Not a saved config value; call startAcquisition (Run) to go back to free-running. */
  armSpectrumCapture: () => request<SpectrumConfig>('/acquisition/spectrum-capture', { method: 'POST' }),

  /** Backs the "Clear" button: discards the retained (stopped) spectrum server-side, so Save measurement result has nothing stale left to save. */
  clearSpectrum: () => request<void>('/acquisition/clear', { method: 'POST' }),

  listProjects: () => request<Project[]>('/projects'),

  createProject: (req: SaveProjectRequest) => request<{ id: number }>('/projects', { method: 'POST', body: JSON.stringify(req) }),

  updateProject: (id: number, req: SaveProjectRequest) =>
    request<void>(`/projects/${id}`, { method: 'PUT', body: JSON.stringify(req) }),

  deleteProject: (id: number) => request<void>(`/projects/${id}`, { method: 'DELETE' }),

  listMeasurementTypes: (projectId: number) => request<MeasurementType[]>(`/projects/${projectId}/measurement-types`),

  createMeasurementType: (projectId: number, req: SaveMeasurementTypeRequest) =>
    request<{ id: number }>(`/projects/${projectId}/measurement-types`, { method: 'POST', body: JSON.stringify(req) }),

  updateMeasurementType: (id: number, req: SaveMeasurementTypeRequest) =>
    request<void>(`/measurement-types/${id}`, { method: 'PUT', body: JSON.stringify(req) }),

  deleteMeasurementType: (id: number) => request<void>(`/measurement-types/${id}`, { method: 'DELETE' }),

  /**
   * Rewrites the project's sidebar order — `ids` must be every one of its measurement types
   * exactly once, in the order they should be listed (409 otherwise; see
   * ProjectRepository.ReorderTypesAsync for why a partial list is refused rather than applied).
   */
  reorderMeasurementTypes: (projectId: number, ids: number[]) =>
    request<void>(`/projects/${projectId}/measurement-types/order`, { method: 'PUT', body: JSON.stringify({ ids }) }),

  /**
   * Pushes this type's stored spectrum/range parameters into the live app (silent, safe at select
   * time) and returns its generator/acquisition settings for the UI to stage into its forms —
   * loading a measurement never starts it playing; see App.vue's Run button for that.
   */
  applyMeasurementType: (id: number) => request<MeasurementTypeAppliedResponse>(`/measurement-types/${id}/apply`, { method: 'POST' }),

  /** Saves the currently retained (stopped) spectrum snapshot — 409 while acquiring, or when nothing has been captured. */
  saveMeasurement: (req: SaveMeasurementRequest) =>
    request<MeasurementDetail>('/measurements', { method: 'POST', body: JSON.stringify(req) }),

  listMeasurements: (filter: MeasurementFilter = {}) =>
    request<MeasurementListResponse>(measurementsQuery(filter)),

  getMeasurement: (id: number) => request<MeasurementDetail>(`/measurements/${id}`),

  updateMeasurement: (id: number, req: UpdateMeasurementRequest) =>
    request<void>(`/measurements/${id}`, { method: 'PUT', body: JSON.stringify(req) }),

  deleteMeasurement: (id: number) => request<void>(`/measurements/${id}`, { method: 'DELETE' }),

  /** Not fetched through `request` — a plain download link the caller renders as an `<a href=... download>`. */
  measurementCsvExportUrl: (id: number) => `/api/measurements/${id}/export.csv`,

  startThdSweep: (req: ThdSweepStartRequest) =>
    request<ThdSweepStarted>('/sweep/thd-vs-frequency/start', { method: 'POST', body: JSON.stringify(req) }),

  /** The level axis. Cancel/status/clear/save are shared with the frequency sweep — one runner, one sweep at a time. */
  startThdAmplitudeSweep: (req: ThdAmplitudeSweepStartRequest) =>
    request<ThdSweepStarted>('/sweep/thd-vs-amplitude/start', { method: 'POST', body: JSON.stringify(req) }),

  cancelSweep: () => request<ThdSweepStatus>('/sweep/cancel', { method: 'POST' }),

  /** Backs the sweep screen's "Clear": discards the points server-side, where they live — clearing them only in the browser would undo itself on the next getSweepStatus. */
  clearSweep: () => request<ThdSweepStatus>('/sweep/clear', { method: 'POST' }),

  // Carries the accumulated points, not just a counter — this is how a browser that reloaded
  // mid-sweep redraws everything measured so far instead of waiting for the next point.
  getSweepStatus: () => request<ThdSweepStatus>('/sweep/status'),

  saveSweep: (req: { measurementTypeId: number; name: string; notes: string | null }) =>
    request<{ id: number }>('/sweep/save', { method: 'POST', body: JSON.stringify(req) }),

  getStoredSweep: (measurementId: number) => request<StoredSweepResponse>(`/measurements/${measurementId}/sweep-points`),

  startFrequencyResponse: (req: FrequencyResponseStartRequest) =>
    request<FrequencyResponseStarted>('/frequency-response/start', { method: 'POST', body: JSON.stringify(req) }),

  cancelFrequencyResponse: () =>
    request<FrequencyResponseStatus>('/frequency-response/cancel', { method: 'POST' }),

  clearFrequencyResponse: () =>
    request<FrequencyResponseStatus>('/frequency-response/clear', { method: 'POST' }),

  // Carries the finished curves, not just a counter — how a browser that reloaded mid-measurement
  // redraws what was measured instead of having to run it again.
  getFrequencyResponseStatus: () => request<FrequencyResponseStatus>('/frequency-response/status'),

  saveFrequencyResponse: (req: { measurementTypeId: number; name: string; notes: string | null }) =>
    request<{ id: number }>('/frequency-response/save', { method: 'POST', body: JSON.stringify(req) }),

  // The stepped-sine Bode plot. Deliberately the same five verbs and the same status payload as the
  // chirp above: the two produce identical data and the browser drives them through one store.
  startSteppedResponse: (req: SteppedResponseStartRequest) =>
    request<SteppedResponseStarted>('/stepped-response/start', { method: 'POST', body: JSON.stringify(req) }),

  cancelSteppedResponse: () =>
    request<FrequencyResponseStatus>('/stepped-response/cancel', { method: 'POST' }),

  clearSteppedResponse: () =>
    request<FrequencyResponseStatus>('/stepped-response/clear', { method: 'POST' }),

  getSteppedResponseStatus: () => request<FrequencyResponseStatus>('/stepped-response/status'),

  saveSteppedResponse: (req: { measurementTypeId: number; name: string; notes: string | null }) =>
    request<{ id: number }>('/stepped-response/save', { method: 'POST', body: JSON.stringify(req) }),

  // The impulse response. The same five verbs as the two response measurements above, plus the
  // loopback calibration sub-resource — capturing one has a physical precondition (patch the
  // generator output straight back to the input) and produces a calibration, not a result.
  startImpulseResponse: (req: ImpulseResponseStartRequest) =>
    request<ImpulseResponseStarted>('/impulse-response/start', { method: 'POST', body: JSON.stringify(req) }),

  cancelImpulseResponse: () =>
    request<ImpulseResponseStatus>('/impulse-response/cancel', { method: 'POST' }),

  clearImpulseResponse: () =>
    request<ImpulseResponseStatus>('/impulse-response/clear', { method: 'POST' }),

  getImpulseResponseStatus: () => request<ImpulseResponseStatus>('/impulse-response/status'),

  saveImpulseResponse: (req: { measurementTypeId: number; name: string; notes: string | null }) =>
    request<{ id: number }>('/impulse-response/save', { method: 'POST', body: JSON.stringify(req) }),

  listLoopbackReferences: () =>
    request<{ references: LoopbackReferenceInfo[] }>('/impulse-response/reference'),

  captureLoopbackReference: (req: ImpulseReferenceCaptureRequest) =>
    request<LoopbackReferenceInfo>('/impulse-response/reference/capture', {
      method: 'POST',
      body: JSON.stringify(req),
    }),

  deleteLoopbackReference: (id: number) =>
    request<void>(`/impulse-response/reference/${id}`, { method: 'DELETE' }),

  // The two decay surfaces. POSTs because they carry an analysis, and separate from the status
  // because they are questions put to the record already captured — changing one redraws rather
  // than re-measures.
  computeImpulseCsd: (req: ImpulseCsdRequest) =>
    request<DecaySurface>('/impulse-response/decay/csd', { method: 'POST', body: JSON.stringify(req) }),

  computeImpulseBurstDecay: (req: ImpulseBurstDecayRequest) =>
    request<DecaySurface>('/impulse-response/decay/burst', { method: 'POST', body: JSON.stringify(req) }),

  getStoredImpulse: (measurementId: number) =>
    request<StoredImpulseResult>(`/measurements/${measurementId}/impulse`),

  getStoredResponse: (measurementId: number) =>
    request<StoredResponseTracesResponse>(`/measurements/${measurementId}/response-traces`),

  getUpdateStatus: () => request<UpdateStatus>('/update-check'),
}
