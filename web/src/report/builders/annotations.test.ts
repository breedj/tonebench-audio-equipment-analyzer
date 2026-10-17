import { describe, expect, it } from 'vitest'
import { createSpectrumDisplaySource } from '../../composables/useChartDisplay'
import type { ChartAnnotation } from '../../measurements/chartAnnotations'
import { annotationsSections } from './annotations'
import { chapterSections } from './index'
import type { ReportSubject } from '../types'

/** An FFT subject whose live display carries `annotations` — the one kind whose display is refs. */
function spectrumSubject(annotations: ChartAnnotation[]): ReportSubject {
  const display = createSpectrumDisplaySource(null, [0, 1], annotations)
  return {
    id: 'stored-7',
    resultId: 7,
    typeId: 3,
    kind: 'FFTSpectrum',
    typeName: 'FFT analysis',
    resultName: 'before mod',
    projectName: 'Bench',
    typeNotes: null,
    resultNotes: null,
    paramsJson: '{}',
    timestampUtc: '2026-10-17T12:00:00Z',
    facts: [],
    sampleRateHz: 48000,
    detail: null,
    data: {
      kind: 'spectrum',
      spectrum: {},
      meters: {},
      intermodulation: {},
      noise: {},
      weightingLabel: null,
      multitone: {},
      selectedMetrics: [],
      stimulus: null,
      display,
      spectrogram: null,
      oscilloscope: null,
    },
  } as unknown as ReportSubject
}

/** A sweep subject, whose display is a plain object — the shape every non-FFT kind has. */
function sweepSubject(annotations: ChartAnnotation[]): ReportSubject {
  return {
    ...spectrumSubject([]),
    kind: 'ThdVsFrequency',
    data: {
      kind: 'sweep',
      points: [],
      method: 'Classic',
      family: 'thd',
      display: { annotations },
    },
  } as unknown as ReportSubject
}

function annotation(partial: Partial<ChartAnnotation> = {}): ChartAnnotation {
  return {
    id: 1,
    style: 'point',
    text: 'Mains hum',
    view: 'Spectrum',
    x: 50,
    y: -98.24,
    x2: null,
    y2: null,
    xQuantity: 'hz',
    yQuantity: 'dbv',
    channel: 0,
    labelOffsetX: 0.4,
    labelOffsetY: 12,
    ...partial,
  }
}

const hum = annotation()
const corner = annotation({ id: 2, style: 'frequency', text: 'Bandwidth corner', x: 21500, y: null, channel: null })
const band = annotation({
  id: 3,
  style: 'box',
  text: 'Hum band',
  x: 900,
  y: -40,
  x2: 1200,
  y2: -95,
  channel: null,
  labelOffsetX: 0.3,
  labelOffsetY: 10,
})

describe('annotationsSections', () => {
  it('prints the annotations numbered the way the chart numbers them', () => {
    const [section] = annotationsSections(spectrumSubject([corner, hum]))
    expect(section.title).toBe('Annotations')
    expect(section.headers).toEqual(['#', 'Frequency', 'Level', 'Channel', 'Note'])
    // Left to right, whatever order they are stored in -- the same numbering the chart's badges use.
    expect(section.cells).toEqual([
      ['1', '50.00 Hz', '-98.2 dBV', 'L', 'Mains hum'],
      ['2', '21.50 kHz', '—', '—', 'Bandwidth corner'],
    ])
  })

  it('prints nothing at all when there are none', () => {
    expect(annotationsSections(spectrumSubject([]))).toEqual([])
  })

  it('omits the channel column when no annotation names one', () => {
    const [section] = annotationsSections(spectrumSubject([corner]))
    expect(section.headers).toEqual(['#', 'Frequency', 'Level', 'Note'])
    expect(section.cells).toEqual([['1', '21.50 kHz', '—', 'Bandwidth corner']])
  })

  it('prints a box as a range in each of the same two columns', () => {
    const [section] = annotationsSections(spectrumSubject([band]))
    expect(section.headers).toEqual(['#', 'Frequency', 'Level', 'Note'])
    expect(section.cells).toEqual([['1', '0.90 – 1.20 kHz', '-95.0 – -40.0 dBV', 'Hum band']])
  })

  it('still prints a stored rule, which most charts no longer offer to add', () => {
    const [section] = annotationsSections(spectrumSubject([corner]))
    expect(section.cells).toEqual([['1', '21.50 kHz', '—', 'Bandwidth corner']])
  })

  it('reads the annotations of a kind whose display is a plain object, not a bag of refs', () => {
    // The FFT's display is the live screen's own state, injected as refs; every other kind's is read
    // out of a stored params_json. Both have to reach the same table.
    const watts = annotation({ view: 'Sweep', x: 2.4, y: 0.0123, xQuantity: 'outputWatt', yQuantity: 'pct', channel: null })
    const [section] = annotationsSections(sweepSubject([watts]))
    expect(section.cells).toEqual([['1', '2.40 W', '0.0123 %', 'Mains hum']])
  })

  it('names the columns after the quantities the rows were stored in', () => {
    const watts = annotation({ view: 'Sweep', x: 2.4, y: 0.0123, xQuantity: 'outputWatt', yQuantity: 'pct', channel: null })
    const [section] = annotationsSections(sweepSubject([watts]))
    expect(section.headers).toEqual(['#', 'Output power', 'Distortion', 'Note'])
  })

  it('prints one table per view, each numbered the way its own chart numbers it', () => {
    const bode = annotation({ id: 1, view: 'Bode', x: 1000, text: 'Corner', channel: null })
    const nyquist = annotation({
      id: 2,
      view: 'Nyquist',
      x: -1.2,
      y: 0.4,
      xQuantity: 're',
      yQuantity: 'im',
      text: 'Margin',
      channel: null,
    })
    const subject = { ...sweepSubject([bode, nyquist]), kind: 'FrequencyResponse' } as ReportSubject

    const sections = annotationsSections(subject, ['Bode', 'Nyquist'])
    expect(sections.map((s) => s.title)).toEqual(['Annotations — Bode', 'Annotations — Nyquist'])
    // Each table starts at 1, which a single globally-numbered table could not do.
    expect(sections[0].cells).toEqual([['1', '1.000 kHz', '-98.2 dBV', 'Corner']])
    expect(sections[1].cells).toEqual([['1', '-1.200', '0.400', 'Margin']])
    expect(sections[1].headers).toEqual(['#', 'Real part', 'Imaginary part', 'Note'])
  })

  it('leaves out a view whose chart this chapter did not print', () => {
    const bode = annotation({ id: 1, view: 'Bode', x: 1000, channel: null })
    const nyquist = annotation({ id: 2, view: 'Nyquist', x: -1.2, xQuantity: 're', yQuantity: 'im', channel: null })
    const subject = { ...sweepSubject([bode, nyquist]), kind: 'FrequencyResponse' } as ReportSubject

    const sections = annotationsSections(subject, ['Bode'])
    expect(sections).toHaveLength(1)
    expect(sections[0].title).toBe('Annotations')
  })

  it('keeps a note from a view this build does not know rather than dropping it', () => {
    // The parser kept the row, so the report keeps the user's words -- the same judgement the whole
    // feature makes about a style or a quantity it cannot place.
    const future = annotation({ view: 'SomethingNewer', text: 'kept', channel: null })
    const sections = annotationsSections(spectrumSubject([future]))
    expect(sections).toHaveLength(1)
    expect(sections[0].cells![0][3]).toBe('kept')
  })

  it('is built only when its element is chosen, on every kind', () => {
    for (const subject of [spectrumSubject([hum]), sweepSubject([hum])]) {
      const titles = (elements: string[]) => chapterSections(subject, elements as never).map((s) => s.title)
      expect(titles(['annotations'])).toContain('Annotations')
      expect(titles(['settings'])).not.toContain('Annotations')
    }
  })

  it('puts the table first, under the picture it is a legend for', () => {
    const titles = chapterSections(spectrumSubject([hum]), ['annotations', 'settings']).map((s) => s.title)
    expect(titles[0]).toBe('Annotations')
  })
})
