import { describe, expect, it } from 'vitest'
import {
  formatPeakSort,
  nextPeakSort,
  parsePeakSort,
  peakSortIndicator,
  sortPeakRows,
  type PeakSortableRow,
} from './peakSort'

/** A harmonics table's rows: the fundamental, then H2..H4, in the order the meters frame gives them. */
const HARMONICS: PeakSortableRow[] = [
  { freqHz: 1000, dbv: -6, dbc: 0 },
  { freqHz: 2000, dbv: -86, dbc: -80 },
  { freqHz: 3000, dbv: -66, dbc: -60 },
  { freqHz: 4000, dbv: -96, dbc: -90 },
]

describe('nextPeakSort', () => {
  it('starts frequency ascending and the two level columns descending', () => {
    expect(nextPeakSort(null, 'frequency')).toEqual({ column: 'frequency', direction: 'asc' })
    expect(nextPeakSort(null, 'level')).toEqual({ column: 'level', direction: 'desc' })
    expect(nextPeakSort(null, 'dbc')).toEqual({ column: 'dbc', direction: 'desc' })
  })

  it('flips the active column, then returns to the natural order on the third click', () => {
    const first = nextPeakSort(null, 'level')
    const second = nextPeakSort(first, 'level')
    expect(second).toEqual({ column: 'level', direction: 'asc' })
    expect(nextPeakSort(second, 'level')).toBeNull()
  })

  it('starts a different column over rather than carrying the old direction onto it', () => {
    const level = nextPeakSort(nextPeakSort(null, 'level'), 'level') // level ascending
    expect(nextPeakSort(level, 'dbc')).toEqual({ column: 'dbc', direction: 'desc' })
  })
})

describe('the stored form', () => {
  it('round-trips every state, null included', () => {
    for (const column of ['frequency', 'level', 'dbc'] as const) {
      for (const direction of ['asc', 'desc'] as const) {
        expect(parsePeakSort(formatPeakSort({ column, direction }))).toEqual({ column, direction })
      }
    }
    expect(formatPeakSort(null)).toBeNull()
    expect(parsePeakSort(null)).toBeNull()
  })

  // A params_json an older or newer build wrote must land on the natural order, not throw on the
  // way into a measurement the user has only just selected.
  it('reads anything unrecognised as no sort', () => {
    expect(parsePeakSort('')).toBeNull()
    expect(parsePeakSort('order-asc')).toBeNull()
    expect(parsePeakSort('level-sideways')).toBeNull()
    expect(parsePeakSort('level')).toBeNull()
    expect(parsePeakSort('-asc')).toBeNull()
  })
})

describe('sortPeakRows', () => {
  it('leaves the natural order alone, as a copy', () => {
    const sorted = sortPeakRows(HARMONICS, null)
    expect(sorted).toEqual(HARMONICS)
    expect(sorted).not.toBe(HARMONICS)
  })

  it('sorts by each column in both directions', () => {
    expect(sortPeakRows(HARMONICS, { column: 'level', direction: 'desc' }).map((r) => r.dbv)).toEqual([
      -6, -66, -86, -96,
    ])
    expect(sortPeakRows(HARMONICS, { column: 'level', direction: 'asc' }).map((r) => r.dbv)).toEqual([
      -96, -86, -66, -6,
    ])
    expect(sortPeakRows(HARMONICS, { column: 'frequency', direction: 'desc' }).map((r) => r.freqHz)).toEqual([
      4000, 3000, 2000, 1000,
    ])
    expect(sortPeakRows(HARMONICS, { column: 'dbc', direction: 'desc' }).map((r) => r.dbc)).toEqual([
      0, -60, -80, -90,
    ])
  })

  // A flat noise floor hands this several rows of the same level twenty times a second. An unstable
  // sort would reshuffle them frame to frame, which reads as a table that will not sit still.
  it('is stable, so ties keep the order they arrived in', () => {
    const tied: PeakSortableRow[] = [
      { freqHz: 300, dbv: -120, dbc: -114 },
      { freqHz: 100, dbv: -120, dbc: -114 },
      { freqHz: 200, dbv: -120, dbc: -114 },
    ]
    expect(sortPeakRows(tied, { column: 'level', direction: 'desc' }).map((r) => r.freqHz)).toEqual([300, 100, 200])
  })

  // A harmonic the analyzer could not measure reads -Infinity. Compared normally it would sit above
  // every real reading in an ascending sort, which is the opposite of what it means.
  it('puts a value it could not measure last in either direction', () => {
    const withGap: PeakSortableRow[] = [
      { freqHz: 2000, dbv: Number.NEGATIVE_INFINITY, dbc: Number.NEGATIVE_INFINITY },
      { freqHz: 1000, dbv: -6, dbc: 0 },
      { freqHz: 3000, dbv: -66, dbc: -60 },
    ]
    expect(sortPeakRows(withGap, { column: 'level', direction: 'asc' }).map((r) => r.freqHz)).toEqual([3000, 1000, 2000])
    expect(sortPeakRows(withGap, { column: 'level', direction: 'desc' }).map((r) => r.freqHz)).toEqual([1000, 3000, 2000])
  })
})

describe('peakSortIndicator', () => {
  it('marks only the active column', () => {
    expect(peakSortIndicator({ column: 'level', direction: 'asc' }, 'level')).toBe('▲')
    expect(peakSortIndicator({ column: 'level', direction: 'desc' }, 'level')).toBe('▼')
    expect(peakSortIndicator({ column: 'level', direction: 'desc' }, 'dbc')).toBeNull()
    expect(peakSortIndicator(null, 'level')).toBeNull()
  })
})
