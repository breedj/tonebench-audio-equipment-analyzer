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
  }
}
