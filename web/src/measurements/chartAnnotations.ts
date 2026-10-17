/**
 * The notes a user draws on a measurement chart: a marker at a point of a trace with a labelled box
 * beside it, a marked region, or a dashed rule at one x value labelled at the top of the plot.
 *
 * <p>Pure, and in its own module rather than in a chart component, for the usual two reasons: every
 * chart, every display rail's list, the Edit measurement dialog and the report's tables all have to
 * agree about what an annotation is and how it is numbered, and nothing in a `.vue` file is testable
 * under the node environment vitest runs in. The *geometry* is a second module
 * (charts/annotationLayout.ts) because it needs a plot to lay anything out against, and the *units*
 * a third (charts/annotationQuantities.ts); everything here is the model and its ordering.</p>
 *
 * <p>Four things in here are decisions rather than mechanics, and each is written out where it
 * applies: the numbers are stored in a named quantity rather than in whatever the axis happens to be
 * plotting, the label's offset is stored in that quantity's own space rather than in pixels, each
 * annotation names the <b>view</b> it was drawn on and is numbered within it, and the ordinal that
 * ties a label on a chart to a row in the report is positional rather than stored.</p>
 *
 * <p>This module was `spectrumAnnotations.ts` until annotations spread beyond the FFT screen, and the
 * fields were `freqHz`/`levelDbv`/`labelOffsetOctaves`/`labelOffsetDb`. {@link parseAnnotations}
 * still reads those names, so everything ever stored comes back meaning exactly what it meant: on a
 * log-frequency x axis against a dB y axis the old semantics <i>are</i> the new ones.</p>
 */

import { annotationQuantity } from '../charts/annotationQuantities'

export const ANNOTATION_STYLES = ['point', 'frequency', 'box'] as const
export type AnnotationStyle = (typeof ANNOTATION_STYLES)[number]

/**
 * As many as one measurement can carry and one report table can be read as a list.
 *
 * <p>A cap on the <b>whole measurement</b>, across all of its views, rather than per view. A
 * per-view cap would make {@link parseAnnotations}' truncation depend on the order the views happen
 * to appear in the stored list, so a document at the limit could silently lose a note from whichever
 * view came last.</p>
 */
export const MAX_ANNOTATIONS = 24

/**
 * As long a note as a chart label can carry. A label is drawn on the plot, not wrapped, so a
 * sentence would cover the trace it points at; truncating on the way in is what keeps a
 * hand-edited params_json from drawing a paragraph across a chart.
 */
export const MAX_ANNOTATION_TEXT = 80

export interface ChartAnnotation {
  /**
   * Unique within its own list and stable across edits, so a drag, a retype and a delete all name
   * the same row. Not the ordinal a reader sees — see {@link numberedAnnotations}.
   */
  id: number
  style: AnnotationStyle
  text: string
  /**
   * Which chart of the measurement this was drawn on — a report view id (report/views.ts), plus
   * `Spectrogram` and `Oscilloscope` for the two FFT charts the report prints as elements.
   *
   * <p>A measurement keeps <b>one flat list</b> across all of its views and each annotation names
   * its own, rather than a map keyed by view. Three readers decide that: the dirty watcher watches a
   * single `ref` and fires on replacement (a map of arrays would need a deep watcher, the exact bug
   * class the "replaced, never mutated" rule here exists to avoid), {@link MAX_ANNOTATIONS} and
   * {@link nextAnnotationId} are whole-list rules, and the server's result-level editor splices one
   * JSON array.</p>
   */
  view: string
  /**
   * Where the annotation sits — a point's or a region's first corner — in {@link xQuantity} and
   * {@link yQuantity}.
   *
   * <p><b>`(x, y)` is a box's top-left corner</b>: lowest x, highest y, with `(x2, y2)` its
   * bottom-right. {@link normalizeAnnotationBox} guarantees it, and that exact assignment is what
   * lets three things stay literally unchanged rather than growing a style branch:
   * {@link numberedAnnotations} sorts on `x`, so a box is numbered by its <i>left</i> edge, where a
   * reader's eye enters it; the label's offset keeps its anchor, so the layout and the clamp work on
   * a box unmodified; and a reader that only knows `y` quotes the <i>top</i> of the region, which is
   * the informative end.</p>
   *
   * <p>`y` is null for a rule, which has no level, and for a point placed where the axis could not
   * say one.</p>
   */
  x: number
  y: number | null
  /** A box's other two edges. Null for every other style. */
  x2: number | null
  y2: number | null
  /**
   * What the two numbers are measured in — a key of charts/annotationQuantities.ts, such as `hz`,
   * `ms`, `outputWatt` or `ohm`.
   *
   * <p>Stored rather than implied by the chart, because a chart's axes are not fixed: the distortion
   * sweep's x is one of four unrelated quantities of the same point and its y is percent or dB, the
   * frequency-response chart's y is dBV or gain dB by view, the impulse chart's y is volts or dB re
   * peak. Without this a note placed in watts would be redrawn, and printed, against volts. With it
   * the rule is one line in {@link layoutAnnotations}: draw it where the axis plots that quantity,
   * and where it does not, skip it — never move it and never delete it, which is how an annotation
   * outside the plotted window has always been treated.</p>
   *
   * <p>A level is therefore still <b>always dBV</b> on the spectrum, never the value the chart
   * happens to be plotting: the chart's unit is a per-frame constant add (spectrumUnits.ts), and an
   * annotation stored this way stays glued to its spot on the trace across a unit switch and a
   * DUT-gain edit.</p>
   */
  xQuantity: string
  yQuantity: string
  /**
   * 0 = L, 1 = R; null for a rule, for a box (a region names no trace), or for a point placed where
   * no trace was identified.
   */
  channel: number | null
  /**
   * Where the label sits relative to its anchor — <b>point and box only</b>, and in each quantity's
   * <b>own space</b> rather than in pixels: a log2 ratio for a ratio quantity (so x on a frequency
   * axis is an offset in octaves, which is what this field held when it was called
   * `labelOffsetOctaves`), and a plain difference for a linear one.
   *
   * <p>This is the whole answer to the report plot being 1600×900 while the screen plot is 400-700 px
   * tall. Both span the same window, so a fixed offset in the axis's space is a fixed <i>fraction of
   * the plot</i>: a label placed by hand on screen lands in the same place on the picture at any
   * size and at any zoom. It is also what keeps a label still across a unit switch, since the offset
   * is applied in the stored quantity and only then converted to what is plotted — a ratio offset in
   * percent being a constant offset in dB. A pixel offset would have been none of those things.</p>
   *
   * <p>A rule's label is pinned to the top of the plot and stacked out of its neighbours' way, which
   * a manual nudge would fight; the fields stay on the record so switching a style leaves nothing
   * stale behind.</p>
   */
  labelOffsetX: number
  labelOffsetY: number
}

/**
 * The quantities and the view an annotation is read against when the stored row does not name them.
 *
 * <p>Every caller knows these because every caller knows the measurement kind and which chart it is
 * reading for. It is what lets a document written before annotations left the FFT screen — where x
 * was always a frequency and y always dBV — come back meaning what it meant.</p>
 */
export interface AnnotationFrame {
  view: string
  xQuantity: string
  yQuantity: string
}

/** The frame every annotation stored before this module existed was written in. */
export const LEGACY_SPECTRUM_FRAME: AnnotationFrame = { view: 'Spectrum', xQuantity: 'hz', yQuantity: 'dbv' }

/**
 * Where a new label starts: up and to the right, clear of the thing it names — as a <b>fraction of
 * the plot</b>, not in any one quantity's units.
 *
 * <p>A fraction because the offset's units differ per chart: the 0.45 octaves and 14 dB this feature
 * shipped with mean nothing on an axis measured in milliseconds, and 14 of anything is off the plot
 * on a coherence axis that spans 0 to 1. These two numbers <b>are</b> that original placement,
 * measured against the FFT screen's own default window — 20 Hz to 20 kHz is 9.966 octaves, of which
 * 0.45 is 4.5 %, and 20 to −150 dBV is 170 dB, of which 14 is 8.2 % — so the chart it was chosen on
 * is unchanged and every other chart gets the same gesture in its own units.
 * {@link clampLabelOffset} keeps the result on the plot regardless.</p>
 */
export const DEFAULT_LABEL_OFFSET_FRACTION = { x: 0.045, y: 0.082 } as const

/**
 * A box with its corners the way every reader assumes them: `x <= x2` and `y >= y2`, i.e. top-left
 * then bottom-right. A no-op for any other style.
 *
 * <p>Called from <b>all three</b> entry points — {@link parseAnnotations}, {@link addAnnotation} and
 * {@link updateAnnotation} — so the layout, the panels, the report and the drag maths may take the
 * ordering for granted and none of them needs a defensive `Math.min`. A drag that ends up and to the
 * left, and an edge dragged past its opposite, both arrive here inverted; this is the one place that
 * sorts them out.</p>
 */
export function normalizeAnnotationBox(annotation: ChartAnnotation): ChartAnnotation {
  if (annotation.style !== 'box') return annotation
  const { x, x2, y, y2 } = annotation
  if (x2 === null || y === null || y2 === null) return annotation

  const lo = Math.min(x, x2)
  const hi = Math.max(x, x2)
  const top = Math.max(y, y2)
  const bottom = Math.min(y, y2)
  if (lo === x && hi === x2 && top === y && bottom === y2) return annotation
  return { ...annotation, x: lo, x2: hi, y: top, y2: bottom }
}

/** An annotation with the ordinal a reader sees on the chart and in the report's table. */
export interface NumberedAnnotation extends ChartAnnotation {
  number: number
}

/** The id to give the next annotation added to `list`: one past the highest in it. */
export function nextAnnotationId(list: readonly ChartAnnotation[]): number {
  let highest = 0
  for (const a of list) if (Number.isFinite(a.id) && a.id > highest) highest = Math.floor(a.id)
  return highest + 1
}

/**
 * One view's annotations, with their ordinals, left to right.
 *
 * <p>Positional and derived, never stored. Three reasons, and the third is the one that matters: a
 * stored ordinal reads `1, 2, 4` after a delete, which looks like a rendering fault rather than a
 * deletion; left-to-right is the order a reader scanning the chart expects, so the badge at 50 Hz is
 * 1 and the one at 19 kHz is last; and because every chart's plugin, every panel's list and the
 * report's tables all call this one function, the numbers on a picture cannot disagree with the rows
 * on the page — which is the entire point of having them.</p>
 *
 * <p><b>Per view, which is why `view` is required rather than optional.</b> A measurement keeps one
 * flat list across its charts, and numbering that list as a whole would interleave a Nyquist plot's
 * real part of −1 with a Bode plot's 1 kHz: neither chart's badges would start at 1, and a report
 * chapter printing both would have two tables that each skip numbers. A caller that omitted the view
 * would get those wrong numbers silently, so the signature does not let it.</p>
 *
 * <p>The id breaks a tie, so two annotations at the same x keep a stable order rather than swapping
 * between frames of a live stream.</p>
 */
export function numberedAnnotations(list: readonly ChartAnnotation[], view: string): NumberedAnnotation[] {
  return list
    .filter((annotation) => annotation.view === view)
    .sort((a, b) => a.x - b.x || a.id - b.id)
    .map((annotation, index) => ({ ...annotation, number: index + 1 }))
}

/**
 * The views that have annotations, in the order they first appear — for the Edit dialog, which lists
 * a result's whole set grouped by the chart each note was drawn on.
 */
export function annotationViewsPresent(list: readonly ChartAnnotation[]): string[] {
  const out: string[] = []
  for (const a of list) if (!out.includes(a.view)) out.push(a.view)
  return out
}

/** How many of `list` belong to some other view than `view` — the panel's "2 more on other views". */
export function annotationsElsewhere(list: readonly ChartAnnotation[], view: string): number {
  return list.reduce((count, a) => (a.view === view ? count : count + 1), 0)
}

/**
 * `list` plus one annotation, as a <b>new array</b>.
 *
 * <p>Every mutator here returns a new array and never touches the one it was given. That is
 * load-bearing rather than tidy: the live list is a `ref` watched by a dirty watcher, which fires on
 * replacement and not on a `push`, so an in-place add would leave the measurement looking saved
 * while carrying an unsaved note. Pinned by a test.</p>
 */
export function addAnnotation(
  list: readonly ChartAnnotation[],
  draft: Omit<ChartAnnotation, 'id'>,
): ChartAnnotation[] {
  if (list.length >= MAX_ANNOTATIONS) return list.slice()
  return [...list, normalizeAnnotationBox({ ...draft, id: nextAnnotationId(list), text: trimText(draft.text) })]
}

/** `list` with one annotation's fields changed, as a new array; unchanged for an id nothing has. */
export function updateAnnotation(
  list: readonly ChartAnnotation[],
  id: number,
  patch: Partial<Omit<ChartAnnotation, 'id'>>,
): ChartAnnotation[] {
  return list.map((annotation) => {
    if (annotation.id !== id) return annotation
    const next = { ...annotation, ...patch }
    return normalizeAnnotationBox({ ...next, text: trimText(next.text) })
  })
}

/** `list` without that annotation, as a new array. */
export function removeAnnotation(list: readonly ChartAnnotation[], id: number): ChartAnnotation[] {
  return list.filter((annotation) => annotation.id !== id)
}

/** `list` without any of one view's annotations — the panel's Remove all, which is per view. */
export function removeAnnotationsOfView(list: readonly ChartAnnotation[], view: string): ChartAnnotation[] {
  return list.filter((annotation) => annotation.view !== view)
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

function text(value: unknown): string | null {
  return typeof value === 'string' && value.trim() !== '' ? value.trim() : null
}

/**
 * A stored `chartAnnotations` value as a list this app can draw, or <b>null</b> when the value is
 * not a list at all — and never a throw.
 *
 * <p>The null is the point of having this beside {@link parseAnnotations}: it carries the server's
 * <i>absent</i> (this result was saved before it could hold annotations, so fall back to the
 * measurement's) apart from its <i>empty</i> (this result's own set, which somebody cleared), which
 * the report's per-result rule needs and an empty array cannot express.</p>
 *
 * <p>Otherwise the same rule as parsePeakSort and parseSpectrumUnit: what comes out of a
 * `params_json` may have been written by another build, or hand-edited, and a measurement the user
 * has merely <i>selected</i> must not fail over it. So everything unusable is dropped or defaulted
 * rather than rejected: an entry with no usable x is dropped (there is nowhere to draw it), an
 * unknown style is read from what the entry carries, ids that collide are reassigned so two rows can
 * never share an identity, and the list is capped so a hand-written document cannot put a thousand
 * labels on a chart.</p>
 */
export function readStoredAnnotations(value: unknown, frame: AnnotationFrame): ChartAnnotation[] | null {
  if (!Array.isArray(value)) return null

  const out: ChartAnnotation[] = []
  const seen = new Set<number>()
  for (const entry of value) {
    if (out.length >= MAX_ANNOTATIONS) break
    if (entry === null || typeof entry !== 'object') continue
    const row = entry as Record<string, unknown>

    // The old names are read where the new ones are absent, which is what makes every annotation
    // stored before this module existed come back unchanged. `x` wins where both are present, so a
    // document written by a newer build and edited by an older one still reads as the newer one
    // meant it.
    const x = finite(row.x) ?? finite(row.freqHz)
    // Any finite x, including zero and below: the impulse response's window crosses t = 0, the
    // s-plane's sigma is negative over most of its area, a Nyquist real part and a Nichols phase are
    // routinely negative, and the spectrogram's age axis is negative throughout. Whether a value has
    // a *position* is the axis's judgement, not this one's -- a log axis answers a non-finite
    // position for a non-positive value, which the layout already skips.
    if (x === null) continue

    const y = finite(row.y) ?? finite(row.levelDbv)
    const x2 = finite(row.x2) ?? finite(row.freqHz2)
    const y2 = finite(row.y2) ?? finite(row.levelDbv2)
    // A box needs both of its other edges. One that has lost them degrades to the point (or the
    // rule) it still describes rather than being dropped: it carries a usable x and, more to the
    // point, the user's own note, and no other branch of this function throws a note away.
    const hasSecondCorner = x2 !== null && y2 !== null && y !== null
    const stored = row.style
    const style: AnnotationStyle =
      stored === 'box' || stored === 'point' || stored === 'frequency'
        ? stored === 'box' && !hasSecondCorner
          ? y === null
            ? 'frequency'
            : 'point'
          : stored
        : // An unknown style — a later build's name — is read from what the entry carries.
          hasSecondCorner
          ? 'box'
          : y === null
            ? 'frequency'
            : 'point'

    const channelRaw = finite(row.channel)
    const id = finite(row.id)

    out.push(
      normalizeAnnotationBox({
        id: id !== null && id > 0 && !seen.has(Math.floor(id)) ? Math.floor(id) : nextAnnotationId(out),
        style,
        text: trimText(typeof row.text === 'string' ? row.text.trim() : row.text == null ? '' : String(row.text).trim()),
        view: text(row.view) ?? frame.view,
        x,
        // Written the other way round -- `style === 'point' ? y : null` -- every box loses its top
        // edge on the way in. Only a rule has no y.
        y: style === 'frequency' ? null : y,
        x2: style === 'box' ? x2 : null,
        y2: style === 'box' ? y2 : null,
        xQuantity: text(row.xQuantity) ?? frame.xQuantity,
        yQuantity: text(row.yQuantity) ?? frame.yQuantity,
        // A region names no trace, so it carries no channel -- the report's channel column would
        // otherwise fill with dashes.
        channel: style !== 'box' && (channelRaw === 0 || channelRaw === 1) ? channelRaw : null,
        labelOffsetX: finite(row.labelOffsetX) ?? finite(row.labelOffsetOctaves) ?? 0,
        labelOffsetY: finite(row.labelOffsetY) ?? finite(row.labelOffsetDb) ?? 0,
      }),
    )
    seen.add(out[out.length - 1].id)
  }
  return out
}

/** {@link readStoredAnnotations}, with an absent list read as none. */
export function parseAnnotations(value: unknown, frame: AnnotationFrame): ChartAnnotation[] {
  return readStoredAnnotations(value, frame) ?? []
}

/**
 * The one-line reading of an annotation: where it is, and what is there — in whatever quantities it
 * was stored in, so a sweep point reads "2.40 W" and an impulse "1.250 ms".
 *
 * <p>Always the <b>stored</b> figure, whatever unit the axis beside it happens to be in — the same
 * call the harmonic markers' labels make, and for the same reason: the annotation names a point of a
 * trace, and a level is a level whether the axis is showing a density or not. The <i>marker</i>
 * follows the axis, because it has to stay on the trace; the quoted figure is the one that was
 * stored.</p>
 */
export function annotationReading(annotation: ChartAnnotation): string {
  const qx = annotationQuantity(annotation.xQuantity)
  const qy = annotationQuantity(annotation.yQuantity)
  if (annotation.style === 'box' && annotation.x2 !== null) {
    const span = qx.formatRange(annotation.x, annotation.x2)
    if (annotation.y === null || annotation.y2 === null) return span
    return `${span} · ${qy.formatRange(annotation.y2, annotation.y)}`
  }
  const where = qx.format(annotation.x)
  if (annotation.style !== 'point' || annotation.y === null) return where
  return `${where} · ${qy.format(annotation.y)}`
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

export interface AnnotationStyleOption {
  value: AnnotationStyle
  label: string
  title: string
}

const POINT_OPTION: AnnotationStyleOption = {
  value: 'point',
  label: 'Point',
  title: 'A marker on the trace with a label beside it, which can be dragged clear. Click the chart to place it.',
}

const BOX_OPTION: AnnotationStyleOption = {
  value: 'box',
  label: 'Box',
  title: 'A marked region of the plot, with a label beside it. Drag a rectangle on the chart to place it.',
}

const RULE_OPTION: AnnotationStyleOption = {
  value: 'frequency',
  label: 'Rule',
  title: 'A dashed line down the plot at one value, labelled at the top. Click the chart to place it.',
}

/**
 * What a display rail's style picker offers for a chart that offers `styles`.
 *
 * <p>Here rather than in a `.vue` so a test can pin it, because what the picker offers is the one
 * part of this feature with data-loss potential. <b>`'frequency'` is offered only where a chart asks
 * for it</b> — it was dropped from the FFT screen's picker (Joost, 2026-10-17) and came back for the
 * charts where it is the only honest style: the spectrogram, whose y axis is a rolling age, and the
 * multi-scale charts, where "the phase flips here" is a statement about an x value and not about a
 * point on any one of three y scales. Dropping it from a picker is a change to the <i>picker
 * alone</i>: it stays in {@link ANNOTATION_STYLES}, in {@link readStoredAnnotations}, in the layout,
 * in every panel's list and in the report, because a measurement saved last year may carry one and
 * an annotation the app quietly stopped drawing would be the user's own words lost.</p>
 */
export function annotationStyleOptions(styles: readonly AnnotationStyle[]): AnnotationStyleOption[] {
  const all = [POINT_OPTION, BOX_OPTION, RULE_OPTION]
  return all.filter((option) => styles.includes(option.value))
}

/** What most charts offer, and what the FFT screen has offered since the box style was added. */
export const DEFAULT_ANNOTATION_STYLES: readonly AnnotationStyle[] = ['point', 'box']
