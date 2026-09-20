import { describe, expect, it } from 'vitest'
import { decodeIntermodulationFrame, HEADER_LENGTH, STREAM_FLAG_HELD, StreamMsgType } from './protocol'

/**
 * Builds a raw Intermodulation frame by hand, mirroring StreamFrame.EncodeIntermodulation's byte
 * layout — the server only ever sends these, so this is the only way to exercise the decoder.
 */
function buildIntermodulationFrame(opts: {
  channel: number
  seq: number
  toneLowHz: number
  toneHighHz: number
  method: 0 | 1
  maxOrder: number
  held?: boolean
  perOrder: { order: number; percent: number }[]
  products: { freqHz: number; dbv: number; dbc: number; m: number; n: number; order: number }[]
}): ArrayBuffer {
  const length = HEADER_LENGTH + 4 + 6 * 4 + 4 + opts.perOrder.length * 8 + opts.products.length * 24
  const buffer = new ArrayBuffer(length)
  const view = new DataView(buffer)

  view.setUint8(0, StreamMsgType.Intermodulation)
  view.setUint8(1, opts.channel)
  view.setUint16(2, opts.held ? STREAM_FLAG_HELD : 0, true)
  view.setUint32(4, opts.seq, true)
  view.setFloat32(8, opts.toneLowHz, true)
  view.setFloat32(12, opts.toneHighHz, true)
  view.setUint32(16, opts.products.length, true)

  let offset = HEADER_LENGTH
  view.setUint8(offset, opts.method)
  view.setUint8(offset + 1, opts.maxOrder)
  offset += 4
  const writeF32 = (v: number) => {
    view.setFloat32(offset, v, true)
    offset += 4
  }
  writeF32(-1.94) // toneLowDbv
  writeF32(-13.98) // toneHighDbv
  writeF32(0.2) // imdPercent
  writeF32(0) // sineEquivalentDbv
  writeF32(-2.28) // combinedRmsDbv
  writeF32(-13.98) // referenceDbv
  view.setUint32(offset, opts.perOrder.length, true)
  offset += 4
  for (const o of opts.perOrder) {
    view.setUint32(offset, o.order, true)
    offset += 4
    writeF32(o.percent)
  }
  for (const p of opts.products) {
    writeF32(p.freqHz)
    writeF32(p.dbv)
    writeF32(p.dbc)
    view.setInt32(offset, p.m, true)
    offset += 4
    view.setInt32(offset, p.n, true)
    offset += 4
    view.setUint32(offset, p.order, true)
    offset += 4
  }

  return buffer
}

describe('decodeIntermodulationFrame', () => {
  it('reads the tones, per-order figures and products', () => {
    const buffer = buildIntermodulationFrame({
      channel: 1,
      seq: 7,
      toneLowHz: 58.59,
      toneHighHz: 7001.95,
      method: 0,
      maxOrder: 5,
      perOrder: [
        { order: 2, percent: 0.14 },
        { order: 3, percent: 0.1 },
      ],
      products: [
        { freqHz: 6943.36, dbv: -75, dbc: -61, m: -1, n: 1, order: 2 },
        { freqHz: 7060.55, dbv: -75.5, dbc: -61.5, m: 1, n: 1, order: 2 },
        { freqHz: 6884.77, dbv: -80, dbc: -66, m: -1, n: 2, order: 3 },
      ],
    })

    const frame = decodeIntermodulationFrame(buffer)

    expect(frame.header.channel).toBe(1)
    expect(frame.header.seq).toBe(7)
    expect(frame.toneLowHz).toBeCloseTo(58.59, 2)
    expect(frame.toneHighHz).toBeCloseTo(7001.95, 2)
    expect(frame.method).toBe('Smpte')
    expect(frame.maxOrder).toBe(5)
    expect(frame.toneLowDbv).toBeCloseTo(-1.94, 2)
    expect(frame.toneHighDbv).toBeCloseTo(-13.98, 2)
    expect(frame.imdPercent).toBeCloseTo(0.2, 5)
    expect(frame.sineEquivalentDbv).toBe(0)
    expect(frame.combinedRmsDbv).toBeCloseTo(-2.28, 2)
    expect(frame.referenceDbv).toBeCloseTo(-13.98, 2)
    expect(frame.perOrder.map((o) => o.order)).toEqual([2, 3])
    expect(frame.perOrder[1]!.percent).toBeCloseTo(0.1, 5)
    expect(frame.products).toHaveLength(3)
    expect(frame.products[2]).toMatchObject({ m: -1, n: 2, order: 3 })
    expect(frame.products[2]!.freqHz).toBeCloseTo(6884.77, 2)
    expect(frame.held).toBe(false)
  })

  it('reads the CCIF method byte and the held flag', () => {
    const buffer = buildIntermodulationFrame({
      channel: 0,
      seq: 1,
      toneLowHz: 19000,
      toneHighHz: 20000,
      method: 1,
      maxOrder: 3,
      held: true,
      perOrder: [],
      products: [],
    })

    const frame = decodeIntermodulationFrame(buffer)

    expect(frame.method).toBe('Ccif')
    expect(frame.held).toBe(true)
    expect(frame.perOrder).toEqual([])
    expect(frame.products).toEqual([])
  })
})
