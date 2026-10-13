// Hand-written client for the endpoints in docs/tonebench-stack-spec.md §4.1.
// Wire shapes must stay in sync with Tonebench.App/Endpoints/*.cs by hand — no codegen.

import { apiErrorMessage } from './errorMessage'

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
   * The simulated generator's fixed harmonic stack (H2 −46 dBc, 0.51 % THD) on/off; same null
   * convention. The nonlinear amplifier models drop the stack regardless, so under one of those the
   * value is reported but has no effect.
   */
  simulatedHarmonicsEnabled: boolean | null
  /**
   * Which simulated device under test is patched in; null when the device isn't the simulator.
   * Unlike the two switches above this changes the DUT's response rather than what is added to it.
   */
  simulatedDut: SimulatedDutModel | null
  /**
   * The simulator's inter-channel coupling (−70 dB, rising above 6 kHz) on/off; same null
   * convention. Off, the simulated channels are perfectly separate and a crosstalk sweep reads the
   * noise floor.
   */
  simulatedCrosstalkEnabled: boolean | null
  /** The simulated amplifier's output stage — None (an ideal source), SolidState or TubeAmp; null when the device isn't the simulator. */
  simulatedOutputStage: SimulatedOutputStageModel | null
  /** What hangs on the simulated amplifier's output; null when the device isn't the simulator. */
  simulatedLoad: SimulatedLoad | null
  /**
   * Which analyzer the session is talking to. Separate from `isSimulated`, which describes a
   * *connected* device and so reads false while the simulator is selected but not yet up.
   */
  deviceKind: DeviceKindName
  /** Input autoranging on/off; null when the device has no autoranging (the simulator). */
  autoRangeEnabled: boolean | null
  /**
   * Who is choosing the two analog ranges — see the status bar's Ranging control. Unlike
   * `autoRangeEnabled` this is never null: the simulator has no hardware ranger but it does have
   * both attenuators, so the mode means the same thing on either analyzer. Always 'Auto' at
   * startup; nothing persists it.
   */
  rangingMode: RangingModeName
  /**
   * The generator is asking for more than the output range's full scale, so what reaches the DUT is
   * clipped. Only reachable under manual ranging — under Auto the range is derived from the
   * generator itself, so it is false by construction.
   */
  outputRangeClipping: boolean
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
  /**
   * Stream frames the worst-off connected client has had evicted from its WebSocket queue since
   * this acquisition started. Not a hole in the sample stream like the two counters above — the
   * measurement and anything saved from it are sound — but one tick's frames are a set this client
   * pairs by seq, so non-zero means what is drawn may be torn: the oscilloscope's "no fundamental
   * to remove" flicker, which has no other tell that distinguishes it from a server-side cause.
   */
  droppedStreamFramesMax: number
  /** The same summed over every connected client; both, because one stalled tab says nothing about the others. */
  droppedStreamFramesTotal: number
  /**
   * What the startup integrity check found wrong with a database file, or null when both files are
   * sound. Shown in the status bar from the first poll, so a damaged file is announced rather than
   * discovered as a 500 on one history entry days later.
   */
  databaseProblem: string | null
  /**
   * The gain of whatever sits between the DUT and the left input, in dB (a 10:1 divider is −20),
   * undone on every sample so every level the app reports is the DUT's. 0 with nothing in the
   * path. The *effective* one: each bench stores its own, and this is whichever analyzer is live.
   * Edited from the status bar's Ext. gain badge.
   */
  externalGainLeftDb: number
  /** The same for the right input. */
  externalGainRightDb: number
  /**
   * The analyzer's housekeeping sensors, or null on a device that has none (the simulator) and
   * before the first reading. A cached *last* reading: the server refreshes it on the same
   * throttled liveness exchange this poll already triggers, and never while a stream is running —
   * so during an acquisition these numbers are whatever they were when it started.
   */
  telemetry: DeviceTelemetry | null
  /**
   * The chosen sound-card input, or null when the analyzer is not a sound card — the same null
   * convention as the `simulated*` fields, so the bar gates on this rather than on a kind string.
   */
  soundCardInputDevice: string | null
  soundCardOutputDevice: string | null
  /**
   * False until both calibration steps have run. An uncalibrated card still measures every *ratio*
   * exactly, because a consistent wrong full-scale constant cancels; what it cannot do is name a
   * voltage, and the bar says so.
   */
  soundCardCalibrated: boolean | null
  /**
   * Whether the bar reads the two external gains in dB or as a ratio — `'Db'` or `'Times'`.
   * Display only: the stored value is dB. The *effective* unit, like the gains themselves, since
   * each bench stores its own. Never null.
   */
  externalGainUnit: string
}

/**
 * The figures QuantAsylum's own software shows beside the firmware version, less the isolated-rail
 * current — a QA403 answers that register with a constant rather than a measurement, which is why
 * the vendor's own status bar shows `---` for it. Units are settled server-side — volts, milliamps,
 * degrees Celsius — so this is formatting only.
 */
export interface DeviceTelemetry {
  usbVoltageV: number
  usbCurrentMa: number
  temperatureC: number
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
  // Nonlinear amplifiers: the models whose distortion depends on level or frequency. Selecting one
  // switches off the simulator's fixed additive harmonic stack, so the DUT's own curve is what shows.
  | 'TriodeSingleEnded'
  | 'ClassAbCrossover'
  | 'SolidStateClipping'
  | 'TransformerCoreSaturation'
  // Linear DUTs with a shape of their own: the RIAA curve (the one DUT that is not unity across the
  // band) and a processor with real latency.
  | 'PhonoRiaa'
  | 'DigitalProcessor'
  // Low-pass filters, all at 2 kHz. The first two are the orders a pole-zero fit can be checked
  // against exactly (one real pole; one pair at Q = 1/√2); the four below are the classic
  // fifth-order alignments — one corner, four different trades.
  | 'FirstOrderLowPass'
  | 'SecondOrderLowPass'
  | 'ButterworthLowPass'
  | 'ChebyshevLowPass'
  | 'BesselLowPass'
  | 'EllipticLowPass'
  // Fixtures rather than DUTs: each is a reference resistor with a load hanging off it, so unlike
  // every model above they are deliberately not unity gain — a divider never is.
  | 'ImpedanceResistive'
  | 'ImpedanceReactive'
  | 'ImpedanceLoudspeaker'
  | 'ImpedanceCapacitive'
  | 'ImpedanceShort'
  | 'ImpedanceOpen'

/**
 * Real QA403 over USB, the synthetic device, or a sound card with an analog front-end. Switchable
 * at runtime — no restart, no flag.
 */
export type DeviceKindName = 'Hardware' | 'Simulated' | 'SoundCard'

/**
 * Who picks the input and output attenuators. One mode for both: they are the two ends of the same
 * analog path, and the reasons to pin either are the same reason. Mirrors Tonebench.App's RangingMode.
 */
export type RangingModeName = 'Auto' | 'Manual'

/** Patch for the simulated DUT's signal profile — omitted fields are left alone. */
export interface SimulatedProfileRequest {
  noiseEnabled?: boolean
  humEnabled?: boolean
  harmonicsEnabled?: boolean
  dut?: SimulatedDutModel
  crosstalkEnabled?: boolean
  outputStage?: SimulatedOutputStageModel
  load?: SimulatedLoad
}

/** How a voltage was entered. A card calibrated against the wrong one is 3.01 dB out. */
export type VoltageEntryUnitName = 'Rms' | 'Peak'

/** One device the picker can offer. */
export interface SoundCardOption {
  hostApi: string
  name: string
  /**
   * The physical interface this device belongs to. The picker offers a card first and its inputs
   * and outputs second, which is what keeps both sides on one card and therefore on one clock.
   */
  interfaceName: string
  hostApiKind: string
  maxInputChannels: number
  maxOutputChannels: number
  /** One device carrying both directions — what ASIO gives, and what nothing else on Windows does. */
  isFullDuplex: boolean
  /** 'Trusted', 'RequiresExclusive' or 'Resamples'. A resampling host API cannot be believed. */
  trust: string
  /** Sort key: the host APIs that reach the hardware first, the ones that resample last. */
  preference: number
  /**
   * Whether the driver has a settings panel of its own — ASIO only, where the buffer size, the
   * clock source and often the sample rate live, none of which PortAudio can set. Windows' own
   * sound panel is not an equivalent: it configures the mixer, which exclusive mode bypasses.
   */
  hasControlPanel: boolean
}

export interface SoundCardControlPanelResult {
  /**
   * True when the driver accepted the request and returned at once, meaning no window appeared —
   * some ASIO drivers keep their settings in a separate application. A guess from the timing, and
   * the only one available: ASIO offers no way to ask whether a window was shown.
   */
  nothingAppeared: boolean
}

export interface SoundCardInventory {
  /** False when PortAudio itself would not start — the dialog says so rather than showing "no cards". */
  available: boolean
  version: string
  inputs: SoundCardOption[]
  outputs: SoundCardOption[]
  problem: string | null
}

export interface SoundCardProbe {
  sampleRatesHz: number[]
  /** 'FullDuplexDevice', 'NamesMatch' or 'Unknown'. Advisory — WDM-KS names carry no interface at all. */
  pairing: string
  inputTrust: string
  outputTrust: string
  problem: string | null
}

/** Mirrors Tonebench.Data.SoundCardConfig. */
export interface SoundCardConfig {
  inputHostApi: string
  inputDeviceName: string
  outputHostApi: string
  outputDeviceName: string
  inputChannelOffset: number
  outputChannelOffset: number
  /**
   * How long to throw away after a stream is opened, before anything is analysed — the card's own
   * settling, not the DUT's (that is Capture delay). Only ever paid on a real open, which in
   * practice means a sample-rate change.
   */
  streamStartSettleMs: number
  /** Null until that step has run: an uncalibrated card reports dBFS and says so. */
  outputFullScalePeakMv: number | null
  inputFullScalePeakMvLeft: number | null
  inputFullScalePeakMvRight: number | null
  calibratedUtc: string | null
  frontEndGainLeftDb: number
  frontEndGainRightDb: number
  gainDisplayUnit: string
  /** Below this a sweep reports the card's own AC coupling as DUT response. */
  minStimulusFrequencyHz: number
}

export interface SoundCardLevels {
  /** Peak on that input as dB below full scale, or null when nothing has been analysed. */
  leftDbFs: number | null
  rightDbFs: number | null
  /** False when nothing is streaming, so the bars read as idle rather than as silence. */
  acquiring: boolean
  /**
   * The calibration tone's level, or null when none is playing. Carried on this poll so the
   * dialog's idea of whether a tone is up cannot drift from the server's.
   */
  playingDbFs: number | null
}

export interface SoundCardCalibrationState {
  running: boolean
  /** The level currently playing, or null when no tone is up. */
  playingDbFs: number | null
  toneHz: number
  outputLevelsDbFs: number[]
}

export interface SoundCardCalibrationResult {
  outputFullScalePeakMv: number | null
  inputFullScalePeakMvLeft: number | null
  inputFullScalePeakMvRight: number | null
  measuredDbFs: number | null
  /** How far apart the two inputs are. Several dB is a wiring mistake, which is what it is for. */
  channelDifferenceDb: number | null
  warning: string | null
}

/** Mirrors Tonebench.Device.Simulation.SimulatedOutputStageModel. */
export type SimulatedOutputStageModel = 'None' | 'SolidState' | 'TubeAmp'

/** Mirrors Tonebench.Device.Simulation.SimulatedLoad. */
export type SimulatedLoad = 'Open' | 'EightOhms' | 'FourOhms'

/** Mirrors Tonebench.Device.GeneratorKind — exactly one stimulus is active per channel. */
export type GeneratorKind = 'Off' | 'Tones' | 'Multitone' | 'WhiteNoise' | 'EchoChirp' | 'Square' | 'Triangle'

/** Mirrors Tonebench.Device.SmoothingOctaves. Stored and round-tripped only; the FR measurement that consumes it is a later phase. */
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

/**
 * Square/triangle parameters, shared by both shapes (the kind is which) — mirrors
 * Tonebench.Device.PeriodicWaveSettings.
 */
export interface PeriodicWaveSettings {
  /** What was asked for. The frequency actually played is quantised to a whole even sample period — see periodicWave.ts. */
  freqHz: number
  /** The waveform's total rms, dBV. Not a peak: a square's peak equals this, a triangle's is sqrt(3) times it. */
  levelDbv: number
}

export interface GeneratorChannelState {
  enabled: boolean
  /** The effective tone list — for kind 'Multitone' this is the derived comb, not the parameters. */
  tones: GeneratorTone[]
  kind: GeneratorKind
  multitone: MultitoneSettings
  noise: WhiteNoiseSettings
  chirp: EchoChirpSettings
  wave: PeriodicWaveSettings
}


// ---------------------------------------------------------------------------
// Frequency response (chirp)
// ---------------------------------------------------------------------------

/** Mirrors Tonebench.App.Services.FrequencyResponseTraceKind. */
export type ResponseTraceKindName = 'Left' | 'Right' | 'Gain' | 'CrosstalkLeftToRight' | 'CrosstalkRightToLeft'

/** Which amplifier channel is the crosstalk aggressor; 'Both' runs the two directions back to back. */
export type CrosstalkDirection = 'LeftToRight' | 'RightToLeft' | 'Both'

/**
 * Mirrors Tonebench.App.Services.ResponsePhaseKind — what phase a run measured, and so what the UI may
 * draw and what it should call it. There is only one phase quantity in these measurements,
 * `arg(L/R)`; what changes is whether it exists and what it means.
 */
export type ResponsePhaseKindName = 'None' | 'PerTrace' | 'DutResponse' | 'ChannelDifference'

/** Mirrors Tonebench.App.Services.FrequencyResponsePhase. */
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
  | 'Analyzing'
  | 'Calibrating'
  /** An output-impedance run waiting for the user to change the load between its two passes. */
  | 'WaitingForLoadChange'

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

// ---------------------------------------------------------------------------------------------
// Impedance
// ---------------------------------------------------------------------------------------------

/** Mirrors Tonebench.App.Services.ImpedanceMethod. Only ReferenceResistor is implemented. */
export type ImpedanceMethodName = 'ReferenceResistor' | 'TwoLoad'

/** Mirrors Tonebench.App.Services.ImpedanceStimulus. */
export type ImpedanceStimulusName = 'Chirp' | 'SteppedSine' | 'PinkNoise'

/** Mirrors Tonebench.App.Services.ImpedanceCableMode. */
export type ImpedanceCableModeName = 'None' | 'Measured' | 'Fixed'

/** Mirrors Tonebench.App.Services.ImpedanceCalTarget. */
export type ImpedanceCalTargetName = 'None' | 'Open' | 'Short'

/** Mirrors Tonebench.App.Services.ImpedanceTraceKind. */
export type ImpedanceTraceKindName = 'Impedance' | 'OpenCal' | 'ShortCal'

/**
 * One stored fixture calibration standard: the measured channel ratio, decimated to a few hundred
 * log-spaced points so it fits in a measurement type's params_json.
 *
 * Mirrors Tonebench.Dsp.ImpedanceCalibration. The browser is the only place these live — the server
 * keeps none of its own, so they travel back with every start request.
 */
export interface ImpedanceCalibrationJson {
  freqHz: number[]
  real: number[]
  imag: number[]
}

/**
 * The impedance fixture as the application remembers it, stored in the <b>configuration database</b>
 * — a separate file from the measurement history, because it describes this machine's QA403 and this
 * bench's leads and must not travel inside a measurement database copied elsewhere.
 */
export interface ImpedanceFixtureConfig {
  /**
   * The series resistor the standards were captured through, and the default a new measurement
   * starts from. The calibration is only valid at this resistance: both standards are stored as the
   * measured channel ratio, and both depend on R.
   */
  referenceOhms: number
  calibrationLevelValue: number
  levelUnit: SweepLevelUnit
  openCal: ImpedanceCalibrationJson | null
  shortCal: ImpedanceCalibrationJson | null
  /** The band the standards actually cover; outside it a calibration curve is held flat. */
  capturedLowHz: number
  capturedHighHz: number
  capturedUtc: string | null
  /** True when there is something worth applying — an open alone is useful, a short alone is not. */
  hasCalibration: boolean
}

/**
 * How the window is laid out — preferences about this screen, not about any measurement. Stored
 * in the configuration database like the fixture, so every tab agrees and a cache clear keeps it.
 */
export interface UiLayoutConfig {
  /** Whether the measurement sidebar stays open at full width; unpinned it collapses to icons and floats open on hover. */
  sidebarPinned: boolean
  /** Whether the settings rail left of the chart is collapsed to its edge strip. */
  settingsRailCollapsed: boolean
  /** Whether the display rail right of the chart is collapsed to its edge strip (applies where it is a column, ≥ 1280 px). */
  displayRailCollapsed: boolean
}

/**
 * Which elements a PDF report includes, remembered per measurement kind (`report.settings` in the
 * configuration database). The ids are the client's own — see report/elements.ts; the server keeps
 * the strings and validates nothing beyond that. A kind with no entry gets every element it offers.
 */
export interface ReportKindSettings {
  elements: string[]
  /** Which of the kind's chart views to include — see report/views.ts. Absent on a document written before views were chosen. */
  views?: string[] | null
  /** How several measurements share a report: one chart, or a chart each. Absent before it existed. */
  mode?: 'combined' | 'individual' | null
}

export interface ReportSettingsConfig {
  kinds: Record<string, ReportKindSettings>
  /** Report charts in black and white. One flag across kinds, since a project report is one document; absent before it existed. */
  chartsMonochrome?: boolean | null
}

/**
 * A PDF report as the browser assembles it and `POST /api/reports/pdf` lays it out. The client owns
 * the content — every number is one the screen already formats, and the chart is the app's own chart
 * rendered to a PNG — and the server owns nothing but the page. Mirrors ReportDocument in
 * ReportsEndpoints.cs.
 */
export interface ReportDocument {
  title: string
  subtitle: string | null
  /** When the client built it, already formatted for the reader. */
  generated: string
  /** Project notes, on the title page. */
  notes: string | null
  chapters: ReportChapter[]
  /** Over the title on the cover: "Project report", "Measurement report". */
  eyebrow?: string | null
  /** The figures beside the contents; the renderer adds the page count itself. */
  stats?: ReportRow[] | null
  /** "Tonebench v0.2.0", in the cover's footer. */
  version?: string | null
  /** Black and white — the document's own accents as well as its charts. */
  monochrome?: boolean
}

export interface ReportChapter {
  /** What the chapter is of: a stored result's name, or "Settings and notes". */
  title: string
  meta: ReportRow[]
  notes: string | null
  sections: ReportSection[]
  /** The measurement the chapter belongs to, over the title in its band. */
  eyebrow?: string | null
  /** The kind's colour as `#rrggbb` (report/palette.ts); the renderer mixes the band's tint from it. */
  accent?: string | null
  /** What the contents page says about this chapter. */
  status?: string | null
  statusTone?: ReportStatusTone | null
}

/** Which pill the contents page gives a chapter's status. */
export type ReportStatusTone = 'stored' | 'live' | 'none'

export interface ReportRow {
  label: string
  value: string
}

export type ReportSectionKind = 'keyValues' | 'table' | 'paragraph' | 'image' | 'figures' | 'groups' | 'callout'

/** One flat shape with a discriminator; only the fields for `kind` are set. */
export interface ReportSection {
  kind: ReportSectionKind
  title?: string | null
  rows?: ReportRow[] | null
  headers?: string[] | null
  cells?: string[][] | null
  /** For `paragraph`, and the body of a `callout` whose `title` is its bold lead-in. */
  text?: string | null
  /** Base64 PNG. */
  png?: string | null
  /** For `image`; the renderer prefixes its own figure number. */
  caption?: string | null
  figures?: ReportFigure[] | null
  groups?: ReportGroup[] | null
}

/** A headline number on a card: what it is, what it reads, and what qualifies it. */
export interface ReportFigure {
  label: string
  value: string
  sub?: string | null
}

/** A named box of rows inside a `groups` section. */
export interface ReportGroup {
  name: string
  rows: ReportRow[]
}

/**
 * How long the DUT is given to settle after every stimulus change, before the analysis window
 * starts filling — the Configuration dialog's "Capture delay". Applied server-side at the one place
 * every generator change passes through, so it covers the live FFT screen and every sweep point
 * alike; stored in the configuration database because the runners cannot read a browser's storage.
 */
export interface CaptureDelayConfig {
  /** Milliseconds discarded after the stimulus latency on every generator change and at every acquisition start with the generator on. 0 disables it. */
  captureDelayMs: number
}

/**
 * The external input gain, per input, in dB — see DeviceStatus.externalGainLeftDb. Stored in the
 * configuration database and applied server-side at the pipeline's door, so the live screen and
 * every runner read DUT-referred levels alike.
 */
export interface ExternalGainConfig {
  leftDb: number
  rightDb: number
  /**
   * How the two figures are read: `'Db'`, or `'Times'` for a plain ratio. A display preference —
   * the stored value is dB either way. Per bench, beside the gain it describes; the sound card
   * keeps its own in `SoundCardConfig.gainDisplayUnit`.
   */
  unit: GainUnitName
}

/** Mirrors `web/src/soundcard/gainUnits.ts`'s `GainUnit`, which is where the conversion lives. */
export type GainUnitName = 'Db' | 'Times'

export interface ImpedanceStartRequest {
  method: ImpedanceMethodName
  stimulus: ImpedanceStimulusName
  /** The series reference resistor. Sensitivity is best when this is near the impedance being measured. */
  referenceOhms: number
  /**
   * Which channel taps the generator side of the reference resistor. False — left as reference — is
   * how a bench fixture is normally wired; the simulator needs true, because its fixture models sit
   * on the left channel.
   */
  rightChannelIsReference: boolean
  frequencySpanHz: number
  resolutionMultiplier: number
  levelValue: number
  /** No `Watt`: a one-port has no output power to aim at, and the server rejects it. */
  levelUnit: SweepLevelUnit
  smoothingDenominator: number
  averageCount: number
  /** RUN sets this and keeps measuring until stopped; SINGLE leaves it false. Rejected for a calibration run. */
  continuous: boolean
  /**
   * The drive level a calibration run uses, in the same unit as `levelValue`. Louder than the
   * measurement's by default: a shorted fixture puts the DUT channel ~60 dB below the reference one,
   * and the fixture is linear, so measuring the standards loudly costs nothing.
   */
  calibrationLevelValue: number
  chirpStartHz: number
  /** None to measure a DUT, or Open/Short to capture that standard. */
  calTarget: ImpedanceCalTargetName
  cableMode: ImpedanceCableModeName
  fixedCableOhms: number
  fixedCableMillihenry: number
  /** For de-embedding the analyzer's own input impedance from a high-impedance measurement. No default — see the server's remarks. */
  analyzerInputOhms: number | null
  applySweepGate: boolean
  openCal: ImpedanceCalibrationJson | null
  shortCal: ImpedanceCalibrationJson | null
  /**
   * What the Open/Short captures play, chosen independently of `stimulus`. A standard is a curve in
   * frequency and a fixture is linear, so a chirp-captured one corrects a stepped measurement
   * unchanged — and in seconds rather than minutes, which is why the two are separate controls.
   */
  calibrationStimulus: ImpedanceStimulusName
  /** The stepped-sine grid. Always sent; the server reads it only when some run will play tones. */
  startFreqHz: number
  endFreqHz: number
  stepsPerOctave: number
  adaptiveResolution: boolean
}

/**
 * An impedance measurement type's stored settings. The `chart*` fields are display state and all
 * optional, so a type saved before one of them existed still loads.
 */
export interface ImpedanceParameters {
  method: ImpedanceMethodName
  stimulus: ImpedanceStimulusName
  referenceOhms: number
  rightChannelIsReference: boolean
  frequencySpanHz: number
  resolutionMultiplier: number
  chirpStartHz: number
  levelValue: number
  levelUnit: SweepLevelUnit
  smoothingDenominator: number
  averageCount: number
  /** The drive level calibration runs use, in the same unit as `levelValue`. */
  calibrationLevelValue?: number | null
  cableMode: ImpedanceCableModeName
  fixedCableOhms: number
  fixedCableMillihenry: number
  analyzerInputOhms?: number | null
  applySweepGate?: boolean | null
  /** The fixture calibration, which is what makes it outlast the app. */
  openCal?: ImpedanceCalibrationJson | null
  shortCal?: ImpedanceCalibrationJson | null
  /** Take the resistor and both standards from the globally configured fixture instead of these. */
  useGlobalFixture?: boolean | null
  generatorAmpDbv?: number | null
  /** Optional: a type stored before the second stimulus existed has neither of these. */
  calibrationStimulus?: ImpedanceStimulusName | null
  startFreqHz?: number | null
  endFreqHz?: number | null
  stepsPerOctave?: number | null
  adaptiveResolution?: boolean | null
  chartView?: ImpedanceView | null
  chartShowPhase?: boolean | null
  chartShowCoherence?: boolean | null
  chartShowShortCal?: boolean | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  chartZTopOhms?: number | null
  chartZBottomOhms?: number | null
  /** How the |Z| axis is scaled. Null on a type stored before the choice existed, which opens as Linear. */
  chartZScale?: ImpedanceZScale | null
  chartXFromHz?: number | null
  chartXToHz?: number | null
  chartPhaseTopDeg?: number | null
  chartPhaseBottomDeg?: number | null
  chartAutofitOnDone?: boolean | null
  /**
   * What the user says is on the fixture, which decides which component metrics the readout offers.
   * Analysis rather than acquisition — it changes nothing about the run — so it lives with the
   * `chart*` display fields and is null on a type stored before it existed.
   */
  chartDutType?: string | null
  /**
   * The frequency the readout's Equivalent circuit block reads, which the comparison table under the
   * chart and the report's own block follow. Display state like the rest of the `chart*` fields, and
   * null on a type stored before it existed — the screen then starts at 1 kHz.
   */
  chartEquivalentHz?: number | null
}

/** Which chart the impedance screen is showing. */
export type ImpedanceView = 'Bode' | 'Nyquist'

/**
 * The |Z| axis: linear ohms, the way an impedance curve is usually drawn, or logarithmic, which is
 * the only scale that shows a fixture's milliohms and a driver's resonance peak in one window.
 */
export type ImpedanceZScale = 'Linear' | 'Log'

export interface ImpedanceTraceJson {
  kind: ImpedanceTraceKindName
  startFreqHz: number
  endFreqHz: number
  freqHz: number[]
  /** dB relative to 1 ohm for an Impedance curve; a plain dB ratio for a calibration standard. */
  magnitudeMinDbOhms: number[]
  magnitudeMaxDbOhms: number[]
  phaseDeg: number[]
  coherence: number[]
  peakDbOhms: number
  peakFreqHz: number
}

export interface ImpedanceStatus {
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
  referenceOhms: number
  rightChannelIsReference: boolean
  calTarget: ImpedanceCalTargetName
  error: string | null
  traces: ImpedanceTraceJson[]
  /** The standard a calibration run just captured, for the browser to store. Null on a DUT measurement. */
  capturedCalibration: ImpedanceCalibrationJson | null
  /** Median |Z| across the analysed band, as a one-number summary. NaN arrives as null over JSON. */
  medianOhms: number | null
  /** Roughly what the DUT was driven with, in amps. Approximate — a sweep's current varies with Z. */
  driveCurrentAmps: number | null
  /** What this run played — the effective one, so a chirp calibration inside a stepped measurement says Chirp. */
  stimulus: ImpedanceStimulusName
  /**
   * Points finished and points in total, for a stepped run. Null — not zero — on a chirp run, which
   * has records rather than points; the progress line tells the two apart by whether these are set.
   */
  pointsMeasured: number | null
  pointCount: number | null
  /**
   * A setup mistake the numbers prove, or null. The run finished and its curve is drawn; it should
   * not be believed. Today: a negative real impedance over most of the band, which means the two
   * inputs are the wrong way round for this fixture.
   */
  warning: string | null
}

export interface ImpedanceStarted {
  sampleRateHz: number
  fftSize: number
  binHz: number
  averageCount: number
  recordSeconds: number
  continuous: boolean
  calTarget: ImpedanceCalTargetName
  stimulus: ImpedanceStimulusName
  /** The measured grid, for a stepped run. All null for a chirp. */
  pointCount: number | null
  frequenciesHz: number[] | null
  estimatedSeconds: number | null
}

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
  /**
   * What the +-1 dB and -3 dB corners are measured down from. 'BandMedian' takes the median level
   * over [bandwidthReferenceLowHz, bandwidthReferenceHighHz] — the right answer for anything with a
   * passband, because an output transformer's leakage resonance sits above the audio band and is
   * routinely taller than it, which makes a peak reference measure the resonance instead. 'Peak' is
   * for a DUT whose response *is* a peak: a bandpass, a tweeter.
   */
  bandwidthReference: ResponseReferenceModeName
  bandwidthReferenceLowHz: number
  bandwidthReferenceHighHz: number
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
  /** Which delay curve the chart draws ('None' | 'Group' | 'Phase') and its axis window in µs. Absent on a type saved before the delay curves existed. */
  chartDelayView?: ResponseDelayView | null
  chartDelayTopUs?: number | null
  chartDelayBottomUs?: number | null
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
  /**
   * Nichols: whether the gain axis is fitted to the curve, and the dB window it uses when it is
   * not. Absent on a type saved before the control existed, which is treated as fitted — what those
   * builds always did. There is deliberately no phase pair: that axis carries unwrapped degrees, so
   * its span belongs to the DUT rather than to a preference (see response/projection.ts).
   */
  chartNicholsAutoGain?: boolean | null
  chartNicholsTopDb?: number | null
  chartNicholsBottomDb?: number | null
  /**
   * Pole-zero map: whether the window is fitted to the roots, and the window in hertz when it is
   * not — σ/2π along x, jω/2π up y, signed, and warped by whichever radius scale is in force.
   */
  chartPoleZeroAutoWindow?: boolean | null
  chartPoleZeroXMinHz?: number | null
  chartPoleZeroXMaxHz?: number | null
  chartPoleZeroYMinHz?: number | null
  chartPoleZeroYMaxHz?: number | null
  chartInvertLoopSign?: boolean | null
  chartRestoreDelay?: boolean | null
  /** Nyquist and Nichols: draw the fitted model over the measurement. */
  chartShowFittedModel?: boolean | null
  /** The Poles and zeros panel's Auto checkbox. Absent on a type saved before the orders were stored, which is treated as on. */
  chartFitAutoOrder?: boolean | null
  /** The pinned denominator order (poles) used when Auto is off. */
  chartFitPoles?: number | null
  /** The pinned numerator order (zeros), never more than the poles. */
  chartFitZeros?: number | null
  /** Pole-zero map: hide the roots the fit placed outside the measured band. */
  chartHideOutOfBandRoots?: boolean | null
  /** How both s-plane views lay out radius. Absent on a type saved before it existed, i.e. linear. */
  chartSurfaceScale?: SPlaneScaleName | null
  /** |H(s)| surface: flat heat map, or oblique relief. */
  chartSurfaceMode?: SurfaceModeName | null
  /** |H(s)| surface: dB above the passband reference that the colour ramp reaches, where poles saturate. */
  chartSurfaceTopDb?: number | null
  /** |H(s)| surface: dB below the passband reference that the colour ramp reaches. */
  chartSurfaceBottomDb?: number | null
  /** |H(s)| surface: which colour ramp, shared with the decay surfaces. */
  chartSurfacePalette?: DecayPalette | null
  /** |H(s)| surface: draw the iso-dB contour lines, which are what make it quantitative and printable. */
  chartSurfaceContours?: boolean | null
  /**
   * A *run* setting, not a chart one: it changes the numbers in the Measurements table rather than
   * how they are drawn. Absent on a type saved before the reference was configurable — those runs
   * were peak-referenced, but the panel still falls back to its own default rather than restoring a
   * reference that was wrong for the DUT.
   */
  bandwidthReference?: ResponseReferenceModeName | null
  bandwidthReferenceLowHz?: number | null
  bandwidthReferenceHighHz?: number | null
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
export type ResponseProjection = 'Bode' | 'Nyquist' | 'Nichols' | 'PoleZero' | 'SPlane'

/**
 * How the s-plane views lay out radius — the pole–zero map's and the |H(s)| surface's.
 *
 * `Linear` is the plane as a textbook draws it and stays the default, so nothing already saved
 * changes meaning. `LogModulus` scales |s| and leaves arg(s) alone, which is the only way a DUT whose
 * roots span decades fits in one square window; see `response/splaneScale.ts`.
 */
export type SPlaneScaleName = 'Linear' | 'LogModulus'

/**
 * How the |H(s)| surface lays the same field out: flat, or as an oblique relief with a pole as a
 * spike. Both read one evaluated field and one colour window, so they cannot disagree about a level.
 */
export type SurfaceModeName = 'Heatmap' | 'Relief' | 'Slice'

/**
 * Which delay curve the Bode chart draws on its own right-hand axis. One at a time, because they
 * share that axis: group delay (−dφ/df) and phase delay (−φ/f) differ by orders of magnitude on a
 * DUT with a high-pass corner and would need two windows.
 */
export type ResponseDelayView = 'None' | 'Group' | 'Phase'

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
  /**
   * What the +-1 dB and -3 dB corners are measured down from. 'BandMedian' takes the median level
   * over [bandwidthReferenceLowHz, bandwidthReferenceHighHz] — the right answer for anything with a
   * passband, because an output transformer's leakage resonance sits above the audio band and is
   * routinely taller than it, which makes a peak reference measure the resonance instead. 'Peak' is
   * for a DUT whose response *is* a peak: a bandpass, a tweeter.
   */
  bandwidthReference: ResponseReferenceModeName
  bandwidthReferenceLowHz: number
  bandwidthReferenceHighHz: number
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
 * Wire shape for starting a crosstalk run — the stepped sweep's grid and level modes, one driven
 * channel at a time. The level modes probe the *driven* channel. See CrosstalkRequest server-side.
 */
export interface CrosstalkStartRequest {
  startFreqHz: number
  endFreqHz: number
  stepsPerOctave: number
  direction: CrosstalkDirection
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  loadOhms: number
  averageCount: number
  adaptiveResolution: boolean
}

export interface CrosstalkStarted {
  /** 1 for a single direction, 2 for Both. */
  passCount: number
  /** Points per pass; the status's pointCount is the total over every pass. */
  pointCount: number
  frequenciesHz: number[]
  sampleRateHz: number
  averageCount: number
  /** For every pass together. */
  estimatedSeconds: number
}

/** Starts an output-impedance (damping factor) run — see OutputImpedanceStartRequest server-side. */
export interface OutputImpedanceStartRequest {
  startFreqHz: number
  endFreqHz: number
  stepsPerOctave: number
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  /** The resistor connected for the first pass — also what a power target converts through. */
  loadOhms: number
  /** The resistor connected for the second pass, or null for an open output (the tutorial's way — never for a tube amplifier). */
  secondLoadOhms: number | null
  /** What the damping factor is quoted against; 8 Ω on every specification sheet. */
  nominalOhms: number
  /** Loop the right output to the right input in both passes and measure Zout as a complex value. */
  rightChannelIsReference: boolean
  averageCount: number
  adaptiveResolution: boolean
}

export interface OutputImpedanceStarted {
  passCount: number
  /** Per pass; the status's pointCount is the total over both. */
  pointCount: number
  frequenciesHz: number[]
  sampleRateHz: number
  averageCount: number
  estimatedSeconds: number
}

/** Mirrors Tonebench.App.Services.OutputImpedanceStatus. Every reading is null until measured — never NaN. */
export interface OutputImpedanceStatus {
  running: boolean
  phase: ResponsePhaseName
  passesCompleted: number
  pointsMeasured: number
  /** Over both passes. */
  pointCount: number
  generatorAmpDbv: number
  /** The one input range both passes were read at. */
  inputRangeDbv: number | null
  binHz: number
  sampleRateHz: number
  fftSize: number
  loadOhms: number
  secondLoadOhms: number | null
  nominalOhms: number
  rightChannelIsReference: boolean
  error: string | null
  warning: string | null
  /** The Zout curve — an impedance trace in every respect; null until the second pass has two points. */
  trace: ImpedanceTraceJson | null
  medianOhms: number | null
  zoutAt1kOhms: number | null
  dampingFactorAt1k: number | null
  firstGainAt1kDb: number | null
  secondGainAt1kDb: number | null
}

/** An output-impedance type's stored settings — see OutputImpedanceParameters server-side. */
export interface OutputImpedanceParameters {
  startFreqHz: number
  endFreqHz: number
  stepsPerOctave: number
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  loadOhms: number
  secondLoadOhms?: number | null
  nominalOhms: number
  rightChannelIsReference: boolean
  fftSize: number
  window: WindowType
  averageCount: number
  sampleRateHz: number
  adaptiveResolution: boolean
  generatorAmpDbv?: number | null
  inputRangeDbv?: number | null
  chartZTopOhms?: number | null
  chartZBottomOhms?: number | null
  chartZScale?: ImpedanceZScale | null
  chartShowDampingFactor?: boolean | null
  chartShowPhase?: boolean | null
  chartXFromHz?: number | null
  chartXToHz?: number | null
  chartPhaseTopDeg?: number | null
  chartPhaseBottomDeg?: number | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  chartAutofitOnDone?: boolean | null
  captureDelayMs?: number | null
}

/** A crosstalk type's stored settings — see CrosstalkParameters server-side. */
export interface CrosstalkParameters {
  startFreqHz: number
  endFreqHz: number
  stepsPerOctave: number
  direction: CrosstalkDirection
  levelMode: SweepLevelMode
  levelValue: number
  levelUnit: SweepLevelUnit
  loadOhms: number
  fftSize: number
  window: WindowType
  averageCount: number
  sampleRateHz: number
  adaptiveResolution: boolean
  /** The generator level each pass ran at — solved by probing in the output-referred modes, so only known after the fact. */
  generatorAmpDbvLeftToRight?: number | null
  generatorAmpDbvRightToLeft?: number | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  chartYTopDb?: number | null
  chartYBottomDb?: number | null
  chartXFromHz?: number | null
  chartXToHz?: number | null
  chartAutofitOnDone?: boolean | null
  captureDelayMs?: number | null
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
  /** See FrequencyResponseParameters.chartDelayView. */
  chartDelayView?: ResponseDelayView | null
  chartDelayTopUs?: number | null
  chartDelayBottomUs?: number | null
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
  /**
   * Nichols: whether the gain axis is fitted to the curve, and the dB window it uses when it is
   * not. Absent on a type saved before the control existed, which is treated as fitted — what those
   * builds always did. There is deliberately no phase pair: that axis carries unwrapped degrees, so
   * its span belongs to the DUT rather than to a preference (see response/projection.ts).
   */
  chartNicholsAutoGain?: boolean | null
  chartNicholsTopDb?: number | null
  chartNicholsBottomDb?: number | null
  /**
   * Pole-zero map: whether the window is fitted to the roots, and the window in hertz when it is
   * not — σ/2π along x, jω/2π up y, signed, and warped by whichever radius scale is in force.
   */
  chartPoleZeroAutoWindow?: boolean | null
  chartPoleZeroXMinHz?: number | null
  chartPoleZeroXMaxHz?: number | null
  chartPoleZeroYMinHz?: number | null
  chartPoleZeroYMaxHz?: number | null
  chartInvertLoopSign?: boolean | null
  chartRestoreDelay?: boolean | null
  /** Nyquist and Nichols: draw the fitted model over the measurement. */
  chartShowFittedModel?: boolean | null
  /** The Poles and zeros panel's Auto checkbox. Absent on a type saved before the orders were stored, which is treated as on. */
  chartFitAutoOrder?: boolean | null
  /** The pinned denominator order (poles) used when Auto is off. */
  chartFitPoles?: number | null
  /** The pinned numerator order (zeros), never more than the poles. */
  chartFitZeros?: number | null
  /** Pole-zero map: hide the roots the fit placed outside the measured band. */
  chartHideOutOfBandRoots?: boolean | null
  /** How both s-plane views lay out radius. Absent on a type saved before it existed, i.e. linear. */
  chartSurfaceScale?: SPlaneScaleName | null
  /** |H(s)| surface: flat heat map, or oblique relief. */
  chartSurfaceMode?: SurfaceModeName | null
  /** |H(s)| surface: dB above the passband reference that the colour ramp reaches, where poles saturate. */
  chartSurfaceTopDb?: number | null
  /** |H(s)| surface: dB below the passband reference that the colour ramp reaches. */
  chartSurfaceBottomDb?: number | null
  /** |H(s)| surface: which colour ramp, shared with the decay surfaces. */
  chartSurfacePalette?: DecayPalette | null
  /** |H(s)| surface: draw the iso-dB contour lines, which are what make it quantitative and printable. */
  chartSurfaceContours?: boolean | null
  /**
   * A *run* setting, not a chart one: it changes the numbers in the Measurements table rather than
   * how they are drawn. Absent on a type saved before the reference was configurable — those runs
   * were peak-referenced, but the panel still falls back to its own default rather than restoring a
   * reference that was wrong for the DUT.
   */
  bandwidthReference?: ResponseReferenceModeName | null
  bandwidthReferenceLowHz?: number | null
  bandwidthReferenceHighHz?: number | null
}

/** A -1 dB or -3 dB reading. A clamped end means the curve never got there, not that the corner is at that frequency. */
export interface ResponseBandwidthJson {
  dropDb: number
  lowerHz: number
  upperHz: number
  bandwidthHz: number
  lowerClamped: boolean
  upperClamped: boolean
  /** The band is +-dropDb of the reference rather than a one-sided fall to reference - dropDb. */
  symmetric: boolean
}

/** What a trace's bandwidth corners were measured down from. Mirrors BandwidthReferenceMode. */
export type ResponseReferenceModeName = 'Peak' | 'BandMedian'

/**
 * The bandwidth reference applied to the response traces on screen — `PUT .../bandwidth-reference`
 * on either response measurement. The same three fields the start requests carry; here they
 * re-find the corners of what was already measured, without a new run.
 */
export interface BandwidthReferenceRequest {
  bandwidthReference: ResponseReferenceModeName
  bandwidthReferenceLowHz: number
  bandwidthReferenceHighHz: number
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
  /** Seconds per column; see FrequencyResponseTrace server-side for when they are physical and when excess only. */
  groupDelayS: number[]
  phaseDelayS: number[]
  oneDb: ResponseBandwidthJson
  threeDb: ResponseBandwidthJson
  peakDb: number
  peakFreqHz: number
  referenceDb: number
  referenceMode: ResponseReferenceModeName
  referenceFellBackToPeak: boolean
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
  /**
   * Each measured point's small FFT/waveform capture, one entry per point per input — the two
   * preview charts beside a stepped-sine response. Absent for every runner that takes none (a chirp
   * has no points) and on a status from a build that predates them.
   */
  pointPreviews?: ResponsePointPreviewJson[]
}

/** One point's decimated spectrum and waveform on one input. Mirrors Tonebench.App.Services.ResponsePointPreview. */
export interface ResponsePointPreviewJson {
  freqHz: number
  /** The input: 0 left, 1 right. Not a trace kind — a preview is a capture, and the gain curve is derived. */
  channel: number
  fft: SweepPreviewSeries | null
  scope: SweepPreviewSeries | null
}

/** What an impulse-response run divides the captured response by. Mirrors `ImpulseReferenceMode`. */
export type ImpulseReferenceModeName = 'None' | 'RightChannel' | 'StoredLoopback'

/**
 * Gate windows the impulse-response panel offers. Rectangular is a real choice: a bare cut
 * isolates a reflection exactly. The rest are a ladder of taper, listed in that order — must stay
 * in step with `ImpulseResponseRequest.SupportedGateWindows`, which validates the choice.
 */
export const IMPULSE_GATE_WINDOWS: { value: WindowType; label: string }[] = [
  { value: 'Rectangular', label: 'Rectangular (bare cut)' },
  { value: 'Hann', label: 'Hann' },
  { value: 'BlackmanHarris', label: 'Blackman-Harris' },
  { value: 'Kaiser5', label: 'Kaiser 5' },
  { value: 'Kaiser7', label: 'Kaiser 7' },
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
  /** The Response view's delay curve and its axis window — see FrequencyResponseParameters.chartDelayView. */
  chartDelayView?: ResponseDelayView | null
  chartDelayTopUs?: number | null
  chartDelayBottomUs?: number | null
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

/** A stored impulse response as its display trace; `impulse` is null when the result holds none. */
export interface StoredImpulseTraceResponse {
  impulse: ImpulseTraceJson | null
}

export interface StoredImpulseResult {
  impulses: StoredImpulseTrace[]
  harmonics: StoredHarmonicCurve[]
}

/** One stored curve, as `GET /api/measurements/{id}/response-traces` returns it. */
export interface StoredResponseTrace {
  /** The row's kind as stored (`response_trace.kind`): the response kinds, the crosstalk kinds, or an impedance kind (`z`, `z_open`, `z_short`). */
  kind: 'left' | 'right' | 'gain' | 'xtalk_l2r' | 'xtalk_r2l' | 'z' | 'z_open' | 'z_short' | 'zout' | 'gain_load1' | 'gain_load2'
  startHz: number
  endHz: number
  peakDb: number
  peakHz: number
  oneDb: { lowerHz: number; upperHz: number; lowerClamped: boolean; upperClamped: boolean }
  threeDb: { lowerHz: number; upperHz: number; lowerClamped: boolean; upperClamped: boolean }
  /** Null on a result saved before the reference was recorded — the chart then draws no reference line. */
  referenceDb: number | null
  referenceMode: ResponseReferenceModeName | null
  freqHz: number[]
  magnitudeMinDb: number[]
  magnitudeMaxDb: number[]
  phaseDeg: number[]
  coherence: number[]
  /** Null on a result saved before the delay curves were recorded — the chart then draws none and the panel shows a dash. */
  groupDelayS: number[] | null
  phaseDelayS: number[] | null
}

export interface StoredResponseTracesResponse {
  traces: StoredResponseTrace[]
}

/**
 * One stored point of a stepped-sine response, as `GET /api/measurements/{id}/response-previews`
 * returns it: the tone, and each input's two decimated captures keyed by kind ('fft' / 'scope').
 * A kind a run did not record is simply absent.
 */
export interface StoredResponsePreviewPoint {
  freqHz: number
  byChannel: Record<string, { fft?: SweepPreviewSeries; scope?: SweepPreviewSeries }>
}

export interface StoredResponsePreviewsResponse {
  points: StoredResponsePreviewPoint[]
}

export interface DeviceRanges {
  sampleRatesHz: number[]
  inputRangesDbv: number[]
  outputRangesDbv: number[]
  maxChannelCount: number
  /** The lowest frequency this analyzer can excite, in Hz; 0 on one with no floor. See stimulusFloor.ts. */
  minStimulusFrequencyHz: number
  /**
   * The highest sample rate this analyzer can *play* a stimulus at, in Hz, or null on one with no
   * ceiling (a sound card's generator works wherever it captures). 192 kHz on a QA403, where
   * 384 kHz is an input-only rate. See generatorRateCeiling.ts.
   */
  maxGeneratorSampleRateHz: number | null
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
  wave?: Partial<PeriodicWaveSettings>
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
  /**
   * The "Single" button: capture the configured averages, then hold (SpectrumPipelineOptions.SingleShot).
   * Rides on the start itself rather than a follow-up call, because the start's own flushes are what
   * arm the capture — a separate arming flush after it zeroed the capture-delay discard the start had
   * just booked. Omitted/false free-runs (Run). Not a saved config value.
   */
  singleShot?: boolean
}

/**
 * Mirrors `Tonebench.Dsp.WindowType`. The Kaiser numbers are ARTA's alpha, i.e. beta = pi*alpha —
 * see the convention note on `Window` server-side before adding or renaming one.
 */
export type WindowType =
  | 'Rectangular'
  | 'Hann'
  | 'Hamming'
  | 'BlackmanHarris'
  | 'FlatTop'
  | 'Kaiser5'
  | 'Kaiser7'
export type AveragingMode = 'Off' | 'Linear' | 'Exponential'

// Mirrors Tonebench.App.Streaming.SpectrumPipelineOptions. Server serializes/accepts WindowType and
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
  /** The two-tone IMD spec in force, or null when no IMD is measured — see IntermodulationConfigRequest. */
  intermodulation?: IntermodulationSpec | null
  /** The noise weighting in effect — see WeightingConfigRequest. */
  weighting: WeightingSpec
  /** Whether the pipeline also sends each scope tail with its fundamental removed — the oscilloscope's Residual view. */
  scopeResidual: boolean
}

export type ImdMethodName = 'Smpte' | 'Ccif'

import type { WeightingSpec, WeightingStandardName } from '../streaming/protocol'
// The listing's shape belongs with the dialog's own arithmetic, which is where it is read -- see
// issueReport.ts. Re-exported here so a caller needs one import for the request and the response.
import type { IssueReportContents } from '../issueReport'
export type { IssueReportContents }
export type { WeightingSpec, WeightingStandardName }

/**
 * Patch value for SpectrumConfigRequest.weighting: every member is optional and an omitted one
 * keeps the server's current value.
 */
export interface WeightingConfigRequest {
  standard?: WeightingStandardName
  user1?: boolean
  user2?: boolean
}

/** What the Bandwidth & weighting panel shows for a user weighting slot; null when the slot is empty. */
export interface UserWeightingSummary {
  name: string
  sourcePath: string | null
  pointCount: number
  minFreqHz: number
  maxFreqHz: number
  importedUtc: string
}

export interface UserWeightingResponse {
  user1: UserWeightingSummary | null
  user2: UserWeightingSummary | null
}

/** The weighting in effect sampled as a curve (GET /api/acquisition/weighting-curve); empty arrays and `weighted: false` when nothing is selected. */
export interface WeightingCurveResponse {
  weighted: boolean
  freqHz: number[]
  gainDb: number[]
}

/** Mirrors Tonebench.App.Streaming.IntermodulationSpec. */
export interface IntermodulationSpec {
  toneLowHz: number
  toneHighHz: number
  method: ImdMethodName
  maxOrder: number
  maxProductFreqHz: number | null
}

/**
 * Patch value for SpectrumConfigRequest.intermodulation: `enabled: false` clears the spec (the
 * other fields are ignored); omitted altogether leaves the server's spec as it is. A null
 * maxProductFreqHz means "the measurement band's upper edge" — in-band products only.
 */
export interface IntermodulationConfigRequest {
  enabled: boolean
  toneLowHz?: number
  toneHighHz?: number
  method?: ImdMethodName
  maxOrder?: number
  maxProductFreqHz?: number | null
}

export interface SpectrumConfigRequest {
  weighting?: WeightingConfigRequest
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
  intermodulation?: IntermodulationConfigRequest
  /** Send the scope tail with its fundamental removed as well — see SpectrumConfig.scopeResidual. Omitted leaves it alone. */
  scopeResidual?: boolean
}

// Projects / measurement-types / measurements — mirrors Tonebench.App/Endpoints/ProjectsEndpoints.cs,
// ProjectSessionEndpoints.cs and MeasurementsEndpoints.cs. A project is one `.tbench` file holding
// several configured measurement-types; saving a measurement snapshots the type's parameters as-run.

export interface Project {
  id: number
  name: string
  /** The project's description. The wire name predates the Project menu. */
  notes: string | null
  createdUtc: string
}

/** What is open, if anything — GET /api/project. Never fails, which is how the app picks welcome vs workspace. */
export interface ProjectSession {
  open: boolean
  path: string | null
  project: Project | null
}

export interface CreateProjectRequest {
  name: string
  notes: string | null
  /** Absolute; `.tbench` is appended when missing. */
  path: string
}

export interface SaveProjectAsRequest extends CreateProjectRequest {
  /** Copy the whole file (results included), or a fresh file with the measurement types only. */
  includeResults: boolean
}

export interface ProjectPropertiesRequest {
  name: string
  notes: string | null
}

export interface RecentProject {
  path: string
  name: string
  lastOpenedUtc: string
  /** Checked when listed: a file on an unplugged drive is shown as missing, not dropped. */
  exists: boolean
}

export interface StartupConfig {
  showWelcomeOnStartup: boolean
}

/** Configuration ▸ General ▸ Autosave: whether a measurement's settings are saved the moment they change. */
/**
 * Configuration ▸ Acquisition ▸ "Allow 384 kHz sample rate (experimental)". Adds 384 kHz to the
 * analyzer's reported capture rates, which is all the FFT screen's dropdown needs. Input-only: the
 * server refuses a voiced generator at that rate, because the QA403's DAC produces garbage there.
 */
export interface Experimental384kConfig {
  enabled: boolean
}

export interface AutosaveConfig {
  enabled: boolean
}

/**
 * Configuration ▸ General ▸ Detailed logging: whether the log carries every API request and the
 * Debug-level lines, for a fault report. `logsDirectory` is where the file the tester will be asked
 * for lives.
 */
export interface DiagnosticLoggingStatus {
  enabled: boolean
  logsDirectory: string
}

/**
 * Configuration ▸ Diagnostics ▸ Record every acquisition: `StreamRecorder.Describe()`. The switch
 * and the cap are the setting; everything below `recording` is what the recorder is doing now and
 * what the last recording came to, which the panel has no other way of knowing.
 */
export interface StreamRecordingStatus {
  enabled: boolean
  maxFileMegabytes: number
  directory: string
  keptRecordings: number
  recording: boolean
  currentName: string | null
  lastName: string | null
  lastSeconds: number
  lastInputFrames: number
  lastStimulusFrames: number
  lastBytes: number
  lastReachedCap: boolean
  droppedBuffers: number
}

/**
 * Configuration ▸ Diagnostics ▸ Capture suspect records: `DiagnosticsEndpoints.DiagnosticsStatus`.
 * Deliberately *not* persisted server-side — arming resets when the app restarts, because this
 * writes a CSV row per analysed record and a WAV per trigger, and a switch left on for a week is a
 * folder nobody asked for.
 */
export interface DiagnosticsStatus {
  enabled: boolean
  logMetrics: boolean
  captureRawOnTrigger: boolean
  triggerThresholdDb: number
  directory: string
  recordsObserved: number
  capturesWritten: number
  queueDrops: number
}

/** Configuration ▸ General ▸ Date and time format: an id from `dateTime.ts`; the server keeps the string. */
export interface DateTimeFormatConfig {
  format: string
}

/**
 * The Screenshot dialog's remembered answers (`screenshot.settings`). `title` is null unless
 * `saveText` was on; the ids are `screenshot.ts`'s and the server only checks they are present.
 */
export interface ScreenshotSettingsConfig {
  title: string | null
  saveText: boolean
  addDate: boolean
  widthPx: number
  aspectRatio: string
  destination: string
  monochrome: boolean
}

export interface FileBrowseEntry {
  name: string
  path: string
  modifiedUtc: string | null
  sizeBytes: number | null
}

export interface FileBrowseRoot {
  label: string
  path: string
}

/** One directory listing for the in-app file dialog — see FileBrowserService. */
export interface FileBrowseResponse {
  path: string
  parent: string | null
  directories: FileBrowseEntry[]
  files: FileBrowseEntry[]
  roots: FileBrowseRoot[]
  defaultFolder: string
  /** The server's directory separator, so a folder and a file name are joined the way it will read them. */
  separator: string
}

/**
 * The folders the file browser was last in, keyed by what it was being used for. Server-side rather
 * than in browser storage, because it has to survive a relaunch.
 */
export interface LastDirectories {
  directories: Record<string, string>
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

/** Mirrors Tonebench.App.Endpoints.MeasurementTypeResultCountResponse. */
export interface MeasurementTypeResultCount {
  count: number
}

/**
 * The trash, as the two levels the view draws. Mirrors Tonebench.App.Endpoints.TrashListingResponse.
 *
 * `totalCount` is what the sidebar's badge shows: the measurements actually in the trash plus every
 * trashed result. A header-only entry (one whose measurement is still there) is not an item.
 */
export interface TrashListing {
  types: TrashTypeEntry[]
  totalCount: number
}

export interface TrashTypeEntry {
  measurementTypeId: number
  name: string
  kind: string
  /** Null when the measurement itself is not in the trash — then this is only a header for its trashed results. */
  deletedUtc: string | null
  /** How many results would come back with this measurement. Not the nested ones: those were deleted separately and stay. */
  restorableResultCount: number
  /** A live measurement has taken this name, so the restore is refused until one of them is renamed. */
  nameTaken: boolean
  results: TrashResultEntry[]
}

export interface TrashResultEntry {
  resultId: number
  name: string
  notes: string | null
  timestampUtc: string
  deletedUtc: string
}

/** What an Empty Trashcan destroyed. Mirrors Tonebench.App.Endpoints.TrashEmptiedResponse. */
export interface TrashEmptied {
  measurementTypes: number
  results: number
}

/** How long the trash keeps things. Mirrors Tonebench.Data.TrashRetentionConfig; 1–3650 days. */
export interface TrashRetentionConfig {
  days: number
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
  /** Whether the live spectrum line itself is drawn — see ChartDisplayPanel.vue's "FFT data" checkbox. Null means drawn. */
  chartShowFftTrace?: boolean | null
  /** Whether the harmonic markers are drawn over the spectrum — see ChartDisplayPanel.vue's "Harmonic markers" checkbox. Null means drawn. */
  chartShowHarmonicMarkers?: boolean | null
  /** How the Harmonics and Other peaks tables are sorted — see measurements/peakSort.ts. Null is each table's natural order. */
  chartHarmonicsSort?: string | null
  chartOtherPeaksSort?: string | null
  /** Off (null/false) unless the user opts in — see ChartDisplayPanel.vue's "Autofit when measurement done" checkbox. */
  chartAutofitOnDone?: boolean | null
  /** Which of the FFT screen's secondary panels are drawn — null (a type saved before these existed) means the Oscilloscope and Harmonics shown, the Spectrogram and Other peaks hidden. */
  chartShowSpectrogram?: boolean | null
  chartShowOscilloscope?: boolean | null
  chartShowHarmonics?: boolean | null
  chartShowOtherPeaks?: boolean | null
  /** This type's stored IMD template and analysis choice — null for a type without two-tone IMD. See useGeneratorStimulus.ts. */
  imdPreset?: string | null
  imdMethod?: ImdMethodName | null
  imdMaxOrder?: number | null
  /** The stored weighting (already applied to the pipeline by the server), for the settings panel to show. */
  weighting?: WeightingStandardName | null
  weightingUser1?: boolean | null
  weightingUser2?: boolean | null
  /** The DUT gain under the chart's unit dropdown, and that unit — see useChartDisplay.ts and spectrumUnits.ts. */
  dutGainDb?: number | null
  chartUnit?: string | null
  chartShowWeightingCurve?: boolean | null
  /** Whether the oscilloscope shows the THD residual — live pipeline state like the IMD spec, re-applied on load. */
  scopeResidual?: boolean | null
  /** The unit the generator panel's level boxes are typed in — see generatorLevel.ts. Null means dBV; the tones are always dBV. */
  generatorLevelUnit?: string | null
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
  /** Whether the live spectrum line itself is drawn — see ChartDisplayPanel.vue's "FFT data" checkbox. Null means drawn. */
  chartShowFftTrace?: boolean | null
  /** Whether the harmonic markers are drawn over the spectrum — see ChartDisplayPanel.vue's "Harmonic markers" checkbox. Null means drawn. */
  chartShowHarmonicMarkers?: boolean | null
  /** How the Harmonics and Other peaks tables are sorted — see measurements/peakSort.ts. Null is each table's natural order. */
  chartHarmonicsSort?: string | null
  chartOtherPeaksSort?: string | null
  /** Off (null/false) unless the user opts in — see ChartDisplayPanel.vue's "Autofit when measurement done" checkbox. */
  chartAutofitOnDone?: boolean | null
  /** Which of the FFT screen's secondary panels are drawn — null (a type saved before these existed) means the Oscilloscope and Harmonics shown, the Spectrogram and Other peaks hidden. */
  chartShowSpectrogram?: boolean | null
  chartShowOscilloscope?: boolean | null
  chartShowHarmonics?: boolean | null
  chartShowOtherPeaks?: boolean | null
  /** The generator panel's IMD template and analysis choice — see useGeneratorStimulus.ts. */
  imdPreset?: string | null
  imdMethod?: ImdMethodName | null
  imdMaxOrder?: number | null
  /** The DUT gain under the chart's unit dropdown, and that unit — see useChartDisplay.ts and spectrumUnits.ts. */
  dutGainDb?: number | null
  chartUnit?: string | null
  chartShowWeightingCurve?: boolean | null
  /** The unit the generator panel's level boxes are typed in — see generatorLevel.ts. */
  generatorLevelUnit?: string | null
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
  /**
   * `null` for a measured run, `'simulation'` for one imported from an LTspice .raw. On the
   * summary so the history list can group by it without a fetch per row.
   */
  source: string | null
}

export const SIMULATION_SOURCE = 'simulation'

export interface SimulationTrace {
  index: number
  name: string
  /** `V` or `A`. A branch-current trace is in amps, so the unit travels with it. */
  unit: string
}

export interface SimulationFileInfo {
  path: string
  title: string
  date: string | null
  plotname: string
  flags: string[]
  pointCount: number
  startTimeS: number
  endTimeS: number
  durationS: number
  nativeRateHz: number
  minStepS: number
  medianStepS: number
  maxStepS: number
  largestFftSize: number
  suggestedFftSize: number
  /**
   * What the result's resolution will be, at *any* FFT size — it is 1/duration. Only a longer
   * simulation improves it.
   */
  binWidthHz: number
  traces: SimulationTrace[]
  warnings: string[]
  /**
   * The runs the file holds, always at least one. A `.step`ped transient holds one per step; the
   * figures above describe the *first*, since the steps are the same directive solved at different
   * parameters.
   */
  sweeps: SimulationSweep[]
}

/** One run inside a `.raw` file. */
export interface SimulationSweep {
  index: number
  pointCount: number
  startTimeS: number
  endTimeS: number
  /** The simulator's own words for this step, from the sibling `.log`; null for a QSpice run, which writes none. */
  label: string | null
}

export interface ImportSimulationRequest {
  path: string
  leftTraceIndex: number
  rightTraceIndex?: number | null
  startTimeS?: number
  levelOffsetDb?: number
  fftSize?: number
  window?: string
  measurementLoFreqHz?: number
  measurementHiFreqHz?: number
  name?: string | null
  notes?: string | null
  periodicRecord?: boolean
  /** Which runs of a `.step`ped file to import, or omitted for all of them. */
  sweepIndices?: number[]
}

export interface ReanalyzeSimulationRequest {
  startTimeS?: number
  levelOffsetDb?: number
  fftSize?: number
  window?: string
  measurementLoFreqHz?: number
  measurementHiFreqHz?: number
  periodicRecord?: boolean
}

export interface ImportedSimulation {
  resultId: number
}

/**
 * What an `.ac` sweep holds. A separate shape from `SimulationFileInfo`, not an extension of it:
 * a transient run is described by a duration and a timestep and a sweep by a range and a point
 * density, and neither set means anything to the other.
 */
export interface AcSimulationFileInfo {
  path: string
  title: string
  date: string | null
  plotname: string
  flags: string[]
  pointCount: number
  startFreqHz: number
  endFreqHz: number
  /** How finely the sweep is sampled — `.ac dec 100` gives 100. */
  pointsPerDecade: number
  /** False for an `.ac lin` sweep, which puts almost every point in its top decade. */
  isLogSpaced: boolean
  traces: SimulationTrace[]
  warnings: string[]
  /**
   * The sweeps the file holds, always at least one. A `.step`ped run holds one per step, written
   * end to end, and each is imported as a result of its own — `UNIQUE(capture_id, kind)` holds one
   * curve of each kind per result, and separate results are what makes them independently
   * overlayable anyway.
   */
  sweeps: AcSimulationSweep[]
}

/** One sweep of an `.ac` file. `pointCount`/`startFreqHz`/... on the parent describe the first. */
export interface AcSimulationSweep {
  index: number
  pointCount: number
  startFreqHz: number
  endFreqHz: number
  pointsPerDecade: number
  isLogSpaced: boolean
  /**
   * What the simulator called this step, e.g. `freq=2000`, read from the sibling `.log`. Null when
   * there is nothing to read it from, which is every QSpice run: it prints its `.step` lines to the
   * console and writes no log file.
   */
  label: string | null
}

export interface ImportAcSimulationRequest {
  path: string
  outputTraceIndex: number
  /** The node to divide by, or null to take the output as the transfer function on its own. */
  referenceTraceIndex?: number | null
  levelOffsetDb?: number
  analysisLowHz?: number
  analysisHighHz?: number
  bandwidthReference?: string | null
  bandwidthReferenceLowHz?: number
  bandwidthReferenceHighHz?: number
  name?: string | null
  notes?: string | null
  /** Which sweeps of a stepped file to import, or omitted for all of them. */
  sweepIndices?: number[]
}

/** One result id per sweep imported, in sweep order. One entry for an ordinary run. */
export interface ImportedSimulations {
  resultIds: number[]
}

/**
 * The same `.ac` file read as ohms: one voltage trace over one current trace. Both are required —
 * with a 1 A source the current column is a constant 1, so the "read the voltage as ohms" shortcut
 * is just picking that source's current trace, and no assumption is left to get wrong.
 */
export interface ImportAcImpedanceRequest {
  path: string
  voltageTraceIndex: number
  currentTraceIndex: number
  /**
   * `Auto`, `AsStored` or `Inverted`. Auto is almost always right: a simulator's source current
   * flows into its own + terminal, which puts V/I half a turn round, and a passive one-port cannot
   * present a negative real part across its band — so the data says which sign is meant.
   */
  currentDirection?: string | null
  name?: string | null
  notes?: string | null
  sweepIndices?: number[]
}

/** Omitting the direction leaves the curve facing the way it is. */
export interface ReanalyzeAcImpedanceRequest {
  /** `Auto`, `AsStored` or `Inverted`. */
  currentDirection?: string | null
}

/** Everything optional: null leaves that setting as it was stored. */
export interface ReanalyzeAcSimulationRequest {
  levelOffsetDb?: number
  analysisLowHz?: number
  analysisHighHz?: number
  /** Sent whole or not at all — a mode without its band would leave the band to be guessed. */
  bandwidthReference?: string | null
  bandwidthReferenceLowHz?: number
  bandwidthReferenceHighHz?: number
  rightChannelIsReference?: boolean
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
  /**
   * The stored window's equivalent noise bandwidth — converts this channel's dBV columns to
   * dBV/√Hz with *its own* window, which need not be the live one's. 0 on a row an older server
   * sends (no such server exists, but the field defaults that way).
   */
  enbwHz?: number
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
  /** `null` for a measured run, `'simulation'` for an imported one. */
  source?: string | null
  /** Non-null when this result carries stored samples, i.e. can be re-analysed. */
  pcmSampleRateHz?: number | null
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

/**
 * Mirrors Tonebench.App.Endpoints.AppIdentity — what a second instance of the app reads off port 8399
 * to decide whether the listener there is us. `version` is this build's informational version, read
 * from the assembly, so it is available with no network and whatever the update check is set to.
 */
export interface AppIdentity {
  product: string
  pid: number
  version: string
}

/** Mirrors Tonebench.App.Services.UpdateCheckResult. latestVersion/latestUrl are null when the check failed or found nothing newer. */
export interface UpdateStatus {
  currentVersion: string
  latestVersion: string | null
  latestUrl: string | null
  updateAvailable: boolean
}

/**
 * A non-2xx response.
 *
 * <p>`message` is <b>the server's own sentence and nothing else</b> — every endpoint here that
 * refuses something writes one for the person at the bench ("A measurement type named 'Frequency
 * response' already exists in this project."), and an error banner is where a person reads it.
 * Until 2026-10-11 it was prefixed with the method, the path and the status
 * (`POST /api/projects/1/measurement-types failed: 409 …`), which put a line of plumbing in front
 * of every refusal and made each one read as a fault in the app rather than an answer to what was
 * asked (Joost). Two panels had already grown their own regex to strip it back off.</p>
 *
 * <p>`body` is that same text unprocessed, kept for the places that must quote the server verbatim
 * or not at all — see issueReport.ts's `serverReason`. `request` is the method and path, which no
 * longer reach the screen; it is there so a log or a bug report can still say which call failed.</p>
 */
export class ApiError extends Error {
  readonly status: number
  readonly body: string
  /** `POST /api/projects/1/measurement-types` — for diagnostics, never for a banner. */
  readonly request: string

  constructor(status: number, body: string, request: string, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.body = body
    this.request = request
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  })

  if (!response.ok) {
    const body = await response.text()
    throw new ApiError(
      response.status,
      body,
      `${init?.method ?? 'GET'} /api${path}`,
      apiErrorMessage(response.status, body),
    )
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

/** Mirrors Tonebench.App.Services.ThdSweepLevelMode. */
export type SweepLevelMode = 'GeneratorVoltage' | 'OutputVoltage' | 'OutputPower'

/** Mirrors Tonebench.App.Services.ThdSweepLevelUnit. 'Watt' is only valid with the OutputPower mode. */
export type SweepLevelUnit = 'Dbv' | 'Volt' | 'MilliVolt' | 'Watt'

/**
 * Mirrors Tonebench.App.Services.ThdMethod: what each harmonic is divided by. Conventional is the
 * fundamental at the stimulus frequency; FrequencyNormalized is the fundamental the same sweep
 * measured at the harmonic's own frequency n·f, which removes the DUT's frequency response from
 * the reading. Frequency axis only.
 */
export type ThdMethodName = 'Conventional' | 'FrequencyNormalized'

/** Mirrors Tonebench.App.Services.ThdSweepPhase. */
export type SweepPhaseName =
  | 'Idle'
  | 'DeterminingLevel'
  | 'DeterminingRange'
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
  /**
   * How the harmonics are referenced. Under FrequencyNormalized the sweep keeps stepping the
   * fundamental past `endFreqHz` (to 5x it, or Nyquist) so every harmonic has a fundamental to be
   * divided by; those extra points come back like any other.
   */
  thdMethod: ThdMethodName
}

/**
 * Mirrors Tonebench.Data.ThdVsFrequencyParameters — what a ThdVsFrequency measurement type stores in
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
  /**
   * The rail previews' two **Fit all** toggles — the spectrum's level axis and the oscilloscope's
   * volts axis held to one window covering every point of every sweep on the chart. Display state
   * like the fields above; null on a type saved before they existed, read as off.
   */
  chartFitPreviewSpectrum?: boolean | null
  chartFitPreviewScope?: boolean | null
}

/**
 * Mirrors Tonebench.App.Endpoints.ThdAmplitudeSweepStartRequest. Much smaller than its frequency
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
  /** End the sweep once a channel's THD exceeds this many percent; the crossing point is kept. Null or absent means sweep to the end. */
  stopThdPercent?: number | null
}

/**
 * Mirrors Tonebench.Data.ThdVsAmplitudeParameters — what a ThdVsAmplitude measurement type stores in
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
  /** End the sweep once a channel's THD exceeds this many percent. Absent or null on a type saved without it, meaning sweep to the end. */
  stopThdPercent?: number | null
  /** The rail previews' Fit all toggles — see ThdVsFrequencyParameters.chartFitPreviewSpectrum. */
  chartFitPreviewSpectrum?: boolean | null
  chartFitPreviewScope?: boolean | null
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

/** One decimated (x, min, max) trace — mirrors the streaming protocol's SweepPreviewSeries and Tonebench.Data.StoredPreviewSeries. */
export interface SweepPreviewSeries {
  /** Frequency (Hz) for an FFT preview, elapsed time (s) for a scope preview. */
  x: number[]
  min: number[]
  max: number[]
}

/**
 * The least the two small preview charts need of a point: which input it was captured on, the tone
 * that was playing, and the two decimated captures.
 *
 * <p>A structural type rather than a base interface, because two unrelated measurements produce it:
 * a sweep point (<see cref="ThdSweepPoint"/> and the IMD sweep's) and a stepped-sine response point
 * (useFrequencyResponse's ResponsePreviewPoint). Both satisfy it as they stand, which is what lets
 * SweepFftPreviewChart/SweepScopePreviewChart and `nearestSweepPoint` serve both screens with no
 * conversion in between.</p>
 */
export interface PreviewPoint {
  channel: number
  freqHz: number
  previewFft: SweepPreviewSeries | null
  previewScope: SweepPreviewSeries | null
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
  /**
   * The frequency-normalized figures — each harmonic over the fundamental measured at its own
   * frequency. Null on a conventional sweep; on a normalized one, null until the sweep has
   * measured the fundamental at 2f (the point is re-sent as its orders fill in), and null for good
   * near the top of the extended grid, above which nothing was swept.
   */
  normalizedThdPercent: number | null
  /** Normalized D2..D5, index 0 = 2nd harmonic; four entries, each null without a reference. Null as a whole on a conventional sweep. */
  normalizedHarmonicPercent: (number | null)[] | null
  normalizedD6PlusPercent: number | null
}

// The IMD-vs-amplitude sweep — mirrors Tonebench.App/Endpoints/ImdSweepEndpoints.cs and
// Services/ImdSweepModels.cs. Levels are sine-equivalent volts: the single sine whose peak equals
// the two-tone pair's peak (see imd/presets.ts).

export interface ImdSweepStartRequest {
  /** An ImdPresetId; 'Custom' for a typed pair. Kept for the record — the tones below are what run. */
  preset: string
  toneLowHz: number
  toneHighHz: number
  /** Amplitude ratio low:high — 4 for SMPTE/DIN, 1 for CCIF. */
  lowToHighRatio: number
  method: ImdMethodName
  maxOrder: number
  startLevelVolt: number
  endLevelVolt: number
  stepsPerOctave: number
  loadOhms: number
  processLeft: boolean
  processRight: boolean
  averageCount: number
  measureNoiseFloor: boolean
  /** Highest product frequency counted; null means the measurement band's upper edge (in-band products only). */
  maxProductFreqHz: number | null
}

export interface ImdSweepStarted {
  pointCount: number
  levelsVolt: number[]
}

/** Mirrors Tonebench.App.Services.ImdSweepPoint. */
export interface ImdSweepPoint {
  channel: number
  generatorSineEquivalentDbv: number
  toneLowDbv: number
  toneHighDbv: number
  sineEquivalentDbv: number
  combinedRmsDbv: number
  gainDb: number
  powerWatt: number
  imdPercent: number
  /** Per-order figure, index 0 = order 2, contiguous up to the last order with any product in band. */
  orderPercent: number[]
  noisePercent: number
  toneLowHz: number
  toneHighHz: number
  previewFft: SweepPreviewSeries | null
  previewScope: SweepPreviewSeries | null
}

export interface ImdSweepStatus {
  running: boolean
  phase: SweepPhaseName
  index: number
  total: number
  currentLevelVolt: number
  error: string | null
  points: ImdSweepPoint[]
  method: ImdMethodName
  maxOrder: number
}

/**
 * Mirrors Tonebench.Data.ImdVsAmplitudeParameters. The chart display fields reuse the THD amplitude
 * sweep's names (chartShowThd is the IMD total, chartShowD2..D5 the IM2..IM5 orders, chartShowD6Plus
 * IM6+), so sweep/display.ts reads both records alike.
 */
export interface ImdVsAmplitudeParameters {
  preset: string
  toneLowHz: number
  toneHighHz: number
  lowToHighRatio: number
  method: ImdMethodName
  maxOrder: number
  startLevelVolt: number
  endLevelVolt: number
  stepsPerOctave: number
  loadOhms: number
  processLeft: boolean
  processRight: boolean
  fftSize: number
  window: WindowType
  averageCount: number
  sampleRateHz: number
  measureNoiseFloor: boolean
  maxProductFreqHz: number | null
  chartXAxis?: string | null
  chartUnit?: string | null
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
  chartXFrom?: number | null
  chartXTo?: number | null
  chartYTopDb?: number | null
  chartYBottomDb?: number | null
  chartAutofitOnDone?: boolean | null
  captureDelayMs?: number | null
  /** The rail previews' Fit all toggles — see ThdVsFrequencyParameters.chartFitPreviewSpectrum. */
  chartFitPreviewSpectrum?: boolean | null
  chartFitPreviewScope?: boolean | null
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
  /** Which quantity the running sweep steps. Mirrors Tonebench.App.Services.SweepAxis. */
  axis: 'Frequency' | 'Amplitude'
  /** The generator amplitude of the point being measured, in volts. Only moves on the amplitude axis. */
  currentAmplitudeVolt: number
  error: string | null
  /** Every point measured so far. Present on a finished, cancelled or still-running sweep alike. */
  points: ThdSweepPoint[]
  /** How the points' harmonics are referenced — which of a point's two sets of figures this run is about. */
  thdMethod: ThdMethodName
  /** Why a Finished sweep ended before its last planned point (the THD stop threshold). Null for a sweep that ran to the end. */
  stopReason?: string | null
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

  /**
   * Switches who chooses both analog ranges. Replaces the old setAutoRange, which spoke only for
   * the input attenuator — two calls for one mode would be two ways to reach half a state each.
   * Refused (409) while a measurement is running.
   */
  setRangingMode: (mode: RangingModeName) =>
    request<DeviceStatus>('/device/ranging', { method: 'POST', body: JSON.stringify({ mode }) }),

  setAutoRangeStepDownDelay: (delayMs: number) =>
    request<DeviceStatus>('/device/auto-range-step-down-delay', { method: 'POST', body: JSON.stringify({ delayMs }) }),

  setDeviceKind: (kind: DeviceKindName) =>
    request<DeviceStatus>('/device/kind', { method: 'POST', body: JSON.stringify({ kind }) }),

  // ---- sound card -------------------------------------------------------------------------
  /** What the machine has. A native call, so the dialog asks for it on open rather than on the poll. */
  getSoundCardDevices: (refresh = false) =>
    request<SoundCardInventory>(`/soundcard/devices${refresh ? '?refresh=true' : ''}`),
  /** Whether a pairing works and at which rates. Re-asked whenever either side or either offset changes. */
  probeSoundCard: (q: {
    inputHostApi: string
    inputDevice: string
    outputHostApi: string
    outputDevice: string
    inputChannelOffset?: number
    outputChannelOffset?: number
  }) => request<SoundCardProbe>(`/soundcard/probe${toQuery({ ...q })}`),
  getSoundCardConfig: () => request<SoundCardConfig>('/config/soundcard'),
  setSoundCardConfig: (config: SoundCardConfig) =>
    request<SoundCardConfig>('/config/soundcard', { method: 'PUT', body: JSON.stringify(config) }),
  getSoundCardCalibration: () => request<SoundCardCalibrationState>('/soundcard/calibration'),
  startSoundCardTone: (dbFs: number) =>
    request<SoundCardCalibrationState>('/soundcard/calibration/output/start', {
      method: 'POST',
      body: JSON.stringify({ dbFs }),
    }),
  applySoundCardOutput: (value: number, unit: VoltageEntryUnitName) =>
    request<SoundCardCalibrationResult>('/soundcard/calibration/output/apply', {
      method: 'POST',
      body: JSON.stringify({ value, unit }),
    }),
  measureSoundCardInput: (value: number, unit: VoltageEntryUnitName, channel: 'Left' | 'Right') =>
    request<SoundCardCalibrationResult>('/soundcard/calibration/input', {
      method: 'POST',
      body: JSON.stringify({ value, unit, channel }),
    }),
  /**
   * Both inputs at once, against the card's own output through patch cables. Needs the output
   * calibrated, and a cable on each channel.
   */
  measureSoundCardInputViaLoopback: (dbFs: number) =>
    request<SoundCardCalibrationResult>('/soundcard/calibration/input/loopback', {
      method: 'POST',
      body: JSON.stringify({ dbFs }),
    }),
  /** What the inputs are hearing right now, for the level bars. */
  getSoundCardLevels: () => request<SoundCardLevels>('/soundcard/levels'),
  /**
   * Raises the ASIO driver's own panel. **Does not resolve until the user closes it** — the panel
   * is modal and PortAudio offers no other way to show it — so the caller shows that it is open.
   */
  showSoundCardControlPanel: (hostApi: string, deviceName: string) =>
    request<SoundCardControlPanelResult>('/soundcard/control-panel', {
      method: 'POST',
      body: JSON.stringify({ hostApi, deviceName }),
    }),
  cancelSoundCardCalibration: () =>
    request<SoundCardCalibrationState>('/soundcard/calibration/cancel', { method: 'POST' }),

  setSimulatedProfile: (patch: SimulatedProfileRequest) =>
    request<DeviceStatus>('/device/sim-profile', { method: 'POST', body: JSON.stringify(patch) }),

  setGenerator: (config: GeneratorRequest) =>
    request<DeviceStatus>('/generator', { method: 'POST', body: JSON.stringify(config) }),

  startAcquisition: (config: AcquisitionStartRequest) =>
    request<DeviceStatus>('/acquisition/start', { method: 'POST', body: JSON.stringify(config) }),

  stopAcquisition: () => request<DeviceStatus>('/acquisition/stop', { method: 'POST' }),
  /** Cancels every running measurement (sweeps, responses, impedance…) and waits for them to unwind; the plain acquisition is left to stopAcquisition. */
  cancelRuns: () => request<DeviceStatus>('/acquisition/cancel-runs', { method: 'POST' }),

  getSpectrumConfig: () => request<SpectrumConfig>('/acquisition/spectrum-config'),

  setSpectrumConfig: (config: SpectrumConfigRequest) =>
    request<SpectrumConfig>('/acquisition/spectrum-config', { method: 'POST', body: JSON.stringify(config) }),

  /** Backs the "Clear" button: discards the retained (stopped) spectrum server-side, so Save measurement result has nothing stale left to save. */
  clearSpectrum: () => request<void>('/acquisition/clear', { method: 'POST' }),

  // The open project file — see ProjectSessionEndpoints.cs. Every mutation answers with the new
  // session state; 400 carries a message about the file, 409 one about why not right now (a
  // measurement running, a target that exists, nothing open).
  getProjectSession: () => request<ProjectSession>('/project'),

  openProject: (path: string) => request<ProjectSession>('/project/open', { method: 'POST', body: JSON.stringify({ path }) }),

  createProject: (req: CreateProjectRequest) => request<ProjectSession>('/project/create', { method: 'POST', body: JSON.stringify(req) }),

  closeProject: () => request<void>('/project/close', { method: 'POST' }),

  /** The path is sent back and checked against the open project, so a stale tab cannot delete whatever is open now. */
  deleteProject: (path: string) => request<void>('/project/delete', { method: 'POST', body: JSON.stringify({ path }) }),

  saveProjectAs: (req: SaveProjectAsRequest) => request<ProjectSession>('/project/save-as', { method: 'POST', body: JSON.stringify(req) }),

  updateProjectProperties: (req: ProjectPropertiesRequest) =>
    request<ProjectSession>('/project/properties', { method: 'PUT', body: JSON.stringify(req) }),

  listRecentProjects: () => request<RecentProject[]>('/projects/recent'),

  removeRecentProject: (path: string) => request<void>('/projects/recent/remove', { method: 'POST', body: JSON.stringify({ path }) }),

  getStartupConfig: () => request<StartupConfig>('/config/startup'),

  setStartupConfig: (config: StartupConfig) =>
    request<StartupConfig>('/config/startup', { method: 'PUT', body: JSON.stringify(config) }),

  getAutosaveConfig: () => request<AutosaveConfig>('/config/autosave'),

  setAutosaveConfig: (config: AutosaveConfig) =>
    request<AutosaveConfig>('/config/autosave', { method: 'PUT', body: JSON.stringify(config) }),

  getExperimental384k: () => request<Experimental384kConfig>('/config/experimental-384k'),

  setExperimental384k: (config: Experimental384kConfig) =>
    request<Experimental384kConfig>('/config/experimental-384k', {
      method: 'PUT',
      body: JSON.stringify(config),
    }),

  getDiagnosticLogging: () => request<DiagnosticLoggingStatus>('/config/diagnostic-logging'),

  setDiagnosticLogging: (enabled: boolean) =>
    request<DiagnosticLoggingStatus>('/config/diagnostic-logging', { method: 'PUT', body: JSON.stringify({ enabled }) }),

  /** Opens the log folder in the system file manager, on the machine the server runs on. */
  openLogFolder: () => request<void>('/config/diagnostic-logging/open-folder', { method: 'POST' }),

  getStreamRecording: () => request<StreamRecordingStatus>('/config/stream-recording'),

  setStreamRecording: (enabled: boolean, maxFileMegabytes: number) =>
    request<StreamRecordingStatus>('/config/stream-recording', {
      method: 'PUT',
      body: JSON.stringify({ enabled, maxFileMegabytes }),
    }),

  openRecordingsFolder: () => request<void>('/config/stream-recording/open-folder', { method: 'POST' }),

  openDiagnosticsFolder: () => request<void>('/config/diagnostics/open-folder', { method: 'POST' }),

  getDiagnostics: () => request<DiagnosticsStatus>('/diagnostics'),

  /** Every field optional: an omitted one leaves that setting alone, so arming is `{ enabled: true }`. */
  setDiagnostics: (config: Partial<Pick<DiagnosticsStatus, 'enabled' | 'logMetrics' | 'captureRawOnTrigger' | 'triggerThresholdDb'>>) =>
    request<DiagnosticsStatus>('/diagnostics', { method: 'POST', body: JSON.stringify(config) }),

  getDateTimeFormatConfig: () => request<DateTimeFormatConfig>('/config/date-time-format'),

  setDateTimeFormatConfig: (config: DateTimeFormatConfig) =>
    request<DateTimeFormatConfig>('/config/date-time-format', { method: 'PUT', body: JSON.stringify(config) }),

  getScreenshotSettings: () => request<ScreenshotSettingsConfig>('/config/screenshot-settings'),

  setScreenshotSettings: (settings: ScreenshotSettingsConfig) =>
    request<ScreenshotSettingsConfig>('/config/screenshot-settings', { method: 'PUT', body: JSON.stringify(settings) }),

  /**
   * One directory of the server's file system, for the in-app file dialog. No path lists the
   * default projects folder; `ext` filters the files shown (the server's default is `.tbench`).
   */
  browseFiles: (path: string | null, ext?: string) => {
    const params = new URLSearchParams()
    if (path) params.set('path', path)
    if (ext) params.set('ext', ext)
    const query = params.toString()
    return request<FileBrowseResponse>(`/files/browse${query ? `?${query}` : ''}`)
  },

  /** Where the file browser was last pointed, per purpose. See FileBrowserDialog's `rememberAs`. */
  lastDirectories: () => request<LastDirectories>('/config/last-directories'),

  rememberDirectory: (key: string, path: string) =>
    request<LastDirectories>(`/config/last-directories/${encodeURIComponent(key)}`, {
      method: 'PUT',
      body: JSON.stringify({ path }),
    }),

  listMeasurementTypes: (projectId: number) => request<MeasurementType[]>(`/projects/${projectId}/measurement-types`),

  createMeasurementType: (projectId: number, req: SaveMeasurementTypeRequest) =>
    request<{ id: number }>(`/projects/${projectId}/measurement-types`, { method: 'POST', body: JSON.stringify(req) }),

  updateMeasurementType: (id: number, req: SaveMeasurementTypeRequest) =>
    request<void>(`/measurement-types/${id}`, { method: 'PUT', body: JSON.stringify(req) }),

  /**
   * How many results `deleteMeasurementType` would destroy along with the type — what its
   * confirmation quotes. Read here rather than off `listMeasurementTypes`, whose rows are only
   * reloaded when a *type* changes and so would still say 0 for a measurement run and saved since
   * the project was opened; and not counted from `listMeasurements`, which pages at 100.
   */
  countMeasurementTypeResults: (id: number) =>
    request<MeasurementTypeResultCount>(`/measurement-types/${id}/result-count`),

  /**
   * Moves the measurement to the trash, every result stored under it included — nothing is
   * destroyed, and `restoreTrashedMeasurementType` brings all of it back. It still asks first (see
   * useProjects.deleteMeasurementType): the row is narrow and a stray click should not make a
   * measurement vanish from the sidebar unannounced.
   */
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

  /**
   * Reads an LTspice .raw's header and time axis. A path rather than an upload, because the
   * browser cannot read a local file — the same shape the user-weighting import uses, and the
   * same FileBrowserDialog picks it.
   */
  inspectSimulation: (path: string, measurementHiFreqHz?: number, measurementLoFreqHz?: number) =>
    request<SimulationFileInfo>('/simulations/inspect', {
      method: 'POST',
      body: JSON.stringify({ path, measurementHiFreqHz, measurementLoFreqHz }),
    }),

  importSimulation: (measurementTypeId: number, body: ImportSimulationRequest) =>
    request<ImportedSimulations>(`/simulations/${measurementTypeId}/import`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  /**
   * The same stepped file read as a sweep rather than as a stack of spectra: each run becomes one
   * point of a THD-vs-frequency result, at the fundamental that run's own analysis located.
   */
  importSimulationSweep: (measurementTypeId: number, body: ImportSimulationRequest) =>
    request<ImportedSimulation>(`/simulations/${measurementTypeId}/import-sweep`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  /** Rebuilds a stored simulation's spectrum from its own samples at new settings. */
  reanalyzeSimulation: (resultId: number, body: ReanalyzeSimulationRequest) =>
    request<ImportedSimulation>(`/measurements/${resultId}/reanalyze`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  /** Reads an `.ac` sweep's header and frequency axis — the response counterpart of inspectSimulation. */
  inspectAcSimulation: (path: string) =>
    request<AcSimulationFileInfo>('/simulations/inspect-ac', {
      method: 'POST',
      body: JSON.stringify({ path }),
    }),

  importAcSimulation: (measurementTypeId: number, body: ImportAcSimulationRequest) =>
    request<ImportedSimulations>(`/simulations/${measurementTypeId}/import-ac`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  /** The same file as an impedance curve, onto an Impedance measurement. */
  importAcImpedance: (measurementTypeId: number, body: ImportAcImpedanceRequest) =>
    request<ImportedSimulations>(`/simulations/${measurementTypeId}/import-impedance`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  /**
   * Turns a stored imported impedance over, or leaves it alone. The direction is the only thing an
   * impedance import can be asked to change — there is no band, no corner search and no reference
   * level on an impedance curve — and it needs no file, because negating one leaves the magnitude
   * alone and moves the phase half a turn.
   */
  reanalyzeAcImpedance: (resultId: number, body: ReanalyzeAcImpedanceRequest) =>
    request<ImportedSimulation>(`/measurements/${resultId}/reanalyze-impedance`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  /**
   * Rebuilds a stored `.ac` sweep's curves from its own rows at a new band or reference. Nothing
   * is re-read from the file: the rows hold the sweep at full resolution.
   */
  reanalyzeAcSimulation: (resultId: number, body: ReanalyzeAcSimulationRequest) =>
    request<ImportedSimulation>(`/measurements/${resultId}/reanalyze-response`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  /**
   * A saved measurement's spectrum columns, re-decimated server-side over `window` — the same
   * bins the detail call returns, but with the display budget spread across the zoom instead of
   * the whole span, so a stored trace resolves more of itself as you zoom in. Omit the window for
   * the whole span. Only the columns come back: the metrics and parameters a zoom does not change
   * stay on the detail call.
   */
  getMeasurementSpectrum: (id: number, window: { loFreqHz: number; hiFreqHz: number } | null) =>
    request<{ channelsData: MeasurementChannel[] }>(
      window === null
        ? `/measurements/${id}/spectrum`
        : `/measurements/${id}/spectrum?loHz=${window.loFreqHz}&hiHz=${window.hiFreqHz}`,
    ),

  updateMeasurement: (id: number, req: UpdateMeasurementRequest) =>
    request<void>(`/measurements/${id}`, { method: 'PUT', body: JSON.stringify(req) }),

  /** Moves the result to the trash. Nothing is destroyed; `restoreTrashedMeasurement` brings it back. */
  deleteMeasurement: (id: number) => request<void>(`/measurements/${id}`, { method: 'DELETE' }),

  // The trash itself. Everything above deletes *into* it; these are what lists it, takes things back
  // out, and destroys them for good.

  listTrash: () => request<TrashListing>('/trash'),

  /** 409 when a live measurement has taken the name — the listing's `nameTaken` says so beforehand. */
  restoreTrashedMeasurementType: (id: number) =>
    request<void>(`/trash/measurement-types/${id}/restore`, { method: 'POST' }),

  /** 409 while the result's own measurement is in the trash too — restore that first. */
  restoreTrashedMeasurement: (id: number) =>
    request<void>(`/trash/measurements/${id}/restore`, { method: 'POST' }),

  /** Permanent, one item. Ask first — see trash.ts's confirmations. */
  purgeTrashedMeasurementType: (id: number) =>
    request<void>(`/trash/measurement-types/${id}`, { method: 'DELETE' }),

  purgeTrashedMeasurement: (id: number) => request<void>(`/trash/measurements/${id}`, { method: 'DELETE' }),

  /** Empty Trashcan: permanently deletes everything in it, and says how much that was. */
  emptyTrash: () => request<TrashEmptied>('/trash', { method: 'DELETE' }),

  getTrashRetention: () => request<TrashRetentionConfig>('/config/trash-retention'),
  setTrashRetention: (config: TrashRetentionConfig) =>
    request<TrashRetentionConfig>('/config/trash-retention', { method: 'PUT', body: JSON.stringify(config) }),

  /**
   * Not fetched through `request` — a plain download link the caller renders as an
   * `<a href=... download>`.
   *
   * `part` is read by an impulse-response result alone, whose three grids cannot share one table:
   * `impulse` (its samples against time, the default), `curves` (the gated magnitude and phase
   * against frequency) or `harmonics`. Every other kind ignores it, so a caller that does not know
   * the kind can leave it out.
   */
  measurementCsvExportUrl: (id: number, part?: 'impulse' | 'curves' | 'harmonics') =>
    part ? `/api/measurements/${id}/export.csv?part=${part}` : `/api/measurements/${id}/export.csv`,

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

  // The IMD-vs-amplitude sweep: its own runner, so its own five verbs. A stored result reads back
  // through getStoredSweep like any sweep (see imd/points.ts for the metric names).
  startImdSweep: (req: ImdSweepStartRequest) =>
    request<ImdSweepStarted>('/imd-sweep/start', { method: 'POST', body: JSON.stringify(req) }),
  cancelImdSweep: () => request<ImdSweepStatus>('/imd-sweep/cancel', { method: 'POST' }),
  clearImdSweep: () => request<ImdSweepStatus>('/imd-sweep/clear', { method: 'POST' }),
  getImdSweepStatus: () => request<ImdSweepStatus>('/imd-sweep/status'),
  saveImdSweep: (req: { measurementTypeId: number; name: string; notes: string | null }) =>
    request<{ id: number }>('/imd-sweep/save', { method: 'POST', body: JSON.stringify(req) }),

  // The impedance measurement. The same five verbs as every other runner; a stored result is read
  // back through getResponseTraces, because impedance stores its curves in the same response_trace
  // rows (with different `kind` strings and dB-relative-to-1-ohm magnitudes).
  // The application configuration, which lives in its own database. GET returns defaults rather
  // than 404 on a machine that has never calibrated, so no caller needs a special case.
  getImpedanceFixture: () => request<ImpedanceFixtureConfig>('/config/impedance-fixture'),

  setImpedanceFixture: (fixture: ImpedanceFixtureConfig) =>
    request<ImpedanceFixtureConfig>('/config/impedance-fixture', {
      method: 'PUT',
      body: JSON.stringify(fixture),
    }),

  clearImpedanceFixture: () =>
    request<ImpedanceFixtureConfig>('/config/impedance-fixture', { method: 'DELETE' }),

  getUiLayout: () => request<UiLayoutConfig>('/config/ui-layout'),

  getReportSettings: () => request<ReportSettingsConfig>('/config/report-settings'),

  setReportSettings: (settings: ReportSettingsConfig) =>
    request<ReportSettingsConfig>('/config/report-settings', { method: 'PUT', body: JSON.stringify(settings) }),

  /**
   * The rendered PDF. Not `request`, which reads the body as JSON; and a POST rather than a GET link
   * because the body is the whole document, chart PNGs included.
   */
  renderReportPdf: async (document: ReportDocument): Promise<Blob> => {
    const response = await fetch('/api/reports/pdf', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(document),
    })
    if (!response.ok) {
      const body = await response.text()
      // The same sentence an ApiError would carry -- this one call reads the response itself (it
      // wants a Blob), so it cannot go through `request`.
      throw new Error(apiErrorMessage(response.status, body))
    }
    return response.blob()
  },

  setUiLayout: (layout: UiLayoutConfig) =>
    request<UiLayoutConfig>('/config/ui-layout', { method: 'PUT', body: JSON.stringify(layout) }),

  getCaptureDelay: () => request<CaptureDelayConfig>('/config/capture-delay'),

  getUserWeighting: () => request<UserWeightingResponse>('/config/weighting'),

  /** The combined weighting curve in effect, log-spaced over [loHz, hiHz] — for the chart's overlay. */
  getWeightingCurve: (loHz: number, hiHz: number, points = 400) =>
    request<WeightingCurveResponse>(`/acquisition/weighting-curve?loHz=${loHz}&hiHz=${hiHz}&points=${points}`),

  /** Reads and parses a "frequency, dB" file on the server's machine into user weighting slot 1 or 2. */
  importUserWeighting: (slot: 1 | 2, path: string) =>
    request<UserWeightingResponse>(`/config/weighting/${slot}/import`, { method: 'POST', body: JSON.stringify({ path }) }),

  clearUserWeighting: (slot: 1 | 2) => request<UserWeightingResponse>(`/config/weighting/${slot}`, { method: 'DELETE' }),

  setCaptureDelay: (config: CaptureDelayConfig) =>
    request<CaptureDelayConfig>('/config/capture-delay', { method: 'PUT', body: JSON.stringify(config) }),

  getExternalGain: () => request<ExternalGainConfig>('/config/external-gain'),
  setExternalGain: (config: ExternalGainConfig) =>
    request<ExternalGainConfig>('/config/external-gain', { method: 'PUT', body: JSON.stringify(config) }),

  startImpedance: (req: ImpedanceStartRequest) =>
    request<ImpedanceStarted>('/impedance/start', { method: 'POST', body: JSON.stringify(req) }),

  cancelImpedance: () => request<ImpedanceStatus>('/impedance/cancel', { method: 'POST' }),

  clearImpedance: () => request<ImpedanceStatus>('/impedance/clear', { method: 'POST' }),

  getImpedanceStatus: () => request<ImpedanceStatus>('/impedance/status'),

  saveImpedance: (req: { measurementTypeId: number; name: string; notes: string | null }) =>
    request<{ id: number }>('/impedance/save', { method: 'POST', body: JSON.stringify(req) }),

  // Output impedance / damping factor — mirrors Tonebench.App/Endpoints/OutputImpedanceEndpoints.cs.
  startOutputImpedance: (req: OutputImpedanceStartRequest) =>
    request<OutputImpedanceStarted>('/output-impedance/start', { method: 'POST', body: JSON.stringify(req) }),

  /** Resumes a run waiting for the load to be changed — the CONTINUE button. A 409 at any other time. */
  continueOutputImpedance: () => request<OutputImpedanceStatus>('/output-impedance/continue', { method: 'POST' }),

  cancelOutputImpedance: () => request<OutputImpedanceStatus>('/output-impedance/cancel', { method: 'POST' }),

  clearOutputImpedance: () => request<OutputImpedanceStatus>('/output-impedance/clear', { method: 'POST' }),

  getOutputImpedanceStatus: () => request<OutputImpedanceStatus>('/output-impedance/status'),

  saveOutputImpedance: (req: { measurementTypeId: number; name: string; notes: string | null }) =>
    request<{ id: number }>('/output-impedance/save', { method: 'POST', body: JSON.stringify(req) }),

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

  // Re-finds the corners of the traces on screen against a new reference; the rebuilt traces
  // arrive over the stream like a pass's, and the status is returned for whoever wants it.
  setFrequencyResponseBandwidthReference: (req: BandwidthReferenceRequest) =>
    request<FrequencyResponseStatus>('/frequency-response/bandwidth-reference', { method: 'PUT', body: JSON.stringify(req) }),

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

  setSteppedResponseBandwidthReference: (req: BandwidthReferenceRequest) =>
    request<FrequencyResponseStatus>('/stepped-response/bandwidth-reference', { method: 'PUT', body: JSON.stringify(req) }),

  // Crosstalk: the stepped sweep's mechanics with one output muted, so the same status payload and
  // the same store — its own endpoints because it is its own runner (two passes, two traces).
  startCrosstalk: (req: CrosstalkStartRequest) =>
    request<CrosstalkStarted>('/crosstalk/start', { method: 'POST', body: JSON.stringify(req) }),

  cancelCrosstalk: () => request<FrequencyResponseStatus>('/crosstalk/cancel', { method: 'POST' }),

  clearCrosstalk: () => request<FrequencyResponseStatus>('/crosstalk/clear', { method: 'POST' }),

  getCrosstalkStatus: () => request<FrequencyResponseStatus>('/crosstalk/status'),

  saveCrosstalk: (req: { measurementTypeId: number; name: string; notes: string | null }) =>
    request<{ id: number }>('/crosstalk/save', { method: 'POST', body: JSON.stringify(req) }),

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

  /** The stored impulse as the chart draws it — gated and windowed server-side by the runner's own code. */
  getStoredImpulseTrace: (measurementId: number) =>
    request<StoredImpulseTraceResponse>(`/measurements/${measurementId}/impulse-trace`),

  /** The two decay surfaces of a stored result, from its stored IR — the same analyses the live endpoints run on the runner's. */
  computeStoredImpulseCsd: (measurementId: number, req: ImpulseCsdRequest) =>
    request<DecaySurface>(`/measurements/${measurementId}/decay/csd`, { method: 'POST', body: JSON.stringify(req) }),

  computeStoredImpulseBurstDecay: (measurementId: number, req: ImpulseBurstDecayRequest) =>
    request<DecaySurface>(`/measurements/${measurementId}/decay/burst`, { method: 'POST', body: JSON.stringify(req) }),

  getStoredResponse: (measurementId: number) =>
    request<StoredResponseTracesResponse>(`/measurements/${measurementId}/response-traces`),

  // Answers for any result; a kind that stores no per-point captures (a chirp, or a stepped run
  // saved before they were recorded) comes back with an empty list rather than a 404.
  getStoredResponsePreviews: (measurementId: number) =>
    request<StoredResponsePreviewsResponse>(`/measurements/${measurementId}/response-previews`),

  getUpdateStatus: () => request<UpdateStatus>('/update-check'),

  // The about dialog's version, and deliberately not `getUpdateStatus().currentVersion`: the update
  // check is opt-out (an air-gapped bench machine turns it off in Configuration) and short-circuits
  // before it ever fills that field in, which would leave the about dialog blank on exactly the
  // machines least able to look the version up elsewhere. This one touches no network.
  getAppInfo: () => request<AppIdentity>('/app'),

  /** What is on this machine for a report to carry, so the dialog can say so before it builds one. */
  getIssueReportContents: () => request<IssueReportContents>('/issue-report'),

  /**
   * Builds the zip. It is written to a folder on the machine rather than returned as a body — with
   * the recordings box ticked it is hundreds of megabytes, which is not a thing to pull through a
   * Blob, and a file in a known folder is still there tomorrow when the user gets round to
   * attaching it. The result says where, and `issueReportDownloadUrl` turns that into a link.
   */
  buildIssueReport: (request_: IssueReportRequest) =>
    request<IssueReportResult>('/issue-report', { method: 'POST', body: JSON.stringify(request_) }),

  openIssueReportFolder: () => request<void>('/issue-report/open-folder', { method: 'POST' }),
}

export interface IssueReportRequest {
  answers: { id: string; question: string; answer: string }[]
  screenshotPngBase64?: string | null
  logDays: number
  includeDiagnostics: boolean
  includeRecordings: boolean
  includeSettings: boolean
  /** Everything the browser knows and the server cannot — see IssueReportService's `client` block. */
  clientStateJson?: string | null
}

export interface IssueReportResult {
  fileName: string
  path: string
  bytes: number
  included: string[]
  warnings: string[]
}

/** The download link for a finished report. A plain href, so the browser's own download handles it. */
export function issueReportDownloadUrl(fileName: string): string {
  return `/api/issue-report/download?name=${encodeURIComponent(fileName)}`
}
