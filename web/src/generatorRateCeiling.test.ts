import { describe, expect, it } from 'vitest'
import { generatorRateCeilingNotice } from './generatorRateCeiling'

describe('generatorRateCeilingNotice', () => {
  // A sound card plays at every rate it captures at, and the server sends null for it. Infinity is
  // the same answer arriving from a server that does not send the field — comparing against it
  // would make every rate "above the ceiling".
  it('generatorRateCeilingNotice_OnAnAnalyzerWithNoCeiling_SaysNothing', () => {
    expect(generatorRateCeilingNotice(384000, null)).toBeNull()
    expect(generatorRateCeilingNotice(384000, undefined)).toBeNull()
    expect(generatorRateCeilingNotice(384000, Number.POSITIVE_INFINITY)).toBeNull()
  })

  // The QA403's three documented rates, where the generator is the whole point of the screen.
  it('generatorRateCeilingNotice_AtOrBelowTheCeiling_SaysNothing', () => {
    expect(generatorRateCeilingNotice(48000, 192000)).toBeNull()
    expect(generatorRateCeilingNotice(96000, 192000)).toBeNull()
    expect(generatorRateCeilingNotice(192000, 192000)).toBeNull()
  })

  it('generatorRateCeilingNotice_AboveTheCeiling_NamesTheRateAndWhatToDo', () => {
    const notice = generatorRateCeilingNotice(384000, 192000)
    expect(notice).toContain('384 kHz')
    expect(notice).toContain('input-only rate')
    expect(notice).toContain('external generator')
  })

  it('generatorRateCeilingNotice_WithANonsenseRate_SaysNothing', () => {
    expect(generatorRateCeilingNotice(Number.NaN, 192000)).toBeNull()
  })
})
