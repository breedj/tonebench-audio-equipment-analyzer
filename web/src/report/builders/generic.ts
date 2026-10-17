import type { ReportRow, ReportSection, SweepLevelUnit } from '../../api/client'
import { levelUnitLabel } from '../../measurements/levelMode'
import type { ReportSubject } from '../types'

/**
 * The two elements every kind offers. A kind with its own builder replaces the settings section with
 * labelled rows; the generic one lists whatever the as-run `params_json` holds, which is at least
 * honest about what ran.
 */

export function descriptionSections(subject: ReportSubject): ReportSection[] {
  const sections: ReportSection[] = []
  if (subject.resultNotes?.trim()) {
    sections.push({ kind: 'paragraph', title: 'Notes on this result', text: subject.resultNotes.trim() })
  }
  if (subject.typeNotes?.trim()) {
    sections.push({ kind: 'paragraph', title: 'Notes on the measurement', text: subject.typeNotes.trim() })
  }
  if (sections.length === 0) {
    sections.push({ kind: 'paragraph', title: 'Description', text: 'No notes.' })
  }
  return sections
}

/**
 * The words a key is spelled with that a reader would get wrong from the key alone: the units, and
 * the acronyms this app's own vocabulary is full of. `humanizeKey` lowercases every word before
 * looking it up, so this table is the whole of what stops `dutGainDb` reading as "Dut gain db".
 *
 * <p>Keyed on a whole word, never a substring — "dbv" is a unit and "dbvolts" would not be — and
 * deliberately short: a word nobody listed is simply left as it is, which is what the naive
 * spelling already did.</p>
 */
const KEY_WORDS: Record<string, string> = {
  db: 'dB',
  dbv: 'dBV',
  dbfs: 'dBFS',
  dbc: 'dBc',
  dbr: 'dBr',
  hz: 'Hz',
  khz: 'kHz',
  ms: 'ms',
  us: 'µs',
  dut: 'DUT',
  fft: 'FFT',
  imd: 'IMD',
  thd: 'THD',
  rms: 'RMS',
  snr: 'SNR',
  csv: 'CSV',
  pcm: 'PCM',
  id: 'ID',
}

/** "frequencySpanHz" → "Frequency span Hz": readable enough for a kind nobody has labelled yet. */
export function humanizeKey(key: string): string {
  const words = key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/_/g, ' ')
    .toLowerCase()
    .split(' ')
    .filter((word) => word.length > 0)
  if (words.length === 0) return ''

  const spelled = words.map((word) => KEY_WORDS[word] ?? word)
  // The first word is capitalised only when the table has not already spelled it: "DUT gain dB",
  // not "DUT Gain dB", and not "DUt gain dB" either.
  if (!(words[0] in KEY_WORDS)) spelled[0] = spelled[0].charAt(0).toUpperCase() + spelled[0].slice(1)
  return spelled.join(' ')
}

/**
 * The suffix a key's own name promises as a unit, and which words of the label it therefore replaces.
 *
 * <p>Only ohms so far, and it earns its place: `loadOhms` read "Load ohms: 8", a label wearing a
 * unit the value then had to be read without. The label drops the word and the value carries the
 * symbol, which is how every hand-written row in the other builders already prints a load.</p>
 */
const KEY_SUFFIX_UNITS: { suffix: string; unit: string }[] = [{ suffix: 'Ohms', unit: 'Ω' }]

/** A key's label and the unit its value is to be suffixed with, from the key alone. */
function labelAndUnit(key: string): { label: string; unit: string | null } {
  for (const { suffix, unit } of KEY_SUFFIX_UNITS) {
    if (key.length > suffix.length && key.endsWith(suffix)) {
      return { label: humanizeKey(key.slice(0, -suffix.length)), unit }
    }
  }
  return { label: humanizeKey(key), unit: null }
}

export function formatScalar(value: unknown): string | null {
  if (value === null || value === undefined) return null
  if (typeof value === 'boolean') return value ? 'Yes' : 'No'
  if (typeof value === 'number') return Number.isInteger(value) ? String(value) : value.toPrecision(6).replace(/\.?0+$/, '')
  if (typeof value === 'string') return value
  return null
}

/**
 * Every scalar in the as-run settings except the chart's own (`chart*`), which describe the screen
 * rather than the run. Nested objects are skipped: nothing a report reader would want is nested.
 */
export function genericSettingsRows(paramsJson: string): ReportRow[] {
  let parsed: unknown
  try {
    parsed = JSON.parse(paramsJson)
  } catch {
    return []
  }
  if (!parsed || typeof parsed !== 'object') return []

  const rows: ReportRow[] = []
  for (const [key, value] of Object.entries(parsed as Record<string, unknown>)) {
    if (key.startsWith('chart')) continue
    const text = formatValue(key, value)
    if (text === null) continue
    const { label, unit } = labelAndUnit(key)
    rows.push({ label, value: unit === null ? text : `${text} ${unit}` })
  }
  return rows
}

/**
 * A setting's value as a reader expects it rather than as it is stored. One rule: a key naming a
 * *unit* holds an enum name (`Dbv`, `MilliWatt`), and the dropdown that wrote it spells that name
 * `dBV` and `mW` — so the report quotes it the way the panel does. `levelUnitLabel` is that
 * dropdown's own table and hands back anything it does not recognise unchanged, which is what makes
 * it safe against a params_json another build wrote.
 */
function formatValue(key: string, value: unknown): string | null {
  if (key.endsWith('Unit') && typeof value === 'string') return levelUnitLabel(value as SweepLevelUnit)
  return formatScalar(value)
}

/**
 * The groups the as-run settings are shown in, and the one word that puts a row in each.
 *
 * A measurement's `params_json` runs to thirty-odd keys, and thirty rows in one column is a list
 * nobody reads (the 2026-10-13 design draws four boxes instead). The grouping is by keyword rather
 * than by an exhaustive table of key names, because the keys differ per kind and a table would have
 * to be written again for every one of them — and a keyword nobody matched is not a lost row:
 * everything left over goes in the last group, which is what `groupSettingsRows` is tested on.
 */
const SETTING_GROUPS: { name: string; match: RegExp }[] = [
  { name: 'Acquisition', match: /sample rate|fft|window|averag|block|channel|resolution|span|sweep|steps|points|duration/i },
  { name: 'Analysis', match: /band|harmonic|fundamental|notch|peak|weight|smooth|method|reference|gate|limit/i },
  { name: 'Levels & generator', match: /level|range|gain|amplitude|volt|dbv|generator|load|impedance|stimulus|tone|freq/i },
]

/** The last group, which is what makes the grouping lossless. */
const OTHER_GROUP = 'Other settings'

/**
 * Every row in exactly one named group, in the groups' own order. Exported for the test: the
 * property that matters is not which box a row lands in but that every row lands in one, since a
 * settings list that quietly drops a row is a report that lies about what ran.
 */
export function groupSettingsRows(rows: readonly ReportRow[]): { name: string; rows: ReportRow[] }[] {
  const named = new Map<string, ReportRow[]>()
  for (const row of rows) {
    const group = SETTING_GROUPS.find((g) => g.match.test(row.label))?.name ?? OTHER_GROUP
    const bucket = named.get(group)
    if (bucket) bucket.push(row)
    else named.set(group, [row])
  }

  const order = [...SETTING_GROUPS.map((g) => g.name), OTHER_GROUP]
  return order.filter((name) => named.has(name)).map((name) => ({ name, rows: named.get(name)! }))
}

export function genericSettingsSection(paramsJson: string): ReportSection {
  const rows = genericSettingsRows(paramsJson)
  if (rows.length === 0) return { kind: 'paragraph', title: 'Settings', text: 'No settings were recorded.' }
  // Short enough to read down a column stays one; past that the boxes earn their keep.
  if (rows.length <= 6) return { kind: 'keyValues', title: 'Settings', rows }
  return { kind: 'groups', title: 'Settings', groups: groupSettingsRows(rows) }
}
