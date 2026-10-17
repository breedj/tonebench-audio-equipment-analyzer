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

export const ANNOTATION_STYLES = ['point', 'frequency', 'box'] as const
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
  /**
   * A <b>box</b>'s other two edges — its second frequency and its second level. Null for every other
   * style.
   *
   * <p><b>`(freqHz, levelDbv)` is the box's top-left corner</b> — lowest frequency, highest level —
   * and this pair is its bottom-right. {@link normalizeAnnotationBox} guarantees it, and that exact
   * assignment is what lets three things stay literally unchanged rather than growing a style
   * branch: {@link numberedAnnotations} sorts on `freqHz`, so a box is numbered by its <i>left</i>
   * edge, where a reader's eye enters it; the label's offset keeps its anchor, so `layoutAnnotations`
   * and `clampLabelOffset` work on a box unmodified; and a reader that only knows `levelDbv` quotes
   * the <i>top</i> of the region, which is the informative end.</p>
   *
   * <p>Two absolute figures rather than an extent in octaves and dB, unlike the label offset below.
   * The offset is relative because a label's meaning is "beside the thing"; a region's meaning is
   * "these columns, these levels", which is what the panel shows as four absolute numbers and what
   * the report prints as two ranges.</p>
   */
  freqHz2: number | null
  levelDbv2: number | null
  /**
   * 0 = L, 1 = R; null for a frequency rule, for a box (a region names no trace), or for a point
   * placed where no trace was identified.
   */
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

/**
 * A box with its corners the way every reader assumes them: `freqHz <= freqHz2` and
 * `levelDbv >= levelDbv2`, i.e. top-left then bottom-right. A no-op for any other style.
 *
 * <p>Called from <b>all three</b> entry points — {@link parseAnnotations}, {@link addAnnotation} and
 * {@link updateAnnotation} — so the layout, the panel, the report and the drag maths may take the
 * ordering for granted and none of them needs a defensive `Math.min`. A drag that ends up and to
 * the left, and an edge dragged past its opposite, both arrive here inverted; this is the one place
 * that sorts them out.</p>
 */
export function normalizeAnnotationBox(annotation: SpectrumAnnotation): SpectrumAnnotation {
  if (annotation.style !== 'box') return annotation
  const { freqHz, freqHz2, levelDbv, levelDbv2 } = annotation
  if (freqHz2 === null || levelDbv === null || levelDbv2 === null) return annotation

  const loHz = Math.min(freqHz, freqHz2)
  const hiHz = Math.max(freqHz, freqHz2)
  const top = Math.max(levelDbv, levelDbv2)
  const bottom = Math.min(levelDbv, levelDbv2)
  if (loHz === freqHz && hiHz === freqHz2 && top === levelDbv && bottom === levelDbv2) return annotation
  return { ...annotation, freqHz: loHz, freqHz2: hiHz, levelDbv: top, levelDbv2: bottom }
}

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
  return [...list, normalizeAnnotationBox({ ...draft, id: nextAnnotationId(list), text: trimText(draft.text) })]
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
    return normalizeAnnotationBox({ ...next, text: trimText(next.text) })
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
    const freqHz2 = finite(row.freqHz2)
    const levelDbv2 = finite(row.levelDbv2)
    // A box needs both of its other edges. One that has lost them degrades to the point (or the
    // frequency rule) it still describes rather than being dropped: it carries a usable frequency
    // and, more to the point, the user's own note, and no other branch of this function throws a
    // note away.
    const hasSecondCorner = freqHz2 !== null && freqHz2 > 0 && levelDbv2 !== null && levelDbv !== null
    const stored = row.style
    const style: AnnotationStyle =
      stored === 'box' || stored === 'point' || stored === 'frequency'
        ? stored === 'box' && !hasSecondCorner
          ? levelDbv === null
            ? 'frequency'
            : 'point'
          : stored
        : // An unknown style — a later build's name — is read from what the entry carries.
          hasSecondCorner
          ? 'box'
          : levelDbv === null
            ? 'frequency'
            : 'point'

    const channelRaw = finite(row.channel)
    const id = finite(row.id)

    out.push(
      normalizeAnnotationBox({
        id: id !== null && id > 0 && !seen.has(Math.floor(id)) ? Math.floor(id) : nextAnnotationId(out),
        style,
        text: trimText(typeof row.text === 'string' ? row.text.trim() : row.text == null ? '' : String(row.text).trim()),
        freqHz,
        // Written the other way round -- `style === 'point' ? levelDbv : null` -- every box loses
        // its top edge on the way in. Only a frequency rule has no level.
        levelDbv: style === 'frequency' ? null : levelDbv,
        freqHz2: style === 'box' ? freqHz2 : null,
        levelDbv2: style === 'box' ? levelDbv2 : null,
        // A region names no trace, so it carries no channel -- the report's channel column would
        // otherwise fill with dashes.
        channel: style !== 'box' && (channelRaw === 0 || channelRaw === 1) ? channelRaw : null,
        labelOffsetOctaves: finite(row.labelOffsetOctaves) ?? 0,
        labelOffsetDb: finite(row.labelOffsetDb) ?? 0,
      }),
    )
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
  if (annotation.style === 'box' && annotation.freqHz2 !== null) {
    const span = formatAnnotationHzRange(annotation.freqHz, annotation.freqHz2)
    if (annotation.levelDbv === null || annotation.levelDbv2 === null) return span
    return `${span} · ${annotation.levelDbv2.toFixed(1)} … ${annotation.levelDbv.toFixed(1)} dBV`
  }
  const hz = formatAnnotationHz(annotation.freqHz)
  if (annotation.style !== 'point' || annotation.levelDbv === null) return hz
  return `${hz} · ${annotation.levelDbv.toFixed(1)} dBV`
}

/**
 * A frequency range, in <b>one</b> unit chosen from its high end — "0.90 – 1.20 kHz" rather than
 * "900.0 Hz – 1.200 kHz". A range is read as one quantity, and two units inside it make the reader
 * convert before they can compare the ends.
 */
export function formatAnnotationHzRange(loHz: number, hiHz: number): string {
  if (!Number.isFinite(loHz) || !Number.isFinite(hiHz)) return '—'
  if (hiHz >= 1000) return `${(loHz / 1000).toFixed(2)} – ${(hiHz / 1000).toFixed(2)} kHz`
  return `${loHz.toFixed(1)} – ${hiHz.toFixed(1)} Hz`
}

/**
 * The colour an annotation's ordinal is drawn in — on the chart's badge and on the matching badge in
 * the display rail's list, so a label on the picture can be tied to its row at a glance without
 * reading either number (Joost, 2026-10-17).
 *
 * <p>Keyed on the <b>ordinal</b> rather than the id, so the number and the colour are one identity:
 * delete the first of three and the remaining two renumber, and their colours move with their
 * numbers rather than leaving a gap in the sequence. It also guarantees the first six on a chart are
 * all different, which keying on an id could not.</p>
 *
 * <p>Six hues, spaced around the wheel and readable on both the light plot and the dark theme's
 * near-black well, and deliberately not the trace blue (FFT_LIVE_COLOR) or the harmonic markers'
 * amber: an annotation is a note *about* the picture, not another curve in it. They cycle past six,
 * by which point the numbers are doing the work anyway. A black-and-white report desaturates them
 * with everything else, which is why the ordinal is drawn as well as coloured.</p>
 */
export const ANNOTATION_COLORS = ['#7C5CD6', '#0E9A8A', '#C2458F', '#E07B16', '#4C9A2A', '#D14545'] as const

/** The colour for the annotation numbered `ordinal` (1-based, as {@link numberedAnnotations} hands them out). */
export function annotationColor(ordinal: number): string {
  const index = Number.isFinite(ordinal) ? Math.max(0, Math.round(ordinal) - 1) : 0
  return ANNOTATION_COLORS[index % ANNOTATION_COLORS.length]
}

/**
 * What the display rail's style picker offers.
 *
 * <p>Here rather than in ChartDisplayPanel.vue so a test can pin it, because <b>Frequency is
 * deliberately absent</b>: the picker offers Point and Box, and a frequency rule can no longer be
 * added (Joost, 2026-10-17). That is a change to the <i>picker alone</i> — `'frequency'` stays in
 * {@link ANNOTATION_STYLES}, in {@link parseAnnotations}, in the layout, in the panel's list and in
 * the report, because a measurement saved last week may carry one and an annotation the app quietly
 * stopped drawing would be the user's own words lost. Nothing in a `.vue` file is testable here, so
 * the one decision in this feature with data-loss potential would otherwise be pinned by nothing.</p>
 */
export const ANNOTATION_STYLE_OPTIONS: { value: AnnotationStyle; label: string; title: string }[] = [
  {
    value: 'point',
    label: 'Point',
    title: 'A marker on the trace with a label beside it, which can be dragged clear. Click the chart to place it.',
  },
  {
    value: 'box',
    label: 'Box',
    title: 'A marked region of the plot, with a label beside it. Drag a rectangle on the chart to place it.',
  },
]
