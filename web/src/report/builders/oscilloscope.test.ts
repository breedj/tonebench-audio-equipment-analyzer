import { describe, expect, it } from 'vitest'
import type { ScopeChannelSlice, ScopeRecordResponse, ScopeSliceMeasurements } from '../../api/client'
import { DEFAULT_SCOPE_DISPLAY } from '../../scope/display'
import type { ReportSubject } from '../types'
import {
  scopeChosenMeasurementSection,
  scopeMeasurementSections,
  scopeSections,
  scopeSettingsRows,
} from './oscilloscope'
import { chapterSections } from './index'

const params = JSON.stringify({
  sampleRateHz: 192000,
  recordSeconds: 0.02,
  preTriggerFraction: 0.25,
  mode: 'Normal',
  triggerKind: 'Edge',
  triggerSource: 'ChannelA',
  triggerSlope: 'Rising',
  triggerLevelV: 0.25,
  triggerHysteresisV: 0.4,
  holdoffSeconds: 0,
  processLeft: true,
  processRight: false,
  captureDelayMs: 300,
  chartVoltsPerDivision: 0.5,
})

function rowsToObject(rows: { label: string; value: string }[]) {
  return Object.fromEntries(rows.map((r) => [r.label, r.value]))
}

/**
 * A decimated slice whose *measurements* are supplied separately from its columns -- which is the
 * shape the wire really has, and the thing these tests are about: the readings are the analyzer's
 * own reading of the samples, not of the min/max pairs beside them.
 */
function slice(channel: number, min: number[], max: number[], measured: Partial<ScopeSliceMeasurements>): ScopeChannelSlice {
  const measurements: ScopeSliceMeasurements = {
    maxV: Math.max(...max),
    minV: Math.min(...min),
    peakToPeakV: Math.max(...max) - Math.min(...min),
    meanV: 0,
    rmsV: 0,
    acRmsV: 0,
    ...measured,
  }
  return { channel, x0: -0.005, dx: 1e-5, raw: false, samples: null, min, max, measurements, gated: null, residual: null }
}

function record(channels: ScopeChannelSlice[], extra: Partial<ScopeRecordResponse> = {}): ScopeRecordResponse {
  return {
    sampleRateHz: 192000,
    frameCount: 3840,
    triggerOffsetS: 0.005,
    fromS: -0.005,
    toS: 0.015,
    spliced: false,
    forced: false,
    channels,
    ...extra,
  }
}

function subjectWith(data: ReportSubject['data']): ReportSubject {
  return {
    id: 'live',
    resultId: null,
    typeId: 1,
    kind: 'Oscilloscope',
    typeName: 'Scope 1',
    resultName: null,
    projectName: 'Bench',
    typeNotes: null,
    resultNotes: null,
    paramsJson: params,
    timestampUtc: null,
    facts: [],
    sampleRateHz: 192000,
    detail: null,
    data,
  }
}

const scopeData = (overrides: Partial<Extract<ReportSubject['data'], { kind: 'scope' }>> = {}) =>
  ({
    kind: 'scope' as const,
    record: record([
      slice(0, [-1, -0.9], [1, 0.95], { meanV: 0.02, rmsV: 0.7, acRmsV: 0.7, frequencyHz: 1000, periodS: 1e-3 }),
      slice(1, [-0.5, -0.4], [0.5, 0.45], { meanV: -0.01, rmsV: 0.35, acRmsV: 0.35 }),
    ]),
    display: { ...DEFAULT_SCOPE_DISPLAY },
    triggerLevelV: 0.25,
    triggerHysteresisV: 0.4,
    triggerSource: 'ChannelA' as const,
    recordSeconds: 0.02,
    preTriggerFraction: 0.25,
    ...overrides,
  })

describe('scopeSettingsRows', () => {
  it('labels the run the way the two config sections label it', () => {
    expect(rowsToObject(scopeSettingsRows(params))).toEqual({
      'Sample rate': '192 kS/s',
      // The control that was turned, and what it cost. Ten divisions apart by construction.
      'Time/div': '2.000 ms',
      // The figure is grouped the way the screen groups it, which is the reader's own locale --
      // so the expectation asks for the same thing rather than for one locale's spelling of it.
      Record: `20.00 ms (${(3840).toLocaleString()} samples)`,
      'Pre-trigger': '25 %',
      Channels: 'Left',
      Mode: 'Normal — waits for a crossing',
      Trigger: 'Rising edge on Left',
      Level: '0.25 V ± 0.4 V hysteresis',
      'Capture delay': '300 ms',
    })
  })

  it('answers a document it cannot read with nothing rather than throwing', () => {
    expect(scopeSettingsRows('{ not json')).toEqual([])
    expect(scopeSettingsRows('"a string"')).toEqual([])
  })
})

describe('scopeMeasurementSections', () => {
  it('quotes the extremes off the picture and the mean off the samples', () => {
    const sections = scopeMeasurementSections(scopeData().record, 0.5, true, true)
    const table = sections.find((s) => s.kind === 'table')
    expect(table?.headers).toEqual(['Reading', 'Left', 'Right'])
    const cells = table?.cells ?? []
    const row = (label: string) => cells.find((c) => c[0] === label)
    // The mean is the slice's own, not derived from the min/max pair -- which for this slice would
    // be 0.000 V on both channels, and is the whole reason it travels beside the picture.
    expect(row('Maximum')).toEqual(['Maximum', '1.000 V', '0.500 V'])
    expect(row('Mean')).toEqual(['Mean', '0.020 V', '-0.010 V'])
    // A reading one channel has and the other does not is a blank cell rather than a missing row.
    expect(row('Frequency')).toEqual(['Frequency', '1.00 kHz', '—'])
  })

  it('leaves out a channel the display rail is hiding, as the screen does', () => {
    const sections = scopeMeasurementSections(scopeData().record, 0.5, true, false)
    const table = sections.find((s) => s.kind === 'table')
    expect(table?.headers).toEqual(['Reading', 'Left'])
  })

  it('says so when nothing was captured', () => {
    expect(scopeMeasurementSections(null, 0.5, true, true)).toEqual([
      { kind: 'paragraph', title: 'Measurements', text: 'Nothing captured.' },
    ])
  })

  it('carries the two flags into the record facts, because a trace that looks ordinary is not', () => {
    const sections = scopeMeasurementSections(
      record([slice(0, [-1], [1], { meanV: 0 })], { forced: true, spliced: true }),
      0.5,
      true,
      true,
    )
    const facts = sections.find((s) => s.kind === 'keyValues')
    const labels = (facts?.rows ?? []).map((r) => r.label)
    expect(labels).toContain('Untriggered')
    expect(labels).toContain('Spliced')
  })

  /**
   * The eye's own table, which the X-Y had and this did not: `eyeReadings` was written alongside
   * `xyReadings` and then imported by nothing, so the jitter the analyzer measures appeared
   * neither on screen nor on paper (2026-11-22). Its figures are not the channels' own readings --
   * a spread, a phase of the fold and a time -- so they would put three units in one column.
   */
  it('prints the eye’s own readings when one was folded', () => {
    const sections = scopeMeasurementSections(
      record([slice(0, [-1], [1], { meanV: 0 })], {
        eye: {
          channel: 0,
          folds: [],
          foldFrameCount: 40,
          foldSeconds: 0.0005,
          periodHz: 2000,
          foldCount: 64,
          windowFoldCount: 64,
          maxSpreadV: 0.012,
          spreadAtS: 0.00025,
          meanSpreadV: 0.008,
          jitterS: 8e-10,
        },
      }),
      0.5,
      true,
      true,
    )

    const eye = sections.find((s) => s.kind === 'table' && s.title === 'Eye')
    expect(eye, 'the eye table is missing').toBeDefined()
    const jitter = (eye?.cells ?? []).find((c) => c[0] === 'Jitter')
    // In picoseconds: the axis formatter its neighbours use would write this as `0.001 µs`.
    expect(jitter?.[1]).toContain('ps')
  })

  it('leaves the eye table out where no eye was folded', () => {
    const sections = scopeMeasurementSections(scopeData().record, 0.5, true, true)
    expect(sections.find((s) => s.kind === 'table' && s.title === 'Eye')).toBeUndefined()
  })

  it('says on paper what the panel says in a tooltip about AC coupling', () => {
    // Paper has no tooltip, and the mean on a quiet input is the one reading here that is routinely
    // read as the DUT's.
    const text = scopeMeasurementSections(scopeData().record, 0.5, true, true)
      .filter((s) => s.kind === 'paragraph')
      .map((s) => s.text ?? '')
      .join(' ')
    expect(text).toMatch(/AC-coupled/)
  })
})

describe('scopeSections', () => {
  it('prints only the elements asked for', () => {
    const subject = subjectWith(scopeData())
    expect(scopeSections(subject, ['settings']).map((s) => s.title)).toEqual(['Settings'])
    expect(scopeSections(subject, ['measurements']).map((s) => s.title)).toEqual([
      // The chosen few first and the full set after them: the readout is what the reader was
      // watching, and a page that opens with forty rows buries it.
      'Selected measurements',
      'Measurements',
      'The record',
      undefined,
    ])
  })

  it('is reached by the chapter dispatch for this kind', () => {
    // The thing a ninth builder forgets: written and never wired up.
    const sections = chapterSections(subjectWith(scopeData()), ['settings'])
    expect(sections.map((s) => s.title)).toEqual(['Settings'])
  })

  it('still prints its settings for a stored result, which keeps no waveform', () => {
    const sections = chapterSections(subjectWith(scopeData({ record: null })), ['settings', 'measurements'])
    expect(sections.map((s) => s.title)).toEqual(['Settings', 'Measurements'])
  })
})

describe('scopeChosenMeasurementSection', () => {
  const display = {
    measurements: ['pkpk', 'freq'],
    measurementStatistics: false,
    showLeft: true,
    showRight: true,
  }

  it('prints the chosen readings per drawn channel, in the catalogue order', () => {
    const [table] = scopeChosenMeasurementSection(scopeData().record, display, null)
    expect(table.headers).toEqual(['Measurement', 'Value'])
    expect(table.cells?.map((row) => row[0])).toEqual([
      'Left · Pk-Pk',
      'Right · Pk-Pk',
      'Left · Freq',
      'Right · Freq',
    ])
    // The second channel's record carries no frequency, which is a dash rather than a blank: in a
    // set of rows a reader chose, a blank reads as something that failed to arrive.
    expect(table.cells?.[3][1]).toBe('—')
  })

  it('quotes the same numbers the screen does', () => {
    const [table] = scopeChosenMeasurementSection(scopeData().record, display, null)
    // 2.000 V peak to peak on the left (−1 to +1) and 1.000 kHz, at the magnitude each reads at.
    expect(table.cells?.[0][1]).toBe('2.000 V')
    expect(table.cells?.[2][1]).toBe('1.000 kHz')
  })

  it('leaves the statistics out when nothing has counted any', () => {
    // Which is every stored result, and any run that was never started. Five columns of dashes say
    // less than no columns at all.
    const [table] = scopeChosenMeasurementSection(
      scopeData().record,
      { ...display, measurementStatistics: true },
      null,
    )
    expect(table.headers).toEqual(['Measurement', 'Value'])
  })

  it('prints each statistic in its own row’s units and at its own magnitude', () => {
    const stats = {
      'pkpk:ch0': { count: 83, mean: 2.000_1, min: 1.999, max: 2.001, peakToPeak: 0.002, stdev: 0.000_757, last: 2 },
    }
    const [table] = scopeChosenMeasurementSection(
      scopeData().record,
      { ...display, measurementStatistics: true },
      stats,
    )
    expect(table.headers).toEqual(['Measurement', 'Value', 'Mean', 'Min', 'Max', 'Pk-Pk', 'Stdev', 'Count'])
    const row = table.cells?.find((cells) => cells[0] === 'Left · Pk-Pk')
    // The mean in volts and the standard deviation in microvolts, off one formatter -- which is
    // the whole point of writing a statistic at its own magnitude rather than the value's.
    expect(row?.[2]).toBe('2.000 V')
    expect(row?.[6]).toBe('757.0 µV')
    expect(row?.[7]).toBe((83).toLocaleString())
    // A row nothing counted still prints, dashed.
    const other = table.cells?.find((cells) => cells[0] === 'Right · Pk-Pk')
    expect(other?.[2]).toBe('—')
  })

  it('is nothing at all when the reader has chosen nothing', () => {
    expect(scopeChosenMeasurementSection(scopeData().record, { ...display, measurements: [] }, null)).toEqual([])
  })

  it('is nothing at all for a stored result, which keeps no waveform', () => {
    expect(scopeChosenMeasurementSection(null, display, null)).toEqual([])
  })
})
