import { describe, expect, it } from 'vitest'
import type { MeasurementDetail } from '../../api/client'
import { createSpectrumDisplaySource } from '../../composables/useChartDisplay'
import { defaultReportElements, reportElementsForKind } from '../elements'
import { storedGeneratorTones, storedMetersFrames } from '../spectrumFrames'
import type { ReportSubject } from '../types'
import { chapterSections } from './index'
import { harmonicsSections, metricsSections } from './spectrum'

/** A stored FFT result the way MeasurementSaveService writes one: a metric row per meter and a pair per harmonic. */
const detail: MeasurementDetail = {
  resultId: 7,
  measurementTypeId: 3,
  projectName: 'Bench',
  measurementTypeName: 'FFT analysis',
  kind: 'FFTSpectrum',
  name: 'before mod',
  notes: null,
  timestampUtc: '2026-09-18T14:51:00Z',
  paramsJson: '{}',
  sampleRateHz: 48000,
  fftSize: 32768,
  channels: 2,
  inRangeDbv: 0,
  outRangeDbv: -12,
  genJson: JSON.stringify({ left: { enabled: true, tones: [{ freqHz: 1000.49, ampDbv: -18 }] }, right: { enabled: false, tones: [] } }),
  channelsData: [
    {
      channel: 0,
      columns: [],
      metrics: {
        thd_percent: 0.001,
        thd_n_percent: 0.002,
        snr_db: 110,
        fundamental_hz: 1000.49,
        fundamental_dbv: -18.05,
        noise_floor_dbv: -128.05,
        peak_dbv: -18.0,
        total_rms_dbv: -18.04,
        h1_hz: 1000.49,
        h1_dbc: 0,
        h2_hz: 2000.98,
        h2_dbc: -101.2,
        h3_hz: 3001.47,
        h3_dbc: -110.5,
      },
    },
    // A channel saved without a fundamental has no metric rows and gets no frame.
    { channel: 1, columns: [], metrics: {} },
  ],
}

function subjectOf(selectedMetrics: string[]): ReportSubject {
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
    paramsJson: '{"fftSize":32768}',
    timestampUtc: detail.timestampUtc,
    facts: [],
    sampleRateHz: 48000,
    detail,
    data: {
      kind: 'spectrum',
      spectrum: {},
      meters: storedMetersFrames(detail),
      display: createSpectrumDisplaySource(null, [0, 1]),
      selectedMetrics,
      generatorTones: storedGeneratorTones(detail.genJson),
      loadOhms: 8,
      scope: {},
      status: null,
      waterfall: null,
    },
  }
}

describe('storedMetersFrames', () => {
  it('repacks the metric rows into a meters frame, fundamental first, for channels that have one', () => {
    const frames = storedMetersFrames(detail)
    expect(Object.keys(frames)).toEqual(['0'])
    const frame = frames[0]
    expect(frame.fundamentalFreqHz).toBe(1000.49)
    expect(frame.thdPercent).toBe(0.001)
    expect(frame.binSizeHz).toBeCloseTo(48000 / 32768)
    expect(frame.harmonics.map((h) => h.dbc)).toEqual([0, -101.2, -110.5])
  })

  it('puts the fundamental back when the saved list starts at H2', () => {
    const withoutH1 = {
      ...detail,
      channelsData: [
        {
          channel: 0,
          columns: [],
          metrics: { fundamental_hz: 1000, fundamental_dbv: -18, h2_hz: 2000, h2_dbc: -100 },
        },
      ],
    }
    const frame = storedMetersFrames(withoutH1)[0]
    expect(frame.harmonics).toEqual([
      { freqHz: 1000, dbc: 0 },
      { freqHz: 2000, dbc: -100 },
    ])
  })
})

describe('storedGeneratorTones', () => {
  it('reads the left channel tones, and survives no JSON at all', () => {
    expect(storedGeneratorTones(detail.genJson)).toEqual([{ freqHz: 1000.49, ampDbv: -18 }])
    expect(storedGeneratorTones(null)).toEqual([])
    expect(storedGeneratorTones('{')).toEqual([])
  })
})

describe('spectrum report sections', () => {
  it('prints the selected tiles per channel, from the screen formulas, in the selected order', () => {
    const sections = metricsSections(subjectOf(['Pk dBV', 'THD %', 'Gain dB', 'Gen']))
    expect(sections).toHaveLength(1)
    expect(sections[0].title).toBe('Metrics — L')
    expect(sections[0].rows).toEqual([
      { label: 'Gen', value: '1000.490 Hz, -18.00 dBV' },
      { label: 'Pk dBV', value: '-18.00 dBV' },
      { label: 'THD %', value: '0.00100 %' },
      // Gain is the fundamental against the generator's own tone.
      { label: 'Gain dB', value: '-0.05 dB' },
    ])
  })

  it('tabulates the harmonics the way HarmonicsTable does', () => {
    const [table] = harmonicsSections(subjectOf([]))
    expect(table.kind).toBe('table')
    expect(table.headers).toEqual(['Order', 'Frequency', 'Level', 'dBc'])
    expect(table.cells).toEqual([
      ['Fundamental', '1000.5 Hz', '-18.05 dBV', '—'],
      ['H2', '2001.0 Hz', '-119.25 dBV', '-101.2 dB'],
      ['H3', '3001.5 Hz', '-128.55 dBV', '-110.5 dB'],
    ])
  })

  it('says so rather than printing nothing when there are no metrics to print', () => {
    const empty = subjectOf(['THD %'])
    if (empty.data.kind === 'spectrum') empty.data.meters = {}
    expect(metricsSections(empty)[0].text).toContain('No fundamental')
    expect(harmonicsSections(empty)[0].text).toContain('No fundamental')
    expect(metricsSections(subjectOf([]))[0].text).toContain('No metrics are selected')
  })

  it('is what chapterSections builds for an FFT subject, in element order', () => {
    const sections = chapterSections(subjectOf(['THD %']), ['metrics', 'harmonics', 'description', 'settings'])
    expect(sections.map((s) => s.title)).toEqual(['Metrics — L', 'Harmonics — L', 'Description', 'Settings'])
  })
})

describe('report element defaults', () => {
  it('offers the FFT its metrics, harmonics and the two secondary charts', () => {
    expect(reportElementsForKind('FFTSpectrum').map((e) => e.id)).toEqual([
      'chart',
      'description',
      'settings',
      'metrics',
      'harmonics',
      'oscilloscope',
      'waterfall',
    ])
  })

  it('includes everything a kind offers except the settings until the user chooses', () => {
    expect(defaultReportElements('FFTSpectrum')).toEqual(['chart', 'description', 'metrics', 'harmonics', 'oscilloscope', 'waterfall'])
    expect(defaultReportElements('FrequencyResponse')).toEqual(['chart', 'description', 'measurements'])
    expect(defaultReportElements('ThdVsFrequency')).toEqual(['chart', 'description'])
  })
})
