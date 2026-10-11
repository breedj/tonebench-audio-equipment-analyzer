/**
 * Column sorting for the FFT screen's two peak tables — Harmonics and Other peaks.
 *
 * <p>Pure, and in its own module rather than in either table, for the usual two reasons: the two
 * tables have to agree about what "sorted by level" means (they are read side by side, and one of
 * them is a filtered view of the other's complement), and nothing in a `.vue` file is testable
 * under the node environment vitest runs in.</p>
 *
 * <p>A sort is stored with the measurement (`FftSpectrumParameters.ChartHarmonicsSort` /
 * `ChartOtherPeaksSort`), so the wire form is a string and <b>null means the table's own natural
 * order</b> — order 1, 2, 3 … for the harmonics, strongest-first for the peaks. That is not the
 * same as "sorted ascending by anything": the harmonics' natural order is the one that makes H2
 * sit under the fundamental, and the peaks' is the server's own ranking (PeakFinder.cs), which is
 * what the `#` column numbers. Both are worth being able to get back to, which is why
 * {@link nextPeakSort} cycles through null rather than only flipping direction.</p>
 */

/** The three sortable columns. The leading Order/# column is the natural order itself, not a sort. */
export type PeakSortColumn = 'frequency' | 'level' | 'dbc'

export interface PeakSort {
  column: PeakSortColumn
  direction: 'asc' | 'desc'
}

/**
 * Which way a column sorts on its *first* click. Frequency ascends — low to high is how a spectrum
 * is read — while the two level columns descend, because "what is the biggest thing in here" is the
 * only question either of them is ever asked first.
 */
const FIRST_DIRECTION: Record<PeakSortColumn, 'asc' | 'desc'> = {
  frequency: 'asc',
  level: 'desc',
  dbc: 'desc',
}

/**
 * The sort a click on `column` produces, given the one in force.
 *
 * Clicking a fresh column starts at that column's own first direction; clicking the active one
 * flips it, and clicking it once more returns to the natural order (null). Three states rather than
 * two because the natural order is a real reading of both tables and a toggle would make it
 * unreachable once anything had been clicked.
 */
export function nextPeakSort(current: PeakSort | null, column: PeakSortColumn): PeakSort | null {
  if (current?.column !== column) return { column, direction: FIRST_DIRECTION[column] }
  if (current.direction === FIRST_DIRECTION[column]) {
    return { column, direction: current.direction === 'asc' ? 'desc' : 'asc' }
  }
  return null
}

/** The stored form: `"level-desc"`. Null (no sort) is stored as null, not as a sentinel string. */
export function formatPeakSort(sort: PeakSort | null): string | null {
  return sort ? `${sort.column}-${sort.direction}` : null
}

/**
 * The stored form back, or null — which is also what anything unrecognised becomes. A params_json
 * written by a build that spelled a column differently must fall back to the natural order rather
 * than throwing on the way into a measurement the user has just selected.
 */
export function parsePeakSort(stored: string | null | undefined): PeakSort | null {
  if (!stored) return null

  const dash = stored.lastIndexOf('-')
  if (dash <= 0) return null

  const column = stored.slice(0, dash)
  const direction = stored.slice(dash + 1)
  if (column !== 'frequency' && column !== 'level' && column !== 'dbc') return null
  if (direction !== 'asc' && direction !== 'desc') return null

  return { column, direction }
}

/** What both tables' rows carry, and all this needs of them. */
export interface PeakSortableRow {
  freqHz: number
  /** The absolute level, dBV. */
  dbv: number
  /** Relative to the fundamental, dB. Zero on the fundamental's own row, by definition. */
  dbc: number
}

function valueOf(row: PeakSortableRow, column: PeakSortColumn): number {
  switch (column) {
    case 'frequency':
      return row.freqHz
    case 'level':
      return row.dbv
    case 'dbc':
      return row.dbc
  }
}

/**
 * `rows` in `sort`'s order, as a new array; the input's order unchanged when there is no sort.
 *
 * <p>Two details. The sort is <b>stable</b> (ES2019 onwards guarantees it), so rows that tie — two
 * peaks at the same level, which a flat noise floor produces readily — keep their natural order
 * rather than shuffling between frames of a live stream. And a non-finite value sorts <b>last in
 * either direction</b>: a harmonic the analyzer could not measure reads −∞ dBc, and letting that
 * compare normally would park it at the top of every ascending sort, above every real reading.</p>
 */
export function sortPeakRows<T extends PeakSortableRow>(rows: readonly T[], sort: PeakSort | null): T[] {
  if (!sort) return rows.slice()

  const sign = sort.direction === 'asc' ? 1 : -1
  return rows.slice().sort((a, b) => {
    const av = valueOf(a, sort.column)
    const bv = valueOf(b, sort.column)
    const aOk = Number.isFinite(av)
    const bOk = Number.isFinite(bv)
    if (!aOk || !bOk) return aOk ? -1 : bOk ? 1 : 0
    return sign * (av - bv)
  })
}

/** The arrow a header shows: the active column's direction, or nothing. */
export function peakSortIndicator(sort: PeakSort | null, column: PeakSortColumn): '▲' | '▼' | null {
  if (sort?.column !== column) return null
  return sort.direction === 'asc' ? '▲' : '▼'
}
