import { describe, expect, it } from 'vitest'
import { stimulusFloorNotice } from './stimulusFloor'

describe('stimulusFloorNotice', () => {
  // A QA403 is DC-coupled: it has no floor, and must never show this.
  it('stimulusFloorNotice_OnAnAnalyzerWithNoFloor_SaysNothing', () => {
    expect(stimulusFloorNotice(2, 0)).toBeNull()
    expect(stimulusFloorNotice(2, null)).toBeNull()
    expect(stimulusFloorNotice(2, undefined)).toBeNull()
  })

  it('stimulusFloorNotice_AboveTheFloor_SaysNothing', () => {
    expect(stimulusFloorNotice(20, 20)).toBeNull()
    expect(stimulusFloorNotice(50, 20)).toBeNull()
  })

  // The 2 Hz default is what a tube-amp project carries; on a card it cannot be played.
  it('stimulusFloorNotice_BelowTheFloor_NamesBothFrequencies', () => {
    const notice = stimulusFloorNotice(2, 20)
    expect(notice).toContain('20 Hz')
    expect(notice).toContain('2 Hz')
  })

  it('stimulusFloorNotice_RoundsToOneDecimal', () => {
    expect(stimulusFloorNotice(2.5, 17.25)).toContain('17.3 Hz')
  })

  it('stimulusFloorNotice_WithANonsenseRequest_SaysNothing', () => {
    expect(stimulusFloorNotice(Number.NaN, 20)).toBeNull()
  })

  // Not a refusal: the measurement is good over the band the card can drive, so this reads as a
  // caveat rather than an error the user has to clear before running.
  it('stimulusFloorNotice_ExplainsWhatWouldBeMeasuredInstead', () => {
    expect(stimulusFloorNotice(2, 20)).toContain("its own high-pass")
  })
})
