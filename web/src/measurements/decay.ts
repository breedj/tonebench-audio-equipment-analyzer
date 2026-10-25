import type { CsdWindowModeName, DecayPalette, DecaySurface } from '../api/client'

/**
 * The arithmetic behind the two decay charts — the oblique projection, the level-to-colour ramp and
 * the sample-rate-derived defaults.
 *
 * Pure and in its own module for the reason `reorder.ts` and `impulseUnit.ts` are: nothing in a
 * `.vue` file is testable under the node environment vitest runs in, and every one of these is
 * arithmetic that fails by drawing a plausible picture rather than by throwing.
 */

/** Where a slice's own origin sits, and how much of the box is left for the curve itself. */
export interface DecayLayout {
  /** Where the <em>back</em> slice starts. Non-zero only when the stack recedes leftwards. */
  left: number
  top: number
  /** Horizontal span of one slice's curve. Less than the box's width by the depth axis's own run. */
  curveWidth: number
  /** Vertical span of one slice's level range. Less than the box's height by the depth axis's own drop. */
  levelHeight: number
  /** How far the whole stack travels sideways, and down, between the first slice and the last. */
  depthX: number
  depthY: number
}

/**
 * Splits a drawing box between the level axis and the depth axis.
 *
 * <p>The depth axis runs <b>downwards</b>, always: the first slice — time zero — is drawn at the
 * back and each later one lower. That direction is not a choice, it is what makes the painter's
 * algorithm work, since a nearer curve being drawn lower is what lets filling underneath it hide
 * the slices behind. The hidden-line removal is the whole readability of the chart.</p>
 *
 * <p>Sideways it may run either way, and a <b>negative</b> <c>depthFractionX</c> — the default,
 * matching ARTA — sends the stack down and to the <em>left</em>. That is why <c>left</c> exists:
 * the back slice has to start one whole depth run in from the edge so the front one, the furthest
 * left, still lands on it. Everything drawn against the box therefore asks <c>sliceOrigin</c> where
 * an edge is rather than assuming the back wall sits at zero.</p>
 *
 * @param depthFractionY How much of the height the depth axis takes. The rest is the level range,
 * so a large value makes a deep-looking chart with a squashed dB scale.
 */
export function decayLayout(
  width: number,
  height: number,
  depthFractionX = -0.12,
  depthFractionY = 0.5,
): DecayLayout {
  const depthX = width * depthFractionX
  const depthY = height * depthFractionY
  return {
    left: depthX < 0 ? -depthX : 0,
    top: 0,
    curveWidth: Math.max(1, width - Math.abs(depthX)),
    levelHeight: Math.max(1, height - depthY),
    depthX,
    depthY,
  }
}

/**
 * One slice's origin — the point its 0 dB line starts from.
 *
 * @param depth 0 for the back slice, 1 for the front one.
 */
export function sliceOrigin(layout: DecayLayout, depth: number): { x: number; y: number } {
  return { x: layout.left + depth * layout.depthX, y: layout.top + depth * layout.depthY }
}

/**
 * Screen y for a level, measured from a slice's own origin. Clamped to the window, because a curve
 * allowed to run off the bottom would be drawn over the slices in front of it — and clipping the
 * canvas instead would clip them all to the same rectangle, which is not what a stack of
 * parallelograms needs.
 */
export function levelY(layout: DecayLayout, originY: number, levelDb: number, topDb: number, bottomDb: number): number {
  const span = topDb - bottomDb || 1
  const t = (topDb - levelDb) / span
  return originY + Math.min(1, Math.max(0, t)) * layout.levelHeight
}

/**
 * Where a value sits along an axis, as a fraction from 0 at its low end to 1 at its high one.
 *
 * <p>Logarithmic for frequency and linear for time, and which of those runs across the chart is the
 * difference between the two waterfalls — so the mapping is a parameter rather than baked into the
 * drawing. See <c>orientSurface</c>.</p>
 */
export function axisFraction(value: number, low: number, high: number, logarithmic: boolean): number {
  if (logarithmic) {
    const span = Math.log(high / low) || 1
    return Math.log(value / low) / span
  }
  const span = high - low || 1
  return (value - low) / span
}

/** Every value of an axis as its fraction, taking the axis's ends from the values themselves. */
export function axisFractions(values: number[], logarithmic: boolean): number[] {
  if (values.length === 0) return []
  const low = values[0]
  const high = values[values.length - 1]
  return values.map((value) => axisFraction(value, low, high, logarithmic))
}

/** Screen x for a point of a curve, measured from that curve's own origin. */
export function acrossX(layout: DecayLayout, originX: number, fraction: number): number {
  return originX + fraction * layout.curveWidth
}

/** Colour stops, low level (cold) first. Each is `[position, r, g, b]`. */
type Stop = [number, number, number, number]

const PALETTES: Record<DecayPalette, Stop[]> = {
  // The same ramp as the live spectrogram's, and for the same reason: sharp hue transitions are what
  // let a ridge be picked out of a floor at a glance, which a monotone lightness ramp does badly.
  Jet: [
    [0.0, 8, 12, 64],
    [0.12, 0, 24, 180],
    [0.25, 0, 96, 255],
    [0.38, 0, 200, 240],
    [0.5, 0, 220, 150],
    [0.62, 130, 230, 60],
    [0.75, 245, 220, 0],
    [0.87, 255, 128, 0],
    [1.0, 190, 0, 0],
  ],
  Hot: [
    [0.0, 10, 0, 0],
    [0.35, 150, 0, 0],
    [0.65, 255, 110, 0],
    [0.85, 255, 210, 40],
    [1.0, 255, 255, 235],
  ],
  // For printing, and for anyone who reads a decay by shape rather than by hue. The only palette
  // that has to follow the theme: a monochrome ramp says "loud" by contrast against the background,
  // so a fixed one drew the front slice — the loudest curve, the one the chart is for — in near
  // black on a near-black page. See `paletteLut`.
  Grey: [
    [0.0, 235, 235, 235],
    [1.0, 25, 25, 25],
  ],
}

/** The grey ramp reversed, for a dark page: loud is light there rather than dark. */
const GREY_ON_DARK: Stop[] = [
  [0.0, 40, 40, 40],
  [1.0, 245, 245, 245],
]

export const PALETTE_NAMES = Object.keys(PALETTES) as DecayPalette[]

const LUT_SIZE = 256

/**
 * A palette expanded to a flat RGB lookup table, so the per-point path is an array index.
 *
 * @param onDark Whether the page behind it is dark. Only <c>Grey</c> reads it — see its stops.
 */
export function paletteLut(name: DecayPalette, onDark = false): Uint8Array {
  const stops = stopsFor(name, onDark)
  const lut = new Uint8Array(LUT_SIZE * 3)
  for (let i = 0; i < LUT_SIZE; i++) {
    const t = i / (LUT_SIZE - 1)
    let s = 0
    while (s < stops.length - 2 && t > stops[s + 1][0]) s++
    const [t0, r0, g0, b0] = stops[s]
    const [t1, r1, g1, b1] = stops[s + 1]
    const f = t1 === t0 ? 0 : (t - t0) / (t1 - t0)
    lut[i * 3] = r0 + (r1 - r0) * f
    lut[i * 3 + 1] = g0 + (g1 - g0) * f
    lut[i * 3 + 2] = b0 + (b1 - b0) * f
  }
  return lut
}

function stopsFor(name: DecayPalette, onDark: boolean): Stop[] {
  if (name === 'Grey') return onDark ? GREY_ON_DARK : PALETTES.Grey
  return PALETTES[name] ?? PALETTES.Jet
}

/**
 * The colour a level maps to, as a CSS colour. Index 0 is the palette's floor, which is the one
 * every waterfall needs by name: it is what the area under each curve is filled with, and filling
 * it with the palette rather than with the page is the whole difference between a solid surface and
 * a thicket of lines. See `DecaySurfaceChart`.
 */
export function lutColor(lut: Uint8Array, index: number): string {
  const i = (index < 0 ? 0 : index > LUT_SIZE - 1 ? LUT_SIZE - 1 : index) * 3
  return `rgb(${lut[i]},${lut[i + 1]},${lut[i + 2]})`
}

/**
 * dB ticks down the level axis: 0 dB first, the foot of the range last, and a step chosen so the
 * axis carries seven labels at most however deep the window is. The colour key is labelled from the
 * same list because it *is* the same axis — a key whose numbers disagreed with the ruler beside it
 * would be worse than no key.
 */
export function levelTicks(rangeDb: number): number[] {
  const span = rangeDb > 0 ? rangeDb : 1
  const step = [1, 2, 5, 10, 20, 25, 50].find((candidate) => span / candidate <= 7) ?? 100

  const ticks: number[] = []
  for (let db = 0; db >= -span + 1e-9; db -= step) ticks.push(db)
  if (Math.abs(ticks[ticks.length - 1] + span) > 1e-9) ticks.push(-span)
  return ticks
}

/** Index into a palette LUT for a level, clamped to the window. */
/**
 * The colour stops of the trailing wall's gradient, from the top of the level axis to the foot of
 * it — so the wall reads like the key beside it, red at the top and blue at the bottom.
 *
 * <p><b>The direction is the whole of this.</b> The wall used to be painted by hanging each slice's
 * own colour straight down from its curve's end, which gradates it along <i>time</i>: red at the
 * back where the response is loud, blue at the front where it has decayed. That is a second,
 * contradictory key on the same picture, and it was reported as exactly that. The wall is the side
 * face of the box, and a point on it has a level — the colour at any height is the colour of the
 * level that height stands for, which puts the iso-colour bands parallel to the depth axis, as
 * ARTA draws them.</p>
 *
 * @param steps How many stops to sample the palette at. Enough that the eye cannot find the joins,
 * and few enough that a quad per slice costs nothing to build.
 */
export function wallGradientStops(steps = 32): { offset: number; index: number }[] {
  const stops: { offset: number; index: number }[] = []
  for (let i = 0; i <= steps; i++) {
    const offset = i / steps
    stops.push({ offset, index: paletteIndex(1 - offset) })
  }

  return stops
}

/**
 * How much of the palette the <em>bottom</em> of the level scale is held above: the foot of the
 * scale reads the colour that used to sit a quarter of the way up it.
 *
 * <p>Asked for because the floor was too dark to read (Joost, 2026-10-24) — Jet's own foot is a
 * near-black navy, and a surface whose whole quiet half is painted in it loses the shape of what is
 * decaying. Compressing the ramp instead of brightening the palette keeps every colour the palette
 * already had, in the same order; it is the <em>mapping</em> from level to colour that is squeezed
 * into the top three quarters, which is the one place all three readers of it agree.</p>
 *
 * <p>The quarter the levels no longer reach is not wasted: <c>DecaySurfaceChart</c>'s body fill is
 * <c>lutColor(lut, 0)</c> and stays the true floor, which is what hides the slices behind each
 * curve. Darker than anything drawn on it is exactly what that fill wants to be.</p>
 */
export const PALETTE_FLOOR_FRACTION = 0.25

/**
 * The lookup index a position on the level scale maps to — 0 at the foot of the scale, 1 at the top.
 * Every path from a level to a colour goes through here: the curves, the sonogram, the trailing
 * wall and the key beside them, which is what stops any of them disagreeing about what a colour
 * means.
 */
export function paletteIndex(fraction: number): number {
  const f = fraction < 0 ? 0 : fraction > 1 ? 1 : fraction
  return Math.round((PALETTE_FLOOR_FRACTION + (1 - PALETTE_FLOOR_FRACTION) * f) * (LUT_SIZE - 1))
}

export function levelIndex(levelDb: number, topDb: number, bottomDb: number): number {
  const span = topDb - bottomDb || 1
  return paletteIndex((levelDb - bottomDb) / span)
}

/** The transform lengths the panel offers, as powers of two. */
export const CSD_FFT_LENGTHS = [128, 256, 512, 1024, 2048, 4096, 8192, 16384]

/**
 * What a transform length means at this sample rate, for the label beside the field: a length in
 * samples says nothing on its own, and the two numbers it implies are the ones the setting is
 * actually about.
 */
export function describeCsdFftLength(fftLength: number, sampleRateHz: number): string {
  if (!(sampleRateHz > 0)) return `${fftLength} samples`
  const ms = (fftLength / sampleRateHz) * 1000
  const hz = sampleRateHz / fftLength
  return `${ms.toFixed(ms < 10 ? 2 : 1)} ms window, resolves ${hz.toFixed(hz < 10 ? 1 : 0)} Hz`
}

/**
 * How far past the impulse a set of CSD slices reaches, in milliseconds, given the block shift in
 * samples that used to be the setting. Only {@link readImpulseDisplay} calls it, to convert a
 * measurement stored before the span was the setting.
 */
export function csdSpanMs(blockShiftSamples: number, blockCount: number, sampleRateHz: number): number {
  if (!(sampleRateHz > 0)) return 0
  return ((blockCount - 1) * blockShiftSamples * 1000) / sampleRateHz
}

export interface CsdAnalysis {
  fftLength: number
  /**
   * How far past the impulse the surface reaches, in milliseconds — and the setting the user turns,
   * where it used to be the block shift in samples.
   *
   * <p>A shift in samples means four different things at the three sample rates, which is the trap
   * every CSD setting carries: ARTA's 2 samples is 2.46 ms of decay off a 48 kHz sound card and
   * 0.61 ms here at 192. A span in milliseconds is the same measurement at any rate, and it is what
   * a reader of the chart actually wants to say. It is honoured to the sample — see
   * <c>csdSpanSamples</c> — and the slices are spread across it, so the rounding lands on the gap
   * between them and never on the length of the axis.</p>
   */
  spanMs: number
  /** How many slices to cut that span into. Changing it never changes how far the surface reaches. */
  slices: number
  /**
   * How far ahead of the impulse the first slice sits, in milliseconds. Zero starts the surface at
   * the impulse itself, which is what it always did.
   *
   * <p>ARTA's time zero is the cursor, which its manual has you set some way before the peak, so
   * the front of an ARTA surface is the window sliding <em>towards</em> the arrival rather than away
   * from it. This is the same head start, said in milliseconds; the time axis goes negative rather
   * than re-zeroing, so the impulse stays at 0 wherever the surface begins.</p>
   */
  startBeforePeakMs: number
  windowMode: CsdWindowModeName
  riseTimeMs: number
}

/**
 * The surface's depth in whole samples, which is what the server takes.
 *
 * <p><b>It does not depend on the slice count, and that is the point.</b> It used to derive a
 * <em>step</em> between slices instead, which had to be a whole sample and so put the rounding on
 * the span: 2.46 ms asked for at 96 kHz came out 2.458 ms over 60 slices, 2.063 over 100 and 3.104
 * over 150, the axis changing length because of a setting that has nothing to do with it. The span
 * is rounded once here and the slices are spread across it server-side.</p>
 */
export function csdSpanSamples(spanMs: number, sampleRateHz: number): number {
  const samples = Math.round((spanMs / 1000) * sampleRateHz)
  return Math.max(1, Number.isFinite(samples) ? samples : 1)
}

/**
 * The span that really comes out, to the sample — within half a sample of the one asked for at any
 * slice count, where it used to be out by a quarter of itself.
 */
export function csdAchievedSpanMs(analysis: CsdAnalysis, sampleRateHz: number): number {
  return sampleRateHz > 0 ? (csdSpanSamples(analysis.spanMs, sampleRateHz) / sampleRateHz) * 1000 : 0
}

/**
 * The average gap between slices, in samples — what the panel prints beside the span. Fractional on
 * purpose: the slices land on whole samples individually, and a figure rounded here would claim a
 * regularity they do not have.
 */
export function csdSliceStepSamples(analysis: CsdAnalysis, sampleRateHz: number): number {
  const steps = Math.max(1, Math.round(analysis.slices) - 1)
  return csdSpanSamples(analysis.spanMs, sampleRateHz) / steps
}

/**
 * **ARTA's own analysis, said in units that do not move with the sample rate** (Joost,
 * 2026-10-24), so that a new measurement opens on what ARTA opens on and the two surfaces can be
 * put side by side.
 *
 * <p>ARTA's four numbers are 256 points, a shift of 2 and 60 blocks off a 48 kHz sound card: a
 * 5.33 ms window resolving 187.5 Hz over 2.46 ms of decay. Two of those are in <b>samples</b> and
 * mean something else at another rate, so what is written down here is the pair that does not —
 * 2.46 ms of span over 60 slices, which is ARTA's own 2.4583 ms to within two microseconds and is
 * now honoured to the sample at any rate and any slice count. The transform length is still in
 * samples because it has to be a power of two, and 1024 is the one that gives 5.33 ms and 187.5 Hz
 * at the 192 kHz an impulse measurement now opens at.</p>
 */
export const DEFAULT_CSD_ANALYSIS: Readonly<CsdAnalysis> = Object.freeze({
  fftLength: 1024,
  spanMs: 2.46,
  slices: 60,
  windowMode: 'Apodizing',
  riseTimeMs: 0.2,
  startBeforePeakMs: 0,
})

/**
 * How many slices to draw, when the surface has more of them than the chart has pixels of depth.
 *
 * <p>One curve per pixel of depth, which at any ordinary chart size means <em>all</em> of them. It
 * was one per six, then one per three, both from when the area under a curve was filled with the
 * page: a dense stack of unfilled curves really is an unreadable thicket, and thinning it was the
 * only defence. A filled stack is the opposite — each curve hides what is behind it — and the thing
 * that separates adjacent curves is not the depth offset at all, it is the <em>level</em> between
 * them. That is why ARTA can draw every one of its slices and stay legible, and why thinning cost
 * detail without buying anything: at the default settings the burst decay computes
 * <c>30 x 8 + 1 = 241</c> slices, of which one per six drew 43 and one per three drew 81.</p>
 *
 * <p>The cap remains for the pathological case — a very deep analysis on a very short chart — where
 * the level separation is not there to save it either.</p>
 */
export function sliceStride(sliceCount: number, depthPixels: number): number {
  const maxSlices = Math.max(8, Math.floor(depthPixels))
  return sliceCount <= maxSlices ? 1 : Math.ceil(sliceCount / maxSlices)
}

/**
 * Which slices the waterfall actually draws, back to front. The last one is always included however
 * the stride falls — it is the front edge of the whole picture, and its absence reads as a chart
 * that stops early.
 *
 * <p>Shared between the drawing and the hit test rather than derived twice: the hit test replays the
 * painter's algorithm to work out which slice owns a pixel, and a list that disagreed by one slice
 * would report a neighbour's level with no sign that anything was wrong.</p>
 */
export function drawnSliceIndices(sliceCount: number, depthPixels: number): number[] {
  if (sliceCount <= 0) return []

  const stride = sliceStride(sliceCount, depthPixels)
  const indices: number[] = []
  for (let i = 0; i < sliceCount; i += stride) indices.push(i)
  if (indices[indices.length - 1] !== sliceCount - 1) indices.push(sliceCount - 1)
  return indices
}

/**
 * One surface, read either way round.
 *
 * <p>A decay surface is level against frequency and against time, and which of the two runs across
 * the curves is a presentation choice, not a measurement one — ARTA calls the two views Waterfall_F
 * and Waterfall_P. Reading frequency across gives a curve per instant, which is the familiar shape
 * and shows where in the band something rings. Reading <em>time</em> across gives a curve per
 * frequency, each one a decay envelope, which is the shape that shows how long.</p>
 *
 * <p>Everything downstream — the drawing, the axes, the hit test — works through this, so neither
 * view is a special case and the two cannot drift apart.</p>
 */
export interface OrientedSurface {
  /** Values along the horizontal axis of every curve, low to high. */
  across: number[]
  /** Each of those as a fraction of the axis. Log for frequency, linear for time. */
  acrossFraction: number[]
  acrossIsFrequency: boolean
  /** One value per curve, back to front. */
  depth: number[]
  depthIsFrequency: boolean
  /** The level at one point of one curve, or null where the analysis has no answer there. */
  level(depthIndex: number, acrossIndex: number): number | null
}

/**
 * @param depthIsFrequency Whether the stack recedes in frequency (time across the curves) rather
 * than in time.
 */
export function orientSurface(surface: DecaySurface, depthIsFrequency: boolean): OrientedSurface {
  const times = surface.slices.map((slice) => slice.value)
  const across = depthIsFrequency ? times : surface.freqHz
  const depth = depthIsFrequency ? surface.freqHz : times

  return {
    across,
    acrossFraction: axisFractions(across, !depthIsFrequency),
    acrossIsFrequency: !depthIsFrequency,
    depth,
    depthIsFrequency,
    // The transpose is only in the indices. Note what it does to the missing data: read frequency
    // across, a slice is missing its *low* frequencies, so its curve starts late; read time across,
    // a low frequency runs out of record partway, so its curve stops early. Same rows, and both
    // read straight through `levelAt` rather than through a copy of the surface.
    level: depthIsFrequency
      ? (depthIndex, acrossIndex) => levelAt(surface, acrossIndex, depthIndex)
      : (depthIndex, acrossIndex) => levelAt(surface, depthIndex, acrossIndex),
  }
}

/** The level at one column of one slice, or null where that slice has no data for it. */
export function levelAt(surface: DecaySurface, sliceIndex: number, column: number): number | null {
  const slice = surface.slices[sliceIndex]
  if (!slice || column < slice.firstValidColumn) return null
  const value = slice.levelDb[column - slice.firstValidColumn]
  return value === undefined ? null : value
}

/** A point of the surface, found under the cursor. Coordinates are relative to the plot box. */
export interface DecayPick {
  depthIndex: number
  acrossIndex: number
  levelDb: number
  x: number
  y: number
}

/**
 * What the waterfall is showing at a screen point — the arithmetic behind the readout.
 *
 * <p>It replays the painter's algorithm rather than sampling anything. Each slice is drawn as a
 * filled body running from its curve down to the foot of the box, back to front, so the slice that
 * <em>owns</em> a pixel is the nearest one whose body covers it: walk the drawn slices from the
 * front backwards and take the first whose curve at that frequency lies at or above the point. That
 * makes the whole visible surface pickable rather than only the one-pixel curves, which is the
 * difference between a readout you can use and one you have to hunt for.</p>
 *
 * <p>The point along the curve comes out of the projection instead of a search: the values are
 * evenly spaced in whatever the axis is — the frequency columns are log-spaced and the axis is
 * logarithmic, the times are linearly spaced and that axis is linear — so the index is the axis
 * fraction scaled, either way round. A slice's own origin is what shifts that fraction as the stack
 * recedes.</p>
 *
 * @param x Horizontal position inside the plot box, 0 at its left edge.
 * @param y Vertical position inside the plot box, 0 at the top.
 */
export function pickSurfacePoint(
  oriented: OrientedSurface,
  layout: DecayLayout,
  drawnIndices: number[],
  x: number,
  y: number,
  topDb: number,
  bottomDb: number,
): DecayPick | null {
  const columns = oriented.across.length
  const count = oriented.depth.length
  if (columns === 0 || count === 0) return null

  for (let k = drawnIndices.length - 1; k >= 0; k--) {
    const index = drawnIndices[k]
    const depth = count > 1 ? index / (count - 1) : 0
    const origin = sliceOrigin(layout, depth)

    const u = (x - origin.x) / layout.curveWidth
    if (u < 0 || u > 1) continue

    const acrossIndex = Math.round(u * (columns - 1))
    const levelDb = oriented.level(index, acrossIndex)
    if (levelDb === null) continue

    const curveY = levelY(layout, origin.y, levelDb, topDb, bottomDb)
    if (y < curveY) continue

    return {
      depthIndex: index,
      acrossIndex,
      levelDb,
      x: acrossX(layout, origin.x, oriented.acrossFraction[acrossIndex]),
      y: curveY,
    }
  }

  return null
}

/** One rib of the trailing wall: where a curve ends, and how far its own floor is below that. */
export interface WallRib {
  x: number
  y: number
  /** The curve's own baseline — the bottom of the wall at this depth, not the foot of the box. */
  floorY: number
  levelDb: number
}

/**
 * Whether a curve's trailing end reaches past every curve drawn before it, and so adds a rib to the
 * wall rather than a fold back inside it.
 *
 * <p>The <em>trailing</em> end is the one away from the direction the stack recedes in: the right
 * end when it recedes leftwards. That is the edge with a visible wall, and which edge that is
 * follows from the occlusion rather than from taste. Take a leftward stack. The strip between two
 * neighbouring right ends is one no nearer curve reaches into, because every nearer curve ends
 * further left — so exactly one curve owns it, and the wall standing there is what the viewer sees.
 * The strip between two neighbouring <em>left</em> ends is the opposite: every nearer curve extends
 * at least that far, so it is painted over until only the front curve's body is left, and there is
 * no wall to see. Drawing one there paints a face that is not exposed.</p>
 *
 * <p>Non-monotonic ends are the reason this is a test and not an assumption. A curve that runs out
 * of record ends early, folding back inside the wall; ribbed at that point the wall would double
 * back on itself and its quads would overlap.</p>
 */
export function advancesTrailingEdge(x: number, reachedX: number | null, recedesLeft: boolean): boolean {
  if (reachedX === null) return true
  return recedesLeft ? x < reachedX : x > reachedX
}

/** How far the trailing edge has reached, once this curve is included. */
export function furthestReached(x: number, reachedX: number | null, recedesLeft: boolean): number {
  if (reachedX === null) return x
  return recedesLeft ? Math.min(reachedX, x) : Math.max(reachedX, x)
}

/**
 * Decade ticks for a log frequency axis — 1, 2 and 5 per decade, which is what every other chart in
 * the app labels and so what a reader already expects. Its own generator rather than uPlot's,
 * because these charts are drawn straight onto a canvas.
 */
export function logTicks(lowHz: number, highHz: number): number[] {
  if (!(lowHz > 0) || !(highHz > lowHz)) return []

  const ticks: number[] = []
  const firstDecade = Math.floor(Math.log10(lowHz))
  const lastDecade = Math.ceil(Math.log10(highHz))
  for (let decade = firstDecade; decade <= lastDecade; decade++) {
    for (const mantissa of [1, 2, 5]) {
      const value = mantissa * Math.pow(10, decade)
      if (value >= lowHz && value <= highHz) ticks.push(value)
    }
  }

  return ticks
}

/**
 * Round ticks for a linear axis — a 1, 2 or 5 times a power of ten, chosen so there are no more
 * than `maximum` of them. The time axis of a Waterfall_P needs these; the frequency axis never
 * does, and the two share nothing but the shape of the loop that draws them.
 */
export function linearTicks(low: number, high: number, maximum = 6): number[] {
  const span = high - low
  if (!(span > 0) || maximum < 1) return []

  const power = Math.pow(10, Math.floor(Math.log10(span / maximum)))
  const step = [1, 2, 5, 10].map((multiple) => multiple * power).find((candidate) => span / candidate <= maximum)
  if (step === undefined) return []

  const ticks: number[] = []
  // Rounded off the accumulated sum: adding 0.2 five times does not reach 1 in binary floating
  // point, and a tick labelled "0.6000000000000001" is the kind of thing nobody notices in review.
  for (let i = Math.ceil(low / step - 1e-9); i * step <= high + step * 1e-9; i++) {
    ticks.push(Number((i * step).toFixed(10)))
  }
  return ticks
}

/** A frequency as an axis label: 20, 200, 2k, 20k. */
export function formatHz(hz: number): string {
  if (hz >= 1000) {
    const k = hz / 1000
    return `${k >= 10 || Number.isInteger(k) ? k.toFixed(0) : k.toFixed(1)}k`
  }
  return hz >= 10 ? hz.toFixed(0) : hz.toFixed(1)
}

/**
 * How many frequency columns each analysis is asked for. Fixed rather than exposed: they are the
 * resolution of the <em>picture</em>, not of the measurement, and a chart is a few hundred pixels
 * wide however the analysis is set up. The cumulative spectral decay gets more because its columns
 * are cheap — one reduction of an FFT already computed — while every burst-decay column is its own
 * convolution.
 */
export const DECAY_DISPLAY_COLUMNS = 400

export const BURST_FREQUENCY_COLUMNS = 200
