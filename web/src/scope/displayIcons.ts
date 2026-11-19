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
  /** A stroked path. <code>w</code> overrides the stroke width, which only Thick lines needs. */
  | { t: 'path'; d: string; w?: number }
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
