import { describe, expect, it } from 'vitest'
import type { StoredResponseTrace } from '../api/client'
import { toResponseTraces } from './useMeasurements'

function stored(kind: string): StoredResponseTrace {
  return {
    kind,
    startHz: 20,
    endHz: 20000,
    freqHz: [20, 20000],
    magnitudeMinDb: [-70, -59],
    magnitudeMaxDb: [-70, -59],
    phaseDeg: [0, 0],
    coherence: [1, 1],
    oneDb: { lowerHz: 0, upperHz: 0, lowerClamped: false, upperClamped: false },
    threeDb: { lowerHz: 0, upperHz: 0, lowerClamped: false, upperClamped: false },
    peakDb: -59,
    peakHz: 20000,
    referenceDb: -59,
    referenceMode: 'Peak',
  } as StoredResponseTrace
}

describe('toResponseTraces', () => {
  it('maps the stored crosstalk kinds onto the wire names', () => {
    expect(toResponseTraces([stored('xtalk_l2r'), stored('xtalk_r2l'), stored('gain')]).map((t) => t.kind)).toEqual([
      'CrosstalkLeftToRight',
      'CrosstalkRightToLeft',
      'Gain',
    ])
  })

  it('refuses a kind it does not know rather than drawing it as gain', () => {
    expect(() => toResponseTraces([stored('z')])).toThrow(/Unknown response trace kind 'z'/)
  })
})
