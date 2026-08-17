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
   * RX chunks dropped since connect because the consumer fell behind. Each one is a hole in the
   * sample stream, so the FFT records around it are spliced — non-zero means noise-floor readings
   * are suspect, not just that a display frame was skipped.
   */
  droppedRxChunks: number
}

/** Patch for the simulated DUT's signal profile — omitted fields are left alone. */
export interface SimulatedProfileRequest {
  noiseEnabled?: boolean
  humEnabled?: boolean
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
  typeId?: number
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

export const api = {
  getDevice: () => request<DeviceStatus>('/device'),
  getDeviceRanges: () => request<DeviceRanges>('/device/ranges'),

  setInputRange: (dbv: number) =>
    request<DeviceStatus>('/device/input-range', { method: 'POST', body: JSON.stringify({ dbv }) }),

  setOutputRange: (dbv: number) =>
    request<DeviceStatus>('/device/output-range', { method: 'POST', body: JSON.stringify({ dbv }) }),

  setAutoRange: (enabled: boolean) =>
    request<DeviceStatus>('/device/auto-range', { method: 'POST', body: JSON.stringify({ enabled }) }),

  setAutoRangeStepDownDelay: (delayMs: number) =>
    request<DeviceStatus>('/device/auto-range-step-down-delay', { method: 'POST', body: JSON.stringify({ delayMs }) }),

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
   * Pushes this type's stored spectrum/range parameters into the live app (silent, safe at select
   * time) and returns its generator/acquisition settings for the UI to stage into its forms —
   * loading a measurement never starts it playing; see App.vue's Run button for that.
   */
  applyMeasurementType: (id: number) => request<MeasurementTypeAppliedResponse>(`/measurement-types/${id}/apply`, { method: 'POST' }),

  /** Saves the currently retained (stopped) spectrum snapshot — 409 while acquiring, or when nothing has been captured. */
  saveMeasurement: (req: SaveMeasurementRequest) =>
    request<MeasurementDetail>('/measurements', { method: 'POST', body: JSON.stringify(req) }),

  listMeasurements: (filter: MeasurementFilter = {}) =>
    request<MeasurementListResponse>(
      `/measurements${toQuery({ projectId: filter.projectId, typeId: filter.typeId, from: filter.from, to: filter.to, skip: filter.skip, take: filter.take })}`,
    ),

  getMeasurement: (id: number) => request<MeasurementDetail>(`/measurements/${id}`),

  deleteMeasurement: (id: number) => request<void>(`/measurements/${id}`, { method: 'DELETE' }),

  /** Not fetched through `request` — a plain download link the caller renders as an `<a href=... download>`. */
  measurementCsvExportUrl: (id: number) => `/api/measurements/${id}/export.csv`,
}
