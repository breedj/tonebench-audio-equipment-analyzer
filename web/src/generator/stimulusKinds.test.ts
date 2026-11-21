import { describe, expect, it } from 'vitest'
import {
  STIMULUS_KINDS,
  STIMULUS_SECTIONS,
  stimulusSection,
  stimulusSections,
} from './stimulusKinds'

describe('the stimulus picker’s sections', () => {
  /**
   * The one that matters: a kind whose section nothing defines is dropped from the dialog with
   * nothing anywhere to say so — the stimulus is simply not on offer any more, and the panel's
   * button goes on naming it.
   */
  it('offers every kind exactly once', () => {
    const offered = stimulusSections().flatMap((entry) => entry.items.map((item) => item.value))
    expect(offered.sort()).toEqual(STIMULUS_KINDS.map((k) => k.value).sort())
    expect(new Set(offered).size).toBe(STIMULUS_KINDS.length)
  })

  it('draws the sections in the table’s own order, and none empty', () => {
    const shown = stimulusSections()
    expect(shown.map((entry) => entry.section.id)).toEqual(STIMULUS_SECTIONS.map((s) => s.id))
    for (const entry of shown) expect(entry.items.length, entry.section.id).toBeGreaterThan(0)
  })

  it('puts the tone burst with the tones, where its purpose is, not with the waveforms', () => {
    // The section is by what a stimulus is *for*: a burst is read at one frequency like a sine,
    // where the four waveforms are chosen for a harmonic series they are known to have.
    const tones = stimulusSections().find((entry) => entry.section.id === 'tones')
    expect(tones?.items.map((i) => i.value)).toEqual(['Tones', 'ToneBurst'])
  })

  it('answers one kind’s section, for the button that draws its figure', () => {
    // The button is a picture of the choice, so it draws the figure the tile drew -- which needs
    // this. A kind the picker does not offer has no section and the button falls back to the plain
    // figure, which is what a stored `EchoChirp` still gets.
    expect(stimulusSection('Square')?.id).toBe('waveforms')
    expect(stimulusSection('ToneBurst')?.id).toBe('tones')
    expect(stimulusSection('Off')).toBeNull()
    expect(stimulusSection('EchoChirp')).toBeNull()
    for (const option of STIMULUS_KINDS) {
      expect(stimulusSection(option.value), option.label).not.toBeNull()
    }
  })

  it('gives every section a heading worth reading', () => {
    for (const section of STIMULUS_SECTIONS) {
      expect(section.label.length, section.id).toBeGreaterThan(3)
      // Short is fine -- "DC content" is the whole of what that section has in common -- but
      // a section with no note at all is a badge the reader has to guess the meaning of.
      expect(section.note.length, section.id).toBeGreaterThan(5)
    }
  })
})
