import { describe, expect, it } from 'vitest'
import { manualPageForKind } from '../manual/pages'
import { CROSSTALK_KIND, isCrosstalkKind, isFrequencyResponseKind, isImpedanceKind, isSweepKind, MEASUREMENT_KINDS } from './kinds'

describe('measurement kinds', () => {
  it('lists crosstalk as a kind of its own, with a manual page', () => {
    expect(MEASUREMENT_KINDS.some((k) => k.kind === CROSSTALK_KIND)).toBe(true)
    expect(manualPageForKind(CROSSTALK_KIND)).toBeTruthy()
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
