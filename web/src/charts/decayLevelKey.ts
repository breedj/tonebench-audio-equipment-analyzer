import { levelTicks } from '../measurements/decay'
import type { Graph3dRgb } from './graph3d'

/**
 * <p>The 3D decay surface's colour key, drawn by this app rather than by the chart library.</p>
 *
 * <p><b>Why ours.</b> `vis-graph3d` draws its own at `clientWidth - margin`, top right, and offers
 * no way to put it anywhere else: `margin` sets the inset from the top *and* the right at once, so
 * the one value that would carry the bar to the left edge takes its top down with it. The key was
 * wanted on the left (Joost, 2026-10-30), so `showLegend` is off and this takes over — which also
 * retires the direct write to `margin` and the second `redraw()` that write cost, the library
 * having already painted by the time a field can be set.</p>
 *
 * <p>It is drawn on an overlay canvas over the chart's own, and onto the export's canvas by the
 * same function, so the two cannot come to disagree about where the key is or what it says. The
 * colours come through the same `colourAt` the surface is built from — one source of colour, so a
 * key cannot drift from the picture it is a key to, which is the rule `DecaySurfaceChart`'s own
 * key already follows.</p>
 */

/** Everything the key occupies, in CSS pixels from the plot's top left. */
export interface DecayLevelKeyLayout {
  x: number
  y: number
  width: number
  height: number
}

/** How far the bar sits from the plot's left and top edges. */
export const DECAY_KEY_INSET_PX = 14
/** The bar's width; its labels stand to the right of it, where the library's stood to the left. */
export const DECAY_KEY_WIDTH_PX = 18
/** The shortest the bar may be, for a chart too short to give it a quarter of the height. */
export const DECAY_KEY_MIN_HEIGHT_PX = 100

/**
 * Where the key goes: the left edge, a quarter of the plot tall, never shorter than
 * {@link DECAY_KEY_MIN_HEIGHT_PX} and never taller than the room there is.
 *
 * <p>A quarter is the library's own proportion, kept so the swap does not resize it as well as
 * move it. Separate from the painting so it can be tested — vitest runs in the node environment
 * here, where there is no canvas to paint on but arithmetic is arithmetic.</p>
 */
export function decayLevelKeyLayout(plotHeight: number): DecayLevelKeyLayout {
  const room = Math.max(0, plotHeight - 2 * DECAY_KEY_INSET_PX)
  return {
    x: DECAY_KEY_INSET_PX,
    y: DECAY_KEY_INSET_PX,
    width: DECAY_KEY_WIDTH_PX,
    height: Math.min(room, Math.max(Math.round(plotHeight * 0.25), DECAY_KEY_MIN_HEIGHT_PX)),
  }
}

export interface DecayLevelKeyPaint {
  /** The plot's size in CSS pixels — the key is laid out from its height. */
  plotHeight: number
  /** dB below 0 the surface spans, which is what the bar's foot stands for. */
  rangeDb: number
  /** The surface's own colormap: 0 at the bottom of the range, 1 at the top. */
  colourAt: (fraction: number) => Graph3dRgb
  /** The ink the labels and the bar's outline take. */
  ink: string
}

/**
 * Paints the key onto `ctx`, which is expected to be in CSS pixels already (a caller on a hidpi
 * screen sets the transform before calling, as the live overlay does).
 *
 * <p>Row by row out of `colourAt` rather than a canvas gradient built from stops: the ramp is not
 * linear in the palettes this app offers — `paletteIndex` lifts each off its own floor — so a
 * two-stop gradient would be a different ramp from the surface's.</p>
 */
export function drawDecayLevelKey(ctx: CanvasRenderingContext2D, paint: DecayLevelKeyPaint): void {
  const layout = decayLevelKeyLayout(paint.plotHeight)
  if (layout.height <= 0) return

  const rows = Math.max(1, Math.round(layout.height))
  for (let row = 0; row < rows; row++) {
    const { r, g, b } = paint.colourAt((rows - 1 - row) / (rows - 1 || 1))
    ctx.fillStyle = `rgb(${r}, ${g}, ${b})`
    ctx.fillRect(layout.x, layout.y + row, layout.width, 1)
  }

  ctx.strokeStyle = paint.ink
  ctx.lineWidth = 1
  ctx.strokeRect(layout.x + 0.5, layout.y + 0.5, layout.width, layout.height)

  ctx.font = '11px system-ui, sans-serif'
  ctx.fillStyle = paint.ink
  ctx.textAlign = 'left'
  ctx.textBaseline = 'middle'
  const span = paint.rangeDb > 0 ? paint.rangeDb : 1
  for (const db of levelTicks(paint.rangeDb)) {
    const y = layout.y + (-db / span) * layout.height
    ctx.fillText(db.toFixed(0), layout.x + layout.width + 5, y)
  }

  ctx.textAlign = 'center'
  ctx.textBaseline = 'top'
  ctx.fillText('dB', layout.x + layout.width / 2, layout.y + layout.height + 6)
}
