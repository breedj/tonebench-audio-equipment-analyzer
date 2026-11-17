import { describe, expect, it } from 'vitest'
import type { ScopeChannelSlice, ScopeRecordResponse, ScopeSliceMeasurements } from '../../api/client'
import { DEFAULT_SCOPE_DISPLAY } from '../../scope/display'
import type { ReportSubject } from '../types'
import { scopeMeasurementSections, scopeSections, scopeSettingsRows } from './oscilloscope'
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
  return { channel, x0: -0.005, dx: 1e-5, raw: false, samples: null, min, max, measurements, gated: null }
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
