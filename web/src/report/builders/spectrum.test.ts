import { describe, expect, it } from 'vitest'
import type { MeasurementDetail } from '../../api/client'
import { createSpectrumDisplaySource } from '../../composables/useChartDisplay'
import { defaultReportElements, migrateStoredElements, reportElementsForKind } from '../elements'
import { storedGeneratorTones, storedIntermodulationFrames, storedMetersFrames, storedMultitoneFrames, storedNoiseFrames, storedStimulus } from '../spectrumFrames'
import { NO_WEIGHTING } from '../../weighting'
import type { StimulusSummary } from '../../metrics'
import type { ReportSubject } from '../types'
import { chapterSections } from './index'
import { annotationsSections, harmonicsSections, metricsSections, multitoneSections } from './spectrum'
import type { ChartAnnotation } from '../../measurements/chartAnnotations'

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
        band_rms_dbv: -18.04,
        noise_density_dbv_per_root_hz: -149.5,
      },
      enbwHz: 2.197,
    },
    // A channel saved without a fundamental has no metric rows and gets no frame.
    { channel: 1, columns: [], metrics: {} },
  ],
}

function subjectOf(selectedMetrics: string[], stimulus: StimulusSummary | null = null): ReportSubject {
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
      intermodulation: storedIntermodulationFrames(detail, { method: 'Smpte', maxOrder: 5 }),
      noise: storedNoiseFrames(detail, { standard: 'A', user1: false, user2: false }, { loHz: 20, hiHz: 20000 }),
      weightingLabel: 'A-weighted',
      display: createSpectrumDisplaySource(null, [0, 1]),
      selectedMetrics,
      generatorTones: storedGeneratorTones(detail.genJson),
      stimulus,
      loadOhms: 8,
      scope: {},
      status: null,
      spectrogram: null,
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

describe('storedNoiseFrames', () => {
  it('repacks the two noise rows with the channel ENBW and the given weighting, for channels that have them', () => {
    const frames = storedNoiseFrames(detail, NO_WEIGHTING, { loHz: 20, hiHz: 80000 })
    expect(Object.keys(frames)).toEqual(['0'])
    expect(frames[0].bandRmsDbv).toBe(-18.04)
    expect(frames[0].noiseDensityDbvPerRootHz).toBe(-149.5)
    expect(frames[0].enbwHz).toBe(2.197)
    expect(frames[0].measurementHiHz).toBe(80000)
    expect(frames[0].weighting).toEqual(NO_WEIGHTING)
  })
})

describe('storedGeneratorTones', () => {
  it('reads the left channel tones, and survives no JSON at all', () => {
    expect(storedGeneratorTones(detail.genJson)).toEqual([{ freqHz: 1000.49, ampDbv: -18 }])
    expect(storedGeneratorTones(null)).toEqual([])
    expect(storedGeneratorTones('{')).toEqual([])
  })
})

describe('a two-tone result\'s metric rows', () => {
  it('prints n/a for the noise figures and spells out why once, not on all thirteen rows', () => {
    const labels = ['THD %', 'THD+N %', 'SNR dB', 'RMS dBV(bw)', 'RMS dBV']
    const sections = metricsSections(subjectOf(labels, { kind: 'Tones', toneCount: 2 }))
    const rows = sections[0].rows ?? []
    expect(rows.map((r) => [r.label, r.value])).toEqual([
      ['THD %', '0.00100 %'],
      ['THD+N %', 'n/a'],
      ['SNR dB', 'n/a'],
      ['RMS dBV(bw)', 'n/a'],
      ['RMS dBV', '-18.04 dBV'],
      // The band the suppressed figures would have been integrated over, once per channel like
      // the caveat under it -- a band tile is printed, so the band is stated.
      ['Measurement band', '20 Hz – 20 kHz'],
      ['n/a', expect.stringMatching(/^The stimulus is a two-tone pair,.*\.$/)],
    ])
  })

  it("leaves a single-tone result's rows, weighting qualifiers and all, exactly as they were", () => {
    const sections = metricsSections(subjectOf(['THD+N %', 'SNR dB'], { kind: 'Tones', toneCount: 1 }))
    const rows = sections[0].rows ?? []
    expect(rows.map((r) => r.label)).toEqual(['THD+N % (A)', 'SNR dB (A)', 'Measurement band'])
    expect(rows.every((r) => r.value !== 'n/a')).toBe(true)
  })
})

describe('storedStimulus', () => {
  const genJson = (left: unknown) => JSON.stringify({ left, right: { enabled: false, tones: [] } })

  it('reads the kind and the tone count a result was captured with', () => {
    const two = genJson({ enabled: true, kind: 'Tones', tones: [{ freqHz: 60, ampDbv: -2 }, { freqHz: 7000, ampDbv: -14 }] })
    expect(storedStimulus(two)).toEqual({ kind: 'Tones', toneCount: 2 })
  })

  it('counts the stored comb for a multitone capture, which gen_json already expands', () => {
    const comb = genJson({ enabled: true, kind: 'Multitone', tones: [{ freqHz: 5, ampDbv: -50 }, { freqHz: 10, ampDbv: -50 }] })
    expect(storedStimulus(comb)).toEqual({ kind: 'Multitone', toneCount: 2 })
  })

  it('treats a tone list with no kind as Tones, and gives up on no generator at all', () => {
    expect(storedStimulus(detail.genJson)).toEqual({ kind: 'Tones', toneCount: 1 })
    expect(storedStimulus(null)).toBeNull()
    expect(storedStimulus('{')).toBeNull()
    expect(storedStimulus(genJson({ enabled: false, tones: [] }))).toBeNull()
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

  it('prints the noise tiles and carries the weighting qualifier on the label', () => {
    const sections = metricsSections(subjectOf(['SNR dB', 'THD %', 'Noise dBV/√Hz', 'Noise in nV/√Hz']))
    expect(sections[0].rows).toEqual([
      { label: 'SNR dB (A)', value: '110.00 dB' },
      { label: 'THD %', value: '0.00100 %' },
      { label: 'Noise dBV/√Hz (A)', value: '-149.50 dBV/√Hz' },
      { label: 'Noise in nV/√Hz (A)', value: '33.5 nV/√Hz' },
      { label: 'Measurement band', value: '20 Hz – 20 kHz' },
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
  it('offers the FFT its metrics, harmonics, annotations and the two secondary charts', () => {
    expect(reportElementsForKind('FFTSpectrum').map((e) => e.id)).toEqual([
      'chart',
      'description',
      'settings',
      'metrics',
      'harmonics',
      'annotations',
      'oscilloscope',
      'spectrogram',
    ])
  })

  it('includes everything a kind offers except the settings until the user chooses', () => {
    expect(defaultReportElements('FFTSpectrum')).toEqual([
      'chart',
      'description',
      'metrics',
      'harmonics',
      'annotations',
      'oscilloscope',
      'spectrogram',
    ])
    expect(defaultReportElements('FrequencyResponse')).toEqual(['chart', 'description', 'measurements'])
    expect(defaultReportElements('ThdVsFrequency')).toEqual(['chart', 'description', 'measurements'])
    expect(defaultReportElements('Impedance')).toEqual(['chart', 'description', 'measurements', 'component', 'equivalent'])
    expect(defaultReportElements('ImpulseResponse')).toEqual(['chart', 'description', 'measurements'])
    // A kind no build of this client knows: chart and description, and the settings dump behind them.
    expect(defaultReportElements('SomethingNewer')).toEqual(['chart', 'description'])
  })

  it('reads a choice saved before the impedance split as still including the component fit', () => {
    // It used to be part of `measurements`, so a stored choice cannot say `component` and taking it
    // literally would silently drop a block the user had been getting. Backfilling what the choice
    // already appeared to mean is migration 008's rule.
    expect(migrateStoredElements(['chart', 'measurements'])).toEqual(['chart', 'measurements', 'component'])
  })

  it('leaves a choice that knows about the split exactly as it stands', () => {
    expect(migrateStoredElements(['chart', 'measurements', 'equivalent'])).toEqual(['chart', 'measurements', 'equivalent'])
    expect(migrateStoredElements(['chart', 'component'])).toEqual(['chart', 'component'])
    // Nothing to backfill onto: the component fit was never part of a choice without Measurements.
    expect(migrateStoredElements(['chart', 'description'])).toEqual(['chart', 'description'])
  })

  it('reads a choice saved when the spectrogram was called the waterfall', () => {
    // The id is what a stored choice holds and the caller drops an id this build does not know, so
    // without the rename the chapter would quietly stop being printed.
    expect(migrateStoredElements(['chart', 'metrics', 'waterfall'])).toEqual(['chart', 'metrics', 'spectrogram'])
    expect(migrateStoredElements(['waterfall'])).toEqual(['spectrogram'])
    expect(migrateStoredElements(['chart', 'spectrogram'])).toEqual(['chart', 'spectrogram'])
  })
})

describe('intermodulation', () => {
  /** The `imd_*` rows MeasurementSaveService.IntermodulationMetrics writes for an SMPTE capture with two sideband pairs. */
  const imdDetail: MeasurementDetail = {
    ...detail,
    paramsJson: '{"imdMethod":"Smpte","imdMaxOrder":3,"imdPreset":"Smpte"}',
    channelsData: [
      {
        channel: 0,
        columns: [],
        metrics: {
          ...detail.channelsData[0]!.metrics,
          imd_percent: 0.02,
          imd_db: -73.98,
          imd_tone_low_hz: 58.59,
          imd_tone_high_hz: 7001.95,
          imd_tone_low_dbv: -1.94,
          imd_tone_high_dbv: -13.98,
          imd_sine_equivalent_dbv: 0,
          imd_combined_rms_dbv: -2.28,
          imd_reference_dbv: -13.98,
          im2_percent: 0.015,
          im3_percent: 0.013,
          imd_p0_hz: 6943.36,
          imd_p0_dbv: -95,
          imd_p0_dbc: -81,
          imd_p0_m: -1,
          imd_p0_n: 1,
          imd_p0_order: 2,
          imd_p1_hz: 7060.55,
          imd_p1_dbv: -95.2,
          imd_p1_dbc: -81.2,
          imd_p1_m: 1,
          imd_p1_n: 1,
          imd_p1_order: 2,
        },
      },
      { channel: 1, columns: [], metrics: {} },
    ],
  }

  it('rebuilds the intermodulation frame from the stored rows', () => {
    const frames = storedIntermodulationFrames(imdDetail, { method: 'Smpte', maxOrder: 3 })

    expect(Object.keys(frames)).toEqual(['0'])
    const f = frames[0]!
    expect(f.toneLowHz).toBe(58.59)
    expect(f.toneHighHz).toBe(7001.95)
    expect(f.imdPercent).toBe(0.02)
    expect(f.perOrder).toEqual([
      { order: 2, percent: 0.015 },
      { order: 3, percent: 0.013 },
    ])
    expect(f.products).toHaveLength(2)
    expect(f.products[1]).toEqual({ freqHz: 7060.55, dbv: -95.2, dbc: -81.2, m: 1, n: 1, order: 2 })
    expect(f.sineEquivalentDbv).toBe(0)
  })

  it('gives a channel without an imd_percent row no frame', () => {
    expect(storedIntermodulationFrames(detail, { method: 'Smpte', maxOrder: 5 })).toEqual({})
  })

  it('prints the intermodulation table and totals after the harmonics, and nothing without one', () => {
    const withImd: ReportSubject = {
      ...subjectOf(['IMD %']),
      data: {
        ...subjectOf(['IMD %']).data,
        meters: storedMetersFrames(imdDetail),
        intermodulation: storedIntermodulationFrames(imdDetail, { method: 'Smpte', maxOrder: 3 }),
      } as ReportSubject['data'],
    }

    const sections = harmonicsSections(withImd)
    const titles = sections.map((s) => s.title)
    expect(titles).toContain('Intermodulation (SMPTE) — L')
    expect(titles).toContain('IMD — L')
    const table = sections.find((s) => s.title === 'Intermodulation (SMPTE) — L')
    expect(table?.kind).toBe('table')
    if (table?.kind === 'table') {
      expect(table.cells?.[0]?.[0]).toBe('Tone f1')
      expect(table.cells?.[2]?.[0]).toBe('IM2 (f2 − f1)')
      expect(table.headers?.[3]).toBe('dBc re the HF carrier')
    }

    const metricRows = metricsSections(withImd).find((s) => s.title === 'Metrics — L')
    expect(metricRows?.kind === 'keyValues' && (metricRows.rows ?? []).some((r) => r.label === 'IMD %')).toBe(true)

    expect(harmonicsSections(subjectOf([])).map((s) => s.title)).not.toContain('IMD — L')
  })
})

describe('multitone', () => {
  /** The `tdn_*` / `mt_*` rows MeasurementSaveService.MultitoneMetrics writes for a three-tone comb whose first gap was unresolved. */
  const multitoneDetail: MeasurementDetail = {
    ...detail,
    channelsData: [
      {
        channel: 0,
        columns: [],
        metrics: {
          ...detail.channelsData[0]!.metrics,
          tdn_percent: 0.112,
          tdn_db: -59.02,
          mt_comb_dbv: -20.5,
          mt_tone_count: 3,
          mt_band_count: 2,
          mt_resolved_bands: 1,
          mt_t0_hz: 1000,
          mt_t0_dbv: -26,
          mt_t1_hz: 1004,
          mt_t1_dbv: -26,
          mt_t2_hz: 2000,
          mt_t2_dbv: -26,
          mt_b1_hz: 1500,
          mt_b1_dbv: -72,
          mt_b1_dbr: -60.9,
          mt_b1_dn_dbr: -58.9,
        },
      },
      { channel: 1, columns: [], metrics: {} },
    ],
  }

  it('rebuilds the multitone frame from the stored rows, with a gap that has no rows unresolved', () => {
    const frames = storedMultitoneFrames(multitoneDetail)

    expect(Object.keys(frames)).toEqual(['0'])
    const f = frames[0]!
    expect(f.combDbv).toBe(-20.5)
    expect(f.tdn).toBeCloseTo(0.00112, 8)
    expect(f.resolvedBands).toBe(1)
    expect(f.tones).toEqual([
      { freqHz: 1000, dbv: -26 },
      { freqHz: 1004, dbv: -26 },
      { freqHz: 2000, dbv: -26 },
    ])
    expect(f.bands).toHaveLength(2)
    expect(f.bands[0]!.resolved).toBe(false)
    expect(f.bands[1]).toEqual({ resolved: true, highestFreqHz: 1500, highestDbv: -72, highestDbr: -60.9, dnDbr: -58.9 })
  })

  it('gives a channel without a tdn_percent row no frame', () => {
    expect(storedMultitoneFrames(detail)).toEqual({})
  })

  it('prints the multitone table and the TD+N total, and the TD+N tiles', () => {
    const base = subjectOf(['TD+N %', 'TD+N dB'])
    const withMultitone: ReportSubject = {
      ...base,
      data: {
        ...base.data,
        meters: storedMetersFrames(multitoneDetail),
        multitone: storedMultitoneFrames(multitoneDetail),
      } as ReportSubject['data'],
    }

    const sections = multitoneSections(withMultitone.data.kind === 'spectrum' ? (withMultitone.data.multitone ?? {}) : {})
    const titles = sections.map((s) => s.title)
    expect(titles).toEqual(['Multitone — L', 'TD+N — L'])
    const table = sections[0]!
    expect(table.kind).toBe('table')
    if (table.kind === 'table') {
      expect(table.cells?.[0]?.[2]).toBe('unresolved')
      expect(table.cells?.[1]?.[2]).toBe('1.50 kHz · -72.0 dBV')
      expect(table.cells?.[2]?.[2]).toBe('—')
    }
    const totals = sections[1]!
    if (totals.kind === 'keyValues') {
      expect(totals.rows?.[0]).toEqual({ label: 'TD+N', value: '0.11200 %, -59.02 dB' })
      expect(totals.rows?.[1]).toEqual({ label: 'Gaps resolved', value: '1 of 2' })
    }

    expect(harmonicsSections(withMultitone).map((s) => s.title)).toContain('Multitone — L')
    const metricRows = metricsSections(withMultitone).find((s) => s.title === 'Metrics — L')
    expect(metricRows?.kind === 'keyValues' && (metricRows.rows ?? []).some((r) => r.label === 'TD+N %')).toBe(true)
    expect(harmonicsSections(subjectOf([])).map((s) => s.title)).not.toContain('Multitone — L')
  })
})

describe('the annotations table', () => {
  function annotated(annotations: ChartAnnotation[]) {
    const subject = subjectOf([])
    if (subject.data.kind !== 'spectrum') throw new Error('not a spectrum subject')
    subject.data.display.annotations.value = annotations
    return subject
  }

  const hum: ChartAnnotation = {
    id: 2,
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
  }
  const corner: ChartAnnotation = {
    id: 1,
    style: 'frequency',
    text: 'Bandwidth corner',
    view: 'Spectrum',
    x: 21500,
    y: null,
    x2: null,
    y2: null,
    xQuantity: 'hz',
    yQuantity: 'dbv',
    channel: null,
    labelOffsetX: 0,
    labelOffsetY: 0,
  }

  it('prints the annotations numbered the way the chart numbers them', () => {
    const [section] = annotationsSections(annotated([corner, hum]))
    expect(section.title).toBe('Annotations')
    expect(section.headers).toEqual(['#', 'Frequency', 'Level', 'Channel', 'Note'])
    // Left to right, whatever order they are stored in -- the same numbering the chart's badges use.
    expect(section.cells).toEqual([
      ['1', '50.00 Hz', '-98.2 dBV', 'L', 'Mains hum'],
      ['2', '21.50 kHz', '\u2014', '\u2014', 'Bandwidth corner'],
    ])
  })

  it('prints nothing at all when there are none', () => {
    expect(annotationsSections(annotated([]))).toEqual([])
  })

  it('omits the channel column when no annotation names one', () => {
    const [section] = annotationsSections(annotated([corner]))
    expect(section.headers).toEqual(['#', 'Frequency', 'Level', 'Note'])
    expect(section.cells).toEqual([['1', '21.50 kHz', '\u2014', 'Bandwidth corner']])
  })

  const band: ChartAnnotation = {
    id: 3,
    style: 'box',
    text: 'Hum band',
    view: 'Spectrum',
    x: 900,
    y: -40,
    x2: 1200,
    y2: -95,
    xQuantity: 'hz',
    yQuantity: 'dbv',
    channel: null,
    labelOffsetX: 0.3,
    labelOffsetY: 10,
  }

  it('prints a box as a frequency range and a level range in the same two columns', () => {
    const [section] = annotationsSections(annotated([band]))
    expect(section.headers).toEqual(['#', 'Frequency', 'Level', 'Note'])
    expect(section.cells).toEqual([['1', '0.90 \u2013 1.20 kHz', '-95.0 \u2013 -40.0 dBV', 'Hum band']])
  })

  it('still prints a stored frequency rule, which the chart no longer offers to add', () => {
    const [section] = annotationsSections(annotated([corner]))
    expect(section.cells).toEqual([['1', '21.50 kHz', '\u2014', 'Bandwidth corner']])
  })

  it('is built for an FFT subject only when its element is chosen', () => {
    const subject = annotated([hum])
    const titles = (elements: string[]) =>
      chapterSections(subject, elements as never).map((section) => section.title)
    expect(titles(['annotations'])).toContain('Annotations')
    expect(titles(['metrics'])).not.toContain('Annotations')
  })
})
