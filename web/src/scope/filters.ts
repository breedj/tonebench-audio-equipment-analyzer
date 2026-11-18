/**
 * The oscilloscope's display path — the coupling and the in-path filter.
 *
 * <p>Both run on the analyzer, over the whole record, before anything is drawn or measured. That is
 * what keeps the picture and the figures beside it describing the same trace: a filter applied on
 * the client would change what is drawn and leave every reading describing something else.</p>
 *
 * <p>Here: the tables, the words, and the preview's geometry — the half nothing in a
 * <code>.vue</code> could be tested on. The filter's own response is <em>not</em> here: it is asked
 * of the analyzer, because two implementations of one filter drift and a preview that is not the
 * filter is worse than no preview.</p>
 */

export type ScopeCoupling = 'Dc' | 'Ac'

export type ScopeFilterKind = 'None' | 'LowPass' | 'HighPass' | 'BandPass' | 'Notch'

export interface ScopeFilterSettings {
  coupling: ScopeCoupling
  kind: ScopeFilterKind
  frequencyHz: number
  q: number
  order: number
}

export const DEFAULT_SCOPE_FILTER: Readonly<ScopeFilterSettings> = Object.freeze({
  coupling: 'Dc',
  kind: 'None',
  frequencyHz: 1_000,
  q: 0.707,
  order: 2,
})

export const SCOPE_COUPLINGS: readonly { value: ScopeCoupling; label: string; title: string }[] =
  Object.freeze([
    {
      value: 'Dc',
      label: 'DC',
      title: 'The trace as the analyzer captured it, standing offset and all.',
    },
    {
      value: 'Ac',
      label: 'AC',
      title:
        'The record’s own mean subtracted. On this hardware that is exactly the right operation: both the QA403 and a sound card already block DC, so what is left is the front end’s own standing offset — a constant, which a mean removes exactly and a high-pass would only approximate while ringing at the start of the record.',
    },
  ])

export const SCOPE_FILTER_KINDS: readonly { value: ScopeFilterKind; label: string; title: string }[] =
  Object.freeze([
    { value: 'None', label: 'Off', title: 'No filter. The trace is the record.' },
    {
      value: 'LowPass',
      label: 'Low-pass',
      title:
        'Keeps what is below the corner — a bandwidth limit, which is a bench scope’s “20 MHz” button. Real noise reduction on a quiet DUT, at the price of a slower edge.',
    },
    {
      value: 'HighPass',
      label: 'High-pass',
      title:
        'Keeps what is above it. For a drift or a rumble inside the record, which the coupling’s mean removal cannot reach.',
    },
    {
      value: 'BandPass',
      label: 'Band-pass',
      title: 'Keeps a band around the centre, at the chosen Q — for looking at one component of a busy trace.',
    },
    {
      value: 'Notch',
      label: 'Notch',
      title: 'Removes one. What mains hum is taken out with, at a high Q so the rest of the trace is untouched.',
    },
  ])

/** The orders offered, as a Butterworth before the zero-phase pass doubles it. */
export const SCOPE_FILTER_ORDERS: readonly number[] = Object.freeze([2, 4, 6, 8])

export const MIN_FILTER_Q = 0.1
export const MAX_FILTER_Q = 50

/** Whether the kind reads the Q box — only the two that name a band. */
export function filterUsesQ(kind: ScopeFilterKind): boolean {
  return kind === 'BandPass' || kind === 'Notch'
}

/** Whether it reads the order — only the two whose shape is a skirt. */
export function filterUsesOrder(kind: ScopeFilterKind): boolean {
  return kind === 'LowPass' || kind === 'HighPass'
}

/** Whether anything in the path changes the trace. */
export function pathChangesTrace(settings: ScopeFilterSettings): boolean {
  return settings.coupling === 'Ac' || (settings.kind !== 'None' && settings.frequencyHz > 0)
}

export function filterDraws(settings: ScopeFilterSettings): boolean {
  return settings.kind !== 'None' && settings.frequencyHz > 0
}

/** A frequency as the summary writes it. */
function hz(value: number): string {
  if (!Number.isFinite(value)) return '—'
  if (value >= 1000) return `${(value / 1000).toFixed(value >= 10_000 ? 1 : 3)} kHz`
  return `${value.toFixed(Number.isInteger(value) ? 0 : 2)} Hz`
}

/**
 * What the path does, in one line — the badge on the chart, the note in the panel, and the row in a
 * report.
 *
 * <p>Said wherever the figures are, because a filter changes every one of them: a low-pass lowers a
 * peak-to-peak and lengthens a rise time, a notch removes a component the RMS was counting. A
 * reading taken through a filter is not wrong, but it is of the filtered trace, and nothing else on
 * screen would say so.</p>
 */
export function pathSummary(settings: ScopeFilterSettings): string | null {
  const parts: string[] = []
  if (settings.coupling === 'Ac') parts.push('AC coupled')
  if (filterDraws(settings)) {
    const kind = SCOPE_FILTER_KINDS.find((k) => k.value === settings.kind)?.label ?? settings.kind
    const shape = filterUsesQ(settings.kind)
      ? `Q ${settings.q.toFixed(2)}`
      : `order ${settings.order} × 2`
    parts.push(`${kind} ${hz(settings.frequencyHz)}, ${shape}`)
  }
  return parts.length > 0 ? parts.join(' · ') : null
}

/** The query the record fetch and the preview both carry, so one spec reaches both. */
export function filterQuery(settings: ScopeFilterSettings): Record<string, string> {
  const query: Record<string, string> = {}
  if (settings.coupling === 'Ac') query.coupling = 'Ac'
  if (filterDraws(settings)) {
    query.filter = settings.kind
    query.filterHz = String(settings.frequencyHz)
    if (filterUsesQ(settings.kind)) query.filterQ = String(settings.q)
    if (filterUsesOrder(settings.kind)) query.filterOrder = String(settings.order)
  }
  return query
}

/**
 * The preview's own geometry: the response curve as an SVG path.
 *
 * <p>Log frequency across, decibels down — the axes a filter response is read on. The curve is the
 * analyzer's; this only places it.</p>
 */
export interface FilterPreviewBox {
  width: number
  height: number
  dbTop: number
  dbBottom: number
}

export const DEFAULT_PREVIEW_BOX: Readonly<FilterPreviewBox> = Object.freeze({
  width: 220,
  height: 72,
  dbTop: 6,
  /**
   * −60 dB, which is deep enough to show a notch as a notch and shallow enough that a skirt still
   * has somewhere to go. The curve is clamped to the box rather than scaled to the data: a preview
   * whose vertical scale moved with the setting would make every filter look the same shape.
   */
  dbBottom: -60,
})

export function filterCurvePath(
  frequenciesHz: readonly number[],
  magnitudesDb: readonly number[],
  box: FilterPreviewBox = DEFAULT_PREVIEW_BOX,
): string {
  const n = Math.min(frequenciesHz.length, magnitudesDb.length)
  if (n < 2) return ''

  const logFrom = Math.log10(Math.max(frequenciesHz[0], 1e-6))
  const logTo = Math.log10(Math.max(frequenciesHz[n - 1], frequenciesHz[0] * 10))
  const span = logTo - logFrom || 1
  const dbSpan = box.dbTop - box.dbBottom || 1

  let d = ''
  for (let i = 0; i < n; i++) {
    const x = ((Math.log10(Math.max(frequenciesHz[i], 1e-6)) - logFrom) / span) * box.width
    const clamped = Math.min(box.dbTop, Math.max(box.dbBottom, magnitudesDb[i]))
    const y = ((box.dbTop - clamped) / dbSpan) * box.height
    d += `${i === 0 ? 'M' : 'L'}${x.toFixed(2)} ${y.toFixed(2)}`
  }
  return d
}

/** Where a frequency sits across the preview, for the corner marker. */
export function filterCurveX(
  freqHz: number,
  frequenciesHz: readonly number[],
  box: FilterPreviewBox = DEFAULT_PREVIEW_BOX,
): number | null {
  if (frequenciesHz.length < 2 || !Number.isFinite(freqHz) || freqHz <= 0) return null
  const logFrom = Math.log10(Math.max(frequenciesHz[0], 1e-6))
  const logTo = Math.log10(Math.max(frequenciesHz[frequenciesHz.length - 1], frequenciesHz[0] * 10))
  const span = logTo - logFrom || 1
  const x = ((Math.log10(freqHz) - logFrom) / span) * box.width
  return x >= 0 && x <= box.width ? x : null
}

/** The decade gridlines the preview draws, as x positions. */
export function filterCurveDecades(
  frequenciesHz: readonly number[],
  box: FilterPreviewBox = DEFAULT_PREVIEW_BOX,
): { x: number; label: string }[] {
  if (frequenciesHz.length < 2) return []
  const from = Math.max(frequenciesHz[0], 1e-6)
  const to = Math.max(frequenciesHz[frequenciesHz.length - 1], from * 10)
  const lines: { x: number; label: string }[] = []
  for (let decade = Math.ceil(Math.log10(from)); decade <= Math.floor(Math.log10(to)); decade++) {
    const f = 10 ** decade
    const x = filterCurveX(f, frequenciesHz, box)
    if (x !== null) lines.push({ x, label: f >= 1000 ? `${f / 1000}k` : String(f) })
  }
  return lines
}
