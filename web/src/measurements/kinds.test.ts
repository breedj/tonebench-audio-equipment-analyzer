import { describe, expect, it } from 'vitest'
import { manualPageForKind } from '../manual/pages'
import {
  CROSSTALK_KIND,
  isCrosstalkKind,
  isFrequencyResponseKind,
  isImpedanceKind,
  isOutputImpedanceKind,
  isSweepKind,
  MEASUREMENT_KINDS,
  OUTPUT_IMPEDANCE_KIND,
} from './kinds'

describe('measurement kinds', () => {
  it('lists crosstalk as a kind of its own, with a manual page', () => {
    expect(MEASUREMENT_KINDS.some((k) => k.kind === CROSSTALK_KIND)).toBe(true)
    expect(manualPageForKind(CROSSTALK_KIND)).toBeTruthy()
  })

  it('lists output impedance as a kind of its own, with a manual page, outside the impedance family', () => {
    expect(MEASUREMENT_KINDS.some((k) => k.kind === OUTPUT_IMPEDANCE_KIND)).toBe(true)
    expect(manualPageForKind(OUTPUT_IMPEDANCE_KIND)).toBeTruthy()
    expect(isOutputImpedanceKind(OUTPUT_IMPEDANCE_KIND)).toBe(true)
    expect(isImpedanceKind(OUTPUT_IMPEDANCE_KIND)).toBe(false)
    expect(isFrequencyResponseKind(OUTPUT_IMPEDANCE_KIND)).toBe(false)
    expect(isOutputImpedanceKind('Impedance')).toBe(false)
  })

  it('keeps crosstalk out of every other family: it shares the response rows, not the response screen', () => {
    expect(isCrosstalkKind(CROSSTALK_KIND)).toBe(true)
    expect(isFrequencyResponseKind(CROSSTALK_KIND)).toBe(false)
    expect(isImpedanceKind(CROSSTALK_KIND)).toBe(false)
    expect(isSweepKind(CROSSTALK_KIND)).toBe(false)
    expect(isCrosstalkKind('FrequencyResponse')).toBe(false)
    expect(isCrosstalkKind(null)).toBe(false)
  })
})
