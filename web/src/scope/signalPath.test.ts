import { describe, expect, it } from 'vitest'
import {
  DEFAULT_SCOPE_PATH,
  SCOPE_COUPLINGS,
  pathChangesTrace,
  pathQuery,
  pathSummary,
} from './signalPath'

describe('the display path', () => {
  it('opens on DC, which changes nothing', () => {
    expect(DEFAULT_SCOPE_PATH.coupling).toBe('Dc')
    expect(pathChangesTrace(DEFAULT_SCOPE_PATH)).toBe(false)
    expect(pathSummary(DEFAULT_SCOPE_PATH)).toBeNull()
    expect(pathQuery(DEFAULT_SCOPE_PATH)).toEqual({})
  })

  it('says so when the trace is not the record', () => {
    // The badge, the panel's note and the report's row all read this, because AC coupling moves
    // every level reading by the record's own mean and nothing else on screen would say so.
    const ac = { coupling: 'Ac' } as const
    expect(pathChangesTrace(ac)).toBe(true)
    expect(pathSummary(ac)).toBe('AC coupled')
  })

  it('carries the coupling to the analyzer, and only when it is asked for', () => {
    // Absent rather than `coupling=Dc`, so the commonest fetch is byte-identical to one sent
    // before any of this existed.
    expect(pathQuery({ coupling: 'Ac' })).toEqual({ coupling: 'Ac' })
    expect(pathQuery({ coupling: 'Dc' })).toEqual({})
  })

  it('offers both couplings, each with a reason', () => {
    expect(SCOPE_COUPLINGS.map((c) => c.value)).toEqual(['Dc', 'Ac'])
    for (const entry of SCOPE_COUPLINGS) {
      expect(entry.label.length).toBeGreaterThan(0)
      expect(entry.title.length).toBeGreaterThan(20)
    }
  })
})
