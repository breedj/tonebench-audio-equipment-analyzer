import { describe, expect, it } from 'vitest'
import { DEFAULT_IMPEDANCE_DISPLAY } from '../../impedance/display'
import { LOUDSPEAKER_NOTE } from '../../impedance/readings'
import { driverTestTrace, impedanceTestTrace } from '../../impedance/testTrace'
import type { ReportSubject } from '../types'
import { impedanceMeasurementSections, impedanceSections, impedanceSettingsRows } from './impedance'
import { chapterSections } from './index'

const chirpParams = JSON.stringify({
  method: 'ReferenceResistor',
  stimulus: 'Chirp',
  referenceOhms: 100,
  rightChannelIsReference: false,
  frequencySpanHz: 24000,
  resolutionMultiplier: 4,
  chirpStartHz: 10,
  levelValue: -20,
  levelUnit: 'Dbv',
  smoothingDenominator: 12,
  averageCount: 2,
  cableMode: 'Measured',
  fixedCableOhms: 0,
  fixedCableMillihenry: 0,
  calibrationLevelValue: 0,
  analyzerInputOhms: null,
  applySweepGate: true,
  openCal: { freqHz: [10, 100], real: [1, 1], imag: [0, 0] },
  shortCal: { freqHz: [10, 100], real: [0.001, 0.001], imag: [0, 0] },
  generatorAmpDbv: -20,
  calibrationStimulus: 'Chirp',
  startFreqHz: 20,
  endFreqHz: 20000,
  stepsPerOctave: 6,
  adaptiveResolution: true,
  captureDelayMs: 300,
})

const steppedParams = JSON.stringify({
  method: 'ReferenceResistor',
  stimulus: 'SteppedSine',
  referenceOhms: 8.2,
  rightChannelIsReference: true,
  frequencySpanHz: 48000,
  resolutionMultiplier: 2,
  chirpStartHz: 5,
  levelValue: 100,
  levelUnit: 'MilliVolt',
  smoothingDenominator: 0,
  averageCount: 1,
  cableMode: 'Fixed',
  fixedCableOhms: 0.12,
  fixedCableMillihenry: 0.002,
  analyzerInputOhms: 100000,
  openCal: null,
  shortCal: null,
  calibrationStimulus: 'Chirp',
  startFreqHz: 20,
  endFreqHz: 20000,
  stepsPerOctave: 6,
  adaptiveResolution: false,
})

function rowsToObject(rows: { label: string; value: string }[]) {
  return Object.fromEntries(rows.map((r) => [r.label, r.value]))
}

describe('impedanceSettingsRows', () => {
  it('labels a calibrated chirp run the way the config panel does', () => {
    expect(rowsToObject(impedanceSettingsRows(chirpParams))).toEqual({
      'Reference resistor': '100.0 Ω',
      'Reference channel': 'Left (generator side)',
      Stimulus: 'Chirp sweep',
      'Drive level': '-20 dBV',
      'Frequency span': '24.0 kHz',
      Resolution: '0.37 Hz',
      'Chirp start': '10.00 Hz',
      Smoothing: '1/12',
      Averages: '2',
      Calibration: 'Open and short standards',
      'Calibration drive level': '0 dBV',
      Leads: 'Measured short subtracted',
      'Sweep gate': 'Harmonic packets gated',
      'Generator level': '-20.00 dBV',
      'Capture delay': '300 ms',
    })
  })

  it('labels an uncalibrated stepped run with its grid, its fixed leads and the calibration chirp marked as such', () => {
    expect(rowsToObject(impedanceSettingsRows(steppedParams))).toEqual({
      'Reference resistor': '8.200 Ω',
      'Reference channel': 'Right (generator side)',
      Stimulus: 'Stepped sine',
      'Drive level': '100 mV',
      'Frequency span (calibration)': '48.0 kHz',
      'Resolution (calibration)': '0.73 Hz',
      'Chirp start (calibration)': '5.00 Hz',
      Sweep: '20.00 Hz – 20.0 kHz',
      'Steps per octave': '6',
      Resolution: 'Fixed',
      Averages: '1',
      Calibration: 'None',
      Leads: 'Fixed value subtracted: 120.0 mΩ + 2.00 µH',
      'Analyzer input impedance': '100.00 kΩ',
    })
  })

  it('returns nothing for JSON it cannot read', () => {
    expect(impedanceSettingsRows('{')).toEqual([])
  })
})

describe('impedanceMeasurementSections', () => {
  it('quotes the readout panel: peak, minimum, median, drive current and the reference', () => {
    const trace = impedanceTestTrace([20, 100, 1000, 10000, 20000], [8, 30, 6, 7, 12])
    const sections = impedanceMeasurementSections([trace], 'None', 100, 0)
    expect(sections).toHaveLength(1)
    expect(sections[0].title).toBe('Measurements')
    expect(rowsToObject(sections[0].rows!)).toEqual({
      'Peak |Z|': '30.000 Ω at 100.0 Hz',
      'Minimum |Z|': '6.000 Ω at 1.00 kHz',
      'Median |Z| (curve)': '8.000 Ω',
      'Drive current': '9.259 mA',
      'Reference R': '100.0 Ω',
    })
  })

  it('adds the Thiele-Small fit and its note for a loudspeaker', () => {
    const sections = impedanceMeasurementSections([driverTestTrace()], 'Loudspeaker', 100, null)
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual(['keyValues:Measurements', 'keyValues:Loudspeaker', 'paragraph:'])
    expect(rowsToObject(sections[0].rows!)['Drive current']).toBe('—')
    expect(sections[1].rows!.map((r) => r.label)).toEqual(['fs', 'Zmax', 'Re', 'f1 / f2', 'Qms', 'Qes', 'Qts'])
    expect(sections[2].text).toBe(LOUDSPEAKER_NOTE)
  })

  it('titles the note with the component when a fit has no rows to title', () => {
    const flat = impedanceTestTrace([20, 100, 1000, 10000, 20000], [8, 8, 8, 8, 8])
    const sections = impedanceMeasurementSections([flat], 'Loudspeaker', 100, null)
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual(['keyValues:Measurements', 'paragraph:Loudspeaker'])
  })

  it('says so when nothing was measured', () => {
    expect(impedanceMeasurementSections([], 'Loudspeaker', 100, -20)).toEqual([
      { kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' },
    ])
  })
})

describe('impedanceSections', () => {
  const subject: ReportSubject = {
    id: 'live',
    resultId: null,
    typeId: 1,
    kind: 'Impedance',
    typeName: 'Woofer',
    resultName: null,
    projectName: 'P',
    typeNotes: null,
    resultNotes: null,
    paramsJson: chirpParams,
    timestampUtc: null,
    facts: [],
    sampleRateHz: 48000,
    detail: null,
    data: { kind: 'impedance', traces: [driverTestTrace()], display: { ...DEFAULT_IMPEDANCE_DISPLAY }, dutType: 'Loudspeaker', referenceOhms: 100, generatorAmpDbv: -20 },
  }

  it('is what the chapter router picks for the kind, in report order', () => {
    const sections = chapterSections(subject, ['chart', 'settings', 'measurements'])
    expect(sections).toEqual(impedanceSections(subject, ['chart', 'settings', 'measurements']))
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual([
      'keyValues:Settings',
      'keyValues:Measurements',
      'keyValues:Loudspeaker',
      'paragraph:',
    ])
  })
})
