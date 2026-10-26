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
 * <p><b>The depth is 14 rather than 9 because the Canvas 2D chart is deeper than that</b> — asked
 * for 2026-10-25, so the two charts of one measurement read alike — and the camera was pulled back
 * with it, which is the half that cannot be skipped: at the old distance a 14-deep box runs off
 * the plot and takes the frequency label with it. See {@link DECAY_3D_ZOOM}, whose two figures
 * were scaled by this same 14/9.</p>
 *
 * <p><b>And it is a small box viewed from close to, not a large one viewed from far off</b>, which
 * is not a free choice: <c>DxViewportX</c>/<c>Y</c> — the viewport translation, and what the
 * engine's own shift-and-drag gesture writes — scale with the camera's distance. At ten times this
 * size and ten times the distance the picture is identical to the pixel, and the whole legal
 * translation range of -3..+3 moves the surface <em>seven pixels</em>, so the gesture looks broken.
 * At this size the same range moves it 47, and the gesture works.</p>
 */
export function decayGridAspect(lengths: DecayAxisLengths = DECAY_AXIS_LENGTH_DEFAULT): { x: number; y: number } {
  // <p><b>Only two of the engine's three aspect numbers may be written, and the third is the trap.</b>
  // It documents <c>GridAspectX</c> as the x axis "as relates to z" and <c>GridAspectY</c> as y
  // "as relates to z" — so z is already the unit those two are measured in — while
  // <c>GridAspectZ</c> is z "as relates to x". Writing all three states the shape twice and
  // contradicts itself, and the box then degenerates to a slab that no later value changes: the
  // three sliders looked completely inert on 2026-10-26, through a reload and through
  // <c>render()</c> alike, and that was why.</p>
  //
  // <p>So the depth is expressed by dividing the other two by it, and <c>GridAspectZ</c> is never
  // written at all. The multiplier is the default depth, which keeps the magnitude the camera was
  // measured against: at the defaults this returns 10 and 4.5, exactly the figures
  // {@link DECAY_3D_ZOOM} was framed around, so a measurement that never touches a slider is drawn
  // to the pixel as before.</p>
  //
  // <p>The engine reads the magnitude as well as the ratio, which is why the multiplier is needed
  // and why a plain <c>x/z</c> is wrong: normalised to about 1 the whole box is drawn some three
  // times smaller, and the camera would have to come in to match — and the viewport translation the
  // two Move sliders write scales with the camera's distance, so that trade buys a correct ratio and
  // breaks the gesture (see {@link DECAY_VIEW_MOVE_X}).</p>
  const depth = lengths.z > 0 ? lengths.z : DECAY_AXIS_LENGTH_DEFAULT.z
  const unit = DECAY_AXIS_LENGTH_DEFAULT.z
  return { x: (lengths.x / depth) * unit, y: (lengths.y / depth) * unit }
}

/** How long each edge of the box is drawn, in the engine's own grid-aspect units. */
export interface DecayAxisLengths {
  /** The across axis — frequency in the by-frequency views, time in the by-period one. */
  x: number
  /** The height axis, which is always the level in dB. */
  y: number
  /** The depth axis, the other of time and frequency. */
  z: number
}

/**
 * The lengths the box is drawn at unless a measurement says otherwise, and the range the three
 * sliders offer.
 *
 * <p>Asked for 2026-10-26 ("controls to set the length of the axes individually"), which is worth
 * having for the reason {@link decayGridAspect} gives: the three axes have no common unit, so
 * there is no proportion to be right and the shape is a reading preference. A flatter height makes
 * a shallow decay legible; a longer depth separates slices that overlap.</p>
 *
 * <p>The defaults are the fixed box these were measured against, so a measurement that never
 * touches them is drawn exactly as before. The camera's framing was measured against that box too
 * (see {@link DECAY_3D_ZOOM}), so stretching an axis a long way can push the surface off the plot
 * — which is what Magnification and the two Move sliders are then for.</p>
 */
/**
 * <p><b>The depth is the one the other two are measured against, so it rescales the picture.</b>
 * That is the engine's own frame and not a choice here: with <c>GridAspectZ</c> left alone (see
 * {@link decayGridAspect}) the depth edge is the unit, and x and y are lengths in it — so halving
 * the depth doubles the other two rather than shortening one edge, and the whole box is drawn
 * larger. Magnification is what compensates, which is part of why the two sit in the same section.
 * The two cannot be separated without writing the third aspect number, and writing it is what made
 * all three inert.</p>
 */
export const DECAY_AXIS_LENGTH_DEFAULT: DecayAxisLengths = { x: 10, y: 4.5, z: 14 }
export const DECAY_AXIS_LENGTH = { min: 2, max: 30, step: 0.5 } as const

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
 * <p><b>More negative is further back</b>, which is worth writing down because the sweep that
 * found it read as non-monotonic by eye and is not: counting surface pixels against the plot's own
 * border, a 14-deep box reads <b>41 edge pixels at -30, 0 at -34 and 0 at -40</b>, with the
 * drawn area falling 30.7 %, 24.9 %, 19.0 % of the canvas as it recedes. Note what the same
 * measurement says about the pairing before the box was deepened: at 9 deep and -20 the surface
 * put <b>252 pixels</b> against the border, so it had been clipping slightly all along, which
 * nothing had caught because the clipped part is the corner of a floor.</p>
 *
 * <p><b>Both figures were measured again on 2026-10-26 for how well they *fill* the plot, which is
 * a different question from whether they clip, and the narrow one was far out.</b> Asked for as a
 * default view matching a supplied picture. The limiting dimension is not the same at the two
 * shapes — a wide plot runs out of height and a near-square one runs out of width — which is the
 * whole reason this is per-aspect. Measured as the surface's own bounding box over the canvas, by
 * magnifying until it reached an edge: at <b>aspect 1.39</b> the surface already spans 0.21-0.89
 * of the height at the fitted distance and has about <b>10 %</b> left, while at <b>aspect 0.95</b>
 * it spans 0.18-0.85 of the width and has about <b>55 %</b>. So the ordinary figure moved a little
 * and the narrow one a lot; it had been scaled with the box the day before without being
 * re-measured, which is what left it pulled so far back — and a chart at a 1000-pixel window sits
 * right on the 0.95 threshold, so that over-conservative rung is what a bench actually saw.</p>
 *
 * <p>The limit is close to -31 at every aspect measured and is reached by a different edge at each
 * (the sides near 1.0, the floor by 1.4), so the ordinary figure is set one notch inside it:
 * at aspect 1.06, <b>-31 puts 217 pixels against the border while -33 leaves a left margin of
 * 0.086 and -34 of 0.104</b>, all three filling far more of the plot than the narrow rung did.</p>
 *
 * <p>The engine's own <c>DxFitControlShape</c> is not the answer: it ignores the zoom and fills
 * the plot, measuring 65–99 % of the canvas covered with thousands of pixels against the edges at
 * every zoom tried. And the relation is not monotonic at extreme shapes — pulling further back
 * from a very tall, narrow plot can clip again — so this is a measured step rather than a formula.</p>
 */
export const DECAY_3D_ZOOM = -33
export const DECAY_3D_ZOOM_NARROW = -38
/** Below this width-over-height the surface no longer fits at the ordinary zoom. */
export const DECAY_3D_NARROW_ASPECT = 0.95

export function decayCameraZoom(widthPx: number, heightPx: number): number {
  if (!(widthPx > 0) || !(heightPx > 0)) return DECAY_3D_ZOOM
  return widthPx / heightPx < DECAY_3D_NARROW_ASPECT ? DECAY_3D_ZOOM_NARROW : DECAY_3D_ZOOM
}

/**
 * The view controls the GigaSoft demo offers and this chart did not, added 2026-10-26 after a
 * side-by-side with it: where the surface sits, how big it is drawn, and where the light comes
 * from. They live in a section of their own, collapsed, because none of them is touched on an
 * ordinary visit — the defaults are the view the chart opens on, and these are for matching a
 * picture or lighting an awkward one.
 *
 * <p>Each is stored with the measurement, so a view that took some turning can be kept. That is
 * the whole reason they are settings rather than a drag: the shift-and-drag gesture already moves
 * the surface and is forgotten the moment the chart is rebuilt.</p>
 */

/**
 * How far the surface is moved across the plot, in the engine's own viewport units.
 *
 * <p>±3 is the documented legal range of <c>DxViewportX</c>/<c>Y</c> and is also why the box is
 * small and close rather than large and far: that range scales with the camera's distance, and at
 * ten times this size it moves the surface seven pixels. See {@link decayGridAspect}.</p>
 *
 * <p>The two axes have their own ranges and their own defaults, which is why they are two
 * constants: the opening view sits further up the plot than across it.</p>
 */
export const DECAY_VIEW_MOVE_X = { min: -3, max: 3, default: 2 } as const

/**
 * The same up the plot, and its range is <b>not</b> the sideways one.
 *
 * <p>Up to 4 rather than 3, asked for 2026-10-26: the default view already sits at 3 and there was
 * no room left above it. The engine documents ±3 as the legal translation, so the top of this
 * slider is past what it promises — it is accepted and keeps moving the surface, and the
 * consequence of asking for too much here is the surface leaving the plot, which is on screen the
 * moment it happens rather than silent.</p>
 */
export const DECAY_VIEW_MOVE_Y = { min: -3, max: 4, default: 3 } as const

/**
 * How large the surface is drawn, as a percentage of the framing the plot's own shape gives it.
 *
 * <p>A percentage of {@link decayCameraZoom} rather than a zoom outright, so that framing — which
 * is what keeps a narrow plot from clipping — still applies and this rides on top of it. 100 is
 * the fitted view.</p>
 */
export const DECAY_VIEW_MAGNIFICATION = { min: 50, max: 250, default: 140 } as const

/** Where the light sits around the box, in degrees; 0 is along +x and it turns towards +z. */
export const DECAY_LIGHT_AZIMUTH = { min: 0, max: 359, default: 180 } as const

/** How high the light sits, in degrees above the floor plane. */
export const DECAY_LIGHT_ELEVATION = { min: -90, max: 90, default: 40 } as const

/**
 * How far the light sits from the box — fixed, and not a control.
 *
 * <p>Brightness is a property of this distance, so leaving it out of the two angle controls is
 * what makes them turn the light rather than dim it. The figure is the vendor demo's own, which
 * {@link DECAY_LIGHT} has matched since it was first turned.</p>
 */
export const DECAY_LIGHT_RADIUS = Math.hypot(2, 7, 7)

/** The light's position from the two angles the panel turns. */
export function decayLightVector(azimuthDeg: number, elevationDeg: number): { x: number; y: number; z: number } {
  const az = (azimuthDeg * Math.PI) / 180
  const el = (elevationDeg * Math.PI) / 180
  const flat = DECAY_LIGHT_RADIUS * Math.cos(el)
  return {
    x: flat * Math.cos(az),
    // Negative is above: the engine's height axis points down for the light, which is why every
    // vector that has ever lit this chart has had a negative y.
    y: -DECAY_LIGHT_RADIUS * Math.sin(el),
    z: flat * Math.sin(az),
  }
}

/**
 * The camera distance for a plot of this shape at this magnification.
 *
 * <p>More negative is further back, so a larger magnification divides. Clamped to the engine's own
 * <c>DxZoomMin</c> at the far end and short of zero at the near one, where the camera would be
 * inside the box.</p>
 */
export function decayMagnifiedZoom(baseZoom: number, magnificationPercent: number): number {
  const pct = Math.min(DECAY_VIEW_MAGNIFICATION.max, Math.max(DECAY_VIEW_MAGNIFICATION.min, magnificationPercent))
  const zoom = baseZoom * (100 / pct)
  return Math.min(-1, Math.max(-90, zoom))
}

/**
 * Where the light goes. One position, not one per camera angle.
 *
 * <p>The vendor demo's light lit its own default view and went dark as the camera moved round the
 * box, which is what the surface looked like once the default lean changed: measured on a 60x400
 * surface, the mean luminance of the lit pixels fell from 92.4 at azimuth 315 to 82.4 at azimuth
 * 10. Reported 2026-10-25.</p>
 *
 * <p><b>Turning the light with the camera, per frame, was the obvious answer and is the worse
 * one.</b> Swept right round the circle in 30-degree steps, a light that follows the camera reads
 * 77.3 at its worst and is below 95 at five of the twelve angles, because the relation between a
 * good light and the camera is not a rotation about the height axis.</p>
 *
 * <p><b>But which side of the box the camera sits on is a different question, and this vector
 * belongs to a side.</b> It was <c>{ 7, -7, -2 }</c> while the camera was at <c>180 - lean</c>;
 * moving it to <c>360 - lean</c> to get the axes the right way round therefore left the light
 * behind the surface, and the chart went dark — reported 2026-10-25 as exactly that, the day after
 * the lighting had been called spot on. Mirroring it about the height axis, which is the same
 * 180 degrees the camera moved, is the fix: measured on one capture, mean luminance
 * <b>70.4 against 86.6</b>, a lift of 23 %, with the darkest quarter unchanged at 25.3 — the
 * mirror lights the faces that face the viewer and does nothing for the shadowed ones, which is
 * what <c>DECAY_LIGHT_STRENGTH</c> is for and why the two complaints needed two fixes. Three other
 * vectors were tried at the new azimuth and all read lower: <c>-7,-7,-2</c> 100.1,
 * <c>7,-7,2</c> 85.6 against this one's 121.1 in that run.</p>
 */
export const DECAY_LIGHT = decayLightVector(DECAY_LIGHT_AZIMUTH.default, DECAY_LIGHT_ELEVATION.default)

/**
 * The ambient floor, which is what decides how dark the darkest colour on the surface reads.
 *
 * <p>The engine's own default is 0.3 of a documented 0.1-0.5, and this is the top of that range.
 * Asked for 2026-10-25 as "the darker blue is too dark": the surface is lit, so every colour in
 * the palette is multiplied by a shading term, and at the foot of the ramp that leaves Jet's deep
 * blue almost black against a near-black page.</p>
 *
 * <p><b>It costs no shading, which is the measured part.</b> Over the same capture the mean
 * luminance of the darkest quarter of the surface goes <b>25.3 at 0.30, 35.1 at 0.42 and 42.0 at
 * 0.50</b> — the dark end lifts by two thirds — while the standard deviation of luminance across
 * the whole surface, which is what makes it read as a solid rather than a flat patch, goes
 * <b>63.4, 66.8, 66.7</b>: it rises slightly and then holds. So brightening here does not flatten
 * the picture, and there is no reason to stop short of the documented maximum.</p>
 *
 * <p><c>BackLight</c>, the secondary light 180 degrees from the primary, is deliberately left at
 * its default: swept to 100 it rendered <em>identically</em> — mean 98.6 and dark quarter 33.4,
 * to the digit, with it at 65 and at 100 — so it reaches nothing on this plot type.</p>
 */
export const DECAY_LIGHT_STRENGTH = 0.5

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
