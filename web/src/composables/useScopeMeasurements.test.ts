import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useScopeMeasurements } from './useScopeMeasurements'
import { DEFAULT_SCOPE_MEASUREMENTS } from '../scope/measurementCatalogue'
import type { ScopeMeasurementRow } from '../scope/measurementRows'

vi.mock('./useProjects', () => ({ useProjects: () => ({ markDirty: () => undefined }) }))

function row(key: string, value: number | null): ScopeMeasurementRow {
  return {
    key,
    id: key.split(':')[0],
    label: key,
    sourceLabel: 'Left',
    title: '',
    value,
    text: value === null ? '—' : String(value),
    format: (v) => String(v),
  }
}

describe('useScopeMeasurements', () => {
  beforeEach(() => {
    const m = useScopeMeasurements()
    m.applyLoaded(null, false)
    m.resetStats()
  })

  it('counts a record per call and reads the figures back', () => {
    const m = useScopeMeasurements()
    m.accumulate([row('pkpk:ch0', 1)], 'sig')
    m.accumulate([row('pkpk:ch0', 3)], 'sig')

    const s = m.stat('pkpk:ch0')!
    expect(s.count).toBe(2)
    expect(s.mean).toBe(2)
    expect(s.min).toBe(1)
    expect(s.max).toBe(3)
    expect(m.statCount.value).toBe(2)
  })

  it('starts again when the signature moves, because the records stopped being comparable', () => {
    const m = useScopeMeasurements()
    m.accumulate([row('pkpk:ch0', 1)], 'sig')
    m.accumulate([row('pkpk:ch0', 2)], 'sig')
    expect(m.stat('pkpk:ch0')!.count).toBe(2)

    m.accumulate([row('pkpk:ch0', 9)], 'other')
    const s = m.stat('pkpk:ch0')!
    expect(s.count).toBe(1)
    expect(s.mean).toBe(9)
  })

  it('keeps a row that is briefly not drawn, so putting it back resumes the count', () => {
    // A channel taken off the chart, or a measurement unticked and ticked again: those are display
    // choices and do not make what was already counted wrong.
    const m = useScopeMeasurements()
    m.accumulate([row('pkpk:ch0', 1), row('rms:ch0', 5)], 'sig')
    m.accumulate([row('pkpk:ch0', 3)], 'sig')
    m.accumulate([row('pkpk:ch0', 5), row('rms:ch0', 7)], 'sig')

    expect(m.stat('pkpk:ch0')!.count).toBe(3)
    expect(m.stat('rms:ch0')!.count).toBe(2)
  })

  it('does not count a reading the record did not have', () => {
    const m = useScopeMeasurements()
    m.accumulate([row('freq:ch1', null)], 'sig')
    expect(m.stat('freq:ch1')).toBeNull()
    m.accumulate([row('freq:ch1', 1000)], 'sig')
    expect(m.stat('freq:ch1')!.count).toBe(1)
  })

  it('does not reset the statistics when a stored display is re-applied', () => {
    // THE regression: the autosave rewrites params_json, the screen reads it back, and resetting
    // here threw the count away every time a display control was touched -- measured, 29 records to
    // zero on one tick of the Residual box. What the statistics belong to is the measurement, and
    // the switch between measurements is what clears them.
    const m = useScopeMeasurements()
    m.accumulate([row('pkpk:ch0', 1)], 'sig')
    m.accumulate([row('pkpk:ch0', 2)], 'sig')

    m.applyLoaded(['pkpk', 'rms'], true)

    expect(m.stat('pkpk:ch0')!.count).toBe(2)
  })

  it('opens on this build default when the measurement states no selection', () => {
    const m = useScopeMeasurements()
    m.applyLoaded(undefined, undefined)
    expect(m.selected.value).toEqual([...DEFAULT_SCOPE_MEASUREMENTS])
    expect(m.statistics.value).toBe(false)
  })

  it('keeps an explicitly empty selection empty', () => {
    const m = useScopeMeasurements()
    m.applyLoaded([], false)
    expect(m.selected.value).toEqual([])
  })

  it('asks for the X–Y figure only while a Channel delay reading is chosen', () => {
    const m = useScopeMeasurements()
    m.setSelected(['pkpk'])
    expect(m.needsPair.value).toBe(false)
    m.setSelected(['pkpk', 'phase'])
    expect(m.needsPair.value).toBe(true)
  })
})

describe('the screen wires the statistics up', () => {
  // App.vue is Vue against a real DOM and the suite runs on environment: 'node', so what is pinned
  // is the shape of the wiring -- which is where both of this feature's own faults were.
  const app = readFileSync(resolve(import.meta.dirname, '..', 'App.vue'), 'utf8')

  it('throws them away on a measurement switch and nowhere else', () => {
    expect(app).toContain('watch(currentTypeId, () => {')
    expect(app).toContain('scopeMeasurementSelection.resetStats()')
  })

  it('counts a capture once however often it is re-fetched', () => {
    // The record object is replaced by every re-fetch, not only by a new capture: ticking Residual,
    // turning the math channel on and moving the cursors all ask for the same record again.
    expect(app).toContain('if (scopeRecordsTaken.value === scopeRecordsCounted) return')
  })

  it('signs the statistics with the window that was asked for, not the record’s own span', () => {
    // fromS is measured from the interpolated trigger, so it moves a picosecond every capture --
    // enough to clear the count on every record and leave it sitting at 1 for ever.
    expect(app).toContain('scopeFetchedWindow.value,')
  })
})
