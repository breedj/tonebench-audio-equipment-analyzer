import type { DecayChartMode, DecayPalette, DecaySurface } from '../api/client'
import type { Graph3dPoint, Graph3dRgb } from '../charts/graph3d'
import {
  DECAY_DEPTH_PERCENT,
  DECAY_ROTATION_DEG,
  formatHz,
  levelAt,
  orientSurface,
  paletteFloorFraction,
  paletteIndex,
  paletteLut,
} from './decay'

/**
 * <p>What <c>DecaySurface3dChart</c> hands <c>vis-graph3d</c>, and the only place that conversion
 * happens. It lives here rather than in the component for the reason <c>decay.ts</c> exists:
 * nothing in a <c>.vue</c> file is testable under the node environment vitest runs in, and a
 * transposed or mirrored surface is the archetypal fault that draws a convincing picture of the
 * wrong data.</p>
 *
 * <p>Everything reads through <c>orientSurface</c> and <c>levelAt</c>, so the two charts cannot
 * drift apart about which way round a waterfall goes or where a slice's data begins.</p>
 */

/**
 * <p><b>A cell with no measurement is left out of the array, and that is the one rule here that
 * cannot be got wrong quietly.</b> The library builds its grid by indexing each row against the
 * distinct values of the two floor axes, so an absent <c>(x, y)</c> combination is simply a hole:
 * its neighbour-linking loop skips it (<c>if (dataMatrix[x][y])</c>) and the four quads around it
 * are not drawn. That is exactly what a burst decay's ragged low-frequency corner needs.</p>
 *
 * <p><b>Passing a sentinel or a NaN instead would be drawn as data at full level.</b>
 * <c>DataGroup.getDataPoints</c> reads every coordinate as <c>data[i][col] || 0</c> — so NaN,
 * null and undefined all become <c>0</c>, and 0 dB is the <em>top</em> of a decay scale. The cells
 * the analysis has no answer for would come out as the loudest thing on the surface, which is the
 * same false decay the sweep gate exists to prevent. The previous engine wanted NaN in those cells
 * and had its own reasons measured for it; this one wants the row gone.</p>
 */
export interface DecayPointCloud {
  /** One per *measured* cell, in row-major order. Holes are absent, not zeroed. */
  points: Graph3dPoint[]
  /** Curves, back to front. */
  rows: number
  /** Columns per curve. */
  cols: number
  /** Whether the across axis is a frequency, and so whether its coordinate is a logarithm. */
  acrossIsFrequency: boolean
  /** Whether the depth axis is a frequency. The two are never both true. */
  depthIsFrequency: boolean
  /** How many cells the analysis had no answer for — the ragged corner, and worth asserting on. */
  omitted: number
}

/**
 * <p><b>A frequency axis carries its logarithm, because the library has no logarithmic
 * scale.</b> Neither its option list nor its axis code offers one, so true hertz on a linear axis
 * puts four decades into the last twentieth of the floor; the coordinate is <c>log10(f)</c> and
 * {@link decayAcrossLabel} prints the hertz back onto the ticks. (The column index with string
 * labels is the other way round and is a dead end — a 3D axis takes no strings, so the ticks would
 * read 0…399.)</p>
 *
 * <p><b>Either floor axis can be the frequency one</b>, which is what the by-period view is: it
 * reads time across the curves and stacks them in frequency. So this takes the flag rather than
 * assuming the across axis, and {@link decayPoints} asks it for both coordinates.</p>
 */
export function decayAxisCoordinate(value: number, isFrequency: boolean): number {
  return isFrequency ? Math.log10(value > 0 ? value : 1) : value
}

/**
 * The value a coordinate stands for, undoing {@link decayAxisCoordinate}.
 *
 * <p><b>Rounded to twelve significant figures</b>, which strips the float residue the logarithm
 * and its inverse leave behind and nothing else: <c>10 ** Math.log10(2000)</c> is
 * 1999.9999999999998, and <c>formatHz</c> reads that as "2.0k" rather than "2k" because it is
 * no longer an integer. Twelve figures is far more precision than any axis label or readout
 * shows, so this cannot round a frequency anyone could tell apart.</p>
 */
export function decayAxisValue(coordinate: number, isFrequency: boolean): number {
  if (!isFrequency) return coordinate
  const value = 10 ** coordinate
  return Number.isFinite(value) ? Number(value.toPrecision(12)) : value
}

/**
 * <p><b>The depth axis is negated, and that is the handedness of the library's camera rather than
 * a preference.</b> Working its projection through for a parallel view: at a horizontal angle of
 * zero, screen x follows the first floor axis and screen y follows
 * <c>cos(v)·height + sin(v)·(second floor axis)</c> — so the second axis recedes <em>away</em> from
 * the viewer as it grows. Slice zero is the back of a waterfall, so the depth coordinate has to
 * fall as the stack comes forward.</p>
 *
 * <p>Turning the camera to the opposite side instead fixes the depth and mirrors the frequency
 * with it, because the floor keeps its handedness however the camera moves — the same trap the
 * previous engine's <c>InvertedXAxis</c> was there to undo. This library has no such flag, which
 * leaves negating one coordinate as the only lever that moves one axis alone; the sign is handed
 * back to the ticks by {@link decayDepthLabel}.</p>
 */
export const DECAY_DEPTH_SIGN = -1

/**
 * The surface as the library wants it: a flat list of vertices, holes simply absent.
 *
 * @param rangeDb How far below 0 dB the chart reaches. Levels are clamped to it rather than
 * dropped, exactly as <c>levelY</c> clamps them in the Canvas 2D chart, so the two agree about
 * what the foot of the scale means.
 */
export function decayPoints(
  surface: DecaySurface,
  mode: DecayChartMode,
  rangeDb: number,
): DecayPointCloud {
  const view = orientSurface(surface, mode === 'WaterfallPeriods')
  const rows = view.depth.length
  const cols = view.across.length
  const floor = -Math.abs(rangeDb)

  // Computed once per column and once per curve rather than per point: the library matches rows
  // to grid positions by exact equality, so every slice has to carry the identical coordinate for
  // a column, and `log10` twice on the same input is not guaranteed to be the same bits.
  const across = view.across.map((v) => decayAxisCoordinate(v, view.acrossIsFrequency))
  const depth = view.depth.map((v) => DECAY_DEPTH_SIGN * decayAxisCoordinate(v, view.depthIsFrequency))

  const points: Graph3dPoint[] = []
  let omitted = 0
  for (let r = 0; r < rows; r++) {
    const y = depth[r]
    for (let c = 0; c < cols; c++) {
      const level = view.level(r, c)
      if (level === null) {
        omitted++
        continue
      }
      points.push({ x: across[c], y, z: level < floor ? floor : level > 0 ? 0 : level })
    }
  }

  return {
    points,
    rows,
    cols,
    acrossIsFrequency: view.acrossIsFrequency,
    depthIsFrequency: view.depthIsFrequency,
    omitted,
  }
}

/** A tick on the across axis, undoing {@link decayAxisCoordinate}. */
export function decayAcrossLabel(coordinate: number, isFrequency: boolean): string {
  if (!Number.isFinite(coordinate)) return ''
  const value = decayAxisValue(coordinate, isFrequency)
  return isFrequency ? formatHz(value) : formatTimeValue(value)
}

/** A tick on the depth axis, undoing both the coordinate and {@link DECAY_DEPTH_SIGN}. */
export function decayDepthLabel(coordinate: number, isFrequency: boolean): string {
  if (!Number.isFinite(coordinate)) return ''
  const value = decayAxisValue(DECAY_DEPTH_SIGN * coordinate, isFrequency)
  return isFrequency ? formatHz(value) : formatTimeValue(value)
}

/** The frequency a point of the cloud stands at, whichever floor axis is carrying it. */
export function decayPointFrequencyHz(point: Graph3dPoint, cloud: DecayPointCloud): number {
  if (cloud.acrossIsFrequency) return decayAxisValue(point.x, true)
  if (cloud.depthIsFrequency) return decayAxisValue(DECAY_DEPTH_SIGN * point.y, true)
  return Number.NaN
}

/** The time or period a point of the cloud stands at — the floor axis that is not the frequency. */
export function decayPointTime(point: Graph3dPoint, cloud: DecayPointCloud): number {
  return cloud.acrossIsFrequency ? DECAY_DEPTH_SIGN * point.y : point.x
}

/** Milliseconds or periods, to as many places as the number needs and no more. */
function formatTimeValue(value: number): string {
  const magnitude = Math.abs(value)
  if (magnitude >= 100) return value.toFixed(0)
  if (magnitude >= 10) return value.toFixed(1)
  return value.toFixed(2)
}

/**
 * The colour of a height, as the library's <c>colormap</c> wants it: a function of the level's
 * position in the z range, 0 at the foot of the scale.
 *
 * <p>It reads <c>paletteLut</c> through <c>paletteIndex</c>, so the palette and
 * <c>PALETTE_FLOOR_FRACTION</c> carry over exactly and a level means the same colour on both
 * charts and in the key beside either of them.</p>
 */
export function decayColormap(palette: DecayPalette, onDark: boolean): (fraction: number) => Graph3dRgb {
  const lut = paletteLut(palette, onDark)
  const floor = paletteFloorFraction(palette)
  return (fraction: number) => {
    const index = paletteIndex(Number.isFinite(fraction) ? fraction : 0, floor)
    return { r: lut[index * 3], g: lut[index * 3 + 1], b: lut[index * 3 + 2] }
  }
}

/**
 * How tall the box is drawn, against its own floor — the <b>Explode Z axis</b> slider.
 *
 * <p>Asked for 2026-10-29, when it replaced three per-axis sliders, and it is the one control over
 * the box's shape that this library has: the floor's two axes are frequency and time, which have no
 * common unit and so no proportion that would be right, and setting them apart only ever tilted the
 * picture. What is worth stretching is the height, which is the only axis a decay surface is
 * actually read along.</p>
 *
 * <p>The range 1 to 50 was the previous engine's demo's own and is kept, because it is what every
 * measurement already stored this field on. {@link decayVerticalRatio} is what maps it.</p>
 */
export const DECAY_EXPLODE_Z = { min: 1, max: 50, step: 1, default: 23 } as const

/**
 * <p>The library's <c>verticalRatio</c>, which multiplies the height axis's scale and is documented
 * as 0.1 to 1.0. The slider's whole travel maps onto the whole of that, so neither end is a dead
 * stretch — the fault the previous engine's magnification had, where seven eighths of the slider
 * rendered byte-identically.</p>
 *
 * <p>The default lands on 0.504, which is the library's own default of 0.5 to within a step. That
 * is a coincidence worth keeping rather than designing around: it means the box this chart opens on
 * is the shape the library was tuned to draw.</p>
 */
export function decayVerticalRatio(explodeZ: number = DECAY_EXPLODE_Z.default): number {
  const value = Number.isFinite(explodeZ) ? explodeZ : DECAY_EXPLODE_Z.default
  const clamped = Math.min(DECAY_EXPLODE_Z.max, Math.max(DECAY_EXPLODE_Z.min, value))
  const t = (clamped - DECAY_EXPLODE_Z.min) / (DECAY_EXPLODE_Z.max - DECAY_EXPLODE_Z.min)
  return VERTICAL_RATIO_MIN + t * (VERTICAL_RATIO_MAX - VERTICAL_RATIO_MIN)
}

const VERTICAL_RATIO_MIN = 0.1
const VERTICAL_RATIO_MAX = 1.0

/**
 * The Explode Z a measurement's stored axis lengths come to, for one saved before this slider
 * existed. What that measurement chose was the height against the floor, so that ratio is what
 * carries over: invert the previous engine's <c>y = √2 × explode/100</c> against the across
 * length, which the floor was square on anyway in the only box this app ever opened on.
 */
export function explodeZForAxisLengths(acrossLength: number, levelLength: number): number {
  if (!(acrossLength > 0) || !(levelLength > 0)) return DECAY_EXPLODE_Z.default
  const explode = (100 * levelLength) / (acrossLength * Math.SQRT2)
  const stepped = Math.round(explode / DECAY_EXPLODE_Z.step) * DECAY_EXPLODE_Z.step
  return Math.min(DECAY_EXPLODE_Z.max, Math.max(DECAY_EXPLODE_Z.min, stepped))
}

/**
 * How far the surface is moved across the plot and up it.
 *
 * <p>The ranges and the defaults are the previous engine's viewport units, kept so that no stored
 * value changes meaning: {@link decayCenterPercent} maps them onto this library's own
 * <c>xCenter</c>/<c>yCenter</c> instead of being migrated. The two axes have their own ranges for
 * the reason they always did — the opening view has more room above it than beside it.</p>
 */
export const DECAY_VIEW_MOVE_X = { min: -3, max: 3, default: 0 } as const

/** The same up the plot, and its range is <b>not</b> the sideways one. Positive moves it up. */
export const DECAY_VIEW_MOVE_Y = { min: -3, max: 4, default: 0 } as const

/** How far one unit of either Move slider shifts the box, as a percentage of the plot. */
export const DECAY_MOVE_PERCENT_PER_UNIT = 8

/** Where the library puts the box with no Move at all. */
const DECAY_CENTER_X_PERCENT = 55
const DECAY_CENTER_Y_PERCENT = 45

/**
 * The two Move sliders as the library's percentage strings.
 *
 * <p><b>Y is subtracted</b>: the library measures its vertical centre down the canvas, and a slider
 * labelled "Move vertically" that sent the surface down when pushed up would be its own bug.</p>
 */
export function decayCenterPercent(
  moveX: number = DECAY_VIEW_MOVE_X.default,
  moveY: number = DECAY_VIEW_MOVE_Y.default,
): { xCenter: string; yCenter: string } {
  const x = Number.isFinite(moveX) ? moveX : DECAY_VIEW_MOVE_X.default
  const y = Number.isFinite(moveY) ? moveY : DECAY_VIEW_MOVE_Y.default
  return {
    xCenter: `${DECAY_CENTER_X_PERCENT + x * DECAY_MOVE_PERCENT_PER_UNIT}%`,
    yCenter: `${DECAY_CENTER_Y_PERCENT - y * DECAY_MOVE_PERCENT_PER_UNIT}%`,
  }
}

/**
 * How large the surface is drawn, as a percentage of the view a measurement opens on.
 *
 * <p>The range and the default are kept from the previous engine so no stored value changes
 * meaning. What it drives here is the camera's arm: in a parallel projection the library's own
 * screen conversion is <c>dx / armLength</c>, so the drawn size is exactly the reciprocal of the
 * distance, and the documented arm of 0.71 to 5.0 is a sevenfold span of picture.</p>
 */
export const DECAY_VIEW_MAGNIFICATION = { min: 20, max: 800, step: 5, default: 170 } as const

/** The library's documented arm length. More is further back, so a larger magnification is less. */
const CAMERA_DISTANCE_NEAR = 0.71
const CAMERA_DISTANCE_FAR = 5.0

/**
 * The camera arm for a magnification.
 *
 * <p><b>Logarithmic, so that every step of the slider moves the picture.</b> The obvious mapping is
 * proportional — hold a reference size and divide — and it saturates: the arm's legal range is only
 * sevenfold where the slider is fortyfold, so a proportional map pins everything below about 58 %
 * and above about 400 % against a limit. That is precisely the fault this slider had on the
 * previous engine, reported twice ("a magnification of 90 % results in a larger graph than 110 %",
 * "above 200 % does not work"), and a mapping that cannot produce it is worth more than a literal
 * percentage. Both ends of the slider reach both ends of the arm, and no two steps are equal.</p>
 *
 * <p>The default falls near 1.61, which is the library's own default arm of 1.7 to within a tenth —
 * so the view a measurement opens on is close to the framing the library was tuned for.</p>
 */
export function decayCameraDistance(
  magnificationPercent: number = DECAY_VIEW_MAGNIFICATION.default,
): number {
  const percent = Number.isFinite(magnificationPercent) && magnificationPercent > 0
    ? Math.min(DECAY_VIEW_MAGNIFICATION.max, Math.max(DECAY_VIEW_MAGNIFICATION.min, magnificationPercent))
    : DECAY_VIEW_MAGNIFICATION.default
  const t =
    Math.log(percent / DECAY_VIEW_MAGNIFICATION.min) /
    Math.log(DECAY_VIEW_MAGNIFICATION.max / DECAY_VIEW_MAGNIFICATION.min)
  const distance = Math.exp(
    Math.log(CAMERA_DISTANCE_FAR) + t * (Math.log(CAMERA_DISTANCE_NEAR) - Math.log(CAMERA_DISTANCE_FAR)),
  )
  // Clamped against its own ends, not because the mapping can overshoot but because exp(log())
  // lands a few parts in 10^16 outside them: the top of the slider came back as 0.70999999999,
  // and the library refuses an arm below 0.71 by silently clamping it.
  return Math.min(CAMERA_DISTANCE_FAR, Math.max(CAMERA_DISTANCE_NEAR, distance))
}

/**
 * Where a stored magnification was written on the old scale, which ran to 2000 and saturated.
 *
 * <p>Anything above this slider's own maximum has to be the old unit; the overlap below it cannot
 * be told apart and is left alone, which costs a measurement stored between 200 and 400 a view
 * twice the size it had. Every build that wrote this field defaulted to 600 or 1500, so that is a
 * narrow window.</p>
 */
export function magnificationFromStored(stored: number): number {
  if (!Number.isFinite(stored) || stored <= 0) return DECAY_VIEW_MAGNIFICATION.default
  const value = stored > DECAY_VIEW_MAGNIFICATION.max ? stored / 10 : stored
  return Math.min(DECAY_VIEW_MAGNIFICATION.max, Math.max(DECAY_VIEW_MAGNIFICATION.min, value))
}

/**
 * The camera, from the two sliders above the Surface view section.
 *
 * <p><b>Rotation is the horizontal angle straight through</b>, with no offset: at zero the library
 * draws the first floor axis left to right, which is the frequency, and that is the view a
 * waterfall is read from. The lean is then the same quantity on both charts, so the two agree
 * about what Rotation means.</p>
 *
 * <p><b>Tilt is how much of the picture the depth axis takes</b>, which on an oblique projection is
 * a fraction of the plot's height and here is how far the camera is raised: 0 is level with the
 * floor, where the stack has no depth at all, and 0.5π looks straight down on it, where it has
 * nothing but. So the percentage maps onto that quarter turn directly.</p>
 */
export function decayCameraAngles(
  rotationDeg: number = DECAY_ROTATION_DEG.default,
  depthPercent: number = DECAY_DEPTH_PERCENT.default,
): { horizontal: number; vertical: number } {
  const rotation = clampTo(rotationDeg, DECAY_ROTATION_DEG)
  const depth = clampTo(depthPercent, DECAY_DEPTH_PERCENT)
  return {
    horizontal: (rotation * Math.PI) / 180,
    vertical: (depth / 100) * (Math.PI / 2),
  }
}

function clampTo(value: number, range: { min: number; max: number; default: number }): number {
  if (!Number.isFinite(value)) return range.default
  return value < range.min ? range.min : value > range.max ? range.max : value
}

/**
 * Everything about the decay view that is a camera or a box — the six sliders, under the names the
 * display state carries them by.
 *
 * <p>It was eight until the GigaSoft engine was replaced on 2026-10-30. The two that went were the
 * light's angles: <c>vis-graph3d</c> has no positionable light, only a view-angle brightness term
 * it applies per quad, so there was nothing for them to turn.</p>
 */
export interface DecayViewSettings {
  decayRotationDeg: number
  decayDepthPercent: number
  decayMoveX: number
  decayMoveY: number
  decayMagnification: number
  decayExplodeZ: number
}

/**
 * The view a measurement opens on, and the one the Surface view panel's <b>Reset view</b> button
 * gives back.
 *
 * <p><b>It is one function because those have to be the same six numbers</b>, and a button that
 * restored a view no new measurement ever opens on would be the worse of two wrong answers.
 * <c>defaultImpulseDisplay</c> spreads this rather than listing the fields beside it, so there is
 * no second copy to drift — which is the trap the per-mode level memory and the impedance stimulus
 * defaults have each already walked into, each time by writing one default in two places.</p>
 *
 * <p><b>Rotation and Tilt are in it although their sliders sit above the panel the button is in.</b>
 * They are this chart's camera — Rotation swings it round the box and Tilt raises it — so a reset
 * that left them where they were would hand back a box seen from whatever angle the last turn left,
 * which is to say not the view it promises. The button's own title says it covers them.</p>
 *
 * <p>The figures are Joost's, turned on the bench and asked for as the defaults on 2026-10-27, and
 * again on 2026-10-28 and 2026-10-29 as the controls around them changed. Nothing here is derived,
 * and nothing here can be: what makes a decay surface readable is a matter of looking at one, which
 * is also why all six are sliders rather than boxes.</p>
 */
export function decayViewDefaults(): DecayViewSettings {
  return {
    decayRotationDeg: DECAY_ROTATION_DEG.default,
    decayDepthPercent: DECAY_DEPTH_PERCENT.default,
    decayMoveX: DECAY_VIEW_MOVE_X.default,
    decayMoveY: DECAY_VIEW_MOVE_Y.default,
    decayMagnification: DECAY_VIEW_MAGNIFICATION.default,
    decayExplodeZ: DECAY_EXPLODE_Z.default,
  }
}

/**
 * The caption a report or a screenshot carries. Shared with the Canvas 2D chart rather than
 * written out twice, so a chapter reads identically whichever chart drew it.
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
 * <p>What the chart's options depend on, as one comparable value — so a pass that changes none of
 * them can skip the rebuild <c>setOptions</c> costs, and a pass that changes one cannot.</p>
 *
 * <p><b>The slice axis is in it, and is the one easy to leave out.</b> It belongs to the
 * <em>surface</em> rather than to a setting: switching the view from the cumulative spectral decay
 * to the burst decay hands over a different surface at the same mode and the same range, and its
 * depth is counted in periods where the other's was in milliseconds. Left out, the burst decay
 * draws under a time axis labelled "Time (ms)" over a scale that is nothing of the kind — which
 * is what it did the first time this comparison was written, and what the test here pins.</p>
 */
export function decayOptionsKey(
  surface: DecaySurface | null,
  mode: DecayChartMode,
  rangeDb: number,
): string {
  return `${surface?.sliceAxis ?? '-'}|${mode}|${rangeDb}`
}

/**
 * Whether the 3D chart can draw this mode at all. The sonogram is a flat colour map — one
 * <c>putImageData</c> blit in the Canvas 2D chart, already correct and already fast — so there is
 * nothing for a turnable surface to add, and a flat-shaded 3D box is a different picture.
 */
export function decayModeIs3d(mode: DecayChartMode): boolean {
  return mode !== 'Sonogram'
}

/** Re-exported so the component has one import for its data work. */
export { levelAt }
