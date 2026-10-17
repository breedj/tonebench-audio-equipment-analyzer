import { describe, expect, it } from 'vitest'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { PHASE_FLOOR_BELOW_REFERENCE_DB, delayDegPerHz, phaseFloorDb, phaseSeries } from './phaseCurve'

/**
 * A trace whose magnitude falls away linearly from its reference, with a phase that reads its own
 * column index — so a blanked column is unmistakable in the output.
 */
function trace(overrides: Partial<ResponseTrace> = {}): ResponseTrace {
  const columns = 5
  const magnitudeMaxDb = [0, -20, -50, -70, -200]
  return {
    kind: 'Left',
    startFreqHz: 10,
    endFreqHz: 100,
    freqHz: [10, 20, 40, 80, 100],
    magnitudeMinDb: magnitudeMaxDb,
    magnitudeMaxDb,
    phaseDeg: Array.from({ length: columns }, (_, i) => i),
    coherence: Array.from({ length: columns }, () => 1),
    groupDelayS: null,
    phaseDelayS: null,
    oneDb: { lowHz: null, highHz: null, widthHz: null, lowClamped: false, highClamped: false },
    threeDb: { lowHz: null, highHz: null, widthHz: null, lowClamped: false, highClamped: false },
    peakDb: 0,
    peakFreqHz: 10,
    referenceDb: 0,
    referenceMode: 'BandMedian',
    referenceFellBackToPeak: false,
    ...overrides,
  } as ResponseTrace
}

const NO_RESTORE = { restoreDelay: false, latencySamples: null, sampleRateHz: null }

describe('the phase floor', () => {
  it('is measured down from the trace\'s bandwidth reference', () => {
    expect(phaseFloorDb(trace({ referenceDb: -12 }), 'PerTrace')).toBe(-12 + PHASE_FLOOR_BELOW_REFERENCE_DB)
  })

  it('falls back to the peak on a row written before the reference was recorded', () => {
    expect(phaseFloorDb(trace({ referenceDb: null, peakDb: -3 }), 'PerTrace')).toBe(-3 + PHASE_FLOOR_BELOW_REFERENCE_DB)
  })

  // A stepped sweep measures one tone at a time and removes no delay, so its phase is a reading at
  // every point it took; blanking it would delete a measurement rather than a wind-up.
  it('applies to a deconvolved run only', () => {
    expect(phaseFloorDb(trace(), 'ChannelDifference')).toBe(Number.NEGATIVE_INFINITY)
    expect(phaseFloorDb(trace(), 'DutResponse')).toBe(Number.NEGATIVE_INFINITY)
    expect(phaseFloorDb(null, 'PerTrace')).toBe(Number.NEGATIVE_INFINITY)
  })
})

describe('the phase series', () => {
  it('blanks the columns below the floor, as gaps rather than zeros', () => {
    // Reference 0 dB, so the floor is −60: the −70 and −200 columns go.
    expect(phaseSeries(trace(), 'PerTrace', NO_RESTORE, 5)).toEqual([0, 1, 2, null, null])
  })

  it('keeps every column of a stepped run', () => {
    expect(phaseSeries(trace(), 'ChannelDifference', NO_RESTORE, 5)).toEqual([0, 1, 2, 3, 4])
  })

  it('puts the removed delay back as a lag of 360·f·τ', () => {
    const restore = { restoreDelay: true, latencySamples: 48, sampleRateHz: 48_000 }
    // 360 × 48 / 48000 = 0.36° per Hz, so the 10 Hz column loses 3.6° and the 20 Hz one 7.2°.
    const curve = phaseSeries(trace(), 'PerTrace', restore, 5)
    expect(curve[0]).toBeCloseTo(0 - 3.6, 10)
    expect(curve[1]).toBeCloseTo(1 - 7.2, 10)
    expect(curve[3]).toBeNull()
  })

  it('pads a trace shorter than the shared grid, and answers all-null for no trace', () => {
    expect(phaseSeries(trace(), 'PerTrace', NO_RESTORE, 7)).toEqual([0, 1, 2, null, null, null, null])
    expect(phaseSeries(null, 'PerTrace', NO_RESTORE, 3)).toEqual([null, null, null])
  })

  it('drops a column whose phase is not a number', () => {
    expect(phaseSeries(trace({ phaseDeg: [0, NaN, 2, 3, 4] }), 'PerTrace', NO_RESTORE, 5)).toEqual([0, null, 2, null, null])
  })
})

describe('the restored delay', () => {
  it('is zero unless there is a delay and the checkbox asks for it', () => {
    expect(delayDegPerHz({ restoreDelay: false, latencySamples: 48, sampleRateHz: 48_000 })).toBe(0)
    expect(delayDegPerHz({ restoreDelay: true, latencySamples: null, sampleRateHz: 48_000 })).toBe(0)
    expect(delayDegPerHz({ restoreDelay: true, latencySamples: 48, sampleRateHz: 0 })).toBe(0)
    expect(delayDegPerHz({ restoreDelay: true, latencySamples: Number.NaN, sampleRateHz: 48_000 })).toBe(0)
  })

  it('is 360·τ/fs', () => {
    expect(delayDegPerHz({ restoreDelay: true, latencySamples: 48, sampleRateHz: 48_000 })).toBeCloseTo(0.36, 12)
  })
})
