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
