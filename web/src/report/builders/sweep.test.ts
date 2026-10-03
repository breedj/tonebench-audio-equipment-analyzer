import { describe, expect, it } from 'vitest'
import type { ThdSweepPoint } from '../../api/client'
import { IMD_VS_AMPLITUDE_KIND, THD_VS_AMPLITUDE_KIND, THD_VS_FREQUENCY_KIND } from '../../measurements/kinds'
import { defaultSweepDisplay } from '../../sweep/display'
import type { ReportSubject } from '../types'
import { chapterSections } from './index'
import { sweepLoadOhmsFromParams, sweepMeasurementSections, sweepSections, sweepSettingsRows, type SweepReportData } from './sweep'

function rowsToObject(rows: { label: string; value: string }[]) {
  return Object.fromEntries(rows.map((r) => [r.label, r.value]))
}

function point(overrides: Partial<ThdSweepPoint> = {}): ThdSweepPoint {
  return {
    freqHz: 1000,
    channel: 0,
    fundamentalDbv: -20,
    gainDb: -0.3,
    powerWatt: 0.00125,
    thdPercent: 0.4,
    thdNPercent: 0.5,
    snrDb: 90,
    harmonicPercent: [0.3, 0.1, 0.02, 0.001],
    d6PlusPercent: 0.005,
    noisePercent: 0.001,
    generatorDbv: -20,
    previewFft: null,
    previewScope: null,
    normalizedThdPercent: null,
    normalizedHarmonicPercent: null,
    normalizedD6PlusPercent: null,
    ...overrides,
  }
}

const frequencyParams = JSON.stringify({
  startFreqHz: 20,
  endFreqHz: 20000,
  stepsPerOctave: 3,
  levelMode: 'OutputPower',
  levelValue: 1,
  levelUnit: 'Watt',
  loadOhms: 8,
  processLeft: true,
  processRight: false,
  fftSize: 32768,
  window: 'Hann',
  averageCount: 2,
  sampleRateHz: 48000,
  measureNoiseFloor: true,
  adaptiveResolution: true,
  thdMethod: 'FrequencyNormalized',
  generatorAmpDbv: -11.02,
  captureDelayMs: 300,
  chartUnit: 'db',
})

const amplitudeParams = JSON.stringify({
  fixedFreqHz: 1000,
  startAmplitudeVolt: 0.01,
  endAmplitudeVolt: 5,
  stepsPerOctave: 2,
  loadOhms: 4,
  processLeft: true,
  processRight: true,
  fftSize: 16384,
  window: 'Hann',
  averageCount: 1,
  sampleRateHz: 48000,
  measureNoiseFloor: false,
  chartXAxis: 'outputWatt',
})

const imdParams = JSON.stringify({
  preset: 'Smpte',
  toneLowHz: 60,
  toneHighHz: 7000,
  lowToHighRatio: 4,
  method: 'Smpte',
  maxOrder: 5,
  startLevelVolt: 0.1,
  endLevelVolt: 2,
  stepsPerOctave: 2,
  loadOhms: 8,
  processLeft: true,
  processRight: false,
  fftSize: 32768,
  window: 'Hann',
  averageCount: 4,
  sampleRateHz: 48000,
  measureNoiseFloor: true,
  maxProductFreqHz: 20000,
  captureDelayMs: 0,
})

describe('sweepSettingsRows', () => {
  it('labels a THD-vs-frequency run the way its config panel does', () => {
    expect(rowsToObject(sweepSettingsRows(THD_VS_FREQUENCY_KIND, frequencyParams))).toEqual({
      Sweep: '20.00 Hz – 20.0 kHz',
      'Steps per octave': '3',
      Level: '1 W — DUT output power into 8 Ω',
      Channels: 'Left',
      Resolution: 'Adaptive per point',
      'Averages per point': '2',
      'Noise floor': 'Measured first, generator off',
      'THD method': 'Frequency normalized',
      'Generator level': '-11.02 dBV',
      'Capture delay': '300 ms',
    })
  })

  it('labels a THD-vs-amplitude run: the one frequency, the amplitude span and the fixed resolution', () => {
    expect(rowsToObject(sweepSettingsRows(THD_VS_AMPLITUDE_KIND, amplitudeParams))).toEqual({
      Frequency: '1.00 kHz',
      Amplitude: '10 mV – 5 V',
      'Steps per octave': '2',
      Load: '4 Ω',
      Channels: 'Left and Right',
      Resolution: '16384-point Hann at 48.0 kHz',
      'Averages per point': '1',
      'Noise floor': 'Not measured',
    })
  })

  it('labels an IMD-vs-amplitude run: the preset, the pair, the method and the product limit', () => {
    expect(rowsToObject(sweepSettingsRows(IMD_VS_AMPLITUDE_KIND, imdParams))).toEqual({
      'IMD preset': 'SMPTE 60 Hz + 7 kHz (4:1)',
      Tones: '60.00 Hz + 7.00 kHz, 4:1',
      Method: 'SMPTE / DIN (sidebands re carrier)',
      'Max order': '5',
      Level: '100 mV – 2 V sine-equivalent',
      'Steps per octave': '2',
      Load: '8 Ω',
      Channels: 'Left',
      Resolution: '32768-point Hann at 48.0 kHz',
      'Averages per point': '4',
      'Noise floor': 'Measured first, generator off',
      'Product band limit': '20.0 kHz',
    })
  })

  it('returns nothing for JSON it cannot read, and 8 Ω for a load it does not find', () => {
    expect(sweepSettingsRows(THD_VS_FREQUENCY_KIND, '{')).toEqual([])
    expect(sweepLoadOhmsFromParams('{')).toBe(8)
    expect(sweepLoadOhmsFromParams(amplitudeParams)).toBe(4)
  })
})

describe('sweepMeasurementSections', () => {
  const data: SweepReportData = {
    kind: 'sweep',
    points: [
      point({ freqHz: 10000, channel: 0, thdPercent: 0.9 }),
      point({ freqHz: 100, channel: 0, thdPercent: 0.2 }),
      point({ freqHz: 1000, channel: 0, thdPercent: 0.05 }),
      point({ freqHz: 1000, channel: 1, thdPercent: 0.07 }),
    ],
    method: 'Conventional',
    family: 'thd',
    display: defaultSweepDisplay(),
  }

  it('summarises each channel, then tabulates its points in x order with the chart columns', () => {
    const sections = sweepMeasurementSections(data, THD_VS_FREQUENCY_KIND, 8)
    expect(sections.map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual([
      'keyValues:Measurements — left channel',
      'table:',
      'keyValues:Measurements — right channel',
      'table:',
    ])
    expect(rowsToObject(sections[0].rows!)).toEqual({
      Points: '3',
      'Worst THD': '0.90% at 10.0 kHz',
      'Best THD': '0.050% at 1.00 kHz',
    })
    expect(sections[1].headers).toEqual([
      'Frequency', 'Generator', 'THD', 'Magnitude', 'D2', 'D3', 'D4', 'D5', 'D6+', 'Power', 'Noise floor',
    ])
    expect(sections[1].cells!.map((c) => c[0])).toEqual(['100 Hz', '1.00 kHz', '10.0 kHz'])
    expect(sections[1].cells![1]).toEqual([
      '1.00 kHz', '100 mV (-20.00 dBV)', '0.050%', '-0.30 dB', '0.30%', '0.10%', '0.020%', '0.0010%', '0.0050%',
      '1.25 mW (8 Ω)', '0.0010%',
    ])
    expect(rowsToObject(sections[2].rows!).Points).toBe('1')
  })

  it('writes the figures in dB when the display says so, and names the IMD family', () => {
    const imd: SweepReportData = { ...data, family: 'imd', display: { ...defaultSweepDisplay(), unit: 'db' } }
    const sections = sweepMeasurementSections(imd, IMD_VS_AMPLITUDE_KIND, 8)
    // -40.92 dBc plus that point's own -0.3 dB of gain: the dB figures on paper are compensated
    // the way the chart's dB axis is, so the summary cannot disagree with the trace it summarises.
    expect(rowsToObject(sections[0].rows!)['Worst IMD']).toBe('-41.22 dB at 100 mV')
    expect(sections[1].headers![0]).toBe('Output voltage')
    expect(sections[1].headers![1]).toBe('Generator')
    expect(sections[1].headers![2]).toBe('IMD')
  })

  it('says so when nothing was measured', () => {
    expect(sweepMeasurementSections({ ...data, points: [] }, THD_VS_FREQUENCY_KIND, 8)).toEqual([
      { kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' },
    ])
  })
})

describe('sweepSections', () => {
  const subject: ReportSubject = {
    id: 'live',
    resultId: null,
    typeId: 1,
    kind: THD_VS_FREQUENCY_KIND,
    typeName: 'THD vs frequency',
    resultName: null,
    projectName: 'P',
    typeNotes: null,
    resultNotes: null,
    paramsJson: frequencyParams,
    timestampUtc: null,
    facts: [],
    sampleRateHz: 48000,
    detail: null,
    data: { kind: 'sweep', points: [point()], method: 'FrequencyNormalized', family: 'thd', display: defaultSweepDisplay() },
  }

  it('is what the chapter router picks for every sweep kind, in report order', () => {
    for (const kind of [THD_VS_FREQUENCY_KIND, THD_VS_AMPLITUDE_KIND, IMD_VS_AMPLITUDE_KIND]) {
      const s = { ...subject, kind }
      expect(chapterSections(s, ['chart', 'settings', 'measurements'])).toEqual(sweepSections(s, ['chart', 'settings', 'measurements']))
    }
    expect(chapterSections(subject, ['chart', 'settings', 'measurements']).map((s) => `${s.kind}:${s.title ?? ''}`)).toEqual([
      'keyValues:Settings',
      'keyValues:Measurements — left channel',
      'table:',
    ])
  })

  it('reads the Power column against the load the run recorded, not the screen', () => {
    const sections = sweepSections({ ...subject, paramsJson: amplitudeParams, kind: THD_VS_AMPLITUDE_KIND }, ['measurements'])
    // By header rather than a fixed index: the column list is the one in sweepMetricColumns, and a
    // column added to it ahead of Power would otherwise fail this for no reason.
    expect(sections[1].cells![0][sections[1].headers!.indexOf('Power')]).toBe('1.25 mW (4 Ω)')
  })
})
