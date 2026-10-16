/**
 * The notes a user draws on the spectrum chart: a marker at a (frequency, level) point of the trace
 * with a labelled box beside it, or a dashed rule at a frequency labelled at the top of the plot.
 *
 * <p>Pure, and in its own module rather than in SpectrumChart.vue, for the usual two reasons: the
 * chart, the display rail's list, the Edit measurement dialog and the report's table all have to
 * agree about what an annotation is and how it is numbered, and nothing in a `.vue` file is
 * testable under the node environment vitest runs in. The *geometry* is a second module
 * (charts/annotationLayout.ts) because it needs a plot to lay anything out against; everything here
 * is the model and its ordering.</p>
 *
 * <p>Three things in here are decisions rather than mechanics, and each is written out where it
 * applies: the level is stored in dBV whatever the chart's unit is, the label's offset is stored in
 * octaves and dB rather than pixels, and the ordinal that ties a label on the chart to a row in the
 * report is positional rather than stored.</p>
 */

export const ANNOTATION_STYLES = ['point', 'frequency'] as const
export type AnnotationStyle = (typeof ANNOTATION_STYLES)[number]

/** As many as one chart can carry and one report table can be read as a list. */
export const MAX_ANNOTATIONS = 24

/**
 * As long a note as a chart label can carry. A label is drawn on the plot, not wrapped, so a
 * sentence would cover the trace it points at; truncating on the way in is what keeps a
 * hand-edited params_json from drawing a paragraph across the spectrum.
 */
export const MAX_ANNOTATION_TEXT = 80

export interface SpectrumAnnotation {
  /**
   * Unique within its own list and stable across edits, so a drag, a retype and a delete all name
   * the same row. Not the ordinal a reader sees — see {@link numberedAnnotations}.
   */
  id: number
  style: AnnotationStyle
  text: string
  freqHz: number
  /**
   * The level the marker sits at, <b>always dBV</b> — never the value the chart happens to be
   * plotting. The chart's unit is a per-frame constant add (see spectrumUnits.ts), the peak-hold
   * maps already store dBV and apply that offset on the way out, and an annotation stored the same
   * way stays glued to its spot on the trace across a unit switch and a DUT-gain edit. Null for a
   * frequency rule, which has no level.
   */
  levelDbv: number | null
  /** 0 = L, 1 = R; null for a frequency rule, or a point placed where no trace was identified. */
  channel: number | null
  /**
   * Where the label sits relative to its marker — <b>point style only</b>, and in <b>data units</b>
   * rather than pixels: x as a frequency offset in octaves, y in dB.
   *
   * <p>This is the whole answer to the report plot being 1600×900 while the screen plot is 400-700 px
   * tall. Both span the same `xFrom..xTo` and `yBottom..yTop`, so a fixed octave offset is a fixed
   * *fraction of the plot's width* and a fixed dB offset a fixed fraction of its height: a label
   * placed by hand on screen lands in the same place on the picture at any size, at any zoom, and
   * under any unit (a dB *difference* is unit-independent, the unit being a constant add). A pixel
   * offset would have been none of those things.</p>
   *
   * <p>A frequency rule's label is pinned to the top of the plot and stacked out of its neighbours'
   * way, which a manual nudge would fight; the fields stay on the record so switching a style
   * leaves nothing stale behind.</p>
   */
  labelOffsetOctaves: number
  labelOffsetDb: number
}

/** Where a new point label starts: up and to the right, clear of the peak it names. */
export const DEFAULT_POINT_OFFSET = { labelOffsetOctaves: 0.45, labelOffsetDb: 14 } as const

/** An annotation with the ordinal a reader sees on the chart and in the report's table. */
export interface NumberedAnnotation extends SpectrumAnnotation {
  number: number
}

/** The id to give the next annotation added to `list`: one past the highest in it. */
export function nextAnnotationId(list: readonly SpectrumAnnotation[]): number {
  let highest = 0
  for (const a of list) if (Number.isFinite(a.id) && a.id > highest) highest = Math.floor(a.id)
  return highest + 1
}

/**
 * The ordinals, left to right.
 *
 * <p>Positional and derived, never stored. Three reasons, and the third is the one that matters: a
 * stored ordinal reads `1, 2, 4` after a delete, which looks like a rendering fault rather than a
 * deletion; left-to-right is the order a reader scanning the chart expects, so the badge at 50 Hz
 * is 1 and the one at 19 kHz is last; and because the plugin, the panel's list and the report's
 * table all call this one function, the numbers on the picture cannot disagree with the rows on the
 * page — which is the entire point of having them.</p>
 *
 * <p>The id breaks a tie, so two annotations at the same frequency keep a stable order rather than
 * swapping between frames of a live stream.</p>
 */
export function numberedAnnotations(list: readonly SpectrumAnnotation[]): NumberedAnnotation[] {
  return list
    .slice()
    .sort((a, b) => a.freqHz - b.freqHz || a.id - b.id)
    .map((annotation, index) => ({ ...annotation, number: index + 1 }))
}

/**
 * `list` plus one annotation, as a <b>new array</b>.
 *
 * <p>Every mutator here returns a new array and never touches the one it was given. That is
 * load-bearing rather than tidy: the live list is a `ref` watched by useChartDisplay's dirty
 * watcher, which fires on replacement and not on a `push`, so an in-place add would leave the
 * measurement looking saved while carrying an unsaved note. Pinned by a test.</p>
 */
export function addAnnotation(
  list: readonly SpectrumAnnotation[],
  draft: Omit<SpectrumAnnotation, 'id'>,
): SpectrumAnnotation[] {
  if (list.length >= MAX_ANNOTATIONS) return list.slice()
  return [...list, { ...draft, id: nextAnnotationId(list), text: trimText(draft.text) }]
}

/** `list` with one annotation's fields changed, as a new array; unchanged for an id nothing has. */
export function updateAnnotation(
  list: readonly SpectrumAnnotation[],
  id: number,
  patch: Partial<Omit<SpectrumAnnotation, 'id'>>,
): SpectrumAnnotation[] {
  return list.map((annotation) => {
    if (annotation.id !== id) return annotation
    const next = { ...annotation, ...patch }
    return { ...next, text: trimText(next.text) }
  })
}

/** `list` without that annotation, as a new array. */
export function removeAnnotation(list: readonly SpectrumAnnotation[], id: number): SpectrumAnnotation[] {
  return list.filter((annotation) => annotation.id !== id)
}

function trimText(text: string): string {
  return text.slice(0, MAX_ANNOTATION_TEXT)
}

/**
 * A stored value as a finite number, or null.
 *
 * <p>Only a number or a string is even considered, which is not fussiness: `Number(null)` is 0 and
 * `Number(true)` is 1, so a looser reading turns a null channel into "Left" and a null level into
 * 0 dBV — a label pinned to the top of the plot claiming the trace peaks there.</p>
 */
function finite(value: unknown): number | null {
  if (typeof value !== 'number' && typeof value !== 'string') return null
  const n = typeof value === 'number' ? value : Number(value.trim())
  return Number.isFinite(n) ? n : null
}

/**
 * A stored `chartAnnotations` value as a list this app can draw — and <b>never a throw</b>.
 *
 * <p>Same rule as parsePeakSort and parseSpectrumUnit: what comes out of a `params_json` may have
 * been written by another build, or hand-edited, and a measurement the user has merely *selected*
 * must not fail over it. So everything unusable is dropped or defaulted rather than rejected: a
 * value that is not a list at all reads as no annotations, an entry with no usable frequency is
 * dropped (a log axis has no position for one — `valToPos` answers −Infinity, the case
 * minMaxBandPlugin already guards), an unknown style is read from whether the entry has a level,
 * ids that collide are reassigned so two rows can never share an identity, and the list is capped
 * so a hand-written document cannot put a thousand labels on a chart.</p>
 */
export function parseAnnotations(value: unknown): SpectrumAnnotation[] {
  if (!Array.isArray(value)) return []

  const out: SpectrumAnnotation[] = []
  const seen = new Set<number>()
  for (const entry of value) {
    if (out.length >= MAX_ANNOTATIONS) break
    if (entry === null || typeof entry !== 'object') continue
    const row = entry as Record<string, unknown>

    const freqHz = finite(row.freqHz)
    if (freqHz === null || freqHz <= 0) continue

    const levelDbv = finite(row.levelDbv)
    const style: AnnotationStyle =
      row.style === 'point' || row.style === 'frequency' ? row.style : levelDbv === null ? 'frequency' : 'point'

    const channelRaw = finite(row.channel)
    const id = finite(row.id)

    out.push({
      id: id !== null && id > 0 && !seen.has(Math.floor(id)) ? Math.floor(id) : nextAnnotationId(out),
      style,
      text: trimText(typeof row.text === 'string' ? row.text.trim() : row.text == null ? '' : String(row.text).trim()),
      freqHz,
      levelDbv: style === 'point' ? levelDbv : null,
      channel: channelRaw === 0 || channelRaw === 1 ? channelRaw : null,
      labelOffsetOctaves: finite(row.labelOffsetOctaves) ?? 0,
      labelOffsetDb: finite(row.labelOffsetDb) ?? 0,
    })
    seen.add(out[out.length - 1].id)
  }
  return out
}

/**
 * The frequency as the chart's own labels print it: whole hertz low down, where the bins are
 * fractions of one, and kilohertz above 10 kHz, where four digits of hertz say nothing.
 */
export function formatAnnotationHz(freqHz: number): string {
  if (!Number.isFinite(freqHz)) return '—'
  if (freqHz >= 10000) return `${(freqHz / 1000).toFixed(2)} kHz`
  if (freqHz >= 1000) return `${(freqHz / 1000).toFixed(3)} kHz`
  if (freqHz >= 100) return `${freqHz.toFixed(1)} Hz`
  return `${freqHz.toFixed(2)} Hz`
}

/**
 * The one-line reading of an annotation: its frequency and, for a point, its level.
 *
 * <p><b>Always dBV</b>, whatever unit the chart's axis is in — the same call the harmonic markers'
 * labels make, and for the same reason: the annotation names a point of a trace, and a level is a
 * level whether the axis beside it is showing a density or not. The *marker* follows the axis (it
 * has to stay on the trace); the quoted figure is the one that was stored.</p>
 */
export function annotationReading(annotation: SpectrumAnnotation): string {
  const hz = formatAnnotationHz(annotation.freqHz)
  if (annotation.style !== 'point' || annotation.levelDbv === null) return hz
  return `${hz} · ${annotation.levelDbv.toFixed(1)} dBV`
}
