import { describe, expect, it } from 'vitest'
import { decodeMultitoneFrame, HEADER_LENGTH, STREAM_FLAG_HELD, StreamMsgType } from './protocol'

/**
 * Builds a raw Multitone frame by hand, mirroring StreamFrame.EncodeMultitone's byte layout — the
 * server only ever sends these, so this is the only way to exercise the decoder.
 */
function buildMultitoneFrame(opts: {
  channel: number
  seq: number
  combDbv: number
  tdn: number
  resolvedBands: number
  held?: boolean
  tones: { freqHz: number; dbv: number }[]
  bands: { resolved: boolean; highestHz: number; highestDbv: number; highestDbr: number; dnDbr: number }[]
}): ArrayBuffer {
  const length = HEADER_LENGTH + 8 + opts.tones.length * 8 + opts.bands.length * 20
  const buffer = new ArrayBuffer(length)
  const view = new DataView(buffer)

  view.setUint8(0, StreamMsgType.Multitone)
  view.setUint8(1, opts.channel)
  view.setUint16(2, opts.held ? STREAM_FLAG_HELD : 0, true)
  view.setUint32(4, opts.seq, true)
  view.setFloat32(8, opts.combDbv, true)
  view.setFloat32(12, opts.tdn, true)
  view.setUint32(16, opts.tones.length, true)

  let offset = HEADER_LENGTH
  view.setUint32(offset, opts.bands.length, true)
  offset += 4
  view.setUint32(offset, opts.resolvedBands, true)
  offset += 4
  const writeF32 = (v: number) => {
    view.setFloat32(offset, v, true)
    offset += 4
  }
  for (const t of opts.tones) {
    writeF32(t.freqHz)
    writeF32(t.dbv)
  }
  for (const b of opts.bands) {
    view.setUint8(offset, b.resolved ? 1 : 0)
    offset += 4
    writeF32(b.highestHz)
    writeF32(b.highestDbv)
    writeF32(b.highestDbr)
    writeF32(b.dnDbr)
  }
  return buffer
}

describe('decodeMultitoneFrame', () => {
  it('reads the comb, the tones, the bands and the held flag', () => {
    const frame = decodeMultitoneFrame(
      buildMultitoneFrame({
        channel: 1,
        seq: 42,
        combDbv: -20.5,
        tdn: 0.00112,
        resolvedBands: 1,
        held: true,
        tones: [
          { freqHz: 1000, dbv: -26 },
          { freqHz: 1004, dbv: -26 },
          { freqHz: 2000, dbv: -26 },
        ],
        bands: [
          { resolved: false, highestHz: 0, highestDbv: 0, highestDbr: 0, dnDbr: 0 },
          { resolved: true, highestHz: 1500, highestDbv: -72, highestDbr: -60.9, dnDbr: -58.9 },
        ],
      }),
    )

    expect(frame.header.channel).toBe(1)
    expect(frame.header.seq).toBe(42)
    expect(frame.held).toBe(true)
    expect(frame.combDbv).toBeCloseTo(-20.5, 5)
    expect(frame.tdn).toBeCloseTo(0.00112, 7)
    expect(frame.resolvedBands).toBe(1)
    expect(frame.tones).toHaveLength(3)
    expect(frame.tones[1]!.freqHz).toBe(1004)
    expect(frame.bands).toHaveLength(2)
    expect(frame.bands[0]!.resolved).toBe(false)
    expect(frame.bands[1]).toEqual({
      resolved: true,
      highestFreqHz: 1500,
      highestDbv: -72,
      highestDbr: expect.closeTo(-60.9, 4),
      dnDbr: expect.closeTo(-58.9, 4),
    })
  })

  it('decodes an empty band list', () => {
    const frame = decodeMultitoneFrame(
      buildMultitoneFrame({ channel: 0, seq: 1, combDbv: -30, tdn: 0, resolvedBands: 0, tones: [{ freqHz: 100, dbv: -30 }], bands: [] }),
    )
    expect(frame.tones).toHaveLength(1)
    expect(frame.bands).toHaveLength(0)
    expect(frame.held).toBe(false)
  })
})
