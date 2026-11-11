import { describe, expect, it } from 'vitest'
import type { ReferenceChannelJson } from '../api/client'
import {
  REFERENCE_SNR_CEILING_DB,
  referenceChannelNote,
  referenceChannelWarning,
} from './referenceChannel'

const reading = (snrDb: number, usable = snrDb >= 20): ReferenceChannelJson => ({ snrDb, usable })

describe('referenceChannelWarning', () => {
  /**
   * The report this exists for. A bench with one channel wired ran with *Right channel is
   * reference* ticked — the default — and got a complete, plausible measurement built on dividing
   * by noise, with nothing on screen saying so.
   */
  it('says what is wrong, what to do about it, and what it costs', () => {
    const notice = referenceChannelWarning(reading(2, false))

    expect(notice).toContain('2 dB')
    // Both ways out, because the one-channel bench is the commonest case and clearing the box is
    // the right answer there — the measurement is then against the synthetic stimulus and sound.
    expect(notice).toContain('right input')
    expect(notice).toContain('clear this box')
    // And the consequence, because a warning the reader can dismiss as cosmetic is worse than
    // none: the curves are drawn and they mean nothing.
    expect(notice).toContain('Gain trace')
    expect(notice).toContain('plausible and mean nothing')
  })

  it('says nothing about a reference that is carrying one', () => {
    expect(referenceChannelWarning(reading(85))).toBeNull()
  })

  it('says nothing where there is no reference to check', () => {
    expect(referenceChannelWarning(null)).toBeNull()
    expect(referenceChannelWarning(undefined)).toBeNull()
    expect(referenceChannelWarning(reading(Number.NaN, false))).toBeNull()
  })

  /**
   * The decision is the server's and this wording follows it rather than re-deriving a threshold.
   * A figure the server called usable is not argued with, however it reads here.
   */
  it('follows the server rather than re-deciding', () => {
    expect(referenceChannelWarning({ snrDb: 2, usable: true })).toBeNull()
    expect(referenceChannelWarning({ snrDb: 85, usable: false })).not.toBeNull()
  })
})

describe('referenceChannelNote', () => {
  // The quiet counterpart. A figure that only ever appears when it is bad teaches nobody what good
  // looks like, and this is the first thing in the app to measure the reference channel at all.
  it('quotes a real figure', () => {
    expect(referenceChannelNote(reading(72))).toContain('72 dB')
  })

  /**
   * Past the ceiling the estimate is reporting the arithmetic's own floor rather than the bench's.
   * A simulated run reads `TransferFunctionSnr`'s 200 dB cap exactly, so it has to land on the
   * wording rather than on a number nobody could act on.
   */
  it('stops quoting one where the figure is the arithmetic rather than the bench', () => {
    const note = referenceChannelNote(reading(REFERENCE_SNR_CEILING_DB + 1))

    expect(note).toBe('The reference input is carrying the stimulus cleanly.')
    expect(note).not.toContain('dB')
  })

  it('says nothing where the warning is speaking instead', () => {
    expect(referenceChannelNote(reading(2, false))).toBeNull()
    expect(referenceChannelNote(null)).toBeNull()
  })
})
