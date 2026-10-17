import { describe, expect, it } from 'vitest'
import {
  annotationViewLabel,
  annotationViewsForKind,
  defaultAnnotationView,
  storedAnnotationFrame,
} from './annotationViews'
import { annotationQuantity, isKnownQuantity } from './annotationQuantities'
import { reportViewsForKind } from '../report/views'

const KINDS = [
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
]

describe('annotationViewsForKind', () => {
  it('gives every kind at least one chart to annotate', () => {
    for (const kind of KINDS) {
      expect(annotationViewsForKind(kind).length, kind).toBeGreaterThan(0)
    }
  })

  it('names every view the report can print a chart of, so a note is never orphaned', () => {
    // The one direction that must hold: a chart the report renders has to be able to carry notes,
    // or a user could annotate nothing on a picture that is printed. The other direction is allowed
    // to differ -- see the two cases below.
    for (const kind of KINDS) {
      const printable = reportViewsForKind(kind).map((v) => v.id)
      const annotatable = annotationViewsForKind(kind)
      const orphaned = printable.filter((v) => !annotatable.includes(v))
      // The two deliberate exclusions, and nothing else: a third would be a chart a user can see
      // printed and cannot annotate, which is the failure this test is for.
      expect(orphaned.filter((v) => v !== 'Csd' && v !== 'BurstDecay'), kind).toEqual([])
    }
  })

  it('leaves the two decay surfaces out, which carry no notes at all', () => {
    const impulse = annotationViewsForKind('ImpulseResponse')
    expect(impulse).not.toContain('Csd')
    expect(impulse).not.toContain('BurstDecay')
    // ...while the report still prints them, which is exactly the asymmetry this file documents.
    expect(reportViewsForKind('ImpulseResponse').map((v) => v.id)).toContain('Csd')
  })

  it('adds the two FFT charts the report prints as elements rather than as views', () => {
    const fft = annotationViewsForKind('FFTSpectrum')
    expect(fft).toContain('Spectrogram')
    expect(fft).toContain('Oscilloscope')
    expect(reportViewsForKind('FFTSpectrum').map((v) => v.id)).not.toContain('Spectrogram')
  })

  it('answers nothing for a kind no build of this client knows', () => {
    expect(annotationViewsForKind('SomethingNewer')).toEqual([])
    expect(annotationViewsForKind(null)).toEqual([])
  })
})

describe('defaultAnnotationView', () => {
  it('is the kind’s first chart, which is what a row that names no view means', () => {
    expect(defaultAnnotationView('FFTSpectrum')).toBe('Spectrum')
    expect(defaultAnnotationView('FrequencyResponse')).toBe('Bode')
    expect(defaultAnnotationView('ThdVsFrequency')).toBe('Sweep')
    expect(defaultAnnotationView('ImpulseResponse')).toBe('Impulse')
  })

  it('falls back to the spectrum for a kind with no charts, rather than to nothing', () => {
    expect(defaultAnnotationView('SomethingNewer')).toBe('Spectrum')
  })
})

describe('storedAnnotationFrame', () => {
  it('reads a row that names no quantities in the ones that kind’s first chart plots', () => {
    expect(storedAnnotationFrame('FFTSpectrum')).toEqual({ view: 'Spectrum', xQuantity: 'hz', yQuantity: 'dbv' })
    expect(storedAnnotationFrame('ThdVsFrequency')).toEqual({ view: 'Sweep', xQuantity: 'hz', yQuantity: 'pct' })
    expect(storedAnnotationFrame('ImpulseResponse')).toEqual({ view: 'Impulse', xQuantity: 'ms', yQuantity: 'volt' })
    expect(storedAnnotationFrame('Impedance')).toEqual({ view: 'Bode', xQuantity: 'hz', yQuantity: 'ohm' })
    expect(storedAnnotationFrame('Crosstalk')).toEqual({ view: 'Crosstalk', xQuantity: 'hz', yQuantity: 'db' })
  })

  it('names quantities this build knows, for every kind', () => {
    // A frame naming an id the registry has never heard of would print its numbers bare and measure
    // its offsets in the wrong space, silently.
    for (const kind of [...KINDS, 'SomethingNewer']) {
      const frame = storedAnnotationFrame(kind)
      expect(isKnownQuantity(frame.xQuantity), `${kind} x`).toBe(true)
      expect(isKnownQuantity(frame.yQuantity), `${kind} y`).toBe(true)
      expect(annotationQuantity(frame.xQuantity).label, `${kind} x label`).not.toBe('')
    }
  })

  it('puts a row in the view it would be drawn on, for every kind', () => {
    for (const kind of KINDS) {
      expect(annotationViewsForKind(kind), kind).toContain(storedAnnotationFrame(kind).view)
    }
  })
})

describe('annotationViewLabel', () => {
  it('names every view a kind offers, so a dialog grouping by view never shows a bare id', () => {
    // Several ids legitimately *are* their label ("Spectrum", "Bode"), so what is checked is that a
    // label exists at all and that the ones a reader could not parse have a real name.
    for (const kind of KINDS) {
      for (const view of annotationViewsForKind(kind)) {
        expect(annotationViewLabel(view), `${kind}/${view}`).toBeTruthy()
      }
    }
    expect(annotationViewLabel('PoleZero')).toBe('Poles and zeros')
    expect(annotationViewLabel('SPlane')).toBe('|H(s)| surface')
    expect(annotationViewLabel('Sweep')).toBe('Distortion')
  })

  it('hands back the id itself for a view this build does not know', () => {
    expect(annotationViewLabel('SomethingNewer')).toBe('SomethingNewer')
  })
})
