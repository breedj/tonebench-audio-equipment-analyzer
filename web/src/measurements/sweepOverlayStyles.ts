import type { OverlayEntry } from '../composables/useMeasurements'

/**
 * Line styling for the THD-vs-frequency chart (ThdSweepChart.vue), its legend, and the sweep
 * screen's history checklist (MeasurementChecklist.vue). The sweep counterpart of
 * spectrumOverlayColors.ts, which does the same job for the FFT chart.
 *
 * The worst case on screen is 4 runs (live + MAX_VISIBLE_OVERLAYS history) x 2 channels x up to 8
 * traces, and three orthogonal things have to stay readable at once. Each gets the visual variable
 * that matches how many levels it needs:
 *
 *   quantity (8: THD, D2..D6+, Magn, Noise)  -> hue      (only hue reliably carries 8)
 *   run      (4: live + 3 history)           -> dash     (dash tops out around 4 -- an exact fit)
 *   channel  (2: L/R)                        -> lightness + width
 *
 * Hue stays bound to the quantity: painting a whole stored run in one colour was tried and dropped,
 * because it made the *same quantity, then versus now* comparison -- the one thing a history
 * overlay exists for -- the one comparison it could not show. See TRACES in ThdSweepChart.vue.
 *
 * What this module fixes is the collision that came out of that: dash used to encode run *and*
 * channel, by concatenating the channel's pattern onto the run's ([20,5] for history 1 left, but
 * [20,5,6,3] for history 1 right, against [6,3] for the live right channel). At 1-2 px those are
 * the same picture, so the right channel read as history. Dash now means run and nothing else, and
 * channel moves onto lightness -- which was previously unused entirely.
 */

/**
 * One dash pattern per history slot; the live run is solid. Three maximally-distinct archetypes
 * (dashed / dotted / dash-dot) rather than the long patterns this replaces -- those were long only
 * so they would survive having the channel dash glued on the end, which no longer happens.
 *
 * One entry per slot MAX_VISIBLE_OVERLAYS allows, so a checked history item always has a pattern
 * of its own.
 */
export const SWEEP_OVERLAY_DASH: number[][] = [
  [10, 5],
  [2, 4],
  [12, 4, 2, 4],
]

/**
 * The overlays this chart can actually draw: visible, and carrying stored sweep points. An FFT
 * result checked on while the sweep screen is open has no points and is skipped rather than drawn
 * empty.
 *
 * Filtered by shape rather than by kind (unlike fftOverlays) because that is what the chart itself
 * can consume. Shared with the checklist so the two cannot disagree about slot order -- a run's
 * dash is its position in *this* list, so both callers must derive it the same way.
 */
export function sweepOverlays(overlays: OverlayEntry[]): OverlayEntry[] {
  return overlays.filter((o) => o.visible && o.sweepPoints?.length)
}

/**
 * The dash pattern one stored sweep draws in, by its position among the currently-visible ones --
 * recomputed on every call rather than assigned when the overlay was added, the same convention
 * fftOverlayColor uses. That is what guarantees the up-to-3 runs on screen are always 3 *distinct*
 * patterns: MAX_VISIBLE_OVERLAYS caps the visible set at exactly SWEEP_OVERLAY_DASH.length, so the
 * position never wraps around onto another visible run the way a persistent add-order index
 * eventually would once enough had been checked and unchecked.
 *
 * Null for an overlay that is not currently drawn -- the checklist renders no swatch for it rather
 * than one that promises a line the chart is not showing.
 */
export function sweepOverlayDash(overlays: OverlayEntry[], id: number): number[] | null {
  const idx = sweepOverlays(overlays).findIndex((o) => o.id === id)
  if (idx < 0) return null
  return SWEEP_OVERLAY_DASH[idx % SWEEP_OVERLAY_DASH.length] ?? null
}

/** The colour a blend moves toward: the page background, so a lightened line stays opaque against it. */
const BLEND_TARGET = { dark: '#0a0a0a', light: '#ffffff' } as const

/**
 * How far each (run, channel) pair is blended toward the background.
 *
 * A single lookup, deliberately *not* a channel factor composed with a run factor: both the L/R
 * split and history's recede-into-the-background want this same dimension, and multiplying the two
 * washes the right-channel history trace out completely.
 *
 * The ladder is monotonic, but note history-left (0.28) is darker than live-right (0.40) -- that is
 * intended and should not be "corrected". Lightness is meant to be read *within* a run (which
 * channel is this?) plus a general sense that stored runs sit behind the live one. Which run a line
 * belongs to is carried by the dash, which is unambiguous.
 */
const BLEND = { live: [0, 0.4], history: [0.28, 0.55] } as const

/** Relative line width per (run, channel), before the trace's own emphasis factor and the floor below. */
const CHANNEL_WIDTH = [1, 0.7] as const
const RUN_WIDTH = { live: 1, history: 0.85 } as const

/**
 * Nothing may be drawn thinner than this. Width carries three factors at once (trace emphasis,
 * channel, run) and the thin-lines worst case multiplies out to 0.71 px, which renders as a barely
 * visible grey smear rather than a line.
 */
const MIN_WIDTH = 0.9

function clamp01(value: number): number {
  return Math.min(1, Math.max(0, value))
}

/** Linear interpolation between two #rrggbb colours. Opaque throughout -- see blendToward. */
export function blendToward(hex: string, target: string, amount: number): string {
  const t = clamp01(amount)
  if (t === 0) return hex

  const from = parseHex(hex)
  const to = parseHex(target)
  if (!from || !to) return hex

  const mix = (a: number, b: number) => Math.round(a + (b - a) * t)
  return `#${[mix(from[0], to[0]), mix(from[1], to[1]), mix(from[2], to[2])]
    .map((c) => c.toString(16).padStart(2, '0'))
    .join('')}`
}

function parseHex(hex: string): [number, number, number] | null {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim())
  if (!m) return null
  const n = parseInt(m[1], 16)
  return [(n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff]
}

export interface SweepLineStyleInput {
  /** 0 for the live sweep, 1-based for a history run (its position among the visible ones). */
  runIdx: number
  /** 0 = left, 1 = right. */
  channel: number
  /** The trace's [light, dark] hue pair and its relative width, straight off ThdSweepChart's TRACES. */
  color: readonly [string, string] | readonly string[]
  /** The trace's own emphasis factor -- 1 for the headline traces (THD, Magnitude), less for harmonics. */
  traceWidth: number
  thickLines: boolean
  isDark: boolean
}

export interface SweepLineStyle {
  stroke: string
  width: number
  /** undefined for the live run (solid); uPlot wants undefined rather than an empty array. */
  dash: number[] | undefined
}

/**
 * The single source of truth for how one sweep line looks. Called by ThdSweepChart's series builder
 * *and* by its legend, which is the point: the legend used to compute its own marker style, so the
 * two could disagree about what a line looked like (uPlot's built-in legend could only ever say
 * "solid" or "dashed", and so could not name which of the three history patterns a row used at all).
 * One function means they cannot drift.
 */
export function sweepLineStyle(input: SweepLineStyleInput): SweepLineStyle {
  const { runIdx, channel, color, traceWidth, thickLines, isDark } = input
  const isHistory = runIdx > 0

  const base = isDark ? (color[1] ?? color[0]!) : color[0]!
  const blend = (isHistory ? BLEND.history : BLEND.live)[channel] ?? 0
  const stroke = blendToward(base, isDark ? BLEND_TARGET.dark : BLEND_TARGET.light, blend)

  const width = Math.max(
    (thickLines ? 2.4 : 1.4) *
      traceWidth *
      (CHANNEL_WIDTH[channel] ?? 1) *
      (isHistory ? RUN_WIDTH.history : RUN_WIDTH.live),
    MIN_WIDTH,
  )

  const dash = isHistory
    ? [...(SWEEP_OVERLAY_DASH[(runIdx - 1) % SWEEP_OVERLAY_DASH.length] ?? [])]
    : undefined

  return { stroke, width, dash }
}

/** `stroke-dasharray` for an SVG swatch, from the same dash array uPlot is given. Empty string = solid. */
export function dashAttr(dash: number[] | undefined | null): string | undefined {
  return dash && dash.length > 0 ? dash.join(',') : undefined
}

/**
 * The overlays a *response* chart can draw: visible, and carrying stored response curves.
 *
 * Separate from `sweepOverlays` because that one filters on `sweepPoints`, which every response
 * overlay lacks — so `sweepOverlayDash` returns null for all of them and `FrequencyResponseChart`
 * falls back to a single shared pattern. That is invisible on a Bode plot, where the curves sit at
 * different heights and are told apart by position. On a polar plot three overlays superimpose on
 * one contour, and one dash pattern between them is unreadable.
 */
export function responseOverlays(overlays: OverlayEntry[]): OverlayEntry[] {
  return overlays.filter((o) => o.visible && o.responseTraces?.length)
}

/**
 * The dash pattern one stored response draws in, by its position among the currently-visible ones —
 * exactly `sweepOverlayDash`'s rule, over the response-shaped list.
 */
export function responseOverlayDash(overlays: OverlayEntry[], id: number): number[] | null {
  const idx = responseOverlays(overlays).findIndex((o) => o.id === id)
  if (idx < 0) return null
  return SWEEP_OVERLAY_DASH[idx % SWEEP_OVERLAY_DASH.length] ?? null
}
