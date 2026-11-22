/**
 * The math channels' own vertical axes — a per-division figure and a centre, exactly as the volts
 * axis has, and an Autoscale that holds rather than hunts.
 *
 * <p><b>Why this exists.</b> The math scale was uPlot's <code>auto: true</code>, which was defended
 * here as "the honest answer for a derived quantity whose magnitude nobody knows in advance, and
 * unlike the volts axis there is no V/div ladder it could be a rung of". The first half is a good
 * argument for the <em>default</em> and no argument at all against having the control: an auto
 * scale re-ranges on every record, so a trace that wobbles redraws the graticule under itself
 * several times a second and nothing on it can be counted. Reported 2026-11-22 in those words —
 * "I had a wobbly trace and the scale is now changing up and down. Make it work the same as V/div.
 * But show the correct unit."</p>
 *
 * <p>The second half was simply wrong: a ladder does not have to be a table. 1-2-5 per decade is
 * arithmetic, and it is as round a number of V², of V/s or of a bare ratio as it is of volts —
 * which is the whole of what a graticule needs, since a scope is read by counting squares.</p>
 *
 * <p><b>Computed rather than tabulated</b>, and that is what makes one module serve five units: the
 * volts ladder is twenty-one rungs between figures the <em>hardware</em> sets, where these five
 * quantities span some twenty decades between them (a V·s of microvolts over microseconds is
 * 1e-12; a V/s of a volt at 20 kHz is 1.3e5). A table wide enough for all of them would be sixty
 * rungs, which is a grid nobody could use — so the tile is given no grid at all and the two step
 * buttons walk the decades. The bounds exist only so a press at either end stops.</p>
 *
 * <p>Here rather than in the panel for this project's usual reason: nothing in a <code>.vue</code>
 * is testable under the node environment vitest runs in, and every rule in this file is the kind
 * that is quietly wrong rather than visibly broken.</p>
 */

import { MAX_FILL_FRACTION, VERTICAL_DIVISIONS } from './voltAxis'
import { voltWindowNeedsFit } from './autoscale'
import { formatMathValue, type ScopeMathUnit } from './math'

/**
 * The graticule these axes are counted against: the volts axis's own.
 *
 * <p>It has to be, rather than merely happening to match — the math trace is drawn on the same
 * plot, so "two divisions of it" and "two divisions of the waveform" are the same height on screen
 * or neither figure means anything.</p>
 */
export const MATH_DIVISIONS = VERTICAL_DIVISIONS

/**
 * The widest and narrowest a division may be.
 *
 * <p>Not a judgement about any quantity: they are the ends the step buttons stop at, placed far
 * enough out that no real reading reaches them. The floor is a picovolt-second, which is a
 * microvolt integrated over a microsecond; the ceiling is well past the 1.3e5 V/s a volt at the
 * top of the band slews at.</p>
 */
export const MIN_MATH_PER_DIVISION = 1e-15
export const MAX_MATH_PER_DIVISION = 1e12

/** One math axis: what the user sets, from which the window is derived. */
export interface ScopeMathAxis {
  /** How much of the quantity one vertical division is worth. */
  perDivision: number
  /** What the middle of the graticule reads. */
  centre: number
  /** Whether the pair follows the trace. See the note on the default. */
  autoscale: boolean
}

/**
 * What a math axis starts at.
 *
 * <p><b>Autoscale defaults on, where the volts axis's defaults off</b>, and the difference is not
 * an inconsistency: a manual V/div is the oscilloscope convention and has been this screen's
 * behaviour since it was written, while the math axis has <em>always</em> ranged itself. Off by
 * default would give every measurement already saved a fixed scale nobody chose, on an axis whose
 * magnitude nobody knows in advance — which is the one case the old `auto: true` really was right
 * about.</p>
 *
 * <p>What changes for a reader who never touches the control is that automatic now means
 * <code>mathAxisNeedsFit</code>'s dead band rather than uPlot's re-range on every record, so the
 * reported wobble stops whether or not the switch is ever found.</p>
 */
export const DEFAULT_SCOPE_MATH_AXIS: ScopeMathAxis = Object.freeze({
  perDivision: 1,
  centre: 0,
  autoscale: true,
})

/** The decade a figure sits in, nudged so a round one does not fall into the decade below. */
function decadeOf(value: number): number {
  // Math.log10(0.001) is not reliably -3, and a bare floor then files a round figure one decade
  // down with a mantissa of 10 -- panels/stepperGrid.ts carries the same nudge for the same reason.
  return Math.pow(10, Math.floor(Math.log10(value) + 1e-9))
}

function clampPerDivision(value: number): number {
  return Math.min(MAX_MATH_PER_DIVISION, Math.max(MIN_MATH_PER_DIVISION, value))
}

/** The 1-2-5 rung at or above <paramref name="wanted"/>. */
export function snapMathPerDivision(wanted: number): number {
  if (!Number.isFinite(wanted) || wanted <= 0) return MIN_MATH_PER_DIVISION

  const decade = decadeOf(wanted)
  const mantissa = wanted / decade
  const rung = mantissa <= 1 + 1e-9 ? 1 : mantissa <= 2 + 1e-9 ? 2 : mantissa <= 5 + 1e-9 ? 5 : 10
  return clampPerDivision(rung * decade)
}

/** One rung up or down, stopping at either end rather than wrapping. */
export function stepMathPerDivision(current: number, direction: 1 | -1): number {
  const at = snapMathPerDivision(current)
  const decade = decadeOf(at)
  // 1, 2 or 5 by construction, since `at` came off the snap.
  const mantissa = Math.round(at / decade)

  const next =
    direction > 0
      ? mantissa === 1
        ? 2 * decade
        : mantissa === 2
          ? 5 * decade
          : 10 * decade
      : mantissa === 1
        ? 0.5 * decade
        : mantissa === 2
          ? decade
          : 2 * decade

  // Re-snapped, because halving a decade is where the arithmetic above can leave a figure a
  // rounding error off its own rung.
  return snapMathPerDivision(next)
}

/** The two ends of the plot, which is what uPlot is given. */
export interface MathWindow {
  top: number
  bottom: number
}

/** Where the graticule puts the two ends, given the pair the user sets. */
export function mathWindow(axis: ScopeMathAxis): MathWindow {
  const perDivision =
    Number.isFinite(axis.perDivision) && axis.perDivision > 0 ? axis.perDivision : 1
  const centre = Number.isFinite(axis.centre) ? axis.centre : 0
  const half = (perDivision * MATH_DIVISIONS) / 2
  return { top: centre + half, bottom: centre - half }
}

/**
 * The pair that frames a trace running between two extremes — <code>fitVoltWindow</code>'s rule on
 * a computed ladder.
 *
 * <p>The centre is the midpoint of the extremes rather than the mean, for the reason that function
 * gives: it is the picture being framed, and a waveform with an unequal duty has a mean nowhere
 * near the middle of it. Unlike the volts fit there is <b>no snap to zero</b>, because zero is not
 * a special value here — a ratio sits at one, a product of two tones sits on its own DC term, and
 * pulling either onto zero would move the trace off the picture it was being fitted into.</p>
 */
export function fitMathAxis(max: number, min: number): Pick<ScopeMathAxis, 'perDivision' | 'centre'> {
  if (!Number.isFinite(max) || !Number.isFinite(min)) {
    return { perDivision: 1, centre: 0 }
  }

  const hi = Math.max(max, min)
  const lo = Math.min(max, min)
  const perDivision = snapMathPerDivision((hi - lo) / (MATH_DIVISIONS * MAX_FILL_FRACTION))
  const centre = roundForWindow((hi + lo) / 2, perDivision * MATH_DIVISIONS)
  return { perDivision, centre }
}

/**
 * A figure rounded to about a thousandth of the window it will be read in.
 *
 * <p><code>roundVoltsForWindow</code>'s arithmetic, written again here rather than imported under
 * its own name: that one is spelled in volts throughout, and a centre of 1.0000000000000002 on a
 * ratio axis is the same fault by a different unit. As a decimal place rather than a multiplier,
 * for the reason it gives — rounding to the nearest multiple of a step that is itself not
 * representable puts every digit straight back.</p>
 */
function roundForWindow(value: number, span: number): number {
  if (!Number.isFinite(value)) return value
  if (!Number.isFinite(span) || span <= 0) return value

  const decimals = Math.min(15, Math.max(0, Math.ceil(-Math.log10(span / 1000))))
  return Number(value.toFixed(decimals))
}

/**
 * Whether the axis no longer frames what is drawn — the vertical's own dead band, on this axis.
 *
 * <p>Shared rather than reimplemented, and it is shareable because that function is arithmetic over
 * a graticule and never over volts: it asks whether the ink reaches past the fraction of the window
 * a fit would have left it inside, and whether what is left fills enough of the height to be read.
 * Both questions are the same in V², in V/s and in nothing at all.</p>
 */
export function mathAxisNeedsFit(axis: ScopeMathAxis, max: number, min: number): boolean {
  return voltWindowNeedsFit(
    { voltsPerDivision: axis.perDivision, offsetV: axis.centre },
    max,
    min,
  )
}

/**
 * What the tile reads — the division written in the quantity's own unit.
 *
 * <p><code>formatMathValue</code> is the one formatter the axis ticks and the measurement table
 * already go through, so a division and the figures counted against it cannot end up in different
 * units on one picture. It carries the unit rule that matters most here: a rate scales its
 * <em>denominator</em>, so a slew of 2.4 million volts a second reads <code>2.400 V/µs</code>.</p>
 */
export function formatMathPerDivision(perDivision: number, unit: ScopeMathUnit): string {
  return formatMathValue(perDivision, unit)
}
