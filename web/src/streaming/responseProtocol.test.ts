import { describe, expect, it } from 'vitest'
import {
  HEADER_LENGTH,
  ResponsePhase,
  ResponseTraceKind,
  StreamMsgType,
  decodeFrequencyResponseFrame,
  decodeFrequencyResponseProgressFrame,
} from './protocol'

interface Column {
  freqHz: number
  minDb: number
  maxDb: number
  phaseDeg: number
  coherence: number
}

interface Bandwidth {
  dropDb: number
  lowerHz: number
  upperHz: number
  bandwidthHz: number
  lowerClamped: boolean
  upperClamped: boolean
}

/**
 * Builds a raw FrequencyResponse frame by hand, mirroring StreamFrame.EncodeFrequencyResponse's
 * byte layout. There is no client-side encoder — the server only ever sends these — so this is the
 * only way to exercise the decoder, and writing the bytes out longhand is what makes it a real
 * check on the two staying in step rather than a round-trip through shared code.
 */
function buildFrame(opts: {
  kind: number
  startFreqHz: number
  endFreqHz: number
  columns: Column[]
  oneDb: Bandwidth
  threeDb: Bandwidth
  peakDb: number
  peakFreqHz: number
}): ArrayBuffer {
  const length = HEADER_LENGTH + opts.columns.length * 20 + 40 + 8
  const buffer = new ArrayBuffer(length)
  const view = new DataView(buffer)

  view.setUint8(0, StreamMsgType.FrequencyResponse)
  view.setUint8(1, opts.kind)
  view.setUint16(2, 0, true)
  view.setUint32(4, 0, true)
  view.setFloat32(8, opts.startFreqHz, true)
  view.setFloat32(12, opts.endFreqHz, true)
  view.setUint32(16, opts.columns.length, true)

  let offset = HEADER_LENGTH
  for (const column of opts.columns) {
    view.setFloat32(offset, column.freqHz, true)
    view.setFloat32(offset + 4, column.minDb, true)
    view.setFloat32(offset + 8, column.maxDb, true)
    view.setFloat32(offset + 12, column.phaseDeg, true)
    view.setFloat32(offset + 16, column.coherence, true)
    offset += 20
  }

  for (const bandwidth of [opts.oneDb, opts.threeDb]) {
    view.setFloat32(offset, bandwidth.dropDb, true)
    view.setFloat32(offset + 4, bandwidth.lowerHz, true)
    view.setFloat32(offset + 8, bandwidth.upperHz, true)
    view.setFloat32(offset + 12, bandwidth.bandwidthHz, true)
    view.setUint8(offset + 16, bandwidth.lowerClamped ? 1 : 0)
    view.setUint8(offset + 17, bandwidth.upperClamped ? 1 : 0)
    offset += 20
  }

  view.setFloat32(offset, opts.peakDb, true)
  view.setFloat32(offset + 4, opts.peakFreqHz, true)

  return buffer
}

const columns: Column[] = [
  { freqHz: 20, minDb: -3.5, maxDb: -3, phaseDeg: -12, coherence: 0.4 },
  { freqHz: 1000, minDb: -0.2, maxDb: 0.1, phaseDeg: -1, coherence: 0.99 },
  { freqHz: 20000, minDb: -1.5, maxDb: -1, phaseDeg: -40, coherence: 0.95 },
]

const oneDb: Bandwidth = {
  dropDb: 1,
  lowerHz: 8.8,
  upperHz: 63200,
  bandwidthHz: 63191.2,
  lowerClamped: false,
  upperClamped: false,
}

const threeDb: Bandwidth = {
  dropDb: 3,
  lowerHz: 4.4,
  upperHz: 90800,
  bandwidthHz: 90795.6,
  lowerClamped: false,
  upperClamped: true,
}

function decode(kind: ResponseTraceKind = ResponseTraceKind.Gain) {
  return decodeFrequencyResponseFrame(
    buildFrame({ kind, startFreqHz: 4, endFreqHz: 22800, columns, oneDb, threeDb, peakDb: 34.49, peakFreqHz: 461.4 }),
  )
}

describe('decodeFrequencyResponseFrame', () => {
  it('reads every per-column array', () => {
    const frame = decode()

    expect(Array.from(frame.freqHz)).toEqual([20, 1000, 20000])
    expect(Array.from(frame.phaseDeg)).toEqual([-12, -1, -40])

    // f32 on the wire, so the decoded values carry binary32 rounding — compared with a tolerance
    // rather than against the exact representation of each decimal literal.
    for (const [i, expected] of [-3, 0.1, -1].entries())
      expect(frame.magnitudeMaxDb[i]).toBeCloseTo(expected, 5)
    for (const [i, expected] of [0.4, 0.99, 0.95].entries())
      expect(frame.coherence[i]).toBeCloseTo(expected, 5)
  })

  it('keeps both magnitude bounds, because both are drawn', () => {
    // Max is the line, min is the band under it. Dropping the min would redraw a rippled response
    // riding along the top of its own ripple.
    const frame = decode()

    for (const [i, expected] of [-3.5, -0.2, -1.5].entries())
      expect(frame.magnitudeMinDb[i]).toBeCloseTo(expected, 5)
    expect(frame.magnitudeMinDb.length).toBe(frame.magnitudeMaxDb.length)
  })

  it('reads the band edges out of the header', () => {
    const frame = decode()

    expect(frame.startFreqHz).toBe(4)
    expect(frame.endFreqHz).toBe(22800)
  })

  it.each([
    [ResponseTraceKind.Left],
    [ResponseTraceKind.Right],
    [ResponseTraceKind.Gain],
  ])('reads the trace kind out of the channel byte (%i)', (kind) => {
    // On these frames the channel byte is a trace kind, not a StreamChannel — Gain is 2, which is
    // not a channel at all, so a client keying off it as left/right would mislabel that curve.
    expect(decode(kind).kind).toBe(kind)
  })

  it('reads both bandwidth blocks with their clamped flags', () => {
    const frame = decode()

    expect(frame.oneDb.dropDb).toBe(1)
    expect(frame.oneDb.lowerHz).toBeCloseTo(8.8, 3)
    expect(frame.oneDb.upperClamped).toBe(false)

    expect(frame.threeDb.dropDb).toBe(3)
    expect(frame.threeDb.lowerHz).toBeCloseTo(4.4, 3)
    // Per-end, not per-reading: this curve has a real lower corner and an upper one that ran off
    // the end of the measured band.
    expect(frame.threeDb.lowerClamped).toBe(false)
    expect(frame.threeDb.upperClamped).toBe(true)
  })

  it('reads the peak from the trailer, after both bandwidth blocks', () => {
    const frame = decode()

    expect(frame.peakDb).toBeCloseTo(34.49, 3)
    expect(frame.peakFreqHz).toBeCloseTo(461.4, 2)
  })
})

describe('decodeFrequencyResponseProgressFrame', () => {
  it('reads the record counts and the phase out of the header', () => {
    const buffer = new ArrayBuffer(HEADER_LENGTH)
    const view = new DataView(buffer)
    view.setUint8(0, StreamMsgType.FrequencyResponseProgress)
    view.setUint32(4, ResponsePhase.Measuring, true)
    view.setFloat32(12, 8, true)
    view.setUint32(16, 3, true)

    const frame = decodeFrequencyResponseProgressFrame(buffer)

    expect(frame.taken).toBe(3)
    expect(frame.total).toBe(8)
    expect(frame.phase).toBe(ResponsePhase.Measuring)
  })
})
