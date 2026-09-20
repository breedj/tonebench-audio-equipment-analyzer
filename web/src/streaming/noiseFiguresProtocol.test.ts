import { describe, expect, it } from 'vitest'
import { decodeNoiseFiguresFrame, HEADER_LENGTH, STREAM_FLAG_HELD, StreamMsgType } from './protocol'

/** Builds a raw NoiseFigures frame by hand, mirroring StreamFrame.EncodeNoiseFigures's layout. */
function buildNoiseFrame(opts: {
  channel: number
  seq: number
  loHz: number
  hiHz: number
  bandRmsDbv: number
  densityDbv: number
  enbwHz: number
  standard: number
  userMask: number
  held?: boolean
}): ArrayBuffer {
  const buffer = new ArrayBuffer(HEADER_LENGTH + 16)
  const view = new DataView(buffer)
  view.setUint8(0, StreamMsgType.NoiseFigures)
  view.setUint8(1, opts.channel)
  view.setUint16(2, opts.held ? STREAM_FLAG_HELD : 0, true)
  view.setUint32(4, opts.seq, true)
  view.setFloat32(8, opts.loHz, true)
  view.setFloat32(12, opts.hiHz, true)
  view.setUint32(16, 0, true)
  view.setFloat32(HEADER_LENGTH, opts.bandRmsDbv, true)
  view.setFloat32(HEADER_LENGTH + 4, opts.densityDbv, true)
  view.setFloat32(HEADER_LENGTH + 8, opts.enbwHz, true)
  view.setUint8(HEADER_LENGTH + 12, opts.standard)
  view.setUint8(HEADER_LENGTH + 13, opts.userMask)
  return buffer
}

describe('decodeNoiseFiguresFrame', () => {
  it('decodes the figures, the ENBW, the band and the weighting', () => {
    const frame = decodeNoiseFiguresFrame(
      buildNoiseFrame({ channel: 1, seq: 9, loHz: 20, hiHz: 20000, bandRmsDbv: -106.9, densityDbv: -150.2, enbwHz: 8.79, standard: 3, userMask: 2, held: true }),
    )
    expect(frame.header.channel).toBe(1)
    expect(frame.header.seq).toBe(9)
    expect(frame.measurementLoHz).toBe(20)
    expect(frame.measurementHiHz).toBe(20000)
    expect(frame.bandRmsDbv).toBeCloseTo(-106.9, 4)
    expect(frame.noiseDensityDbvPerRootHz).toBeCloseTo(-150.2, 4)
    expect(frame.enbwHz).toBeCloseTo(8.79, 4)
    expect(frame.weighting).toEqual({ standard: 'ItuR468', user1: false, user2: true })
    expect(frame.held).toBe(true)
  })

  it('reads an unweighted frame as None and an unknown standard byte as None', () => {
    const none = decodeNoiseFiguresFrame(buildNoiseFrame({ channel: 0, seq: 1, loHz: 20, hiHz: 80000, bandRmsDbv: -100, densityDbv: -140, enbwHz: 1, standard: 0, userMask: 0 }))
    expect(none.weighting).toEqual({ standard: 'None', user1: false, user2: false })
    expect(none.held).toBe(false)

    const unknown = decodeNoiseFiguresFrame(buildNoiseFrame({ channel: 0, seq: 1, loHz: 20, hiHz: 80000, bandRmsDbv: -100, densityDbv: -140, enbwHz: 1, standard: 42, userMask: 3 }))
    expect(unknown.weighting).toEqual({ standard: 'None', user1: true, user2: true })
  })
})
