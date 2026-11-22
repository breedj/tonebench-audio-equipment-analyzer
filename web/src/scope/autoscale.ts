/**
 * Autoscale: the V/div and Time/div knobs kept optimal while the signal changes, rather than one
 * press that frames whatever was on screen at the time.
 *
 * <p>Asked for 2026-11-22 — "the autoscale should be toggle buttons, and if the frequency or the
 * voltages change the Time/div and V/div should automatically be optimized". The answer each
 * press already had is unchanged: <code>fitVoltWindow</code> for the vertical and
 * <code>timebaseForCycles</code> for the horizontal, so a toggle left on lands exactly where a
 * press would have. What this module adds is the one thing a <em>continuous</em> automatic needs
 * and a press does not: <b>when to leave it alone</b>.</p>
 *
 * <p><b>Why that matters more than the fit does.</b> Both settings are rungs of a ladder, and
 * re-deriving the wanted rung on every record is enough to make a signal sitting near a boundary
 * flip between two of them for as long as it is left — which on the vertical redraws the graticule
 * a few times a second, and on the horizontal <em>re-tunes the capture</em>, so the record being
 * measured changes under the measurement that is choosing it. So each knob is left where it is
 * while the picture is inside a band, and moved to the fit when it is not; and both bands are
 * chosen so that one move always lands well inside, which is what makes hunting impossible rather
 * than unlikely. It is <code>Qa40xAutoRanger</code>'s step-down hysteresis one control along, and
 * for the same reason.</p>
 *
 * <p>Here rather than in either panel for this project's usual reason — nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in — and because the two
 * halves are one rule written twice: the vertical's band is read off the volts and the
 * horizontal's off the period, but what each says is "this is still a good picture".</p>
 */

import { MAX_FILL_FRACTION, VERTICAL_DIVISIONS, type VoltFit } from './voltAxis'

/**
 * How little of the graticule a trace may fill before the vertical is re-fitted.
 *
 * <p><b>Below the worst a fit can leave, which is what the dead band is made of.</b> A fit puts the
 * span at no more than <code>MAX_FILL_FRACTION</code> of the height, and the ladder's widest step
 * is 2.5 — so the fill it lands on is somewhere in (0.9 / 2.5, 0.9], i.e. never under 36 %. A floor
 * of 30 % therefore cannot be tripped by the fit's own answer, and a trace that really has shrunk
 * is re-fitted on the next record.</p>
 *
 * <p>The loose end of this band is the price of a 1-2-5 ladder rather than a choice: between two
 * rungs there is nowhere better to put a trace that fills a third of the screen.</p>
 */
export const MIN_FILL_FRACTION = 0.3

/**
 * Whether the vertical pair no longer frames what is drawn.
 *
 * <p>Two ways, and the first is the one that matters: the trace reaches past the fraction of the
 * window a fit would have left it inside — which catches a signal that has grown <em>and</em> one
 * that has drifted off centre, since both end with ink against an edge. The second is the trace
 * having shrunk below the band above, where the picture is still correct and simply too small to
 * read.</p>
 */
export function voltWindowNeedsFit(fit: VoltFit, maxV: number, minV: number): boolean {
  if (!Number.isFinite(maxV) || !Number.isFinite(minV)) return false
  if (!Number.isFinite(fit.voltsPerDivision) || fit.voltsPerDivision <= 0) return true

  const centre = Number.isFinite(fit.offsetV) ? fit.offsetV : 0
  // How far either way the fit itself would have been content to reach.
  const reach = fit.voltsPerDivision * (VERTICAL_DIVISIONS / 2) * MAX_FILL_FRACTION
  const hi = Math.max(maxV, minV)
  const lo = Math.min(maxV, minV)
  if (hi > centre + reach || lo < centre - reach) return true

  const fill = (hi - lo) / (fit.voltsPerDivision * VERTICAL_DIVISIONS)
  return fill < MIN_FILL_FRACTION
}

/**
 * How few and how many cycles of the measured signal the record may hold before the timebase is
 * re-fitted. <code>AUTOSET_PERIODS</code> is what a fit aims at, between the two.
 *
 * <p>Wide for the vertical's reason and then some. The timebase ladder snaps to the <em>nearest</em>
 * rung in the logarithm, so the record a fit lands on is within a factor of about 1.6 either way of
 * five periods — between 3.2 and 7.9 of them. A band of 2.5 to 10 therefore contains every answer a
 * fit can give, with room on both sides, so a move cannot be undone by the record it produces.</p>
 *
 * <p>The cost is that a signal already showing three cycles, or nine, is left alone rather than
 * nudged to five. That is the right trade here: the alternative is a capture that re-tunes itself
 * every time a measured frequency wanders across a rung boundary, and a re-tune throws away the
 * record in flight.</p>
 */
export const AUTOSCALE_MIN_CYCLES = 2.5
export const AUTOSCALE_MAX_CYCLES = 10

/**
 * Whether the record no longer holds a readable number of cycles.
 *
 * <p>False with no period measured — a flat record, or a window holding less than one — which is
 * what leaves the capture alone rather than guessing at a timebase. It is the same answer Autoset
 * gives in that state, and for the same reason: there is nothing to size one from.</p>
 */
export function recordNeedsFit(recordSeconds: number, periodS: number | null): boolean {
  if (periodS === null || !Number.isFinite(periodS) || periodS <= 0) return false
  if (!Number.isFinite(recordSeconds) || recordSeconds <= 0) return true

  const cycles = recordSeconds / periodS
  return cycles < AUTOSCALE_MIN_CYCLES || cycles > AUTOSCALE_MAX_CYCLES
}

/** What the horizontal autoscale does with one record. */
export type TimebaseAction = 'hold' | 'fit' | 'widen'

/**
 * The whole of that decision, including the case the band cannot reach: <b>no frequency at all</b>.
 *
 * <p><b>Why a search is needed and a band is not enough.</b> The period comes from the record, so a
 * record shorter than one cannot measure it — and the band's own lower edge is 2.5 cycles, which
 * means a frequency that <em>drops</em> by more than about that in one step lands below a cycle and
 * the reading disappears. Measured in the app: at 600 Hz on a 10 ms record the knob follows
 * happily, and a jump to 60 Hz leaves it stuck at 1 ms/div for ever, because there is nothing left
 * to fit from. So an unmeasurable record widens the timebase <b>one rung</b>, which is what a bench
 * instrument's own autoset does; the next record either measures something, and is fitted, or
 * widens again, and the ladder's top ends it either way.</p>
 *
 * <p><b>One rung per record, never a jump</b>, because a search that is visible as a search is one
 * a reader can interrupt — and because the right answer is not known: that is what is being looked
 * for.</p>
 *
 * <p><b>A forced record is left alone whatever it measured</b>, and that is the guard that matters
 * rather than a nicety. Auto forces when nothing crossed the trigger level, which is the dead-input
 * case exactly — and a dead input is <em>not</em> silent here: it is the front end's own noise, in
 * which the crossing counter finds a frequency, a high and a different one on every record. Without
 * this the knob fitted five cycles of <em>that</em>: measured in the app with the generator
 * silenced, the timebase collapsed from 5 ms/div to the bottom of the ladder and then wandered
 * 10 → 20 → 10 → 5 µs/div, re-tuning the capture each time. A forced record is the analyzer saying
 * it found nothing to trigger on, so nothing in it is a reading of a signal. (Normal and Single
 * need no such guard: with nothing crossing the level they produce no records at all, so nothing
 * asks.)</p>
 *
 * <p>What is left is a signal that triggers and has no period — noise above the trigger level, a
 * lone pulse — which walks to the top rung and stops. That is the honest end of "keep the timebase
 * at five cycles of the measured signal" where there are no cycles, and it is one press to turn
 * off.</p>
 *
 * <p><b>A search ends in a fit, which is what <paramref name="searching"/> is for.</b> The rung the
 * search first measures something on is arbitrary — whatever the ladder happened to reach — and it
 * is usually inside the band, so without this the knob would stop there and hold. Measured in the
 * app: a tone coming back at 150 Hz was found at 2 ms/div, three cycles on screen, where the same
 * signal fitted from a press lands on 5 ms/div and five. The toggle has to land where the press
 * does, or it is not the same control left on.</p>
 */
export function timebaseAction(
  recordSeconds: number,
  periodS: number | null,
  record: { forced: boolean },
  searching = false,
): TimebaseAction {
  if (record.forced) return 'hold'
  if (periodS !== null && Number.isFinite(periodS) && periodS > 0) {
    return searching || recordNeedsFit(recordSeconds, periodS) ? 'fit' : 'hold'
  }
  return 'widen'
}
