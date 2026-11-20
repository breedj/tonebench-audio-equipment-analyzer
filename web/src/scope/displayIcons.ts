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

export type ScopeToggleShape =
  /**
   * A stroked path. <code>w</code> overrides the stroke width, which only Thick lines needs;
   * <code>dash</code> marks a line that is a <em>setting</em> rather than signal — a cursor, a
   * threshold, a band edge — which is how the chart itself draws all three.
   */
  | { t: 'path'; d: string; w?: number; dash?: boolean }
  /** A marker, for the figures that are about where the samples fell. */
  | { t: 'dot'; x: number; y: number; r?: number }

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

  // ---- The stimuli the generator offers ----

  /** One tone, and a second behind it: the ordinary stimulus, and the two-tone case. */
  stimTones: [
    { t: 'path', d: 'M2,12 C4,5 7,5 9,12 C11,19 14,19 16,12' },
    { t: 'path', d: 'M14,12 C15.5,8 17.5,8 19,12 C20.5,16 21,16 22,14', w: 1 },
  ],

  /** A comb across the band. */
  stimMultitone: [
    { t: 'path', d: 'M3,20 V7' },
    { t: 'path', d: 'M7,20 V5' },
    { t: 'path', d: 'M11,20 V8' },
    { t: 'path', d: 'M15,20 V5' },
    { t: 'path', d: 'M19,20 V9' },
  ],

  /** Noise: no shape at all, which is the shape of it. */
  stimNoise: [{ t: 'path', d: 'M2,12 L4,6 L5,16 L7,8 L9,18 L11,7 L13,15 L15,5 L17,17 L19,9 L21,14 L22,11' }],

  stimSquare: [{ t: 'path', d: 'M2,18 H7 V6 H13 V18 H18 V6 H22' }],
  stimTriangle: [{ t: 'path', d: 'M2,18 L7,6 L12,18 L17,6 L22,18' }],
  /** A ramp and a drop, which is the asymmetry the kind is chosen for. */
  stimSawtooth: [{ t: 'path', d: 'M2,18 L9,6 V18 L16,6 V18 L22,10' }],
  /** A square with its duty chosen, drawn narrow. */
  stimPulse: [
    { t: 'path', d: 'M2,17 H7 V6 H10 V17 H16 V6 H19 V17 H22' },
    { t: 'path', d: 'M7,21 H10' },
  ],

  /** A tone switched on and off in whole cycles. */
  stimBurst: [
    { t: 'path', d: 'M2,12 H4 C5,7 6.5,7 7.5,12 C8.5,17 10,17 11,12 H13' },
    { t: 'path', d: 'M13,12 H22', dash: true },
  ],

  /** A steady voltage, signed — the line and the zero it sits above. */
  stimDc: [
    { t: 'path', d: 'M2,8 H22' },
    { t: 'path', d: 'M2,17 H22', dash: true },
  ],

  /** A file, with a waveform on it. */
  stimArbitrary: [
    { t: 'path', d: 'M5,3 H15 L19,7 V21 H5 Z' },
    { t: 'path', d: 'M8,15 C9,11 10.5,11 11.5,15 C12.5,19 14,19 15,15' },
  ],

  // ---- The math channel's operators ----
  //
  // The operator itself rather than a waveform: what a reader is choosing between here is seven
  // pieces of arithmetic, and the sign for each is the thing they already know it by.

  mathAdd: [
    { t: 'path', d: 'M12,5 V19' },
    { t: 'path', d: 'M5,12 H19' },
  ],
  mathSubtract: [{ t: 'path', d: 'M5,12 H19' }],
  mathMultiply: [
    { t: 'path', d: 'M6,6 L18,18' },
    { t: 'path', d: 'M18,6 L6,18' },
  ],
  mathDivide: [
    { t: 'path', d: 'M5,12 H19' },
    { t: 'dot', x: 12, y: 6.5, r: 1.5 },
    { t: 'dot', x: 12, y: 17.5, r: 1.5 },
  ],
  /** Negate: a waveform turned over, which says more here than a bare minus would. */
  mathNegate: [
    { t: 'path', d: 'M3,8 C5,3 8,3 10,8 C12,13 15,13 17,8', w: 1 },
    { t: 'path', d: 'M3,16 C5,21 8,21 10,16 C12,11 15,11 17,16' },
  ],
  /** A rate: a tangent on a curve. */
  mathDerivative: [
    { t: 'path', d: 'M3,19 C9,19 13,13 15,5', w: 1 },
    { t: 'path', d: 'M6,19 L20,9' },
  ],
  /** An accumulation: the sign itself, which is what every datasheet writes it as. */
  mathIntegral: [{ t: 'path', d: 'M9,21 C7,21 6.5,18 7.5,13 C8.5,8 10,4 12.5,3 C14.5,2.2 16,3.5 15.5,5.5' }],

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
