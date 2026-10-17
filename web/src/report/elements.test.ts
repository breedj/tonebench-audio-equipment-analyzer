import { describe, expect, it } from 'vitest'
import { reportElementsForKind, storedChoiceElements, type ReportElementId } from './elements'

const FFT = reportElementsForKind('FFTSpectrum').map((e) => e.id)

/** What the FFT kind offered before the Annotations table existed. */
const OFFERED_BEFORE: ReportElementId[] = FFT.filter((id) => id !== 'annotations')

describe('storedChoiceElements', () => {
  it('keeps a choice that was offered everything it has, exactly as it stands', () => {
    const chosen: ReportElementId[] = ['chart', 'description', 'metrics']
    expect(storedChoiceElements(FFT, chosen, FFT)).toEqual(chosen)
  })

  it('back-fills an element the choice was never offered', () => {
    // A bench that saved its choice before the Annotations table existed gets it, once.
    const chosen: ReportElementId[] = ['chart', 'description', 'metrics', 'harmonics']
    expect(storedChoiceElements(FFT, chosen, OFFERED_BEFORE)).toContain('annotations')
  })

  it('leaves off an element that was offered and declined', () => {
    // The whole reason the offered list is recorded: this is indistinguishable from the case above
    // without it, and unticking would never stick.
    const chosen: ReportElementId[] = ['chart', 'description', 'metrics', 'harmonics']
    expect(storedChoiceElements(FFT, chosen, FFT)).not.toContain('annotations')
  })

  it('back-fills every element for a choice saved before the offered list was recorded', () => {
    expect(storedChoiceElements(FFT, ['chart'], null)).toEqual(FFT)
    expect(storedChoiceElements(FFT, ['chart'], [])).toEqual(FFT)
  })

  it('answers in the order the kind offers, not the order the choice was stored in', () => {
    expect(storedChoiceElements(FFT, ['harmonics', 'chart'], FFT)).toEqual(['chart', 'harmonics'])
  })

  it('drops a stored id this build does not offer', () => {
    expect(storedChoiceElements(FFT, ['chart', 'somethingOlder'], FFT)).toEqual(['chart'])
  })

  it('still renames a stored waterfall and still back-fills the impedance split', () => {
    expect(storedChoiceElements(FFT, ['chart', 'waterfall'], FFT)).toContain('spectrogram')

    const impedance = reportElementsForKind('Impedance').map((e) => e.id)
    expect(storedChoiceElements(impedance, ['chart', 'measurements'], impedance)).toContain('component')
  })
})

describe('the annotations element', () => {
  it('is offered on every kind, since a note is the same thing on every chart', () => {
    const kinds = [
      'FFTSpectrum',
      'ThdVsFrequency',
      'ThdVsAmplitude',
      'ImdVsAmplitude',
      'FrequencyResponse',
      'SteppedResponse',
      'ImpulseResponse',
      'Impedance',
      'OutputImpedance',
      'Crosstalk',
      'SomethingNewer',
    ]
    for (const kind of kinds) {
      expect(reportElementsForKind(kind).map((e) => e.id)).toContain('annotations')
    }
  })

  it('reaches a choice saved before its kind offered it, once', () => {
    // The Offered back-fill: an element a stored choice was never shown is added to it, so a bench
    // that chose its report elements last month starts getting the new table rather than silently
    // never seeing it. An untick afterwards sticks, because the choice then records it as offered.
    const offered = reportElementsForKind('FrequencyResponse').map((e) => e.id)
    const offeredBefore = ['chart', 'description', 'settings', 'measurements']
    expect(storedChoiceElements(offered, ['chart', 'measurements'], offeredBefore)).toContain('annotations')

    // Once the choice records it as offered, an untick sticks.
    expect(storedChoiceElements(offered, ['chart', 'measurements'], offered)).not.toContain('annotations')
  })
})
