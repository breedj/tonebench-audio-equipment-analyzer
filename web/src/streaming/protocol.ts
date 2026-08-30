// Binary WebSocket wire format, docs/qa403-app-stack-spec.md §4.2. Must stay in sync with
// Qa403.App/Streaming/StreamFrame.cs by hand — no codegen. All fields little-endian.
//
// offset  type      field
// 0       u8        msgType     1=spectrum 2=scope 3=meters
// 1       u8        channel     0=L 1=R
// 2       u16       flags       bit0: decimated, bit1: log-spaced (informational only, see below)
// 4       u32       seq
// 8       f32       x0          first freq (Hz)/time (s), or (decimated) the span's low edge freq (Hz)
// 12      f32       dx          bin width (Hz)/sample period (s), or (decimated) the span's high edge freq (Hz)
// 16      u32       nPoints
// 20      f32[]     payload     spectrum/scope: nPoints*(freqHz,min,max) triples; meters: see decodeMetersFrame
//
// Each decimated column carries its own frequency rather than the client reconstructing one from
// x0/dx/nPoints via a spacing formula. That was tried and is unsafe: the server's actual column
// boundaries are integer bin indices with a "never narrower than one bin" floor, which deviates
// substantially from a smooth log curve whenever there are few source bins relative to the
// column count (e.g. a small FFT decimated to ~1600 columns) — the fundamental was once
// mislabeled by nearly two orders of magnitude this way. Trust the per-column freqHz, not x0/dx.

export const HEADER_LENGTH = 20

// Plain const objects, not `enum` — this project's tsconfig has erasableSyntaxOnly enabled.
export const StreamMsgType = {
  Spectrum: 1,
  Scope: 2,
  Meters: 3,
  /** One finished THD-vs-frequency sweep point — see decodeSweepPointFrame. */
  SweepPoint: 4,
  /** Where a running sweep is; carries no per-channel data — see decodeSweepProgressFrame. */
  SweepProgress: 5,
} as const
export type StreamMsgType = (typeof StreamMsgType)[keyof typeof StreamMsgType]

export const StreamChannel = {
  Left: 0,
  Right: 1,
} as const
export type StreamChannel = (typeof StreamChannel)[keyof typeof StreamChannel]

export const STREAM_FLAG_DECIMATED = 1 << 0
export const STREAM_FLAG_LOG_SPACED = 1 << 1
/**
 * This frame covers only the zoom window this client asked for, at the full column budget — so it
 * resolves detail the overview cannot. Its x0/dx are the window's edges, NOT the spectrum's, so
 * never size an axis from a zoomed frame; only an un-flagged overview frame describes the full
 * span. See StreamFlags.Zoomed server-side.
 */
export const STREAM_FLAG_ZOOMED = 1 << 2
/**
 * Meters-frame-only: this channel's single-shot capture (see the "Single" button and
 * Qa403.App.Streaming.SpectrumPipelineOptions.SingleShot) just completed on this tick, and the
 * pipeline has stopped sending further frames for it until the next Run or Single. This is the
 * signal a client waiting on a single shot polls for — see decodeMetersFrame's `held`.
 */
export const STREAM_FLAG_HELD = 1 << 3

/** Client -> server control message kinds. Everything else is REST. */
export const ClientControlType = {
  SetViewport: 1,
} as const

/**
 * Encodes a viewport update: `u8 type, f32 loFreqHz, f32 hiFreqHz`, little-endian. Pass a
 * degenerate range (hi <= lo) to clear the zoom and go back to the shared overview frame.
 */
export function encodeSetViewport(loFreqHz: number, hiFreqHz: number): ArrayBuffer {
  const buffer = new ArrayBuffer(9)
  const view = new DataView(buffer)
  view.setUint8(0, ClientControlType.SetViewport)
  view.setFloat32(1, loFreqHz, true)
  view.setFloat32(5, hiFreqHz, true)
  return buffer
}

export interface StreamFrameHeader {
  msgType: StreamMsgType
  channel: StreamChannel
  flags: number
  seq: number
  x0: number
  dx: number
  nPoints: number
}

export function decodeHeader(buffer: ArrayBuffer): StreamFrameHeader {
  const view = new DataView(buffer)
  return {
    msgType: view.getUint8(0) as StreamMsgType,
    channel: view.getUint8(1) as StreamChannel,
    flags: view.getUint16(2, true),
    seq: view.getUint32(4, true),
    x0: view.getFloat32(8, true),
    dx: view.getFloat32(12, true),
    nPoints: view.getUint32(16, true),
  }
}

export interface SpectrumFrame {
  header: StreamFrameHeader
  /** Informational span bounds only — see the file-level comment. Not for per-column positioning. */
  loFreqHz: number
  hiFreqHz: number
  /**
   * Zero-copy view straight into the received ArrayBuffer: interleaved (freqHz,min,max) f32
   * triples. Column i is at indices [i*3, i*3+1, i*3+2] = [freqHz, min, max].
   */
  columns: Float32Array
}

/** Decodes a spectrum/scope frame. Assumes the decimated (freqHz,min,max) layout this app always sends. */
export function decodeSpectrumFrame(buffer: ArrayBuffer): SpectrumFrame {
  const header = decodeHeader(buffer)
  const columns = new Float32Array(buffer, HEADER_LENGTH, header.nPoints * 3)
  return { header, loFreqHz: header.x0, hiFreqHz: header.dx, columns }
}

export interface ScopeFrame {
  header: StreamFrameHeader
  /** Sample period (seconds) — the header's `dx` field for Scope frames; x0 is always 0, see decodeScopeFrame. */
  samplePeriodS: number
  /** Zero-copy view straight into the received ArrayBuffer: nPoints raw samples, in volts. */
  samples: Float32Array
}

/**
 * Decodes a scope (time-domain) frame: nPoints raw f32 samples in volts, one per point — unlike
 * decodeSpectrumFrame's (freqHz,min,max) triples, a scope frame is never decimated. Each frame is
 * a fresh, self-contained snapshot of the latest samples (x0 = 0), not something to accumulate
 * across frames into a running timeline.
 */
export function decodeScopeFrame(buffer: ArrayBuffer): ScopeFrame {
  const header = decodeHeader(buffer)
  const samples = new Float32Array(buffer, HEADER_LENGTH, header.nPoints)
  return { header, samplePeriodS: header.dx, samples }
}

export interface HarmonicMarker {
  freqHz: number
  dbc: number
}

export interface MetersFrame {
  header: StreamFrameHeader
  fundamentalFreqHz: number
  /** FFT bin size (Hz) — carried in the header's otherwise-unused `dx` field for Meters frames. */
  binSizeHz: number
  thdPercent: number
  thdNPercent: number
  snrDb: number
  fundamentalDbv: number
  noiseFloorDbv: number
  /** True instantaneous time-domain peak, RMS-equivalent (peakVpk/sqrt2), dBV. */
  peakDbv: number
  /** True broadband time-domain RMS (not just the fundamental bin), dBV. */
  totalRmsDbv: number
  harmonics: HarmonicMarker[]
  /** Strongest-first non-harmonic peaks (mains hum, IMD/mixing products, other spurs) — see PeakFinder.cs. */
  otherPeaks: HarmonicMarker[]
  /** How many spectra have contributed so far, capped at the configured target count; 0 when averaging is off. See SpectrumAverager.Count. */
  averagesTaken: number
  /** True once this channel's single-shot capture has completed — see STREAM_FLAG_HELD. */
  held: boolean
}

/**
 * Meters payload (offsets relative to the 20-byte header):
 * 0  f32  thdPercent
 * 4  f32  thdNPercent
 * 8  f32  snrDb
 * 12 f32  fundamentalDbv
 * 16 f32  noiseFloorDbv
 * 20 f32  peakDbv
 * 24 f32  totalRmsDbv
 * 28 u32  otherPeakCount
 * 32 u32  averagesTaken
 * 36 (f32 freqHz, f32 dbc) * nPoints          — one entry per harmonic, order 1..N in order.
 * .. (f32 freqHz, f32 dbc) * otherPeakCount   — non-harmonic peaks, strongest first.
 */
export function decodeMetersFrame(buffer: ArrayBuffer): MetersFrame {
  const header = decodeHeader(buffer)
  const view = new DataView(buffer)
  let offset = HEADER_LENGTH

  const thdPercent = view.getFloat32(offset, true)
  offset += 4
  const thdNPercent = view.getFloat32(offset, true)
  offset += 4
  const snrDb = view.getFloat32(offset, true)
  offset += 4
  const fundamentalDbv = view.getFloat32(offset, true)
  offset += 4
  const noiseFloorDbv = view.getFloat32(offset, true)
  offset += 4
  const peakDbv = view.getFloat32(offset, true)
  offset += 4
  const totalRmsDbv = view.getFloat32(offset, true)
  offset += 4
  const otherPeakCount = view.getUint32(offset, true)
  offset += 4
  const averagesTaken = view.getUint32(offset, true)
  offset += 4

  const harmonics: HarmonicMarker[] = new Array(header.nPoints)
  for (let i = 0; i < header.nPoints; i++) {
    const freqHz = view.getFloat32(offset, true)
    offset += 4
    const dbc = view.getFloat32(offset, true)
    offset += 4
    harmonics[i] = { freqHz, dbc }
  }

  const otherPeaks: HarmonicMarker[] = new Array(otherPeakCount)
  for (let i = 0; i < otherPeakCount; i++) {
    const freqHz = view.getFloat32(offset, true)
    offset += 4
    const dbc = view.getFloat32(offset, true)
    offset += 4
    otherPeaks[i] = { freqHz, dbc }
  }

  return {
    header,
    fundamentalFreqHz: header.x0,
    binSizeHz: header.dx,
    thdPercent,
    thdNPercent,
    snrDb,
    fundamentalDbv,
    noiseFloorDbv,
    peakDbv,
    totalRmsDbv,
    harmonics,
    otherPeaks,
    averagesTaken,
    held: (header.flags & STREAM_FLAG_HELD) !== 0,
  }
}

/**
 * Mirrors Qa403.App.Services.ThdSweepPhase. Ordinals, because the phase rides in the sweep
 * progress frame's `seq` field — it is an enumeration, not a set of independent flag bits.
 */
export const SweepPhase = {
  Idle: 0,
  DeterminingLevel: 1,
  NoiseFloor: 2,
  Sweeping: 3,
  Finished: 4,
  Cancelled: 5,
  Failed: 6,
} as const
export type SweepPhase = (typeof SweepPhase)[keyof typeof SweepPhase]

/** One decimated (x, min, max) trace — a sweep point's FFT or oscilloscope preview. See SweepPreviewSeries server-side. */
export interface SweepPreviewSeries {
  /** Frequency (Hz) for an FFT preview, elapsed time (s) for a scope preview. */
  x: number[]
  min: number[]
  max: number[]
}

export interface SweepPointFrame {
  header: StreamFrameHeader
  freqHz: number
  channel: StreamChannel
  /** Index of this point within the sweep — the header's `seq`. Both channels of one frequency share it. */
  pointIndex: number
  thdPercent: number
  thdNPercent: number
  snrDb: number
  fundamentalDbv: number
  /** fundamentalDbv minus the generator amplitude actually used. */
  gainDb: number
  /** V1^2 / load; 0 when no load was given. */
  powerWatt: number
  /** 6th harmonic and above, power-summed into one bucket. */
  d6PlusPercent: number
  /** Generator-off residual as a % of this point's fundamental; 0 when the noise pass was skipped. */
  noisePercent: number
  /** The generator amplitude this point was played at — constant on a frequency sweep, the x axis on an amplitude one. */
  generatorDbv: number
  /** D2..D5 as a % of the fundamental, index 0 = 2nd harmonic. May be short near Nyquist. */
  harmonicPercent: number[]
  /** A small decimated spectrum around this point's fundamental, in dBV. Null if the pipeline wasn't asked to capture one. */
  previewFft: SweepPreviewSeries | null
  /** A small decimated oscilloscope trace, in volts, spanning ScopeDisplayWindow.SecondsFor(fundamentalFreqHz). Null if not captured. */
  previewScope: SweepPreviewSeries | null
}

/**
 * Sweep point payload (offsets relative to the 20-byte header):
 * 0  f32  thdPercent
 * 4  f32  thdNPercent
 * 8  f32  snrDb
 * 12 f32  fundamentalDbv
 * 16 f32  gainDb
 * 20 f32  powerWatt
 * 24 f32  d6PlusPercent
 * 28 f32  noisePercent
 * 32 f32  generatorDbv
 * 36 u32  harmonicCount
 * 40 f32 * harmonicCount   — D2..D5 as a % of the fundamental
 * ..  u32  fftPreviewCount
 * ..  (f32 x, f32 min, f32 max) * fftPreviewCount
 * ..  u32  scopePreviewCount
 * ..  (f32 x, f32 min, f32 max) * scopePreviewCount
 *
 * x0 = the point's frequency (Hz), dx = 0, nPoints = harmonicCount, seq = the point's index. A
 * preview count of 0 decodes to `null`, not an empty array — see EncodeSweepPoint server-side.
 */
export function decodeSweepPointFrame(buffer: ArrayBuffer): SweepPointFrame {
  const header = decodeHeader(buffer)
  const view = new DataView(buffer)
  let offset = HEADER_LENGTH

  const readF32 = () => {
    const v = view.getFloat32(offset, true)
    offset += 4
    return v
  }

  const thdPercent = readF32()
  const thdNPercent = readF32()
  const snrDb = readF32()
  const fundamentalDbv = readF32()
  const gainDb = readF32()
  const powerWatt = readF32()
  const d6PlusPercent = readF32()
  const noisePercent = readF32()
  const generatorDbv = readF32()
  const harmonicCount = view.getUint32(offset, true)
  offset += 4

  const harmonicPercent: number[] = new Array(harmonicCount)
  for (let i = 0; i < harmonicCount; i++) harmonicPercent[i] = readF32()

  const readPreview = (): SweepPreviewSeries | null => {
    const count = view.getUint32(offset, true)
    offset += 4
    if (count === 0) return null

    const x: number[] = new Array(count)
    const min: number[] = new Array(count)
    const max: number[] = new Array(count)
    for (let i = 0; i < count; i++) {
      x[i] = readF32()
      min[i] = readF32()
      max[i] = readF32()
    }
    return { x, min, max }
  }

  const previewFft = readPreview()
  const previewScope = readPreview()

  return {
    header,
    freqHz: header.x0,
    channel: header.channel,
    pointIndex: header.seq,
    thdPercent,
    thdNPercent,
    snrDb,
    fundamentalDbv,
    gainDb,
    powerWatt,
    d6PlusPercent,
    noisePercent,
    generatorDbv,
    harmonicPercent,
    previewFft,
    previewScope,
  }
}

export interface SweepProgressFrame {
  header: StreamFrameHeader
  index: number
  total: number
  /**
   * The point currently being measured, in the running sweep's own unit — hertz for a frequency
   * sweep, generator volts for an amplitude one — and 0 when the sweep is not on a point. One slot
   * for two units because the client already knows which axis it started; see
   * StreamFrame.EncodeSweepProgress.
   */
  currentX: number
  phase: SweepPhase
}

/** Decodes a sweep progress frame. No payload: index = nPoints, total = dx, phase = seq. */
export function decodeSweepProgressFrame(buffer: ArrayBuffer): SweepProgressFrame {
  const header = decodeHeader(buffer)
  return {
    header,
    index: header.nPoints,
    total: Math.round(header.dx),
    currentX: header.x0,
    phase: header.seq as SweepPhase,
  }
}
