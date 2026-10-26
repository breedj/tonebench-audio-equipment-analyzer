import { describe, expect, it } from 'vitest'
import { decaySubjectIds, decaySubjects, LIVE_DECAY_LABEL, type DecayOverlay } from './decaySubjects'

function overlay(id: number, name: string): DecayOverlay {
  return { id, detail: { name } }
}

describe('decaySubjects', () => {
  it('draws nothing when there is no run and nothing is checked', () => {
    expect(decaySubjects(false, [])).toEqual([])
  })

  /**
   * The rule this exists for. Subject zero is the primary, and its surface is the one the
   * screenshot gate, the notes under the chart and a report all read — so a history item taking
   * that place would have all three quietly describing somebody else's run.
   */
  it('puts the live run first whenever there is one', () => {
    const subjects = decaySubjects(true, [overlay(7, 'last week'), overlay(9, 'yesterday')])
    expect(subjects.map((s) => s.measurementId)).toEqual([null, 7, 9])
    expect(subjects[0].label).toBe(LIVE_DECAY_LABEL)
  })

  /**
   * With nothing live the first checked item takes that place, which is what the single chart drew
   * before stacking existed: without it a checked item drew its waveform and its gated response
   * and then left both decay views blank.
   */
  it('falls back to the first checked item when nothing is live', () => {
    const subjects = decaySubjects(false, [overlay(7, 'last week'), overlay(9, 'yesterday')])
    expect(subjects.map((s) => s.measurementId)).toEqual([7, 9])
    expect(subjects[0].label).toBe('last week')
  })

  it('keeps the checked items in the order they were given', () => {
    const ids = decaySubjects(false, [overlay(3, 'c'), overlay(1, 'a'), overlay(2, 'b')])
    expect(ids.map((s) => s.measurementId)).toEqual([3, 1, 2])
  })

  /** Four charts at most: one live plus the checklist's own three-overlay cap. */
  it('reaches four runs and no more, given the cap the checklist enforces', () => {
    const subjects = decaySubjects(true, [overlay(1, 'a'), overlay(2, 'b'), overlay(3, 'c')])
    expect(subjects).toHaveLength(4)
  })

  /** The key has to be stable and distinct, since it is what the chart list is rendered by. */
  it('gives every run a key of its own, and the live one a key no result can take', () => {
    const subjects = decaySubjects(true, [overlay(1, 'a'), overlay(2, 'b')])
    const keys = subjects.map((s) => s.key)
    expect(new Set(keys).size).toBe(keys.length)
    expect(keys[0]).toBe('live')
  })
})

describe('decaySubjectIds', () => {
  /**
   * What the surfaces of unchecked runs are pruned against — the live run has no id, and including
   * one for it would keep a stored surface alive under a key nothing draws.
   */
  it('is the stored ids alone, leaving the live run out', () => {
    const subjects = decaySubjects(true, [overlay(4, 'a'), overlay(5, 'b')])
    expect([...decaySubjectIds(subjects)].sort()).toEqual([4, 5])
  })

  it('is empty when only the live run is on screen', () => {
    expect(decaySubjectIds(decaySubjects(true, [])).size).toBe(0)
  })
})
