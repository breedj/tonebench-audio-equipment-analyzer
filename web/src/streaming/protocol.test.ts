import { describe, expect, it } from 'vitest'
import { decodeSweepPointFrame, HEADER_LENGTH, StreamMsgType } from './protocol'

/**
 * Builds a raw SweepPoint frame by hand, mirroring StreamFrame.EncodeSweepPoint's byte layout —
 * there is no client-side encoder (the server only ever sends these), so this is the only way to
 * exercise decodeSweepPointFrame's preview-array parsing.
 */
function buildSweepPointFrame(opts: {
  freqHz: number
  channel: number
  pointIndex: number
  harmonicPercent: number[]
  fftPreview: { x: number; min: number; max: number }[] | null
  scopePreview: { x: number; min: number; max: number }[] | null
}): ArrayBuffer {
  const fftCount = opts.fftPreview?.length ?? 0
  const scopeCount = opts.scopePreview?.length ?? 0
  const length =
    HEADER_LENGTH +
    8 * 4 +
    4 +
    opts.harmonicPercent.length * 4 +
    4 +
    fftCount * 3 * 4 +
    4 +
    scopeCount * 3 * 4

  const buffer = new ArrayBuffer(length)
  const view = new DataView(buffer)

  view.setUint8(0, StreamMsgType.SweepPoint)
  view.setUint8(1, opts.channel)
  view.setUint16(2, 0, true) // flags
  view.setUint32(4, opts.pointIndex, true) // seq
  view.setFloat32(8, opts.freqHz, true) // x0
  view.setFloat32(12, 0, true) // dx
  view.setUint32(16, opts.harmonicPercent.length, true) // nPoints

  let offset = HEADER_LENGTH
  const writeF32 = (v: number) => {
    view.setFloat32(offset, v, true)
    offset += 4
  }

  writeF32(0.05) // thdPercent
  writeF32(0.06) // thdNPercent
  writeF32(90) // snrDb
  writeF32(-20) // fundamentalDbv
  writeF32(-0.1) // gainDb
  writeF32(0.00125) // powerWatt
  writeF32(0.002) // d6PlusPercent
  writeF32(0.001) // noisePercent
  view.setUint32(offset, opts.harmonicPercent.length, true)
  offset += 4
  for (const h of opts.harmonicPercent) writeF32(h)

  const writePreview = (points: { x: number; min: number; max: number }[] | null) => {
    view.setUint32(offset, points?.length ?? 0, true)
    offset += 4
    for (const p of points ?? []) {
      writeF32(p.x)
      writeF32(p.min)
      writeF32(p.max)
    }
  }
  writePreview(opts.fftPreview)
  writePreview(opts.scopePreview)

  return buffer
}

describe('decodeSweepPointFrame', () => {
  it('round-trips the scalar fields and both preview series', () => {
    const buffer = buildSweepPointFrame({
      freqHz: 1000,
      channel: 1,
      pointIndex: 7,
      harmonicPercent: [0.03, 0.01],
      fftPreview: [
        { x: 100, min: -90, max: -85 },
        { x: 200, min: -80, max: -75 },
      ],
      scopePreview: [
        { x: 0, min: -1, max: 1 },
        { x: 0.0005, min: -0.9, max: 0.9 },
      ],
    })

    const frame = decodeSweepPointFrame(buffer)

    expect(frame.freqHz).toBeCloseTo(1000, 3)
    expect(frame.channel).toBe(1)
    expect(frame.pointIndex).toBe(7)
    expect(frame.harmonicPercent).toEqual([
      expect.closeTo(0.03, 5),
      expect.closeTo(0.01, 5),
    ])

    expect(frame.previewFft).not.toBeNull()
    expect(frame.previewFft!.x).toEqual([100, 200])
    expect(frame.previewFft!.min).toEqual([
      expect.closeTo(-90, 3),
      expect.closeTo(-80, 3),
    ])

    expect(frame.previewScope).not.toBeNull()
    expect(frame.previewScope!.x).toEqual([0, expect.closeTo(0.0005, 6)])
  })

  it('decodes a missing preview (count 0) as null, not an empty array', () => {
    const buffer = buildSweepPointFrame({
      freqHz: 500,
      channel: 0,
      pointIndex: 0,
      harmonicPercent: [],
      fftPreview: null,
      scopePreview: null,
    })

    const frame = decodeSweepPointFrame(buffer)

    expect(frame.previewFft).toBeNull()
    expect(frame.previewScope).toBeNull()
  })

  it('decodes only the fft preview when the scope one is absent', () => {
    const buffer = buildSweepPointFrame({
      freqHz: 500,
      channel: 0,
      pointIndex: 0,
      harmonicPercent: [],
      fftPreview: [{ x: 50, min: -100, max: -95 }],
      scopePreview: null,
    })

    const frame = decodeSweepPointFrame(buffer)

    expect(frame.previewFft?.x).toEqual([50])
    expect(frame.previewScope).toBeNull()
  })
})
