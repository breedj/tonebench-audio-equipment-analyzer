import type { ImpedanceTrace } from '../composables/useImpedance'
import { formatHz } from '../response/format'
import type { DutType } from './component'
import { formatCurrent, formatOhms } from './equivalent'
import {
  componentReading,
  equivalentAt,
  equivalentRows,
  impedanceReadings,
  type ComponentRow,
} from './readings'

/**
 * The readout panel's three blocks for *several* curves at once — the measurement on screen and
 * every checked history item — transposed so each curve is a row and each reading a column.
 *
 * <p>Nothing is stored for this and nothing needs to be: the peak, the component fit and the
 * equivalent circuit are all functions of a curve, and a stored result keeps its curve. So a history
 * item is read exactly the way the live measurement is, through `readings.ts` — the same functions
 * the readout panel and the report's impedance chapter call — which is the whole reason the answers
 * cannot drift apart. The only thing a stored result needs beyond its curve is its reference
 * resistor and generator level, and those are in its own `params_json`.</p>
 *
 * <p>Pure, and tested, because nothing in a `.vue` is reachable under vitest's node environment.</p>
 */

/** One curve the comparison covers: the live measurement, or a checked history result. */
export interface ComparisonSubject {
  /** `live`, or `stored-<resultId>` — a stable key for the table's rows. */
  id: string
  /** What the row is called: a result's own name, or "Current measurement" for the live one. */
  name: string
  trace: ImpedanceTrace
  /** The series resistor this curve was measured through — its own, which need not be the one on screen now. */
  referenceOhms: number
  /** The generator level it ran at, for the drive current; null when its params_json does not say. */
  generatorAmpDbv: number | null
}

/**
 * What a column header explains, hovered.
 *
 * <p>Carried over from the three side panels these tables replaced (2026-10-22): each of their rows
 * had its label's meaning on a `title`, and a table of bare symbols — ESR, ESL, DCR, Qms — is a
 * table only someone who already knows them can read. Keyed by label, which is why **SRF is worded
 * for both components**: the capacitor panel said "crosses from capacitive to inductive" and the
 * inductor panel the reverse, and one header has to serve whichever fit is showing.</p>
 */
const COLUMN_TITLES: Readonly<Record<string, string>> = {
  'Minimum |Z|': 'The worst-case load: where the DUT draws the most current from whatever is driving it.',
  'Drive current': "Roughly what the DUT was driven with. Approximate: a sweep's current varies with Z across the band.",
  C: 'Fitted across every sample that is clearly capacitive and below self-resonance.',
  L: 'Fitted across every sample that is clearly inductive and below self-resonance.',
  SRF: 'Self-resonant frequency: where the phase crosses between capacitive and inductive.',
  ESR: '|Z| at self-resonance, where the reactances cancel and only the loss is left. The one place ESR is honestly measurable.',
  ESL: 'Equivalent series inductance, fitted above self-resonance with the capacitive term removed.',
  D: 'tan delta = R/|X| in the capacitive band.',
  DCR: 'Winding resistance: the real part where |Z| is lowest.',
  'Z at SRF': "|Z| at self-resonance. For an inductor this is a peak, where the winding's own capacitance parallels it.",
  Q: "|X|/R over the inductance fit's band.",
  fs: 'Resonance frequency.',
  Re: "Voice-coil resistance, read as the minimum |Z| above resonance. Close to, but not the same as, a DC ohmmeter's answer.",
  'f1 / f2':
    'The frequencies either side of fs where |Z| falls to the geometric mean of Zmax and Re. The width between them is what sets Q.',
}

/** One column: what it is called, and what that name means. */
export interface ComparisonColumn {
  label: string
  /** The hover text, where the label is a symbol rather than a word. */
  title?: string
}

/** A column from its label alone, picking up whatever {@link COLUMN_TITLES} explains about it. */
function column(label: string): ComparisonColumn {
  const title = COLUMN_TITLES[label]
  return title ? { label, title } : { label }
}

/** A row of one comparison table: the subject it describes, and one cell per column. */
export interface ComparisonRow {
  id: string
  name: string
  /** One per column of the table, in the same order; a reading this subject has no answer for is an em dash. */
  cells: string[]
}

/** One transposed table: the column headers (the reading labels), a row per subject, and any per-subject notes. */
export interface ComparisonTable {
  title: string
  columns: ComparisonColumn[]
  rows: ComparisonRow[]
  /** What `componentReading` said about a subject whose fit it withheld — a ported box, say. Empty for the other tables. */
  notes: { id: string; name: string; text: string }[]
}

const DASH = '—'

/** The Measurements block, one row per subject. Every column is defined for every curve that measured at all. */
export function measurementsComparison(subjects: readonly ComparisonSubject[]): ComparisonTable {
  // Written out rather than mapped through `column`, because the two "at" headers mean different
  // things -- the first is the peak's frequency, which on a driver is its resonance, and the second
  // is the minimum's -- and a label-keyed lookup would give them the same note.
  const columns: ComparisonColumn[] = [
    column('Peak |Z|'),
    { label: 'at', title: 'For a driver this is its resonance.' },
    column('Minimum |Z|'),
    { label: 'at' },
    column('Median |Z|'),
    column('Drive current'),
    column('Reference R'),
  ]

  const rows = subjects.map((subject) => {
    const readings = impedanceReadings(subject.trace, subject.referenceOhms, subject.generatorAmpDbv)
    const cells = readings
      ? [
          formatOhms(readings.peak.ohms),
          formatHz(readings.peak.freqHz),
          formatOhms(readings.min.ohms),
          formatHz(readings.min.freqHz),
          formatOhms(readings.medianOhms),
          formatCurrent(readings.driveCurrentAmps),
          formatOhms(subject.referenceOhms),
        ]
      : columns.map(() => DASH)
    return { id: subject.id, name: subject.name, cells }
  })

  return { title: 'Measurements', columns, rows, notes: [] }
}

/**
 * The component fit, one row per subject, or null when the type says the fixture holds nothing
 * nameable (`None`) — there is no fit to compare then, and an empty table says less than no table.
 *
 * <p>The columns are the union of the row labels the subjects produce, in the order they first
 * appear, because a fit is not always complete: a capacitor whose sweep never reaches self-resonance
 * has no ESR row. A subject whose fit was withheld entirely — the ported-loudspeaker case — gets a
 * row of dashes and its note, rather than being dropped, so the reader can see that it *was*
 * measured and why nothing is quoted.</p>
 */
export function componentComparison(subjects: readonly ComparisonSubject[], dutType: DutType): ComparisonTable | null {
  if (dutType === 'None') return null

  const readings = subjects.map((subject) => ({ subject, reading: componentReading(subject.trace, dutType) }))
  if (readings.every((r) => r.reading === null)) return null

  const labels: string[] = []
  for (const { reading } of readings) {
    for (const row of reading?.rows ?? []) if (!labels.includes(row.label)) labels.push(row.label)
  }
  const columns = labels.map(column)

  const label = readings.find((r) => r.reading !== null)?.reading?.label ?? 'Component'
  const notes: ComparisonTable['notes'] = []

  const rows = readings.map(({ subject, reading }) => {
    const byLabel = new Map<string, ComponentRow>((reading?.rows ?? []).map((row) => [row.label, row]))
    for (const note of reading?.notes ?? []) notes.push({ id: subject.id, name: subject.name, text: note })
    return {
      id: subject.id,
      name: subject.name,
      cells: labels.map((label) => byLabel.get(label)?.value ?? DASH),
    }
  })

  return { title: label, columns, rows, notes }
}

/**
 * The equivalent circuit at one frequency, one row per subject.
 *
 * <p>The column that names the reactive component is fixed here — "Series L / C" — where the panel
 * and the report name it after the sign of X. One header has to serve every row, and two curves at
 * the same frequency can legitimately be one inductive and one capacitive; each row's own value
 * still carries its unit, so nothing is ambiguous.</p>
 */
export function equivalentComparison(subjects: readonly ComparisonSubject[], cursorHz: number): ComparisonTable {
  // Taken from the row list rather than written out again, so a row added to the block appears here.
  const template = equivalentRows(null)
  const columns = template.map((row, i) => column(i === 5 ? 'Series L / C' : row.label))

  const rows = subjects.map((subject) => ({
    id: subject.id,
    name: subject.name,
    cells: equivalentRows(equivalentAt(subject.trace, cursorHz)).map((row) => row.value),
  }))

  return { title: `Equivalent circuit at ${formatHz(cursorHz)}`, columns, rows, notes: [] }
}
