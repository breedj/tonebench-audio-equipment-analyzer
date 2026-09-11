import uPlot from 'uplot'
import { freqTickSplits } from '../freqAxis'

// The impedance chart's y axis: ohms, on a log or a linear scale.
//
// The tick *placement* is `freqTickSplits`, reused rather than reimplemented. Nothing in it is about
// frequency — it walks a mantissa ladder (1..9, then 1/2/5, then decades) and takes the finest one
// whose tightest pixel gap still clears a spacing budget, falling back to linear ticks when the span
// is under 3:1. That is exactly what a log ohms axis needs, and a second copy would be a second
// place for "5.859375" to come back.
//
// Only the labels are unit-specific, and they are the part worth having here: an impedance axis
// spans milliohms to kilohms, so a single fixed format is wrong at one end or the other.

/** Vertical pixels a y-axis tick wants to itself. Tighter than the frequency axis' 60 px, since a label is one line tall rather than several characters wide. */
export const OHM_AXIS_SPACE = 34

/**
 * A tick's label. Chosen per value rather than per axis, because the ladder can place ticks either
 * side of a unit boundary — 800 mΩ and 2 Ω can both be on screen — and forcing one unit across the
 * whole axis would print either "0.8" or "2000" somewhere.
 */
export function ohmTickLabel(value: number | null | undefined): string | null {
  if (value === null || value === undefined || !Number.isFinite(value) || value <= 0) return null

  if (value < 1) return `${sig3(value * 1000)}m`
  if (value < 1000) return sig3(value)
  if (value < 1e6) return `${sig3(value / 1000)}k`
  return `${sig3(value / 1e6)}M`
}

/**
 * Three significant figures without ever reaching exponential notation. `toPrecision` flips to
 * `1.00e+3` at a thousand, which is unreadable on an axis and survives no amount of trimming — and
 * the scaled value does reach a thousand at the top of each unit band.
 */
function sig3(v: number): string {
  const abs = Math.abs(v)
  const decimals = abs >= 100 ? 0 : abs >= 10 ? 1 : 2
  return trim(v.toFixed(decimals))
}

/**
 * A tick on an axis that runs through zero and into negatives — the complex plane's reactance axis,
 * where below the line is capacitive.
 *
 * <p>Its own function rather than a flag on {@link ohmTickLabel}, which returns null for anything at
 * or below zero because a <i>logarithmic</i> impedance axis genuinely cannot show one. Here zero is
 * the most important tick on the chart.</p>
 */
export function signedOhmTickLabel(value: number | null | undefined): string | null {
  if (value === null || value === undefined || !Number.isFinite(value)) return null
  if (value === 0) return '0'

  const label = ohmTickLabel(Math.abs(value))
  if (label === null) return null
  return value < 0 ? `-${label}` : label
}

export function ohmTickLabels(splits: (number | null)[]): (string | null)[] {
  return splits.map(ohmTickLabel)
}

/** Drops the trailing zeros `toPrecision` leaves behind, so a tick reads "8" rather than "8.00". */
function trim(s: string): string {
  if (!s.includes('.')) return s
  return s.replace(/\.?0+$/, '')
}

/**
 * The axis config, spread into the chart's own options so the colours and grid stay there.
 *
 * <p>Two shapes. On a <b>logarithmic</b> axis the tick placement is the mantissa ladder above and
 * the labels refuse zero, which the axis cannot show. On a <b>linear</b> one uPlot's own even
 * splits are right — a 0..100 Ω window wants 0, 20, 40, not 1, 2, 5, 10 — and zero is a label like
 * any other, so the signed formatter does the labels.</p>
 *
 * The log splits callback measures the plot's own height rather than taking a prop: the chart is
 * responsive, and a ladder chosen against a stale height is what puts labels on top of each other.
 */
export function ohmAxis(log: boolean = true): Pick<uPlot.Axis, 'space' | 'splits' | 'values'> {
  if (!log) {
    return {
      space: OHM_AXIS_SPACE,
      values: (_u: uPlot, splits: (number | null)[]) => splits.map(signedOhmTickLabel),
    }
  }

  return {
    space: OHM_AXIS_SPACE,
    splits: (u: uPlot, _axisIdx: number, min: number, max: number) => {
      const heightPx = u.bbox.height / (uPlot.pxRatio || 1)
      return freqTickSplits(min, max, heightPx || 400, OHM_AXIS_SPACE)
    },
    values: (_u: uPlot, splits: (number | null)[]) => ohmTickLabels(splits),
  }
}
