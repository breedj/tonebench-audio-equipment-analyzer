import type { ResponsePhaseKindName } from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'

/**
 * What the Bode plot's phase curve actually holds: the trace's own phase, with the estimator's
 * removed delay put back when asked for, and nothing at all on the columns where the phase is no
 * longer a measurement.
 *
 * <p>Here rather than in `FrequencyResponseChart.vue` because two things have to agree about it —
 * the chart that draws the curve and the display panel's **Autofit**, which sizes the axis to it —
 * and because nothing in a `.vue` is testable under the node environment vitest runs in. The
 * anchoring half of the same problem is the server's (`ResponseCaptureSupport.BuildTrace`), where
 * the bandwidth reference's own bin index is to hand.</p>
 */

/**
 * How far below a trace's own reference level its phase stops being a measurement.
 *
 * <p>The estimator's unwrapper starts at bin 0 and walks every bin, so once the response is into
 * the analyzer's noise it unwraps noise and winds up without bound. Measured against the
 * simulator's Butterworth over a 96 kHz span: the phase leaves ±180° at the first stopband column
 * (5.3 kHz, −42 dB), reads +402° at −58 dB and <b>−311 068°</b> at −89 dB, so from the stopband on
 * there is no phase line anywhere on the plot — which is how "the phase is not plotted" was first
 * reported (2026-10-17).</p>
 *
 * <p>60 dB rather than a figure derived from the noise, because there is none to derive it from: a
 * chirp's coherence is 1.0 by construction at one average (and a noise-free simulation agrees with
 * itself at −600 dB), which is the same reason `complexPlane` gates on level and not on coherence.
 * It is a <i>display</i> floor and deliberately far shallower than that one's −120 dB: there the
 * question is whether a bin measured anything at all, here it is whether a reader could follow the
 * curve — and a Bode magnitude window is at most some 60 dB tall, so a column blanked here is one
 * whose magnitude is off the plot as well.</p>
 */
export const PHASE_FLOOR_BELOW_REFERENCE_DB = -60

/**
 * The level a trace's phase floor is measured down from — its bandwidth reference, or its peak on a
 * row written before that was recorded (migration 011) — or −∞ where no floor applies.
 *
 * <p><b>Only on a deconvolved run</b>: the chirp and the impulse response, which are the two that
 * report `PerTrace` and the two whose phase comes from unwrapping a continuous spectrum bin by bin.
 * A stepped sweep measures one tone at a time and removes no delay, so its phase is a coherent
 * reading at every point it took and stays physical down to the analyzer's own floor; blanking it
 * would delete a measurement rather than a wind-up.</p>
 */
export function phaseFloorDb(trace: ResponseTrace | null, phaseKind: ResponsePhaseKindName): number {
  if (!trace || phaseKind !== 'PerTrace') return Number.NEGATIVE_INFINITY
  const reference = trace.referenceDb ?? trace.peakDb
  return Number.isFinite(reference) ? reference + PHASE_FLOOR_BELOW_REFERENCE_DB : Number.NEGATIVE_INFINITY
}

/**
 * The phase axis, which is fixed: a wrapped phase cannot leave ±180°, so there is no window to set
 * and nothing for an Autofit to fit (Joost, 2026-10-18).
 *
 * <p>It occupies the <b>bottom half</b> of the plot, which is STEPS' layout and what the ask named:
 * the magnitude keeps the full height and the phase is read underneath it, so two curves that would
 * otherwise cross and be confused for each other sit in their own halves. uPlot has no notion of
 * half a plot, so the trick is the <i>scale</i>: it spans 720° where the axis shows 360, which puts
 * +180° at mid-height and −180° at the floor, and {@link PHASE_AXIS_SPLITS} is what stops the empty
 * top half drawing ticks nobody can reach.</p>
 */
export const PHASE_SCALE_RANGE: readonly [number, number] = [-180, 540]

/** The ticks the phase axis prints — the bottom half of its scale, and STEPS' own five labels. */
export const PHASE_AXIS_SPLITS: readonly number[] = [-180, -90, 0, 90, 180]

/**
 * A phase brought into <c>(−180°, 180°]</c>, the way every analyzer draws one.
 *
 * <p>The same arithmetic as `GroupDelay.WholeTurnsDeg` server-side, so the wrap on screen and the
 * turn the server anchors away are one convention: +181° reads −179°, −181° reads +179°. Note what
 * it means for a DUT with a restored delay — the phase then runs through many turns, and wrapping
 * draws them as the classic sawtooth rather than as a line off the top of the plot.</p>
 */
export function wrapPhaseDeg(deg: number): number {
  return deg - 360 * Math.ceil((deg - 180) / 360)
}

/** What `delayDegPerHz` needs to know: whether to restore, and the delay the estimator removed. */
export interface PhaseDelayRestore {
  restoreDelay?: boolean
  latencySamples?: number | null
  sampleRateHz?: number | null
}

/**
 * Degrees of phase to put back per hertz — `360·τ/fs`, and zero whenever there is nothing to
 * restore. The same arithmetic `complexPlane.toComplexCurve` applies (it subtracts
 * `derotate · f`), so the Bode phase and the Nyquist contour cannot disagree about which way a
 * restored delay goes.
 */
export function delayDegPerHz(opts: PhaseDelayRestore): number {
  if (!opts.restoreDelay) return 0
  const { latencySamples, sampleRateHz } = opts
  if (latencySamples == null || sampleRateHz == null) return 0
  if (!Number.isFinite(latencySamples) || !(sampleRateHz > 0)) return 0
  return (360 * latencySamples) / sampleRateHz
}

/**
 * Which columns of a trace carry a phase worth drawing: the ones whose magnitude is at or above
 * {@link phaseFloorDb}. Shared with the delay curves, which are the phase's own derivative — where
 * the phase is noise the delay is that noise times the bin spacing.
 */
export function phaseColumnUsable(trace: ResponseTrace, index: number, floorDb: number): boolean {
  return trace.magnitudeMaxDb[index] >= floorDb
}

/**
 * The phase series for a trace, in degrees, with the delay restored where asked and `null` — a gap,
 * never a zero — on every column below the floor, but <b>not yet wrapped</b>.
 *
 * <p>Separate from {@link phaseSeries} for the one caller that must interpolate before it wraps: a
 * stored result is drawn on the live curve's grid, and interpolating an already-wrapped phase
 * across its own ±180° step puts a column in the middle of the jump — a phase the DUT never
 * had.</p>
 */
export function phaseSeriesUnwrapped(
  trace: ResponseTrace | null,
  phaseKind: ResponsePhaseKindName,
  restore: PhaseDelayRestore,
  length: number,
): (number | null)[] {
  const out = new Array<number | null>(length).fill(null)
  if (!trace) return out

  const floorDb = phaseFloorDb(trace, phaseKind)
  const derotate = delayDegPerHz(restore)
  const count = Math.min(length, trace.phaseDeg.length)
  for (let i = 0; i < count; i++) {
    const phase = trace.phaseDeg[i]
    if (!Number.isFinite(phase) || !phaseColumnUsable(trace, i, floorDb)) continue
    out[i] = phase - derotate * trace.freqHz[i]
  }
  return out
}

/**
 * The phase series for a trace, in degrees, with `null` — a gap, never a zero — on every column
 * below the floor. `length` is the shared x grid's, so a trace shorter than it simply runs out.
 */
export function phaseSeries(
  trace: ResponseTrace | null,
  phaseKind: ResponsePhaseKindName,
  restore: PhaseDelayRestore,
  length: number,
): (number | null)[] {
  // Wrapped last, after the delay is back on: restoring it is what puts the turns there, and
  // wrapping first would then hand them straight back.
  return phaseSeriesUnwrapped(trace, phaseKind, restore, length).map((v) => (v === null ? null : wrapPhaseDeg(v)))
}
