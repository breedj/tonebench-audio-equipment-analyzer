import { describe, expect, it } from 'vitest'
import {
  DEFAULT_PREVIEW_BOX,
  DEFAULT_SCOPE_FILTER,
  SCOPE_COUPLINGS,
  SCOPE_FILTER_KINDS,
  SCOPE_FILTER_ORDERS,
  filterCurveDecades,
  filterCurvePath,
  filterCurveX,
  filterDraws,
  filterQuery,
  filterUsesOrder,
  filterUsesQ,
  pathChangesTrace,
  pathSummary,
  type ScopeFilterSettings,
} from './filters'

function settings(patch: Partial<ScopeFilterSettings> = {}): ScopeFilterSettings {
  return { ...DEFAULT_SCOPE_FILTER, ...patch }
}

describe('the tables', () => {
  it('offers each coupling and each kind once, doing nothing first', () => {
    expect(SCOPE_COUPLINGS.map((c) => c.value)).toEqual(['Dc', 'Ac'])
    expect(SCOPE_FILTER_KINDS[0].value).toBe('None')
    expect(new Set(SCOPE_FILTER_KINDS.map((k) => k.value)).size).toBe(SCOPE_FILTER_KINDS.length)
  })

  it('gives every entry something to say about itself', () => {
    for (const entry of [...SCOPE_COUPLINGS, ...SCOPE_FILTER_KINDS]) {
      expect(entry.label.length).toBeGreaterThan(0)
      expect(entry.title.length).toBeGreaterThan(25)
    }
  })

  it('offers only even orders, which is what a second-order section can make', () => {
    for (const order of SCOPE_FILTER_ORDERS) expect(order % 2).toBe(0)
  })

  // Only the two kinds that name a band read a Q, and only the two whose shape is a skirt read an
  // order: a control that is shown but ignored is one a reader will turn and blame.
  it('knows which kind reads which shape control', () => {
    expect(filterUsesQ('BandPass')).toBe(true)
    expect(filterUsesQ('Notch')).toBe(true)
    expect(filterUsesQ('LowPass')).toBe(false)
    expect(filterUsesQ('None')).toBe(false)

    expect(filterUsesOrder('LowPass')).toBe(true)
    expect(filterUsesOrder('HighPass')).toBe(true)
    expect(filterUsesOrder('Notch')).toBe(false)
    expect(filterUsesOrder('None')).toBe(false)
  })
})

describe('what is in the path', () => {
  it('says nothing is when nothing is', () => {
    expect(pathChangesTrace(settings())).toBe(false)
    expect(pathSummary(settings())).toBeNull()
    expect(filterQuery(settings())).toEqual({})
  })

  it('counts the coupling as part of the path', () => {
    expect(pathChangesTrace(settings({ coupling: 'Ac' }))).toBe(true)
    expect(pathSummary(settings({ coupling: 'Ac' }))).toBe('AC coupled')
    expect(filterQuery(settings({ coupling: 'Ac' }))).toEqual({ coupling: 'Ac' })
  })

  // A kind with no frequency is not a filter: the server refuses it, so the client does not ask.
  it('does not ask for a filter with no frequency', () => {
    const none = settings({ kind: 'LowPass', frequencyHz: 0 })
    expect(filterDraws(none)).toBe(false)
    expect(filterQuery(none)).toEqual({})
  })

  it('carries only the shape control the kind actually reads', () => {
    expect(filterQuery(settings({ kind: 'LowPass', frequencyHz: 2000, order: 4, q: 9 }))).toEqual({
      filter: 'LowPass',
      filterHz: '2000',
      filterOrder: '4',
    })
    expect(filterQuery(settings({ kind: 'Notch', frequencyHz: 50, q: 20, order: 8 }))).toEqual({
      filter: 'Notch',
      filterHz: '50',
      filterQ: '20',
    })
  })

  it('writes what the path does, in the words the chart and a report both print', () => {
    expect(pathSummary(settings({ kind: 'LowPass', frequencyHz: 20_000, order: 4 }))).toBe(
      'Low-pass 20.0 kHz, order 4 × 2',
    )
    expect(pathSummary(settings({ coupling: 'Ac', kind: 'Notch', frequencyHz: 50, q: 20 }))).toBe(
      'AC coupled · Notch 50 Hz, Q 20.00',
    )
  })
})

describe('the preview', () => {
  const freqs = [1, 10, 100, 1_000, 10_000]
  const mags = [0, 0, -1, -20, -60]

  it('draws nothing it cannot draw', () => {
    expect(filterCurvePath([], [])).toBe('')
    expect(filterCurvePath([100], [0])).toBe('')
  })

  it('runs the whole width, left to right', () => {
    const d = filterCurvePath(freqs, mags)
    expect(d.startsWith('M0.00 ')).toBe(true)
    const points = d.split(/[ML]/).filter(Boolean).map((p) => p.split(' ').map(Number))
    expect(points.length).toBe(freqs.length)
    expect(points.at(-1)![0]).toBeCloseTo(DEFAULT_PREVIEW_BOX.width, 1)
    // Logarithmic across: five decades over the width means a decade is a fifth of it.
    expect(points[1][0]).toBeCloseTo(DEFAULT_PREVIEW_BOX.width / 4, 1)
  })

  // Clamped to the box rather than scaled to the data: a preview whose vertical scale moved with
  // the setting would make every filter look the same shape.
  it('clamps rather than rescales', () => {
    const d = filterCurvePath([1, 10], [100, -300])
    const points = d.split(/[ML]/).filter(Boolean).map((p) => p.split(' ').map(Number))
    expect(points[0][1]).toBeCloseTo(0, 1)
    expect(points[1][1]).toBeCloseTo(DEFAULT_PREVIEW_BOX.height, 1)
  })

  it('places the corner marker where the curve has that frequency', () => {
    expect(filterCurveX(1, freqs)).toBeCloseTo(0, 1)
    expect(filterCurveX(10_000, freqs)).toBeCloseTo(DEFAULT_PREVIEW_BOX.width, 1)
    expect(filterCurveX(100, freqs)).toBeCloseTo(DEFAULT_PREVIEW_BOX.width / 2, 1)
  })

  it('has nowhere to put a marker outside the curve', () => {
    expect(filterCurveX(0.1, freqs)).toBeNull()
    expect(filterCurveX(1e6, freqs)).toBeNull()
    expect(filterCurveX(Number.NaN, freqs)).toBeNull()
    expect(filterCurveX(1000, [])).toBeNull()
  })

  it('rules the decades it spans, labelled the way the frequency axes are', () => {
    const lines = filterCurveDecades(freqs)
    expect(lines.map((l) => l.label)).toEqual(['1', '10', '100', '1k', '10k'])
    expect(lines[0].x).toBeCloseTo(0, 1)
    expect(lines.at(-1)!.x).toBeCloseTo(DEFAULT_PREVIEW_BOX.width, 1)
  })
})
