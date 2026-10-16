import { describe, expect, it } from 'vitest'
import {
  DEFAULT_HARMONIC_MARKER_UNIT,
  HARMONIC_MARKER_UNITS,
  harmonicMarkerLabel,
  parseHarmonicMarkerUnit,
} from './spectrumUnits'

describe('parseHarmonicMarkerUnit', () => {
  it('takes either unit', () => {
    for (const unit of HARMONIC_MARKER_UNITS) expect(parseHarmonicMarkerUnit(unit)).toBe(unit)
  })

  // A params_json another build wrote must not break a measurement the user has merely selected.
  it('reads anything else as the default', () => {
    expect(parseHarmonicMarkerUnit(null)).toBe(DEFAULT_HARMONIC_MARKER_UNIT)
    expect(parseHarmonicMarkerUnit(undefined)).toBe(DEFAULT_HARMONIC_MARKER_UNIT)
    expect(parseHarmonicMarkerUnit('dbc')).toBe(DEFAULT_HARMONIC_MARKER_UNIT)
    expect(parseHarmonicMarkerUnit('dBu')).toBe(DEFAULT_HARMONIC_MARKER_UNIT)
  })

  it('defaults to the ratio the markers always showed', () => {
    expect(DEFAULT_HARMONIC_MARKER_UNIT).toBe('dBc')
  })
})

describe('harmonicMarkerLabel', () => {
  it('prints dBc to the nearest whole decibel, as the markers always have', () => {
    expect(harmonicMarkerLabel('dBc', -83.4, -6)).toBe('-83')
    expect(harmonicMarkerLabel('dBc', -83.6, undefined)).toBe('-84')
  })

  // The harmonics table's own arithmetic -- dBV = fundamental dBV + dBc. The two must agree: they
  // are the same two columns of the same reading.
  it('adds the fundamental for dBV', () => {
    expect(harmonicMarkerLabel('dBV', -83.4, -6.02)).toBe('-89.4')
    expect(harmonicMarkerLabel('dBV', -40, 12)).toBe('-28.0')
  })

  it('has nothing to print in dBV without a fundamental', () => {
    expect(harmonicMarkerLabel('dBV', -83.4, undefined)).toBeNull()
    expect(harmonicMarkerLabel('dBV', -83.4, Number.NaN)).toBeNull()
  })
})
