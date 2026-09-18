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

export function genericSettingsSection(paramsJson: string): ReportSection {
  const rows = genericSettingsRows(paramsJson)
  return rows.length > 0
    ? { kind: 'keyValues', title: 'Settings', rows }
    : { kind: 'paragraph', title: 'Settings', text: 'No settings were recorded.' }
}
