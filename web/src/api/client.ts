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
}

export interface GeneratorChannelState {
  enabled: boolean
  tones: GeneratorTone[]
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

export interface GeneratorChannelRequest {
  enabled: boolean
  tones: GeneratorTone[]
}

export interface GeneratorRequest {
  left: GeneratorChannelRequest
  right: GeneratorChannelRequest
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
  /** Whether the left/right channel is processed (FFT + meters) and broadcast at all — see AcquisitionPanel's L/R checkboxes. */
  processLeft: boolean
  processRight: boolean
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

  return response.json() as Promise<T>
}

export const api = {
  getDevice: () => request<DeviceStatus>('/device'),
  getDeviceRanges: () => request<DeviceRanges>('/device/ranges'),

  setInputRange: (dbv: number) =>
    request<DeviceStatus>('/device/input-range', { method: 'POST', body: JSON.stringify({ dbv }) }),

  setOutputRange: (dbv: number) =>
    request<DeviceStatus>('/device/output-range', { method: 'POST', body: JSON.stringify({ dbv }) }),

  setGenerator: (config: GeneratorRequest) =>
    request<DeviceStatus>('/generator', { method: 'POST', body: JSON.stringify(config) }),

  startAcquisition: (config: AcquisitionStartRequest) =>
    request<DeviceStatus>('/acquisition/start', { method: 'POST', body: JSON.stringify(config) }),

  stopAcquisition: () => request<DeviceStatus>('/acquisition/stop', { method: 'POST' }),

  getSpectrumConfig: () => request<SpectrumConfig>('/acquisition/spectrum-config'),

  setSpectrumConfig: (config: SpectrumConfigRequest) =>
    request<SpectrumConfig>('/acquisition/spectrum-config', { method: 'POST', body: JSON.stringify(config) }),
}
