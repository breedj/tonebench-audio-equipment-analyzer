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
  /**
   * The across coordinate of each column — frequency in Hz, or time, whichever this view runs
   * across. <b>Negated unless it is the frequency</b>, which {@link acrossNegated} says.
   */
  x: Float64Array
  /** Whether {@link x} is a frequency, and so whether that axis wants a logarithmic scale. */
  acrossIsFrequency: boolean
  /**
   * Whether {@link x} has been negated, which is to say whether its axis is the linear one.
   *
   * <p>The chart sets the engine's <c>InvertedXAxis</c> either way, because the camera sits on the
   * side of the box that mirrors this axis — see <c>DecaySurface3dChart.azimuthFor</c> — and that
   * flag is what puts it back. What the flag <em>does</em> differs with the scale, and both halves
   * were measured against proessentials 11.0.1 on 2026-10-25:</p>
   *
   * <ul>
   *   <li><b>Logarithmic</b> (the frequency axis): the flag reverses the drawing and the labels
   *   together, so the data is left alone. A log scale has no negative half anyway.</li>
   *   <li><b>Linear</b> (the time axis): the flag reverses the labels only — setting it and
   *   clearing it render byte for byte the same picture — so the data has to be negated to
   *   reverse the drawing, and the flag hands the sign back to the ticks. This is exactly what the
   *   vendor's own documentation says of the whole family: "subset data must be multiplied by
   *   (-1) when passed into the chart to achieve the desired effect".</li>
   * </ul>
   */
  acrossNegated: boolean
  /** Whether {@link z} is a frequency. The two are never both true. */
  depthIsFrequency: boolean
  /** Heights, row-major, <c>rows * cols</c> of them. NaN wherever the analysis has no answer. */
  y: Float64Array
  /**
   * One depth coordinate per curve, in its own units and never negated.
   *
   * <p>Which end of it faces the viewer is the camera's business, not the data's — see
   * <c>DecaySurface3dChart.azimuthFor</c>. Negating this was tried for an afternoon, to pair with
   * the engine's <c>InvertedZAxis</c>, and that flag turns out to be <em>refused</em> by the 3D
   * control: written true it reads back false, where the X and Y ones take. Measured 2026-10-25
   * against proessentials 11.0.1.</p>
   */
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
    // The real values — the frequency axis is told to scale itself logarithmically, which is what
    // puts true hertz on the ticks. (The column index plus point labels was the first attempt and
    // is a dead end: a 3D chart has no way to put strings on an axis, so the ticks read 0…399.)
    // Negated on the linear axis only; see `acrossNegated` for the whole of why.
    x: Float64Array.from(view.across, (v) => (view.acrossIsFrequency ? v : -v)),
    acrossNegated: !view.acrossIsFrequency,
    acrossIsFrequency: view.acrossIsFrequency,
    depthIsFrequency: view.depthIsFrequency,
    y,
    // Plain values. The camera is what puts slice zero at the back.
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
 * Rotation swings the camera round it and Tilt raises it. A fixed box is also what lets one zoom
 * frame every surface, whatever its resolution.</p>
 *
 * <p><b>And it is a small box viewed from close to, not a large one viewed from far off</b>, which
 * is not a free choice: <c>DxViewportX</c>/<c>Y</c> — the viewport translation, and what the
 * engine's own shift-and-drag gesture writes — scale with the camera's distance. At ten times this
 * size and ten times the distance the picture is identical to the pixel, and the whole legal
 * translation range of -3..+3 moves the surface <em>seven pixels</em>, so the gesture looks broken.
 * At this size the same range moves it 47, and the gesture works.</p>
 */
export function decayGridAspect(): { x: number; y: number; z: number } {
  return { x: 10, y: 4.5, z: 9 }
}

/**
 * How far to pull the camera back, for a plot of this shape.
 *
 * <p>Measured rather than derived (2026-10-25, against the fixed box {@link decayGridAspect}
 * returns): the ordinary figure frames the surface with nothing touching an edge at every aspect
 * from 0.98 to 4.0, which is every layout this app actually produces. Below about 0.95 — both
 * rails open on a narrow window — it clips, and the narrow figure covers that. Both were measured
 * at ten times this box and ten times this distance, where the picture is identical, and scaled
 * with it.</p>
 *
 * <p>The engine's own <c>DxFitControlShape</c> is not the answer: it ignores the zoom and fills
 * the plot, measuring 65–99 % of the canvas covered with thousands of pixels against the edges at
 * every zoom tried. And the relation is not monotonic at extreme shapes — pulling further back
 * from a very tall, narrow plot can clip again — so this is a measured step rather than a formula.</p>
 */
export const DECAY_3D_ZOOM = -20
export const DECAY_3D_ZOOM_NARROW = -29
/** Below this width-over-height the surface no longer fits at the ordinary zoom. */
export const DECAY_3D_NARROW_ASPECT = 0.95

export function decayCameraZoom(widthPx: number, heightPx: number): number {
  if (!(widthPx > 0) || !(heightPx > 0)) return DECAY_3D_ZOOM
  return widthPx / heightPx < DECAY_3D_NARROW_ASPECT ? DECAY_3D_ZOOM_NARROW : DECAY_3D_ZOOM
}

/**
 * Where the light goes. One position, not one per camera angle.
 *
 * <p>The vendor demo's light lit its own default view and went dark as the camera moved round the
 * box, which is what the surface looked like once the default lean changed: measured on a 60x400
 * surface, the mean luminance of the lit pixels fell from 92.4 at azimuth 315 to 82.4 at azimuth
 * 10. Reported 2026-10-25.</p>
 *
 * <p><b>Turning the light with the camera was the obvious answer and is the worse one.</b> Swept
 * right round the circle in 30-degree steps, a light that follows the camera reads 77.3 at its
 * worst and is below 95 at five of the twelve angles, because the relation between a good light
 * and the camera is not a rotation about the height axis. This position — the demo's vector turned
 * 90 degrees about that axis — reads <b>96.3 at its worst and 129 at its best over the whole
 * circle</b>, and never goes dark at any angle the panel or a drag can reach. Over the lean the
 * panel actually offers it is flatter still: 107.5 to 116.7, a spread of 9.2 against the old
 * light's 31.5.</p>
 */
export const DECAY_LIGHT = { x: 7, y: -7, z: -2 } as const

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
