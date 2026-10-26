import { describe, expect, it } from 'vitest'
import { harmonicsUnavailableNote, harmonicsUnavailableShort } from './harmonicsAvailability'

describe('harmonicsUnavailableNote', () => {
  /** The report itself: a Pink PN run drew an empty chart with Autofit dead and said nothing. */
  it('names the signal the run played, and what to measure instead', () => {
    const note = harmonicsUnavailableNote('PinkNoise', false)
    expect(note).toContain('Pink PN')
    expect(note).toContain('Sweep (chirp)')
  })

  it('names each of the noise signals by the label the dropdown uses', () => {
    expect(harmonicsUnavailableNote('WhiteNoise', false)).toContain('White PN')
    expect(harmonicsUnavailableNote('SpeechMale2020', false)).toContain('Male PN [2020]')
  })

  /** A sweep can produce them, so an empty view there is a measurement question, not a signal one. */
  it('says nothing about a sweep, whose Harmonics section is simply switched off', () => {
    expect(harmonicsUnavailableNote('Chirp', false)).toBeNull()
  })

  it('says nothing while there is something on the chart', () => {
    expect(harmonicsUnavailableNote('PinkNoise', true)).toBeNull()
  })

  /**
   * Nothing has run, or the status comes from a build that does not report its stimulus. The
   * chart's own empty state covers the first and there is nothing honest to say about the second.
   */
  it('says nothing when the run that produced it is unknown', () => {
    expect(harmonicsUnavailableNote(null, false)).toBeNull()
    expect(harmonicsUnavailableNote(undefined, false)).toBeNull()
  })
})

describe('harmonicsUnavailableShort', () => {
  it('is a tooltip-length version of the same thing, and is null in the same cases', () => {
    const short = harmonicsUnavailableShort('PinkNoise', false)
    expect(short).toContain('Pink PN')
    expect(short!.length).toBeLessThan(harmonicsUnavailableNote('PinkNoise', false)!.length)
    expect(harmonicsUnavailableShort('Chirp', false)).toBeNull()
    expect(harmonicsUnavailableShort('PinkNoise', true)).toBeNull()
    expect(harmonicsUnavailableShort(null, false)).toBeNull()
  })
})
