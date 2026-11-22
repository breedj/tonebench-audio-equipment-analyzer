/**
 * The figures on the oscilloscope's display toggles.
 *
 * <p><b>Why figures rather than labels.</b> The Display rail used to be a column of checkboxes with
 * their names beside them, which is the most legible thing there is and also the tallest: six rows
 * of label text on a rail whose whole problem is height. Icon buttons put the same six controls in
 * two rows, and the name a label would have carried is the button's own tooltip and its
 * <code>aria-label</code> — so nothing is lost to a reader who hovers or to a screen reader, only
 * to a reader who is scanning, which is what the figure is for.</p>
 *
 * <p><b>Shapes as data, like <code>measurementIcons.ts</code>.</b> A test can then assert that
 * every toggle the panel draws has a figure and that every figure stays inside its box — which is
 * the only thing that catches a missing or an overflowing one, both of which look deliberate.</p>
 */

/** The box every figure is drawn in, matching the app's other glyph sets. */
export const TOGGLE_ICON_SIZE = 24

/**
 * A colour a shape is drawn in whatever it is drawn on, where almost every figure here inherits
 * <code>currentColor</code> instead.
 *
 * <p><b>The exception is a figure that was <em>supplied</em> as a drawing</b>, and the three View
 * options are the ones that were (2026-11-22, asked for twice: "they now show in one color, use the
 * ones I supplied, they contain multiple colors"). An earlier reading of those three flattened them
 * to one ink on the argument that a lit segmented option already takes a colour of its own; that is
 * overruled. The colours are the drawing's, so a figure here says exactly what the file beside it in
 * <code>docs/design/</code> says, and the three View figures stay recognisably the pictures that
 * were drawn rather than three grey outlines that merely share their geometry.</p>
 *
 * <p>It wins over the <code>mark</code> colour a picker well supplies, since it is the more specific
 * statement: that one is "colour this figure's annotations however this section is coloured", and
 * this is "this stroke is this colour".</p>
 */
export type ScopeToggleShape =
  /**
   * A stroked path. <code>w</code> overrides the stroke width, which only Thick lines needs;
   * <code>dash</code> marks a line that is a <em>setting</em> rather than signal — a cursor, a
   * threshold, a band edge — which is how the chart itself draws all three.
   */
  | { t: 'path'; d: string; w?: number; dash?: boolean; mark?: boolean; ink?: string }
  /** A marker, for the figures that are about where the samples fell. */
  | { t: 'dot'; x: number; y: number; r?: number; mark?: boolean; ink?: string }

/**
 * The inks Joost's own drawings are made of, named once so no two of them can drift apart about
 * what the same part of a picture is coloured.
 *
 * <p><code>structure</code> is the one role rather than a colour: it is whatever the signal is
 * drawn <em>on</em> — the axes of the Y–T figure, the crosshair of the X–Y, the ghosted cycle
 * behind the eye, the neighbouring bins Snap to bin pushes a tone between. The rest are named as
 * colours, because which one a figure uses for its signal and which for the mark on it is that
 * drawing's own choice and not a rule.</p>
 *
 * <p><b>Four are literals and the teal is a variable</b>, which is this app's own split rather than
 * an inconsistency: a colour that is the same in both themes is written where it is drawn, and one
 * that is not lives in <code>style.css</code>. The supplied teal reads 5.38:1 on the light theme's
 * panel and 3.11:1 on the dark one, so the dark theme gets a lighter step — the arithmetic, and
 * what was deliberately left alone with it, are in that file's own note.</p>
 */
const SUPPLIED_INK = {
  structure: '#94a3b8',
  teal: 'var(--view-icon-teal)',
  violet: '#7c3aed',
  orange: '#ea580c',
  blue: '#2563eb',
} as const

/**
 * Whether a shape is an annotation of the signal rather than the signal itself — which is the whole
 * of what the pickers' two-colour figures need to know.
 *
 * <p>A dashed line already meant exactly that (see <code>dash</code> above), so it counts without
 * being flagged; <code>mark</code> is for the annotations that are not dashed, above all the dots a
 * stimulus figure puts on its own zero crossings. Everywhere the figure is drawn in one colour this
 * answers nothing at all.</p>
 */
export function scopeToggleShapeIsMark(shape: ScopeToggleShape): boolean {
  return shape.mark === true || ('dash' in shape && shape.dash === true)
}


/**
 * A 1:2 Lissajous figure — the ∞ the X–Y view draws for a pair a quarter turn apart, which is
 * what Joost's own drawing of it is (<code>docs/design/view-xy.svg</code>, sampled from
 * <code>x = cx + ax·sin φ</code>, <code>y = cy − ay·sin 2φ</code>).
 *
 * <p>Computed rather than written out, for <code>mathFigures.ts</code>' reason one icon set along:
 * the figure then cannot drift from the curve it stands for, and 64 points of polyline is a
 * literal nobody could check by eye. At this size the segments are well under a pixel.</p>
 */
function lissajousPath(cx: number, cy: number, ax: number, ay: number, steps = 64): string {
  const at = (k: number) => {
    const phi = (2 * Math.PI * k) / steps
    const x = cx + ax * Math.sin(phi)
    const y = cy - ay * Math.sin(2 * phi)
    return `${x.toFixed(2)},${y.toFixed(2)}`
  }
  const points = Array.from({ length: steps }, (_, k) => at(k))
  return `M${points[0]} ${points.slice(1).map((p) => `L${p}`).join(' ')} Z`
}

/** The X–Y view's own figure, drawn once at module load. */
const XY_LOCUS = lissajousPath(12, 12, 8, 6.5)

/**
 * One figure per toggle.
 *
 * <p>Each is drawn in the same 24-unit box as <code>panelIcons</code>' glyphs and at the same
 * stroke weight, so a rail of these sits beside the panel headers without looking like a second
 * icon set.</p>
 */
export const SCOPE_TOGGLE_ICONS: Record<string, ScopeToggleShape[]> = {
  /** The level line with the handle that is dragged along it. */
  triggerLevel: [
    { t: 'path', d: 'M2,12 H16' },
    { t: 'path', d: 'M16,8 L22,12 L16,16 Z' },
  ],

  /** A fitted sine above what is left once it is taken out. */
  residual: [
    { t: 'path', d: 'M2,7 C5,1 9,1 12,7 C15,13 19,13 22,7' },
    { t: 'path', d: 'M2,19 l2,-2 2,3 2,-2 2,2 2,-3 2,2 2,-1 2,2' },
  ],

  /** A hairline against the weight the toggle puts on the trace. */
  thickLines: [
    { t: 'path', d: 'M3,9 H21', w: 1 },
    { t: 'path', d: 'M3,16 H21', w: 3.5 },
  ],

  /** A trace with a marker on every sample it was drawn from. */
  dataPoints: [
    { t: 'path', d: 'M3,17 L9,8 L15,14 L21,6' },
    { t: 'dot', x: 3, y: 17 },
    { t: 'dot', x: 9, y: 8 },
    { t: 'dot', x: 15, y: 14 },
    { t: 'dot', x: 21, y: 6 },
  ],

  /** Lines: straight between the samples, which is what a scope has always drawn. */
  drawLines: [{ t: 'path', d: 'M3,17 L9,7 L15,15 L21,5' }],

  /** sin(x)/x: the band-limited curve those same samples came from. */
  drawSinc: [{ t: 'path', d: 'M2,12 C5,3 9,3 12,12 C15,21 19,21 22,12' }],

  /**
   * Snap to bin: a tone pushed to the centre of the bin it is in — Joost's drawing of 2026-11-21
   * (`docs/design/snap-to-bin-centre.svg`), <b>in that drawing's own three colours</b> since
   * 2026-11-22, when the View figures were given theirs and this one was asked for with them.
   *
   * <p>It is a <em>spectrum</em> rather than a pair of bin edges: a baseline with the neighbouring
   * bins standing on it and the tone taller between them, which is what the chart beside this
   * control actually shows. The arrows are what the setting does to it, so they are the mark —
   * and the flags stay on them even though the ink now decides the colour, because they are still
   * the honest answer to "which of these shapes is an annotation" for anything that asks.</p>
   *
   * <p>The three widths are the drawing's too (1.6 / 2.2 / 1.9 against the sheet's default 1.8),
   * which is what keeps the tone reading as the taller, heavier line it is meant to be.</p>
   */
  snapToBin: [
    { t: 'path', d: 'M3,21 H21', w: 1.6, ink: SUPPLIED_INK.structure },
    { t: 'path', d: 'M8,4 V19', w: 1.6, ink: SUPPLIED_INK.structure },
    { t: 'path', d: 'M16,4 V19', w: 1.6, ink: SUPPLIED_INK.structure },
    { t: 'path', d: 'M12,21 V5', w: 2.2, ink: SUPPLIED_INK.blue },
    { t: 'path', d: 'M2.5,12 H6', w: 1.9, mark: true, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M4.6,10.2 L6.4,12 L4.6,13.8', w: 1.9, mark: true, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M21.5,12 H18', w: 1.9, mark: true, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M19.4,10.2 L17.6,12 L19.4,13.8', w: 1.9, mark: true, ink: SUPPLIED_INK.orange },
  ],

  /**
   * The two couplings, from Joost's drawings of 2026-11-20: a wave sitting clear of the dashed zero
   * line with an arrow on the offset it keeps, against the same wave centred on that line.
   *
   * <p><b>Drawn in one ink like everything else here, where the drawings carry three.</b> These sit
   * on a segmented toggle, where the lit option already takes its own hue through the option's
   * <code>dot</code> and the unlit one is grey — so a figure that carried its own colours would
   * either fight that or say "on" twice. What tells the pair apart is where the wave sits against
   * the line, which is a difference of <em>shape</em> and survives being grey, a black-and-white
   * screenshot, and 16 px.</p>
   */
  couplingDc: [
    { t: 'path', d: 'M3,19 H21', dash: true },
    { t: 'path', d: 'M3,9 C4.5,4 6,4 7.5,9 C9,14 10.5,14 12,9 C13.5,4 15,4 16.5,9' },
    { t: 'path', d: 'M20,18 V10 M18.4,11.6 L20,10 L21.6,11.6' },
  ],

  couplingAc: [
    { t: 'path', d: 'M3,12 H21', dash: true },
    {
      t: 'path',
      d: 'M3,12 C4.5,7 6,7 7.5,12 C9,17 10.5,17 12,12 C13.5,7 15,7 16.5,12 C18,17 19.5,17 21,12',
    },
  ],

  // ---- The stimuli the generator offers ----
  //
  // Each carries its signal *and* what is worth noticing about it — a zero line, an envelope, the
  // crossings, the width that is being chosen — flagged `mark` or drawn dashed so the stimulus
  // picker can colour the two apart (Joost's design of 2026-11-20). The annotation is what makes a
  // square tell itself from a pulse at 24 px: both are a pair of levels, and only one of them is
  // marked at its own duty.

  /** One tone, and a second behind it: the ordinary stimulus, and the two-tone case. */
  stimTones: [
    { t: 'path', d: 'M2,12 C4,5 7,5 9,12 C11,19 14,19 16,12' },
    { t: 'path', d: 'M14,12 C15.5,8 17.5,8 19,12 C20.5,16 21,16 22,14', w: 1, mark: true },
  ],

  /** A comb across the band, standing on the floor the gaps are measured in. */
  stimMultitone: [
    { t: 'path', d: 'M3,20 V7' },
    { t: 'path', d: 'M7,20 V5' },
    { t: 'path', d: 'M11,20 V8' },
    { t: 'path', d: 'M15,20 V5' },
    { t: 'path', d: 'M19,20 V9' },
    { t: 'path', d: 'M2,21 H22', mark: true },
    { t: 'dot', x: 5, y: 18.6, r: 1.1, mark: true },
    { t: 'dot', x: 9, y: 18.6, r: 1.1, mark: true },
    { t: 'dot', x: 13, y: 18.6, r: 1.1, mark: true },
    { t: 'dot', x: 17, y: 18.6, r: 1.1, mark: true },
  ],

  /** Noise: no shape at all, which is the shape of it — inside an envelope that says so. */
  stimNoise: [
    { t: 'path', d: 'M2,12 L4,6 L5,16 L7,8 L9,18 L11,7 L13,15 L15,5 L17,17 L19,9 L21,14 L22,11' },
    { t: 'path', d: 'M2,4 H22', dash: true },
    { t: 'path', d: 'M2,20 H22', dash: true },
  ],

  /** Hard-switched, with the crossings marked: the edge is the whole point of this one. */
  stimSquare: [
    { t: 'path', d: 'M2,18 H7 V6 H13 V18 H18 V6 H22' },
    { t: 'path', d: 'M2,12 H22', dash: true },
    { t: 'dot', x: 7, y: 12, r: 1.3, mark: true },
    { t: 'dot', x: 13, y: 12, r: 1.3, mark: true },
    { t: 'dot', x: 18, y: 12, r: 1.3, mark: true },
  ],

  /** The ramps, between the two levels they run to. */
  stimTriangle: [
    { t: 'path', d: 'M2,18 L7,6 L12,18 L17,6 L22,18' },
    { t: 'path', d: 'M2,6 H22', dash: true },
    { t: 'path', d: 'M2,18 H22', dash: true },
    { t: 'dot', x: 7, y: 6, r: 1.3, mark: true },
    { t: 'dot', x: 17, y: 6, r: 1.3, mark: true },
  ],

  /** A ramp and a drop, which is the asymmetry the kind is chosen for. */
  stimSawtooth: [
    { t: 'path', d: 'M2,18 L9,6 V18 L16,6 V18 L22,10' },
    { t: 'path', d: 'M2,12 H22', dash: true },
    { t: 'dot', x: 9, y: 6, r: 1.3, mark: true },
    { t: 'dot', x: 16, y: 6, r: 1.3, mark: true },
  ],

  /** A square with its duty chosen, drawn narrow and dimensioned at the width that is the choice. */
  stimPulse: [
    { t: 'path', d: 'M2,17 H7 V6 H10 V17 H16 V6 H19 V17 H22' },
    { t: 'path', d: 'M7,20 V22', mark: true },
    { t: 'path', d: 'M10,20 V22', mark: true },
    { t: 'path', d: 'M7,21 H10', mark: true },
  ],

  /** A tone switched on and off in whole cycles, between the gates that switch it. */
  stimBurst: [
    { t: 'path', d: 'M2,12 H4 C5,7 6.5,7 7.5,12 C8.5,17 10,17 11,12 H13' },
    { t: 'path', d: 'M13,12 H22', dash: true },
    { t: 'path', d: 'M4,5 V19', mark: true },
    { t: 'path', d: 'M11,5 V19', mark: true },
  ],

  /** A steady voltage, signed — the line, the zero it sits above, and the offset between them. */
  stimDc: [
    { t: 'path', d: 'M2,8 H22' },
    { t: 'path', d: 'M2,17 H22', dash: true },
    { t: 'path', d: 'M18,9 V16', mark: true },
    { t: 'path', d: 'M16.8,10.6 L18,9 L19.2,10.6', mark: true },
    { t: 'path', d: 'M16.8,14.4 L18,16 L19.2,14.4', mark: true },
  ],

  /** A file, with a waveform on it. */
  stimArbitrary: [
    { t: 'path', d: 'M5,3 H15 L19,7 V21 H5 Z', mark: true },
    { t: 'path', d: 'M15,3 V7 H19', mark: true },
    { t: 'path', d: 'M8,15 C9,11 10.5,11 11.5,15 C12.5,19 14,19 15,15' },
  ],

  // ---- The cursors (the Display rail) ----

  /** Two cursors across the time axis, with the span they measure between them. */
  cursorTime: [
    { t: 'path', d: 'M7,3 V21', dash: true },
    { t: 'path', d: 'M17,3 V21', dash: true },
    { t: 'path', d: 'M7,12 H17' },
    { t: 'path', d: 'M10,9 L7,12 L10,15' },
    { t: 'path', d: 'M14,9 L17,12 L14,15' },
  ],

  /** The same pair across the level axis — the one that gates nothing. */
  cursorLevel: [
    { t: 'path', d: 'M3,7 H21', dash: true },
    { t: 'path', d: 'M3,17 H21', dash: true },
    { t: 'path', d: 'M12,7 V17' },
    { t: 'path', d: 'M9,10 L12,7 L15,10' },
    { t: 'path', d: 'M9,14 L12,17 L15,14' },
  ],

  // ---- How the scope re-arms (Mode) ----

  /** Auto: it keeps drawing whatever happens, and the dashed bar is the deadline it gives up on. */
  modeAuto: [
    { t: 'path', d: 'M2,18 H6 V7 H11 V18 H15 V7 H18' },
    { t: 'path', d: 'M21,4 V20', dash: true },
  ],

  /** Normal: one record per crossing of the level, and nothing at all without one. */
  modeNormal: [
    { t: 'path', d: 'M2,18 H8 V7 H14 V18 H22' },
    { t: 'path', d: 'M2,12 H22', dash: true },
  ],

  /** Single: one edge, then the bar it stops against. */
  modeSingle: [
    { t: 'path', d: 'M2,18 H9 V7 H16' },
    { t: 'path', d: 'M20,5 V19' },
  ],

  /** Roll: the samples scroll by, with no trigger at all. */
  modeRoll: [
    { t: 'path', d: 'M2,14 C4,8 7,8 9,14 C11,20 14,20 16,14' },
    { t: 'path', d: 'M18,10 L22,14 L18,18' },
  ],

  // ---- What is done with the records after the first (Records) ----

  /** Normal: each record replaces the one before it, so there is one trace. */
  accNormal: [{ t: 'path', d: 'M2,15 L7,7 L12,15 L17,7 L22,15' }],

  /** Average: several records, and the mean drawn through them. */
  accAverage: [
    { t: 'path', d: 'M2,12 L7,4 L12,12 L17,4 L22,12', w: 0.9 },
    { t: 'path', d: 'M2,18 L7,10 L12,18 L17,10 L22,18', w: 0.9 },
    { t: 'path', d: 'M2,15 L7,7 L12,15 L17,7 L22,15' },
  ],

  /** Envelope: the band every sample position has swept — two outlines and no trace between them. */
  accEnvelope: [
    { t: 'path', d: 'M2,11 L7,3 L12,11 L17,3 L22,11' },
    { t: 'path', d: 'M2,21 L7,13 L12,21 L17,13 L22,21' },
  ],

  // ---- What the trigger watches (Source) ----

  /** The generator's own repeat, which is what the stimulus trigger fires on. */
  sourceStimulus: [
    { t: 'path', d: 'M2,12 C4,6 7,6 9,12 C11,18 14,18 16,12' },
    { t: 'path', d: 'M16,12 H22' },
    { t: 'path', d: 'M19,9 L22,12 L19,15' },
  ],

  // ---- The trigger kinds ----

  /** Edge: a crossing of the level. */
  kindEdge: [
    { t: 'path', d: 'M2,18 H9 L13,6 H22' },
    { t: 'path', d: 'M2,12 H22', dash: true },
  ],

  /** Pulse width: one pulse picked out by how long it is, with the width marked under it. */
  kindPulse: [
    { t: 'path', d: 'M2,15 H9 V5 H13 V15 H22' },
    { t: 'path', d: 'M9,18 V22' },
    { t: 'path', d: 'M13,18 V22' },
    { t: 'path', d: 'M9,20 H13' },
  ],

  /** Window: a band, and the signal going through it. */
  kindWindow: [
    { t: 'path', d: 'M2,7 H22', dash: true },
    { t: 'path', d: 'M2,17 H22', dash: true },
    { t: 'path', d: 'M2,21 C7,21 8,3 12,3 C16,3 17,21 22,21' },
  ],

  /** Dropout: a signal that stops, and the nothing that follows it. */
  kindDropout: [
    { t: 'path', d: 'M2,16 L5,8 L8,16 L11,8 L14,16' },
    { t: 'path', d: 'M15,16 H22', dash: true },
  ],

  /** Runt: a good pulse beside one that did not reach the far threshold. */
  kindRunt: [
    { t: 'path', d: 'M2,6 H22', dash: true },
    { t: 'path', d: 'M2,18 H22', dash: true },
    { t: 'path', d: 'M2,18 H5 V6 H9 V18 H13 V12 H17 V18 H22' },
  ],

  // ---- Which way through it (Slope) ----

  slopeRising: [{ t: 'path', d: 'M3,19 H10 L14,5 H21' }],
  slopeFalling: [{ t: 'path', d: 'M3,5 H10 L14,19 H21' }],
  /** Either: one of each, which is what a pulse is. */
  slopeEither: [{ t: 'path', d: 'M3,19 H7 L10,5 H14 L17,19 H21' }],

  // ---- The width condition ----

  /**
   * Shorter than: the measured span above, the limit dashed below it. One figure per condition
   * rather than one figure with a sign, because what a reader is choosing between is three
   * relations, and the two span lengths say which is which without a word.
   */
  widthLess: [
    { t: 'path', d: 'M4,5 V11' },
    { t: 'path', d: 'M10,5 V11' },
    { t: 'path', d: 'M4,8 H10' },
    { t: 'path', d: 'M4,13 V19' },
    { t: 'path', d: 'M20,13 V19' },
    { t: 'path', d: 'M4,16 H20', dash: true },
  ],

  /** Longer than: the same pair the other way up. */
  widthGreater: [
    { t: 'path', d: 'M4,5 V11' },
    { t: 'path', d: 'M20,5 V11' },
    { t: 'path', d: 'M4,8 H20' },
    { t: 'path', d: 'M4,13 V19' },
    { t: 'path', d: 'M10,13 V19' },
    { t: 'path', d: 'M4,16 H10', dash: true },
  ],

  /** Between: a span with a bound at each end. */
  widthBetween: [
    { t: 'path', d: 'M4,6 V18', dash: true },
    { t: 'path', d: 'M20,6 V18', dash: true },
    { t: 'path', d: 'M4,12 H20' },
    { t: 'path', d: 'M7,9 L4,12 L7,15' },
    { t: 'path', d: 'M17,9 L20,12 L17,15' },
  ],

  // ---- The figures beside the Trigger panel's number boxes ----

  /** Hysteresis: how far past the level the signal must go before it counts. */
  valueHysteresis: [
    { t: 'path', d: 'M3,12 H21' },
    { t: 'path', d: 'M3,8 H21', dash: true },
    { t: 'path', d: 'M3,16 H21', dash: true },
  ],

  /** Holdoff: a trigger, then a stretch in which nothing may trigger again. */
  valueHoldoff: [
    { t: 'path', d: 'M4,5 V19' },
    { t: 'path', d: 'M4,12 H18', dash: true },
    { t: 'path', d: 'M15,9 L18,12 L15,15' },
    { t: 'path', d: 'M20,5 V19' },
  ],

  /** Auto timeout: how long it waits before giving up and drawing what it has. */
  valueTimeout: [
    { t: 'path', d: 'M3,12 H14', dash: true },
    { t: 'path', d: 'M17,7 L21,17' },
    { t: 'path', d: 'M21,7 L17,17' },
  ],

  /**
   * The three views, from Joost's drawings of 2026-11-22 (<code>docs/design/view-yt.svg</code> and
   * its two siblings): the waveform against its axes, the Lissajous figure on a crosshair, and an
   * eye with the cycle behind it ghosted.
   *
   * <p><b>One ink, like the couplings above</b>, where the drawings carry three. These sit on the
   * same segmented toggle, where the lit option already takes a colour and the unlit ones are the
   * track's grey — which is exactly the "inactive grey, active in colour" the design asks for, got
   * for nothing. What tells the three apart is their shape, which survives being grey, a
   * black-and-white report and 16 px.</p>
   *
   * <p>The annotations each drawing marks in a second colour — the axis arrowhead, the dot on the
   * locus, the arrows across the eye's opening — are flagged as marks anyway, so the data says
   * which is signal and which is a note on it even where nothing reads the split.</p>
   */
  viewWaveform: [
    { t: 'path', d: 'M3,3.5 V20.5 H21', w: 1.4, ink: SUPPLIED_INK.structure },
    {
      t: 'path',
      d: 'M4.5,12 C5.8,7 7.2,7 8.5,12 C9.8,17 11.2,17 12.5,12 C13.8,7 15.2,7 16.5,12 C17.8,17 19.2,17 20.5,12',
      w: 2,
      ink: SUPPLIED_INK.teal,
    },
    { t: 'path', d: 'M19.3,19 L20.8,20.5 L19.3,22', mark: true, ink: SUPPLIED_INK.orange },
  ],

  viewXy: [
    { t: 'path', d: 'M12,3 V21 M3,12 H21', w: 1.4, ink: SUPPLIED_INK.structure },
    { t: 'path', d: XY_LOCUS, w: 2, ink: SUPPLIED_INK.violet },
    { t: 'dot', x: 17.66, y: 5.5, r: 1.5, mark: true, ink: SUPPLIED_INK.teal },
  ],

  viewEye: [
    // The cycle behind, at the axes' own weight: a ghost is what says this picture is every cycle
    // drawn over the others rather than one of them.
    { t: 'path', d: 'M4.8,6 C6.8,6 6.8,18 8.8,18', w: 1.4, ink: SUPPLIED_INK.structure },
    { t: 'path', d: 'M4.8,18 C6.8,18 6.8,6 8.8,6', w: 1.4, ink: SUPPLIED_INK.structure },
    { t: 'path', d: 'M16.8,6 C18.8,6 18.8,18 20.8,18', w: 1.4, ink: SUPPLIED_INK.structure },
    { t: 'path', d: 'M16.8,18 C18.8,18 18.8,6 20.8,6', w: 1.4, ink: SUPPLIED_INK.structure },
    { t: 'path', d: 'M2,6 H4', w: 2, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M2,18 H4', w: 2, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M4,6 C6,6 6,18 8,18', w: 2, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M4,18 C6,18 6,6 8,6', w: 2, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M8,6 H16', w: 2, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M8,18 H16', w: 2, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M16,6 C18,6 18,18 20,18', w: 2, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M16,18 C18,18 18,6 20,6', w: 2, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M20,6 H22', w: 2, ink: SUPPLIED_INK.orange },
    { t: 'path', d: 'M20,18 H22', w: 2, ink: SUPPLIED_INK.orange },
    // The opening, which is the whole of what the picture is read for.
    { t: 'path', d: 'M12,9 V15', mark: true, ink: SUPPLIED_INK.blue },
    { t: 'path', d: 'M10.6,10.4 L12,9 L13.4,10.4', mark: true, ink: SUPPLIED_INK.blue },
    { t: 'path', d: 'M10.6,13.6 L12,15 L13.4,13.6', mark: true, ink: SUPPLIED_INK.blue },
  ],

  /** Dots: the samples alone, joined by nothing. */
  drawDots: [
    { t: 'dot', x: 4, y: 16, r: 2 },
    { t: 'dot', x: 9, y: 8, r: 2 },
    { t: 'dot', x: 15, y: 15, r: 2 },
    { t: 'dot', x: 20, y: 7, r: 2 },
  ],
}

/** The default marker radius, mirrored by the test that keeps every figure inside its box. */
export const TOGGLE_DOT_RADIUS = 1.8

/** One toggle's figure, or nothing — a button with no figure draws its label instead. */
export function scopeToggleIcon(name: string): ScopeToggleShape[] {
  return SCOPE_TOGGLE_ICONS[name] ?? []
}
