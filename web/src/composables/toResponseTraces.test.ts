import { describe, expect, it } from 'vitest'
import type { StoredResponseTrace } from '../api/client'
import { toImpedanceTraces, toResponseTraces } from './useMeasurements'

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

  it('reads a stored Zout row as an impedance curve and leaves the gain rows to the response reader', () => {
    const impedance = toImpedanceTraces([stored('zout'), stored('gain_load1'), stored('gain_load2')])
    expect(impedance.map((t) => t.kind)).toEqual(['Impedance'])
    expect(impedance[0].magnitudeMaxDbOhms).toEqual([-70, -59])
  })

  it('refuses a kind it does not know rather than drawing it as gain', () => {
    expect(() => toResponseTraces([stored('z')])).toThrow(/Unknown response trace kind 'z'/)
  })
})
