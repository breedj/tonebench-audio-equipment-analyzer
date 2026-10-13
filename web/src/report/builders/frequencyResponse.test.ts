import { describe, expect, it } from 'vitest'
import { DEFAULT_RESPONSE_DISPLAY } from '../../response/display'
import { makeTrace } from '../../response/testTrace'
import type { ReportSubject } from '../types'
import { frequencyResponseMeasurementSections, frequencyResponseSections, frequencyResponseSettingsRows } from './frequencyResponse'
import { chapterSections } from './index'
import { genericSettingsRows, humanizeKey } from './generic'

const chirpParams = JSON.stringify({
  frequencySpanHz: 96000,
  resolutionMultiplier: 2,
  levelMode: 'OutputPower',
  levelValue: 1,
  levelUnit: 'Watt',
  rightChannelIsReference: true,
  smoothingDenominator: 24,
  averageCount: 4,
  generatorAmpDbv: -18.094,
  chirpStartHz: 2,
  loadOhms: 8,
  bandwidthReference: 'BandMedian',
  bandwidthReferenceLowHz: 200,
  bandwidthReferenceHighHz: 2000,
  captureDelayMs: 300,
  chartView: 'Gain',
  chartXFromHz: 10,
})

const steppedParams = JSON.stringify({
  startFreqHz: 20,
  endFreqHz: 20000,
  stepsPerOctave: 5,
  levelMode: 'GeneratorVoltage',
  levelValue: -20,
  levelUnit: 'Dbv',
  loadOhms: 8,
  generateLeft: true,
  generateRight: true,
  fftSize: 131072,
  window: 'Hann',
  averageCount: 1,
  sampleRateHz: 192000,
  adaptiveResolution: true,
  rightChannelIsReference: false,
  bandwidthReference: 'Peak',
  captureDelayMs: 0,
})

function rowsToObject(rows: { label: string; value: string }[]) {
  return Object.fromEntries(rows.map((r) => [r.label, r.value]))
}

describe('frequencyResponseSettingsRows', () => {
  it('labels a chirp run the way the config panel does', () => {
    const rows = rowsToObject(frequencyResponseSettingsRows('FrequencyResponse', chirpParams))
    expect(rows).toEqual({
      Sweep: '2.00 Hz – 96.0 kHz',
      Resolution: '0.73 Hz',
      Level: '1 W — DUT output power into 8 Ω',
      Reference: 'Right input (left measured relative to it)',
      Smoothing: '1/24',
      Averages: '4',
      'Generator level': '-18.09 dBV',
      'Bandwidth reference': 'Passband median, 200.0 Hz – 2.00 kHz',
      'Capture delay': '300 ms',
    })
  })

  it('labels a stepped run, and leaves a zero capture delay out', () => {
    const rows = rowsToObject(frequencyResponseSettingsRows('SteppedResponse', steppedParams))
    expect(rows).toEqual({
      Sweep: '20.00 Hz – 20.0 kHz',
      'Steps per octave': '5',
      Level: '-20 dBV — Input voltage (generator)',
      'Tone on': 'Left and Right',
      Reference: 'None (channel difference)',
      Resolution: 'Adaptive per point',
      Averages: '1',
      'Bandwidth reference': 'Peak',
    })
  })

  it('quotes the fixed analysis when adaptive resolution is off', () => {
    const params = JSON.stringify({ ...JSON.parse(steppedParams), adaptiveResolution: false })
    expect(rowsToObject(frequencyResponseSettingsRows('SteppedResponse', params)).Resolution).toBe('131072-point Hann at 192.0 kHz')
  })

  it('returns nothing for JSON it cannot read', () => {
    expect(frequencyResponseSettingsRows('FrequencyResponse', '{')).toEqual([])
  })
})

describe('frequencyResponseMeasurementSections', () => {
  const gain = {
    ...makeTrace({ freqHz: [10, 100, 1000], magnitudeMaxDb: [0, 0, 0], phaseDeg: [0, 0, 0] }),
    oneDb: { dropDb: 1, symmetric: true, lowerHz: 23.1, upperHz: 18400, bandwidthHz: 18376.9, lowerClamped: false, upperClamped: false },
    threeDb: { dropDb: 3, symmetric: false, lowerHz: 10, upperHz: 44000, bandwidthHz: 43990, lowerClamped: true, upperClamped: false },
    peakDb: 4.41,
    peakFreqHz: 38000,
    referenceDb: 0.12,
    referenceMode: 'BandMedian' as const,
  }

  it('writes the panel table and readings, with the clamped-corner note', () => {
    const sections = frequencyResponseMeasurementSections([gain], 'Gain', true)

    // The three readings the curve is quoted by lead as cards; the corner table follows, and the
    // delay readings stay rows because they are read as a set.
    expect(sections[0]).toEqual({
      kind: 'figures',
      figures: [
        { label: 'Max. gain', value: '4.41 dB', sub: '1.661 x' },
        { label: 'Peak at', value: '38.0 kHz' },
        { label: '−3 dB bandwidth', value: '> 44.0 kHz', sub: 'partial — an edge was not reached' },
        { label: 'Reference', value: '0.12 dB', sub: 'Passband median' },
      ],
    })
    expect(sections[1]).toEqual({
      kind: 'table',
      title: 'Measurements',
      headers: ['', 'f(low)', 'f(high)', 'Bandwidth'],
      cells: [
        ['±1 dB', '23.10 Hz', '18.4 kHz', '18.4 kHz'],
        ['−3 dB', '< 10.00 Hz', '44.0 kHz', '> 44.0 kHz'],
      ],
    })
    expect(sections[2].kind).toBe('keyValues')
    expect(rowsToObject(sections[2].rows!)).toEqual({
      'Group delay at reference': '—',
      'Phase delay at reference': '—',
      'Group delay min': '—',
      'Group delay max': '—',
      Curve: 'Gain (left relative to the reference)',
    })
    expect(sections[3].kind).toBe('paragraph')
    expect(sections[3].text).toContain('Delay was not recorded')
    expect(sections[3].text).toContain('never reached')
  })

  it('writes the delay rows from the curve the chart draws them on, at the reference band', () => {
    const withDelays = {
      ...gain,
      groupDelayS: [2.5e-3, 12e-6, 4e-6],
      phaseDelayS: [2.6e-3, 13e-6, 5e-6],
    }
    const sections = frequencyResponseMeasurementSections([withDelays], 'Gain', true, 'DutResponse', { lowHz: 50, highHz: 500 })
    expect(rowsToObject(sections[2].rows!)).toMatchObject({
      'Group delay at reference': '12.0 µs',
      'Phase delay at reference': '13.0 µs',
      'Group delay min': '4.0 µs at 1.00 kHz',
      'Group delay max': '2.500 ms at 10.00 Hz',
    })
    expect(sections[3].text).not.toContain('Delay was not recorded')
  })

  it('says so when there is no curve to read', () => {
    expect(frequencyResponseMeasurementSections([], 'Gain', true)).toEqual([
      { kind: 'paragraph', title: 'Measurements', text: 'Nothing measured.' },
    ])
  })
})

describe('frequencyResponseSections / chapterSections', () => {
  const subject: ReportSubject = {
    id: 'stored-1',
    resultId: 1,
    typeId: 7,
    sampleRateHz: 192000,
    detail: null,
    kind: 'FrequencyResponse',
    typeName: 'FR 8 Ω',
    resultName: 'run 3',
    projectName: 'GM70',
    typeNotes: 'Measured at the 8 Ω tap.',
    resultNotes: null,
    paramsJson: chirpParams,
    timestampUtc: '2026-09-18T09:58:00Z',
    facts: [],
    data: {
      kind: 'response',
      // A trace that recorded its delays, so the measurements element carries no "not recorded"
      // note and the section list below is exactly the chosen elements.
      traces: [makeTrace({ freqHz: [10, 100], magnitudeMaxDb: [0, 0], phaseDeg: [0, 0], groupDelayS: [0, 0], phaseDelayS: [0, 0] })],
      // The view is pinned to Gain because the lone trace above is a Gain trace, and
      // `primaryResponseTrace` reads the trace the view asks for: on the default view, `Dbv`, this
      // subject has nothing to measure and the element collapses to one "Nothing measured."
      // paragraph. That is correct behaviour and not what this test is about.
      display: { ...DEFAULT_RESPONSE_DISPLAY, view: 'Gain' },
      phaseKind: 'PerTrace',
      rightChannelIsReference: true,
      latencySamples: null,
    },
  }

  it('includes only the chosen elements, in report order, and never the chart', () => {
    const titles = frequencyResponseSections(subject, ['measurements', 'settings', 'description']).map((s) => s.title ?? s.kind)
    expect(titles).toEqual(['Notes on the measurement', 'Settings', 'figures', 'Measurements', 'keyValues'])
    expect(frequencyResponseSections(subject, ['chart'])).toEqual([])
  })

  it('routes a response kind to its builder and anything else to the generic one', () => {
    expect(chapterSections(subject, ['settings'])[0].rows!.map((r) => r.label)).toContain('Bandwidth reference')

    // Every kind has a builder now, so the generic branch is what an unknown one falls to — a type
    // from a newer build, or the notes-and-settings chapter of a measurement that has no result.
    const other = chapterSections({ ...subject, kind: 'SomethingNewer', paramsJson: JSON.stringify({ frequencySpanHz: 24000, chartView: 'x' }) }, ['settings'])
    expect(other).toEqual([{ kind: 'keyValues', title: 'Settings', rows: [{ label: 'Frequency span hz', value: '24000' }] }])
  })
})

describe('generic settings', () => {
  it('humanizes keys and skips chart fields and nested objects', () => {
    expect(humanizeKey('frequencySpanHz')).toBe('Frequency span hz')
    expect(genericSettingsRows(JSON.stringify({ a: 1.5, chartView: 'Gain', nested: { x: 1 }, flag: true, empty: null }))).toEqual([
      { label: 'A', value: '1.5' },
      { label: 'Flag', value: 'Yes' },
    ])
  })
})
