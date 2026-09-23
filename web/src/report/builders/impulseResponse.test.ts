import { describe, expect, it } from 'vitest'
import { DEFAULT_IMPULSE_DISPLAY } from '../../measurements/impulseDisplay'
import type { ImpulseRunFacts } from '../../measurements/impulseReadings'
import { harmonicTestCurve, impulseTestTrace } from '../../measurements/impulseTestData'
import { IMPULSE_RESPONSE_KIND } from '../../measurements/kinds'
import type { ReportSubject } from '../types'
import { chapterSections } from './index'
import { impulseResponseMeasurementSections, impulseResponseSections, impulseResponseSettingsRows } from './impulseResponse'

/** The fields MeasurementSaveService.SaveImpulseResponseAsync writes for a stored result. */
const params = JSON.stringify({
  frequencySpanHz: 96000,
  resolutionMultiplier: 2,
  levelMode: 'OutputPower',
  levelValue: 1,
  levelUnit: 'Watt',
  smoothingDenominator: 24,
  averageCount: 4,
  referenceMode: 'RightChannel',
  referenceCalibrationId: null,
  gatePreMs: 5,
  gatePostMs: 16,
  gateWindow: 'Hann',
  extractHarmonics: true,
  maxHarmonicOrder: 3,
  generatorAmpDbv: -12.5,
  chirpStartHz: 10,
  loadOhms: 8,
  captureDelayMs: 300,
})

function rowsToObject(rows: { label: string; value: string }[]) {
  return Object.fromEntries(rows.map((r) => [r.label, r.value]))
}

const run: ImpulseRunFacts = {
  referenceMode: 'RightChannel',
  referenceName: null,
  latencySamples: 48.2,
  averagesTaken: 4,
  averageCount: 4,
  generatorAmpDbv: -12.5,
}

const harmonics = [harmonicTestCurve(2, 9600, -60), harmonicTestCurve(3, 6400, -70)]

describe('impulseResponseSettingsRows', () => {
  it('labels the run the way the config panel does, gate and harmonics included', () => {
    expect(rowsToObject(impulseResponseSettingsRows(params))).toEqual({
      Sweep: '10.00 Hz – 96.0 kHz',
      Resolution: '0.73 Hz',
      Level: '1 W — Output power into 8 Ω',
      Averages: '4',
      Gate: '−5 / +16 ms, Hann, resolves 48 Hz',
      Smoothing: '1/24',
      Reference: 'Right input channel',
      Harmonics: 'Extracted up to H3',
      'Generator level': '-12.50 dBV',
      'Capture delay': '300 ms',
    })
  })

  it('says so when harmonics were not extracted', () => {
    const rows = rowsToObject(impulseResponseSettingsRows(JSON.stringify({ extractHarmonics: false, referenceMode: 'None' })))
    expect(rows.Harmonics).toBe('Not extracted')
    expect(rows.Reference).toBe('None (the generated sweep)')
  })

  it('returns nothing for JSON it cannot read', () => {
    expect(impulseResponseSettingsRows('{')).toEqual([])
  })
})

describe('impulseResponseMeasurementSections', () => {
  it('quotes the Measurement block, then the harmonics table and its note', () => {
    const sections = impulseResponseMeasurementSections(impulseTestTrace(), run, harmonics)
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual([
      'keyValues:Measurements',
      'table:Harmonic distortion',
      'paragraph:',
    ])
    expect(rowsToObject(sections[0].rows!)).toEqual({
      Peak: '954 mV',
      Gate: '−5 / +16 ms',
      'Resolved above': '48 Hz',
      Record: '4032 samples at 192.0 kHz',
      Reference: 'right input channel',
      Records: '4 of 4',
      Generator: '-12.50 dBV',
      'DUT delay': '48.2 samples (0.251 ms)',
    })
    expect(sections[1].headers).toEqual(['Order', 'Valid up to', '100.0 Hz', '1.00 kHz', '10.0 kHz'])
    expect(sections[1].cells!.map((c) => c[0])).toEqual(['H2', 'H3'])
  })

  it('leaves the table out when no harmonics were extracted', () => {
    const sections = impulseResponseMeasurementSections(impulseTestTrace(), run, [])
    expect(sections.map((s) => s.kind)).toEqual(['keyValues'])
  })

  it('says so when nothing was measured', () => {
    expect(impulseResponseMeasurementSections(null, run, harmonics)).toEqual([
      { kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' },
    ])
  })
})

describe('impulseResponseSections', () => {
  const subject: ReportSubject = {
    id: 'live',
    resultId: null,
    typeId: 1,
    kind: IMPULSE_RESPONSE_KIND,
    typeName: 'Impulse',
    resultName: null,
    projectName: 'P',
    typeNotes: null,
    resultNotes: null,
    paramsJson: params,
    timestampUtc: null,
    facts: [],
    sampleRateHz: 192000,
    detail: null,
    data: {
      kind: 'impulse',
      impulse: impulseTestTrace(),
      traces: [],
      harmonics,
      display: { ...DEFAULT_IMPULSE_DISPLAY },
      run,
    },
  }

  it('is what the chapter router picks for the kind, in report order', () => {
    const sections = chapterSections(subject, ['chart', 'settings', 'measurements'])
    expect(sections).toEqual(impulseResponseSections(subject, ['chart', 'settings', 'measurements']))
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual([
      'keyValues:Settings',
      'keyValues:Measurements',
      'table:Harmonic distortion',
      'paragraph:',
    ])
  })
})
