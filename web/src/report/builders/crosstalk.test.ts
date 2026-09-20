import { describe, expect, it } from 'vitest'
import { DEFAULT_CROSSTALK_DISPLAY } from '../../crosstalk/display'
import { makeTrace } from '../../response/testTrace'
import type { ReportSubject } from '../types'
import { crosstalkMeasurementSections, crosstalkSections, crosstalkSettingsRows } from './crosstalk'
import { chapterSections } from './index'

const params = JSON.stringify({
  startFreqHz: 20,
  endFreqHz: 20000,
  stepsPerOctave: 5,
  direction: 'Both',
  levelMode: 'OutputPower',
  levelValue: 1,
  levelUnit: 'Watt',
  loadOhms: 8,
  fftSize: 131072,
  window: 'Hann',
  averageCount: 2,
  sampleRateHz: 192000,
  adaptiveResolution: false,
  generatorAmpDbvLeftToRight: -11.02,
  generatorAmpDbvRightToLeft: -10.97,
  captureDelayMs: 300,
  chartYTopDb: 0,
})

function rowsToObject(rows: { label: string; value: string }[]) {
  return Object.fromEntries(rows.map((r) => [r.label, r.value]))
}

const l2r = { ...makeTrace({ freqHz: [20, 1000, 20000], magnitudeMaxDb: [-70, -69.9, -59], phaseDeg: [0, 0, 0] }), kind: 'CrosstalkLeftToRight' as const }
const r2l = { ...makeTrace({ freqHz: [20, 1000, 20000], magnitudeMaxDb: [-80, -79, -65], phaseDeg: [0, 0, 0] }), kind: 'CrosstalkRightToLeft' as const }

describe('crosstalkSettingsRows', () => {
  it('labels the run the way the config panel does, one generator level per direction', () => {
    expect(rowsToObject(crosstalkSettingsRows(params))).toEqual({
      Sweep: '20.00 Hz – 20.0 kHz',
      'Steps per octave': '5',
      Direction: 'Both, one pass each way',
      Level: '1 W — DUT output power (driven channel) into 8 Ω',
      Resolution: '131072-point Hann at 192.0 kHz',
      Averages: '2',
      'Generator level L→R': '-11.02 dBV',
      'Generator level R→L': '-10.97 dBV',
      'Capture delay': '300 ms',
    })
  })

  it('returns nothing for JSON it cannot read', () => {
    expect(crosstalkSettingsRows('{')).toEqual([])
  })
})

describe('crosstalkMeasurementSections', () => {
  it('tabulates one row per direction with the panel figures', () => {
    expect(crosstalkMeasurementSections([l2r, r2l])).toEqual([
      {
        kind: 'table',
        title: 'Measurements',
        headers: ['Direction', 'At 1 kHz', 'Worst', 'Low end', 'High end'],
        cells: [
          ['L→R', '-69.9 dB (1.00 kHz)', '-59.0 dB at 20.0 kHz', '-70.0 dB at 20.00 Hz', '-59.0 dB at 20.0 kHz'],
          ['R→L', '-79.0 dB (1.00 kHz)', '-65.0 dB at 20.0 kHz', '-80.0 dB at 20.00 Hz', '-65.0 dB at 20.0 kHz'],
        ],
      },
    ])
  })

  it('says so when nothing was measured, and ignores non-crosstalk traces', () => {
    expect(crosstalkMeasurementSections([makeTrace({ freqHz: [1], magnitudeMaxDb: [0], phaseDeg: [0] })])).toEqual([
      { kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' },
    ])
  })
})

describe('crosstalkSections', () => {
  const subject: ReportSubject = {
    id: 'live',
    resultId: null,
    typeId: 1,
    kind: 'Crosstalk',
    typeName: 'Crosstalk',
    resultName: null,
    projectName: 'P',
    typeNotes: null,
    resultNotes: null,
    paramsJson: params,
    timestampUtc: null,
    facts: [],
    sampleRateHz: 192000,
    detail: null,
    data: { kind: 'crosstalk', traces: [l2r], display: { ...DEFAULT_CROSSTALK_DISPLAY } },
  }

  it('is what the chapter router picks for the kind, in report order', () => {
    const sections = chapterSections(subject, ['chart', 'settings', 'measurements'])
    expect(sections).toEqual(crosstalkSections(subject, ['chart', 'settings', 'measurements']))
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual(['keyValues:Settings', 'table:Measurements'])
  })
})
