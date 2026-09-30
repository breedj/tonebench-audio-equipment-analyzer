/**
 * The shared y windows for the sweep screen's small point previews — the spectrum's in dB, the
 * oscilloscope's in volts — and the two toggles that turn them on.
 *
 * Every preview is auto-ranged to its own point by default, which is what makes two points hard to
 * compare: hovering along the sweep chart re-scales the axis under the cursor, so a harmonic that
 * grew by 10 dB and one that did not look identical. Fitted, all of them share one window covering
 * every point the main chart is showing — the live sweep's plus each checked overlay's — so the
 * stacked previews are directly comparable both across points and against each other.
 *
 * The two differ only in how the grid is chosen: the spectrum's step is fixed at 20 dB, since a
 * decibel decade is a decade whatever the level, while volts have no such natural step and take a
 * 1-2-5 ladder sized to the waveform. The scope's window is symmetric about zero, which is where a
 * waveform's own axis belongs and what makes a clipped half visible as an asymmetry rather than
 * hidden by an axis that moved with it.
 *
 * Pure so it can be tested: nothing in a `.vue` is testable under the node environment vitest runs
 * in, and the snapping is the part worth pinning.
 */

import { ref } from 'vue'
import type { SweepPreviewSeries } from '../api/client'

/** Grid spacing, in dB. The axis starts and ends on one of these lines. */
export const PREVIEW_GRID_STEP_DB = 20

/**
 * A preview value this low is a dead column, not a measurement: `SpectrumPipeline.BuildFftPreview`
 * floors an all-zero bucket at `double.Epsilon`, which comes back as about −3230 dB. One of those
 * in the union would stretch the axis over twenty grid lines of nothing.
 */
const PREVIEW_FLOOR_DB = -300

export interface PreviewAxisRange {
  /** Both ends are multiples of `stepDb`. */
  min: number
  max: number
}

/**
 * The window covering every given preview's drawn curve, snapped outwards to the grid. Null when
 * none of them holds a usable value — no points yet, or a channel that was never swept.
 *
 * Only `max` is scanned because only `max` is drawn: the previews plot one line per channel rather
 * than a min/max band, see SweepFftPreviewChart.
 */
export function fitPreviewAxis(
  previews: readonly (SweepPreviewSeries | null | undefined)[],
  stepDb = PREVIEW_GRID_STEP_DB,
): PreviewAxisRange | null {
  let lo = Number.POSITIVE_INFINITY
  let hi = Number.NEGATIVE_INFINITY

  for (const preview of previews) {
    if (!preview) continue
    for (const value of preview.max) {
      if (!Number.isFinite(value) || value < PREVIEW_FLOOR_DB) continue
      if (value < lo) lo = value
      if (value > hi) hi = value
    }
  }
  if (!(lo <= hi)) return null

  const min = plusZero(Math.floor(lo / stepDb) * stepDb)
  const max = plusZero(Math.ceil(hi / stepDb) * stepDb)
  // A trace that sits exactly on a grid line (or inside one step of it) would otherwise give a
  // zero-height axis, which uPlot draws as a single line through the middle of the plot.
  return { min, max: max > min ? max : min + stepDb }
}

/** Rounding a small negative outwards yields -0, which has no business appearing on an axis. */
function plusZero(value: number): number {
  return value === 0 ? 0 : value
}

/** Twelve significant figures: enough to keep any window a step ladder can produce, short of the noise. */
function trim(value: number): number {
  return Number(value.toPrecision(12))
}

/** The grid lines inside `[min, max]`, every `stepDb`. Both ends are included by construction. */
export function previewAxisSplits(min: number, max: number, stepDb = PREVIEW_GRID_STEP_DB): number[] {
  if (!Number.isFinite(min) || !Number.isFinite(max) || !(stepDb > 0) || max < min) return []

  // Both bounds carry slack rather than being exact. Neither the division nor the accumulation is
  // exact in floating point, and the window has been snapped to the grid, so a line sits on each
  // of its very edges — where a rounding error of one part in 10^16 would drop it. The start's
  // slack is a hair (a whole line below `min` must stay out); the end's can be half a step, since
  // there is nothing above `max` to let in by mistake.
  const first = Math.ceil((min - stepDb * 1e-9) / stepDb) * stepDb
  const splits: number[] = []
  for (let value = first; value <= max + stepDb / 2; value += stepDb) splits.push(plusZero(value))
  return splits
}

/** Below this, two tick labels touch at the previews' 10 px type. */
const MIN_LABEL_SPACING_PX = 22

/**
 * The tick labels for those grid lines, thinned to whatever fits: every line is still drawn (the
 * grid is what the eye reads a level off), but only every second or third one is numbered.
 *
 * These charts are 150 px tall and the window can be 240 dB deep — the simulator's floor reaches
 * −220 dBV — which is thirteen labels in the space of six. Thinning by *value* rather than by
 * position keeps the numbers on round multiples of the stride, so 0 is labelled whatever the span.
 */
export function previewAxisTickLabels(
  splits: readonly (number | null)[],
  plotHeightPx: number,
  stepDb = PREVIEW_GRID_STEP_DB,
  minSpacingPx = MIN_LABEL_SPACING_PX,
): (string | null)[] {
  return thinnedLabels(splits, plotHeightPx, stepDb, minSpacingPx, String)
}

/** `previewAxisTickLabels`' shared half: which lines get a number, whatever the unit prints like. */
function thinnedLabels(
  splits: readonly (number | null)[],
  plotHeightPx: number,
  step: number,
  minSpacingPx: number,
  format: (value: number) => string,
): (string | null)[] {
  const gaps = splits.length - 1
  const spacing = gaps > 0 && plotHeightPx > 0 ? plotHeightPx / gaps : Number.POSITIVE_INFINITY
  const stride = Math.max(1, Math.ceil(minSpacingPx / spacing))
  return splits.map((value) =>
    value !== null && Math.round(value / step) % stride === 0 ? format(value) : null,
  )
}

// ---------------------------------------------------------------------------
// The oscilloscope preview's volts axis
// ---------------------------------------------------------------------------

/** How many divisions the fitted scope window is allowed per side before the step grows. */
const SCOPE_MAX_DIVISIONS = 4
/** The steps a volts grid may use, per decade — the usual 1-2-5 ladder. */
const SCOPE_STEP_MANTISSAS = [1, 2, 5]

export interface ScopeAxisRange extends PreviewAxisRange {
  /** Grid spacing, in volts. `min`/`max` are both multiples of it, and `min` is `-max`. */
  stepV: number
}

/**
 * The volts window covering every given waveform, symmetric about zero and snapped out to a 1-2-5
 * grid. Null when none of them holds a usable sample.
 *
 * Both `min` and `max` are scanned, unlike the spectrum's: the oscilloscope preview draws a
 * min/max pair per channel (the decimation's envelope), so the trough is as much on screen as the
 * peak.
 */
export function fitScopePreviewAxis(
  previews: readonly (SweepPreviewSeries | null | undefined)[],
): ScopeAxisRange | null {
  let peak = 0
  let seen = false

  for (const preview of previews) {
    if (!preview) continue
    for (const series of [preview.min, preview.max]) {
      for (const value of series) {
        if (!Number.isFinite(value)) continue
        seen = true
        if (Math.abs(value) > peak) peak = Math.abs(value)
      }
    }
  }
  if (!seen) return null

  const stepV = scopeStepFor(peak)
  // At least one division either way: a channel that measured silence still needs a drawable axis,
  // and a peak landing exactly on a line must not be rounded up past it (hence the slack). Trimmed
  // of its binary noise, since 3 x 0.05 is 0.15000000000000002 and this number is read by eye.
  const max = trim(Math.max(stepV, Math.ceil(peak / stepV - 1e-9) * stepV))
  return { min: -max, max, stepV }
}

/**
 * The smallest 1-2-5 step that fits `peak` inside `SCOPE_MAX_DIVISIONS` of them — so the grid is
 * between two and four divisions per side whatever the level, from a 7 V loudspeaker drive down to
 * a millivolt of residual. A peak of exactly zero (a channel that was silent) still needs a
 * drawable axis, so it takes the smallest step rather than none.
 */
function scopeStepFor(peak: number): number {
  if (!(peak > 0)) return 0.001

  const decade = Math.pow(10, Math.floor(Math.log10(peak / SCOPE_MAX_DIVISIONS)))
  for (const mantissa of SCOPE_STEP_MANTISSAS) {
    const step = mantissa * decade
    if (peak / step <= SCOPE_MAX_DIVISIONS + 1e-9) return step
  }
  return 10 * decade
}

/** The scope grid's labels, in volts, thinned the way the spectrum's are. */
export function scopeAxisTickLabels(
  splits: readonly (number | null)[],
  plotHeightPx: number,
  stepV: number,
  minSpacingPx = MIN_LABEL_SPACING_PX,
): (string | null)[] {
  // Decimals from the step, not from the value: the splits are accumulated, so the line at 0.15
  // arrives as 0.15000000000000002 and `String` would print all of it. Each value is snapped back
  // onto its own line before printing for the same reason -- zero accumulates to -1.3e-17, which
  // `toFixed` renders as the nonsense "-0.00".
  const decimals = Math.max(0, Math.ceil(-Math.log10(stepV)))
  return thinnedLabels(splits, plotHeightPx, stepV, minSpacingPx, (value) =>
    plusZero(Math.round(value / stepV) * stepV).toFixed(decimals),
  )
}

// Module-level singletons, so the live pair and every overlay's preview lock together — the whole
// point is comparing one stacked chart against another, which independent toggles would not do.
// Two of them, not one: a spectrum and a waveform are read for different things, and holding one
// axis still is useful without the other. App.vue restores both from the measurement type's
// params_json and writes them back on save (chartFitPreviewSpectrum / chartFitPreviewScope).
const fitAllSpectra = ref(false)
const fitAllScopes = ref(false)

export function useSweepPreviewFit() {
  return { fitAllSpectra, fitAllScopes }
}
