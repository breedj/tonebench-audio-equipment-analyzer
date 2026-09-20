import { describe, expect, it } from 'vitest'
import { impedanceTestTrace as trace } from '../../impedance/testTrace'
import { DEFAULT_OUTPUT_IMPEDANCE_DISPLAY } from '../../outputImpedance/display'
import type { ReportSubject } from '../types'
import { chapterSections } from './index'
import { outputImpedanceMeasurementSections, outputImpedanceSections, outputImpedanceSettingsRows } from './outputImpedance'

const params = JSON.stringify({
  startFreqHz: 20,
  endFreqHz: 20000,
  stepsPerOctave: 3,
  levelMode: 'OutputPower',
  levelValue: 1,
  levelUnit: 'Watt',
  loadOhms: 8,
  secondLoadOhms: 4,
  nominalOhms: 8,
  rightChannelIsReference: true,
  fftSize: 131072,
  window: 'Hann',
  averageCount: 1,
  sampleRateHz: 192000,
  adaptiveResolution: true,
  generatorAmpDbv: -19.4,
  inputRangeDbv: 18,
  captureDelayMs: 0,
  chartZTopOhms: 1,
})

function rowsToObject(rows: { label: string; value: string }[]) {
  return Object.fromEntries(rows.map((r) => [r.label, r.value]))
}

const zout = trace([20, 1000, 20000], [0.028, 0.028, 0.19], [0, 0, 81.5])

describe('outputImpedanceSettingsRows', () => {
  it('labels the run the way the config panel does, method included', () => {
    expect(rowsToObject(outputImpedanceSettingsRows(params))).toEqual({
      Sweep: '20.00 Hz – 20.0 kHz',
      'Steps per octave': '3',
      Method: 'Complex — right channel is the reference, Zout with phase',
      Level: '1 W — DUT output power (first pass) into 8 Ω',
      Loads: '8 Ω, then 4 Ω',
      'Damping factor against': '8 Ω',
      Resolution: 'Adaptive per point',
      Averages: '1',
      'Generator level': '-19.40 dBV',
      'Input range (both passes)': '18 dBV',
    })
  })

  it('returns nothing for JSON it cannot read', () => {
    expect(outputImpedanceSettingsRows('{')).toEqual([])
  })
})

describe('outputImpedanceMeasurementSections', () => {
  it('tabulates the three quoted points, the worst and the median, then the coil', () => {
    const sections = outputImpedanceMeasurementSections(zout, 8, true)
    expect(sections[0]).toEqual({
      kind: 'table',
      title: 'Measurements',
      headers: ['', 'Zout', 'Damping factor (8 Ω)'],
      cells: [
        ['20.00 Hz', '28.0 mΩ', '286'],
        ['1.00 kHz', '28.0 mΩ', '286'],
        ['20.0 kHz', '190.0 mΩ', '42.1'],
        ['Worst, at 20.0 kHz', '190.0 mΩ', '42.1'],
        ['Median', '28.0 mΩ', '286'],
      ],
    })
    expect(sections[1].kind).toBe('keyValues')
    expect(sections[1].rows![0].value).toMatch(/^R 28\.\d mΩ · L 1\.5\d? µH$/)
  })

  it('says so when nothing was measured', () => {
    expect(outputImpedanceMeasurementSections(null, 8, false)).toEqual([
      { kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' },
    ])
  })
})

describe('outputImpedanceSections', () => {
  const subject: ReportSubject = {
    id: 'live',
    resultId: null,
    typeId: 1,
    kind: 'OutputImpedance',
    typeName: 'Damping factor',
    resultName: null,
    projectName: 'P',
    typeNotes: null,
    resultNotes: null,
    paramsJson: params,
    timestampUtc: null,
    facts: [],
    sampleRateHz: 48000,
    detail: null,
    data: { kind: 'outputImpedance', trace: zout, display: { ...DEFAULT_OUTPUT_IMPEDANCE_DISPLAY }, nominalOhms: 8, rightChannelIsReference: true },
  }

  it('is what the chapter router picks for the kind, in report order', () => {
    const sections = chapterSections(subject, ['chart', 'settings', 'measurements'])
    expect(sections).toEqual(outputImpedanceSections(subject, ['chart', 'settings', 'measurements']))
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual(['keyValues:Settings', 'table:Measurements', 'keyValues:'])
  })
})
