import { describe, expect, it } from 'vitest'

import type { ResponseStimulusName } from '../api/client'
import {
  RESPONSE_STIMULI,
  extractsHarmonics,
  isNoiseStimulus,
  readStimulus,
  stimulusNotice,
  usesChirpStart,
  usesNoiseCutoff,
} from './responseStimulus'

const ALL = RESPONSE_STIMULI.map((option) => option.value)

describe('responseStimulus', () => {
  it('offers ARTA\'s own list, the sweep first', () => {
    expect(ALL).toEqual([
      'Chirp',
      'PinkNoise',
      'WhiteNoise',
      'SpeechMale2011',
      'SpeechFemale2011',
      'SpeechMale2020',
    ])
  })

  it('counts everything but the sweep as noise', () => {
    expect(isNoiseStimulus('Chirp')).toBe(false)
    for (const stimulus of ALL.filter((s) => s !== 'Chirp')) {
      expect(isNoiseStimulus(stimulus)).toBe(true)
    }
  })

  it('offers the cut-off only where there is a slope for it to start', () => {
    // White is flat, so a corner means nothing; the sweep has no sequence at all.
    expect(usesNoiseCutoff('Chirp')).toBe(false)
    expect(usesNoiseCutoff('WhiteNoise')).toBe(false)
    expect(usesNoiseCutoff('PinkNoise')).toBe(true)
    expect(usesNoiseCutoff('SpeechMale2011')).toBe(true)
    expect(usesNoiseCutoff('SpeechFemale2011')).toBe(true)
    expect(usesNoiseCutoff('SpeechMale2020')).toBe(true)
  })

  it('reads harmonics off the sweep and nothing else', () => {
    // Noise spreads its distortion over the whole record; there are no packets to read.
    expect(ALL.filter(extractsHarmonics)).toEqual(['Chirp'])
    expect(ALL.filter(usesChirpStart)).toEqual(['Chirp'])
  })

  it('explains every choice but the default', () => {
    expect(stimulusNotice('Chirp')).toBeNull()
    for (const stimulus of ALL.filter((s) => s !== 'Chirp')) {
      expect(stimulusNotice(stimulus)).toBeTruthy()
    }
  })

  it('reads a stored name, and anything it does not know as the sweep', () => {
    // Absent is a type saved before the choice existed, which could only sweep; an unknown name is
    // a measurement from a newer build, and opening it as a sweep beats refusing to open it.
    expect(readStimulus(undefined)).toBe('Chirp')
    expect(readStimulus(null)).toBe('Chirp')
    expect(readStimulus('Nonsense')).toBe('Chirp')
    for (const stimulus of ALL) {
      expect(readStimulus(stimulus)).toBe(stimulus)
    }
  })

  it('labels the noise kinds the way ARTA does, so the two can be compared', () => {
    const label = (value: ResponseStimulusName) => RESPONSE_STIMULI.find((o) => o.value === value)?.label
    expect(label('PinkNoise')).toBe('Pink PN')
    expect(label('WhiteNoise')).toBe('White PN')
    expect(label('SpeechMale2020')).toBe('Male PN [2020]')
  })
})
