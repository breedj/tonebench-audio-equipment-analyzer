/**
 * Where the impedance chart's phase axis sits in the plot, and which ticks it prints.
 *
 * The phase occupies the **top half**: it starts at the top of the plot and ends half way down, so
 * the |Z| curve keeps the full height underneath and two curves that would otherwise cross and be
 * mistaken for each other sit in their own halves (Joost, 2026-10-22).
 *
 * **It is the mirror of the frequency-response chart's arrangement, deliberately.** There the phase
 * is the *bottom* half (`response/phaseCurve.ts`'s `PHASE_SCALE_RANGE`), because a Bode magnitude
 * runs downhill and the phase follows it down. An impedance curve does the opposite — a driver's
 * resonance is a peak, and the interesting part of the trace is the top of the plot — so the phase
 * goes above it rather than through it.
 *
 * uPlot has no notion of half a plot, so the geometry is in the *scale*: it spans twice the window
 * the axis shows, with the window at the **upper** end. Unlike the response chart's, this window is
 * the user's (the Top and Bottom boxes), so the scale is derived from it per render and the ticks
 * have to be generated rather than listed — uPlot's own split generation would label the empty
 * lower half, which is a row of phases nothing can ever be drawn at.
 */

/**
 * The narrowest window the arithmetic will honour. A Top at or below Bottom is a window with no
 * height, which would make the doubled scale degenerate and blank the plot; one degree is small
 * enough to be the user's own business and large enough to divide by.
 */
export const MIN_PHASE_SPAN_DEG = 1

/** At most this many labels, which is what picks the step off the ladder below. */
export const MAX_PHASE_TICKS = 6

/**
 * The tick steps on offer, smallest first — the ladder the axis carried as uPlot `incrs` before the
 * splits were generated here. Degrees of phase are read in halves and quarters of a right angle, so
 * this is 5/10/15/30/45/90 rather than the decimal 1/2/5 ladder a generic axis would use.
 */
export const PHASE_AXIS_INCRS: readonly number[] = [5, 10, 15, 30, 45, 90, 180, 360]

/** The window as the chart will honour it: the user's pair, guarded, as `[bottom, top]`. */
export function phaseWindow(bottomDeg: number, topDeg: number): readonly [number, number] {
  const top = Number.isFinite(topDeg) ? topDeg : 90
  const bottom = Number.isFinite(bottomDeg) ? bottomDeg : -90
  const span = Math.max(top - bottom, MIN_PHASE_SPAN_DEG)
  return [top - span, top]
}

/**
 * The uPlot scale for that window: twice its height, with the window itself at the top.
 *
 * <p>So the phase curve is drawn between the top of the plot and its middle, and the lower half of
 * the scale is empty space nothing is plotted in. Derived from the *top* rather than from the
 * bottom, so a guarded window still puts its own span exactly in the upper half.</p>
 */
export function phaseScaleRange(bottomDeg: number, topDeg: number): [number, number] {
  const [bottom, top] = phaseWindow(bottomDeg, topDeg)
  return [bottom - (top - bottom), top]
}

/**
 * The ticks to label: inside the window and nowhere else, at a step off
 * {@link PHASE_AXIS_INCRS} that keeps them to {@link MAX_PHASE_TICKS}.
 *
 * <p>Rounded to six decimals because the step is accumulated: a window of ±90 at a 45° step walks
 * through 0 exactly, but one at 7.5° would otherwise print `22.500000000000004°`.</p>
 */
export function phaseAxisSplits(bottomDeg: number, topDeg: number): number[] {
  const [bottom, top] = phaseWindow(bottomDeg, topDeg)
  const span = top - bottom

  const step =
    PHASE_AXIS_INCRS.find((incr) => span / incr <= MAX_PHASE_TICKS - 1) ??
    PHASE_AXIS_INCRS[PHASE_AXIS_INCRS.length - 1]

  const splits: number[] = []
  const first = Math.ceil(bottom / step - 1e-9) * step
  for (let value = first; value <= top + 1e-9; value += step) {
    splits.push(Math.round(value * 1e6) / 1e6)
  }

  // A window narrower than one step of the coarsest rung would otherwise label nothing at all, and
  // an axis with no numbers on it is worse than one with two.
  return splits.length > 0 ? splits : [bottom, top]
}
