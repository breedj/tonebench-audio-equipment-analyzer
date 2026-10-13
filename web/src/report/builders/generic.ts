import type { ReportRow, ReportSection } from '../../api/client'
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

/** "frequencySpanHz" → "Frequency span hz": readable enough for a kind nobody has labelled yet. */
export function humanizeKey(key: string): string {
  const spaced = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/_/g, ' ').toLowerCase()
  return spaced.charAt(0).toUpperCase() + spaced.slice(1)
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
    const text = formatScalar(value)
    if (text === null) continue
    rows.push({ label: humanizeKey(key), value: text })
  }
  return rows
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
