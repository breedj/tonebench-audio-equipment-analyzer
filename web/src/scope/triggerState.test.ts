import { describe, expect, it } from 'vitest'
import { scopeTriggerBadge, type ScopeTriggerInput } from './triggerState'

const RUNNING: ScopeTriggerInput = { mode: 'Normal', running: true, engineState: 'Waiting' }

const stateOf = (input: Partial<ScopeTriggerInput>) =>
  scopeTriggerBadge({ ...RUNNING, ...input }).state

describe('the six states', () => {
  it('is Armed while the pre-trigger is still filling', () => {
    expect(stateOf({ engineState: 'Waiting', preTriggerReady: false })).toBe('Armed')
  })

  it('is Ready once it is filled', () => {
    expect(stateOf({ engineState: 'Waiting', preTriggerReady: true })).toBe('Ready')
  })

  it("is Trig'd while the post-trigger samples arrive", () => {
    expect(stateOf({ engineState: 'Filling' })).toBe("Trig'd")
  })

  it('is Scan in Roll, where there is no trigger at all', () => {
    expect(stateOf({ engineState: 'Rolling', mode: 'Roll' })).toBe('Scan')
  })

  /** A Single with its record is finished, which is what a bench scope says after one. */
  it('is Stop on a held record and on an idle engine', () => {
    expect(stateOf({ engineState: 'Held', mode: 'Single' })).toBe('Stop')
    expect(stateOf({ engineState: 'Idle' })).toBe('Stop')
  })

  it('is Stop once the run has stopped, whatever the engine still says', () => {
    expect(stateOf({ engineState: 'Filling', running: false })).toBe('Stop')
    expect(stateOf({ engineState: 'Rolling', mode: 'Roll', running: false })).toBe('Stop')
  })

  it('reads a state this build does not know as stopped', () => {
    expect(stateOf({ engineState: 'Something' })).toBe('Stop')
    expect(stateOf({ engineState: null })).toBe('Stop')
  })
})

describe('Auto', () => {
  /**
   * The decision the mapping turns on: Auto is what the badge says while the analyzer is drawing
   * on its timeout, not while it merely *could*. An Auto run with a good trigger goes round Ready
   * and Trig'd like any other, and saying Auto there would hide the one thing this is read for.
   */
  it('is shown only while an Auto run is actually forcing', () => {
    expect(stateOf({ mode: 'Auto', forced: true, preTriggerReady: true })).toBe('Auto')
    expect(stateOf({ mode: 'Auto', forced: false, preTriggerReady: true })).toBe('Ready')
    expect(stateOf({ mode: 'Auto', forced: false, preTriggerReady: false })).toBe('Armed')
  })

  it('is never shown in a mode that does not force', () => {
    expect(stateOf({ mode: 'Normal', forced: true, preTriggerReady: true })).toBe('Ready')
    expect(stateOf({ mode: 'Single', forced: true, preTriggerReady: true })).toBe('Ready')
  })

  /** A forced record is still on screen after the run stops; the badge is about now, not then. */
  it('is not shown once the run has stopped', () => {
    expect(stateOf({ mode: 'Auto', forced: true, running: false })).toBe('Stop')
  })
})

describe('the badge', () => {
  it('is quiet when stopped, busy while it waits, and green once triggered', () => {
    expect(scopeTriggerBadge({ ...RUNNING, running: false }).tone).toBe('idle')
    expect(scopeTriggerBadge({ ...RUNNING, preTriggerReady: true }).tone).toBe('busy')
    expect(scopeTriggerBadge({ ...RUNNING, engineState: 'Filling' }).tone).toBe('ok')
  })

  /** The one tone worth pinning: a scope drawing on its timeout is the "it needs you" case. */
  it('shows Auto in the amber every other screen uses for that', () => {
    expect(scopeTriggerBadge({ ...RUNNING, mode: 'Auto', forced: true }).tone).toBe('waiting')
  })

  it('says what each state means, in its own words', () => {
    const seen = new Set<string>()
    for (const engineState of ['Waiting', 'Filling', 'Rolling', 'Idle']) {
      const badge = scopeTriggerBadge({ ...RUNNING, engineState, mode: 'Roll' })
      expect(badge.detail.length).toBeGreaterThan(20)
      seen.add(badge.detail)
    }
    expect(seen.size).toBe(4)
  })
})
