import { describe, expect, it } from 'vitest'
import type { ThdSweepPoint } from '../api/client'
import {
  extendGridPerOctave,
  logPerOctave,
  referenceEndFreqHz,
  sweepEndFreqHzFromParams,
  sweepMethodFromParams,
  sweepTraceLabel,
  sweepTraceValue,
} from './points'

function point(overrides: Partial<ThdSweepPoint> = {}): ThdSweepPoint {
  return {
    freqHz: 1000,
    channel: 0,
    fundamentalDbv: -20,
    gainDb: -0.3,
    powerWatt: 0.001,
    thdPercent: 0.4,
    thdNPercent: 0.5,
    snrDb: 90,
    harmonicPercent: [0.3, 0.1, 0.02],
    d6PlusPercent: 0.005,
    noisePercent: 0.001,
    generatorDbv: -20,
    previewFft: null,
    previewScope: null,
    normalizedThdPercent: 0.52,
    normalizedHarmonicPercent: [0.5, 0.12, null, null],
    normalizedD6PlusPercent: null,
    ...overrides,
  }
}

describe('sweepTraceValue', () => {
  it('reads the conventional figures under Conventional', () => {
    const p = point()
    expect(sweepTraceValue(p, 'thd', 'Conventional')).toBe(0.4)
    expect(sweepTraceValue(p, 'd2', 'Conventional')).toBe(0.3)
    expect(sweepTraceValue(p, 'd4', 'Conventional')).toBe(0.02)
    expect(sweepTraceValue(p, 'd5', 'Conventional')).toBeNaN()
    expect(sweepTraceValue(p, 'd6plus', 'Conventional')).toBe(0.005)
  })

  it('reads the normalized figures under FrequencyNormalized, NaN where an order has no reference', () => {
    const p = point()
    expect(sweepTraceValue(p, 'thd', 'FrequencyNormalized')).toBe(0.52)
    expect(sweepTraceValue(p, 'd2', 'FrequencyNormalized')).toBe(0.5)
    expect(sweepTraceValue(p, 'd3', 'FrequencyNormalized')).toBe(0.12)
    expect(sweepTraceValue(p, 'd4', 'FrequencyNormalized')).toBeNaN()
    expect(sweepTraceValue(p, 'd6plus', 'FrequencyNormalized')).toBeNaN()
  })

  it('never falls back to the conventional figure when the normalized one is absent', () => {
    // A point near the top of the extended grid has conventional figures and no normalized ones.
    // Showing the conventional value in its place would draw a plausible line that is the wrong
    // measurement; a gap is the honest reading.
    const p = point({ normalizedThdPercent: null, normalizedHarmonicPercent: null, normalizedD6PlusPercent: null })
    expect(sweepTraceValue(p, 'thd', 'FrequencyNormalized')).toBeNaN()
    expect(sweepTraceValue(p, 'd2', 'FrequencyNormalized')).toBeNaN()
    expect(sweepTraceValue(p, 'd6plus', 'FrequencyNormalized')).toBeNaN()
  })

  it('shows Magnitude and Noise the same way under either method', () => {
    const p = point()
    expect(sweepTraceValue(p, 'magnitude', 'FrequencyNormalized')).toBe(-0.3)
    expect(sweepTraceValue(p, 'noise', 'FrequencyNormalized')).toBe(0.001)
    expect(sweepTraceValue(p, 'magnitude', 'Conventional')).toBe(-0.3)
    expect(sweepTraceValue(p, 'noise', 'Conventional')).toBe(0.001)
  })
})

describe('sweepTraceLabel', () => {
  it('prefixes the distortion traces with n on a normalized run and leaves the rest alone', () => {
    expect(sweepTraceLabel('thd', 'FrequencyNormalized')).toBe('nTHD')
    expect(sweepTraceLabel('d2', 'FrequencyNormalized')).toBe('nD2')
    expect(sweepTraceLabel('d6plus', 'FrequencyNormalized')).toBe('nD6+')
    expect(sweepTraceLabel('magnitude', 'FrequencyNormalized')).toBe('Magn')
    expect(sweepTraceLabel('noise', 'FrequencyNormalized')).toBe('Noise')
    expect(sweepTraceLabel('thd', 'Conventional')).toBe('THD')
    expect(sweepTraceLabel('d6plus', 'Conventional')).toBe('D6+')
  })
})

describe('sweepMethodFromParams', () => {
  it('reads the method off a stored sweep, and treats everything else as conventional', () => {
    expect(sweepMethodFromParams('{"endFreqHz":20000,"thdMethod":"FrequencyNormalized"}')).toBe('FrequencyNormalized')
    expect(sweepMethodFromParams('{"endFreqHz":20000,"thdMethod":"Conventional"}')).toBe('Conventional')
    // Rows written before the method existed.
    expect(sweepMethodFromParams('{"endFreqHz":20000}')).toBe('Conventional')
    // An FFT result's params, an empty string, garbage.
    expect(sweepMethodFromParams('{"fftSize":32768}')).toBe('Conventional')
    expect(sweepMethodFromParams('')).toBe('Conventional')
    expect(sweepMethodFromParams(null)).toBe('Conventional')
    expect(sweepMethodFromParams('not json')).toBe('Conventional')
  })
})

describe('sweepEndFreqHzFromParams', () => {
  it('reads the requested end, or null when the JSON does not say', () => {
    expect(sweepEndFreqHzFromParams('{"endFreqHz":20000}')).toBe(20000)
    expect(sweepEndFreqHzFromParams('{"fftSize":32768}')).toBeNull()
    expect(sweepEndFreqHzFromParams('{"endFreqHz":"20000"}')).toBeNull()
    expect(sweepEndFreqHzFromParams(undefined)).toBeNull()
  })
})

describe('extendGridPerOctave', () => {
  it('continues at the nominal step and includes the first value at or above the cap', () => {
    // 20 kHz towards 100 kHz at 5/octave: 2.32 octaves is 11.6 steps, so the 12th lands above.
    const values = extendGridPerOctave(20000, 100000, 5)
    expect(values).toHaveLength(12)
    expect(values[0]).toBeCloseTo(20000 * 2 ** (1 / 5), 6)
    expect(values[10]).toBeLessThan(100000)
    expect(values[11]).toBeGreaterThanOrEqual(100000)
  })

  it('adds nothing when the cap is at or below the start', () => {
    expect(extendGridPerOctave(20000, 20000, 5)).toEqual([])
    expect(extendGridPerOctave(20000, 10000, 5)).toEqual([])
  })

  it('leaves the requested grid untouched', () => {
    const requested = logPerOctave(20, 20000, 5)
    const extension = extendGridPerOctave(20000, 100000, 5)
    expect(requested[requested.length - 1]).toBeCloseTo(20000, 9)
    expect(extension[0]).toBeGreaterThan(20000)
  })
})

describe('referenceEndFreqHz', () => {
  it('is five times the end, capped at the sweep ceiling', () => {
    expect(referenceEndFreqHz(4000, 48000)).toBe(20000)
    // 5 x 20 kHz is above 0.98 x 96 kHz.
    expect(referenceEndFreqHz(20000, 192000)).toBeCloseTo(94080, 6)
  })
})
