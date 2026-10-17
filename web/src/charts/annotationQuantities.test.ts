import { describe, expect, it } from 'vitest'
import {
  LINEAR_SPACE,
  RATIO_SPACE,
  annotationQuantity,
  annotationQuantityIds,
  formatAnnotationHz,
  formatAnnotationHzRange,
  isKnownQuantity,
} from './annotationQuantities'

describe('annotationQuantity', () => {
  it('prints each quantity in the unit its axis is labelled with', () => {
    expect(annotationQuantity('hz').format(1000)).toBe('1.000 kHz')
    expect(annotationQuantity('dbv').format(-98.24)).toBe('-98.2 dBV')
    expect(annotationQuantity('db').format(-3)).toBe('-3.0 dB')
    expect(annotationQuantity('dbc').format(-72.5)).toBe('-72.5 dBc')
    expect(annotationQuantity('pct').format(0.0123)).toBe('0.0123 %')
    expect(annotationQuantity('outputWatt').format(2.4)).toBe('2.40 W')
    expect(annotationQuantity('ms').format(1.25)).toBe('1.250 ms')
    expect(annotationQuantity('ohm').format(12.5)).toBe('12.5 Ω')
    expect(annotationQuantity('deg').format(-180)).toBe('-180.0 °')
    expect(annotationQuantity('secondsAgo').format(-4)).toBe('-4.00 s')
  })

  it('names each quantity the way a table column head and a field caption want it', () => {
    expect(annotationQuantity('hz').label).toBe('Frequency')
    expect(annotationQuantity('ms').label).toBe('Time')
    expect(annotationQuantity('outputWatt').label).toBe('Output power')
    expect(annotationQuantity('re').label).toBe('Real part')
    expect(annotationQuantity('ohm').label).toBe('Impedance')
  })

  it('prints a range in one unit chosen from its high end', () => {
    expect(annotationQuantity('hz').formatRange(900, 1200)).toBe('0.90 – 1.20 kHz')
    expect(annotationQuantity('dbv').formatRange(-95, -40)).toBe('-95.0 – -40.0 dBV')
  })

  it('answers for an id no build knows rather than throwing, and prints the bare number', () => {
    // The parser's own tolerance rule applied to labelling: an annotation is the user's words, and a
    // note whose unit this build cannot name is still worth showing.
    const unknown = annotationQuantity('gigawatts')
    expect(isKnownQuantity('gigawatts')).toBe(false)
    expect(unknown.format(1.5)).toBe('1.50')
    expect(unknown.unit).toBe('')
    expect(unknown.space).toBe(LINEAR_SPACE)
  })

  it('answers something printable for every value, including the ones no axis has a place for', () => {
    for (const id of annotationQuantityIds()) {
      for (const value of [Number.NaN, Number.POSITIVE_INFINITY]) {
        expect(annotationQuantity(id).format(value)).toBe('—')
      }
      expect(typeof annotationQuantity(id).format(1)).toBe('string')
    }
  })
})

describe('a quantity’s space', () => {
  it('measures a strictly positive quantity as a ratio and a signed one as a difference', () => {
    // The rule: ratio space where the quantity cannot be zero or below by construction (a frequency,
    // an impedance, a distortion percentage -- all log-plotted), linear otherwise.
    for (const id of ['hz', 'pct', 'ohm', 'outputVolt', 'outputWatt', 'inputVolt']) {
      expect(annotationQuantity(id).space).toBe(RATIO_SPACE)
      expect(annotationQuantity(id).positiveOnly).toBe(true)
    }
    for (const id of ['dbv', 'db', 'dbc', 'deg', 'ms', 'secondsAgo', 'volt', 're', 'im']) {
      expect(annotationQuantity(id).space).toBe(LINEAR_SPACE)
      expect(annotationQuantity(id).positiveOnly).toBe(false)
    }
  })

  it('round-trips a value through either space', () => {
    for (const space of [RATIO_SPACE, LINEAR_SPACE]) {
      expect(space.fromSpace(space.toSpace(1234.5))).toBeCloseTo(1234.5, 6)
    }
  })

  it('measures a ratio offset as octaves, which is what the old field name meant', () => {
    expect(RATIO_SPACE.toSpace(2000) - RATIO_SPACE.toSpace(1000)).toBeCloseTo(1, 10)
    expect(RATIO_SPACE.fromSpace(RATIO_SPACE.toSpace(1000) + 0.45)).toBeCloseTo(1000 * Math.pow(2, 0.45), 6)
  })

  it('has no position for zero or below on a ratio axis, which is how the layout skips one', () => {
    expect(Number.isFinite(RATIO_SPACE.toSpace(0))).toBe(false)
    expect(Number.isFinite(RATIO_SPACE.toSpace(-5))).toBe(false)
    expect(LINEAR_SPACE.toSpace(0)).toBe(0)
    expect(LINEAR_SPACE.toSpace(-5)).toBe(-5)
  })
})

describe('formatAnnotationHz', () => {
  it('prints whole hertz low down and kilohertz above ten', () => {
    expect(formatAnnotationHz(50)).toBe('50.00 Hz')
    expect(formatAnnotationHz(440)).toBe('440.0 Hz')
    expect(formatAnnotationHz(1000)).toBe('1.000 kHz')
    expect(formatAnnotationHz(21500)).toBe('21.50 kHz')
  })

  it('prints both ends of a range in the unit the high end asks for', () => {
    expect(formatAnnotationHzRange(900, 1200)).toBe('0.90 – 1.20 kHz')
    expect(formatAnnotationHzRange(40, 120)).toBe('40.0 – 120.0 Hz')
  })
})
