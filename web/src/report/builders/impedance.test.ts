import { describe, expect, it } from 'vitest'
import { DEFAULT_IMPEDANCE_DISPLAY } from '../../impedance/display'
import { LOUDSPEAKER_NOTE } from '../../impedance/readings'
import { driverTestTrace, impedanceTestTrace, portedDriverTestTrace } from '../../impedance/testTrace'
import type { ReportSubject } from '../types'
import {
  impedanceComponentSections,
  impedanceEquivalentSections,
  impedanceMeasurementSections,
  impedanceSections,
  impedanceSettingsRows,
} from './impedance'
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
    const sections = impedanceMeasurementSections([trace], 100, 0)
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

  it('says so when nothing was measured', () => {
    expect(impedanceMeasurementSections([], 100, -20)).toEqual([
      { kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' },
    ])
  })
})

describe('impedanceComponentSections', () => {
  it('is the Thiele-Small fit and its note for a loudspeaker', () => {
    const sections = impedanceComponentSections([driverTestTrace()], 'Loudspeaker')
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual(['keyValues:Loudspeaker', 'paragraph:'])
    expect(sections[0].rows!.map((r) => r.label)).toEqual(['fs', 'Zmax', 'Re', 'f1 / f2', 'Qms', 'Qes', 'Qts'])
    expect(sections[1].text).toBe(LOUDSPEAKER_NOTE)
  })

  it('drops the Thiele-Small block for a ported box and prints the note that says why', () => {
    // The warning has to reach the report as well as the readout panel; both read componentReading.
    const sections = impedanceComponentSections([portedDriverTestTrace()], 'Loudspeaker')
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual(['paragraph:Loudspeaker'])
    expect(sections[0].text).toContain('ported (bass-reflex)')
  })

  it('titles the note with the component when a fit has no rows to title', () => {
    const flat = impedanceTestTrace([20, 100, 1000, 10000, 20000], [8, 8, 8, 8, 8])
    expect(impedanceComponentSections([flat], 'Loudspeaker').map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual([
      'paragraph:Loudspeaker',
    ])
  })

  it('is nothing at all when the fixture holds nothing nameable, or nothing was measured', () => {
    expect(impedanceComponentSections([driverTestTrace()], 'None')).toEqual([])
    expect(impedanceComponentSections([], 'Loudspeaker')).toEqual([])
  })
})

describe('impedanceEquivalentSections', () => {
  it('names the frequency asked for in its title and the column it landed on in its first row', () => {
    const trace = impedanceTestTrace([20, 100, 1000, 10000, 20000], [8, 30, 6, 7, 12])
    const sections = impedanceEquivalentSections([trace], 900)
    expect(sections).toHaveLength(1)
    expect(sections[0].title).toBe('Equivalent circuit at 900.0 Hz')

    const rows = rowsToObject(sections[0].rows!)
    expect(rows['Measured at']).toBe('1.00 kHz')
    expect(rows['|Z|']).toBe('6.000 Ω')
  })

  it('is nothing at all without a curve to read', () => {
    expect(impedanceEquivalentSections([], 1000)).toEqual([])
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
    data: {
      kind: 'impedance',
      traces: [driverTestTrace()],
      display: { ...DEFAULT_IMPEDANCE_DISPLAY },
      dutType: 'Loudspeaker',
      equivalentHz: 1000,
      referenceOhms: 100,
      generatorAmpDbv: -20,
    },
  }

  it('is what the chapter router picks for the kind, in report order', () => {
    const elements = ['chart', 'settings', 'measurements', 'component', 'equivalent'] as const
    const sections = chapterSections(subject, elements)
    expect(sections).toEqual(impedanceSections(subject, elements))
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual([
      'keyValues:Settings',
      'keyValues:Measurements',
      'keyValues:Loudspeaker',
      'paragraph:',
      'keyValues:Equivalent circuit at 1.00 kHz',
    ])
  })

  it('leaves out the blocks whose boxes are unticked', () => {
    expect(impedanceSections(subject, ['measurements']).map((s) => s.title)).toEqual(['Measurements'])
    expect(impedanceSections(subject, ['component']).map((s) => s.title)).toEqual(['Loudspeaker', null])
    expect(impedanceSections(subject, ['equivalent']).map((s) => s.title)).toEqual(['Equivalent circuit at 1.00 kHz'])
  })
})
