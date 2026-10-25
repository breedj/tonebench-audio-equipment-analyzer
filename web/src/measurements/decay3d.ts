import type { DecayChartMode, DecayPalette, DecaySurface } from '../api/client'
import {
  levelAt,
  orientSurface,
  paletteIndex,
  paletteLut,
} from './decay'

/**
 * <p>What <c>DecaySurface3dChart</c> hands the ProEssentials engine, and the only place that
 * conversion happens. It lives here rather than in the component for the reason
 * <c>decay.ts</c> exists: nothing in a <c>.vue</c> file is testable under the node environment
 * vitest runs in, and a Z-matrix transpose is the archetypal fault that draws a convincing
 * picture of the wrong data.</p>
 *
 * <p>Everything reads through <c>orientSurface</c> and <c>levelAt</c>, so the two charts cannot
 * drift apart about which way round a waterfall goes or where a slice's data begins.</p>
 */

/**
 * <p><b>Missing cells are NaN, and that is measured rather than chosen.</b> The engine carries a
 * <c>PeData.NullDataValue</c> property and a <c>PePlot.Option.SurfaceNullDataGaps</c> flag, and on
 * the 3D surface path <em>neither does anything</em>: a <c>-9999</c> sentinel is drawn as data, so
 * a burst decay whose low-frequency corner is 22 % missing auto-scaled its height axis to
 * <c>-10499 … +503</c> and squeezed the real -30…0 dB into a third of a percent of it, while the
 * gaps flag produced byte-identical renders either way. The same surface with NaN in those cells
 * scales to <c>-31.6 … +4.6</c> and leaves the corner undrawn. Verified 2026-10-25 against
 * proessentials 11.0.1.</p>
 */
export interface DecayMesh {
  /** Subsets — one per curve, back to front. */
  rows: number
  /** Points per curve. */
  cols: number
  /** The across coordinate of each column — frequency in Hz, or time, whichever this view runs across. */
  x: Float64Array
  /** Whether {@link x} is a frequency, and so whether that axis wants a logarithmic scale. */
  acrossIsFrequency: boolean
  /** Whether {@link z} is a frequency. The two are never both true. */
  depthIsFrequency: boolean
  /** Heights, row-major, <c>rows * cols</c> of them. NaN wherever the analysis has no answer. */
  y: Float64Array
  /** One depth coordinate per curve. */
  z: Float64Array
  /** How many cells came back NaN — the ragged corner, and a useful thing to assert on. */
  missing: number
}

/**
 * The surface as the engine wants it: counts, three coordinate blocks and a dense height matrix.
 *
 * @param rangeDb How far below 0 dB the chart reaches. Levels are clamped to it rather than
 * dropped, exactly as <c>levelY</c> clamps them in the Canvas 2D chart, so the two agree about
 * what the foot of the scale means.
 */
export function decayMesh(surface: DecaySurface, mode: DecayChartMode, rangeDb: number): DecayMesh {
  const view = orientSurface(surface, mode === 'WaterfallPeriods')
  const rows = view.depth.length
  const cols = view.across.length

  const y = new Float64Array(rows * cols)
  let missing = 0
  for (let r = 0; r < rows; r++) {
    for (let c = 0; c < cols; c++) {
      const level = view.level(r, c)
      if (level === null) {
        y[r * cols + c] = Number.NaN
        missing++
      } else {
        y[r * cols + c] = level < -rangeDb ? -rangeDb : level > 0 ? 0 : level
      }
    }
  }

  return {
    rows,
    cols,
    // The real values, and the frequency axis is told to scale itself logarithmically — which is
    // what puts true hertz on the ticks. (The column index plus point labels was the first attempt
    // and is a dead end: a 3D chart has no way to put strings on an axis, so the ticks read 0…399.)
    x: Float64Array.from(view.across),
    acrossIsFrequency: view.acrossIsFrequency,
    depthIsFrequency: view.depthIsFrequency,
    y,
    z: Float64Array.from(view.depth),
    missing,
  }
}

/**
 * The 256 colours the engine ramps the height axis through, as its own packed ARGB.
 *
 * <p><c>PeColor.SubsetColors[0..255]</c> is the height ramp on a surface — not
 * <c>ContourColorSet</c>, which offers ten fixed ramps and twenty overridable colours. 256 entries
 * is a 1:1 fit for <c>paletteLut</c>, so the palette, <c>paletteIndex</c> and
 * <c>PALETTE_FLOOR_FRACTION</c> all carry over exactly and a level means the same colour on both
 * charts.</p>
 */
export function decaySubsetColors(palette: DecayPalette, onDark: boolean): Uint32Array {
  const lut = paletteLut(palette, onDark)
  const out = new Uint32Array(256)
  for (let i = 0; i < 256; i++) {
    const index = paletteIndex(i / 255)
    out[i] = peColor(lut[index * 3], lut[index * 3 + 1], lut[index * 3 + 2])
  }
  return out
}

/** A ProEssentials colour: opaque, red in the low byte. */
export function peColor(r: number, g: number, b: number): number {
  return ((r | (g << 8) | (b << 16) | (255 << 24)) >>> 0)
}

/**
 * The proportions of the box the surface is drawn in — <b>fixed, not the data's own extents</b>.
 *
 * <p>Taking them from the slice and column counts is the obvious thing and it is wrong: a CSD is
 * 400 columns by 60 slices, so a box in data units is seven times wider than it is deep and the
 * surface is drawn edge-on and clipped. The axes here are frequency and time, which have no
 * common unit, so there is no proportion to preserve — the Canvas 2D chart does not preserve one
 * either, giving the depth axis a share of the plot box rather than a share of the slice count.</p>
 *
 * <p>So the box is a fixed shape and the <em>view</em> is what the panel's two numbers move:
 * Rotation swings the camera round it and Depth tilts it. A fixed box is also what lets one zoom
 * frame every surface, whatever its resolution.</p>
 */
export function decayGridAspect(): { x: number; y: number; z: number } {
  return { x: 100, y: 45, z: 90 }
}

/**
 * How far to pull the camera back, for a plot of this shape.
 *
 * <p>Measured rather than derived (2026-10-25, against the fixed box {@link decayGridAspect}
 * returns): <c>-220</c> frames the surface with nothing touching an edge at every aspect from
 * 0.98 to 4.0, which is every layout this app actually produces. Below about 0.95 — both rails
 * open on a narrow window — it clips, and <c>-320</c> covers that.</p>
 *
 * <p>The engine's own <c>DxFitControlShape</c> is not the answer: it ignores the zoom and fills
 * the plot, measuring 65–99 % of the canvas covered with thousands of pixels against the edges at
 * every zoom tried. And the relation is not monotonic at extreme shapes — pulling further back
 * from a very tall, narrow plot can clip again — so this is a measured step rather than a formula.</p>
 */
export const DECAY_3D_ZOOM = -220
export const DECAY_3D_ZOOM_NARROW = -320
/** Below this width-over-height the surface no longer fits at the ordinary zoom. */
export const DECAY_3D_NARROW_ASPECT = 0.95

export function decayCameraZoom(widthPx: number, heightPx: number): number {
  if (!(widthPx > 0) || !(heightPx > 0)) return DECAY_3D_ZOOM
  return widthPx / heightPx < DECAY_3D_NARROW_ASPECT ? DECAY_3D_ZOOM_NARROW : DECAY_3D_ZOOM
}

/**
 * The caption a report or a screenshot carries. Shared with the Canvas 2D chart rather than
 * written out twice, so a chapter reads identically whichever engine drew it.
 */
export function decayExportTitle(
  surface: DecaySurface | null,
  mode: DecayChartMode,
  rangeDb: number,
): string {
  const kind = surface?.sliceAxis === 'Periods' ? 'Burst decay' : 'Cumulative spectral decay'
  const how = mode === 'Sonogram'
    ? 'sonogram'
    : mode === 'WaterfallPeriods'
      ? 'waterfall, by period'
      : 'waterfall'
  return `${kind} — ${how}, ${rangeDb} dB range`
}

/**
 * Whether the 3D engine can draw this mode at all. The sonogram is a flat colour map — one
 * <c>putImageData</c> blit in the Canvas 2D chart, already correct and already fast — so there is
 * nothing for a lit mesh to add, and the engine's own flat-contour view is a different picture.
 */
export function decayModeIs3d(mode: DecayChartMode): boolean {
  return mode !== 'Sonogram'
}

/** Re-exported so the component has one import for its data work. */
export { levelAt }
