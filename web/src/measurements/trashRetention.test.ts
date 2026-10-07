import { describe, expect, it } from 'vitest'
import { commitRetentionDays } from './trashRetention'

describe('commitRetentionDays', () => {
  it('keeps a usable number of days', () => {
    expect(commitRetentionDays('14')).toBe(14)
    expect(commitRetentionDays('1')).toBe(1)
    expect(commitRetentionDays('3650')).toBe(3650)
    expect(commitRetentionDays(' 30 ')).toBe(30)
  })

  /**
   * Zero clamps up rather than being accepted: a retention of nothing is not a shorter safety net
   * but none at all — the delete would be as irreversible as it was before the trash existed, while
   * the confirmation went on promising a restore.
   */
  it('clamps zero and negatives up to a single day', () => {
    expect(commitRetentionDays('0')).toBe(1)
    expect(commitRetentionDays('-5')).toBe(1)
  })

  it('clamps an absurd number down to the ten-year bound', () => {
    expect(commitRetentionDays('99999')).toBe(3650)
  })

  /** Clearing a box means "whatever the normal thing is", which is the fortnight, not the minimum. */
  it('reads an emptied field as the default fortnight', () => {
    expect(commitRetentionDays('')).toBe(14)
    expect(commitRetentionDays('   ')).toBe(14)
    expect(commitRetentionDays('not a number')).toBe(14)
  })

  /** The purge compares whole days, so a fraction has to become one before it is stored. */
  it('rounds to whole days', () => {
    expect(commitRetentionDays('7.4')).toBe(7)
    expect(commitRetentionDays('7.6')).toBe(8)
    expect(commitRetentionDays('0.2')).toBe(1)
  })
})
