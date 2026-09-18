import { describe, expect, it } from 'vitest'
import { peakLabel, primaryResponseTrace, referenceLabel } from './measurements'
import { makeTrace } from './testTrace'

function traceOf(kind: 'Gain' | 'Left' | 'Right') {
  return { ...makeTrace({ freqHz: [100, 1000], magnitudeMaxDb: [0, 0], phaseDeg: [0, 0] }), kind }
}

describe('primaryResponseTrace', () => {
  const gain = traceOf('Gain')
  const left = traceOf('Left')
  const right = traceOf('Right')

  it('is the gain curve in gain view, or nothing', () => {
    expect(primaryResponseTrace([left, right, gain], 'Gain', true)).toBe(gain)
    expect(primaryResponseTrace([left, right], 'Gain', true)).toBeNull()
  })

  it('is the shown input in dBV view, left first, falling back to whatever exists', () => {
    expect(primaryResponseTrace([left, right, gain], 'Dbv', true)).toBe(left)
    expect(primaryResponseTrace([left, right, gain], 'Dbv', false)).toBe(right)
    expect(primaryResponseTrace([left, gain], 'Dbv', false)).toBe(left)
    expect(primaryResponseTrace([gain], 'Dbv', true)).toBeNull()
  })
})

describe('labels', () => {
  it('captions the peak with the view', () => {
    expect(peakLabel('Gain')).toBe('Max. gain')
    expect(peakLabel('Dbv')).toBe('Max. amplitude')
  })

  it('spells out the reference, and stays silent on a result that never recorded one', () => {
    const base = traceOf('Gain')
    expect(referenceLabel({ ...base, referenceMode: 'Peak' })).toBe('Peak')
    expect(referenceLabel({ ...base, referenceMode: 'BandMedian' })).toBe('Passband median')
    expect(referenceLabel({ ...base, referenceMode: 'BandMedian', referenceFellBackToPeak: true })).toBe('Peak (band not measured)')
    expect(referenceLabel({ ...base, referenceMode: null })).toBeNull()
  })
})
