import { describe, expect, it } from 'vitest'
import { determineAttenuation, nearestInputRange } from './attenuation'

describe('determineAttenuation', () => {
  it('determineAttenuation_AtStepBoundary_PicksThatStep', () => {
    // -5 dBV + 5 dBV headroom = 0, right at the first step's own boundary.
    expect(determineAttenuation(-5)).toBe(0)
  })

  it('determineAttenuation_JustAboveStepBoundary_PicksNextStep', () => {
    // -4.9 dBV + 5 = 0.1, just past 0 — the ladder's first non-zero step.
    expect(determineAttenuation(-4.9)).toBe(6)
  })

  it('determineAttenuation_MidLadder_PicksMatchingStep', () => {
    expect(determineAttenuation(10)).toBe(18) // 10 + 5 = 15, first step >= 15 is 18.
  })

  it('determineAttenuation_AboveHighestStep_ClampsTo42', () => {
    expect(determineAttenuation(100)).toBe(42)
  })
})

describe('nearestInputRange', () => {
  it('nearestInputRange_ExactMatch_ReturnsSameValue', () => {
    expect(nearestInputRange(18, [0, 6, 12, 18, 24, 30, 36, 42])).toBe(18)
  })

  it('nearestInputRange_BetweenTwoOptions_PicksCloser', () => {
    expect(nearestInputRange(20, [0, 6, 12, 18, 24, 30, 36, 42])).toBe(18)
    expect(nearestInputRange(22, [0, 6, 12, 18, 24, 30, 36, 42])).toBe(24)
  })
})
