import { describe, expect, it } from 'vitest'
import { delayReadings, delayResponseTrace, peakLabel, primaryResponseTrace, referenceLabel } from './measurements'
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

describe('delayResponseTrace', () => {
  const gain = traceOf('Gain')
  const left = traceOf('Left')
  const right = traceOf('Right')

  it('follows the view under a per-trace phase, like the chart', () => {
    expect(delayResponseTrace([left, right, gain], 'Dbv', true, 'PerTrace')).toBe(left)
    expect(delayResponseTrace([left, right, gain], 'Gain', true, 'PerTrace')).toBe(gain)
  })

  it('is always the gain curve when only L/R was measured, and nothing without a phase', () => {
    expect(delayResponseTrace([left, right, gain], 'Dbv', true, 'DutResponse')).toBe(gain)
    expect(delayResponseTrace([left, right, gain], 'Dbv', true, 'ChannelDifference')).toBe(gain)
    expect(delayResponseTrace([left, right, gain], 'Gain', true, 'None')).toBeNull()
  })
})

describe('delayReadings', () => {
  const freqHz = [100, 200, 500, 1000, 2000, 5000]
  const band = { lowHz: 200, highHz: 2000 }

  function withDelays(over: Partial<Parameters<typeof makeTrace>[0]> = {}) {
    return makeTrace({
      freqHz,
      magnitudeMaxDb: freqHz.map(() => 0),
      phaseDeg: freqHz.map(() => 0),
      groupDelayS: [50e-6, 10e-6, 12e-6, 11e-6, 13e-6, 1e-6],
      phaseDelayS: [40e-6, 20e-6, 22e-6, 21e-6, 23e-6, 2e-6],
      peakFreqHz: 100,
      ...over,
    })
  }

  it('reads the median over the reference band under a passband reference', () => {
    const readings = delayReadings(withDelays(), band)
    expect(readings.recorded).toBe(true)
    // Band columns 200..2000 Hz: 10, 12, 11, 13 µs → median 11.5 µs.
    expect(readings.groupDelayAtReferenceS).toBeCloseTo(11.5e-6, 12)
    expect(readings.phaseDelayAtReferenceS).toBeCloseTo(21.5e-6, 12)
  })

  it('finds the in-band extremes with their frequencies over the whole trace', () => {
    const readings = delayReadings(withDelays(), band)
    expect(readings.groupDelayMax).toEqual({ s: 50e-6, freqHz: 100 })
    expect(readings.groupDelayMin).toEqual({ s: 1e-6, freqHz: 5000 })
  })

  it('reads the column nearest the peak under a peak reference, or when the band fell back to it', () => {
    expect(delayReadings(withDelays({ referenceMode: 'Peak' }), band).groupDelayAtReferenceS).toBeCloseTo(50e-6, 12)
    expect(delayReadings(withDelays({ referenceFellBackToPeak: true, peakFreqHz: 4800 }), band).groupDelayAtReferenceS).toBeCloseTo(1e-6, 12)
  })

  it('falls back to the peak column when the band holds no columns', () => {
    expect(delayReadings(withDelays(), { lowHz: 20, highHz: 50 }).groupDelayAtReferenceS).toBeCloseTo(50e-6, 12)
  })

  it('reports nothing recorded for a result saved before the delay curves existed', () => {
    const readings = delayReadings(withDelays({ groupDelayS: null, phaseDelayS: null }), band)
    expect(readings.recorded).toBe(false)
    expect(readings.groupDelayAtReferenceS).toBeNull()
    expect(readings.groupDelayMin).toBeNull()
  })
})
