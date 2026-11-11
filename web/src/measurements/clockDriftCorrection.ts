import type { DriftResidualJson, ResponseStimulusName } from '../api/client'
import { usesChirpStart } from './responseStimulus'

/**
 * The clock-drift correction as the two chirp panels offer it.
 *
 * <p>In a module rather than in the two `.vue` panels for the usual reason — nothing in a `.vue` is
 * testable under the node environment vitest runs in — and shared rather than copied for the reason
 * `responseStimulus.ts` is shared: the frequency-response and impulse-response screens are the same
 * measurement at two ends, and a rule that drifted between them is how one screen comes to correct
 * a run the other leaves alone.</p>
 *
 * <p>Note how little is decided here. <b>Whether a bench has a correction at all is the server's
 * answer</b>, arriving as `ranges.clockDriftPpm` — it depends on which analyzer is connected, on
 * whether the drift test has ever run, on which two devices it ran between and on whether the
 * figure is big enough to be worth acting on, and none of those are things a panel can see. What is
 * left for the client is which stimulus can carry the correction, how to word it, and how to read a
 * stored decision.</p>
 */

/**
 * Whether the checkbox is shown at all: a figure applies to this bench, and the stimulus is the
 * sweep.
 *
 * <p>The stimulus half is the client's own because the panel changes it without asking the server:
 * the correction is built on the chirp's time–frequency map — every frequency arrives at a known
 * moment, so a clock ratio reads as a delay that grows across the sweep — and periodic noise has no
 * such map. Noise fails differently and visibly instead: its leakage goes incoherent, so the
 * coherence curve falls rather than the phase quietly tilting.</p>
 */
export function offersClockDriftCorrection(
  clockDriftPpm: number | null | undefined,
  stimulus: ResponseStimulusName,
): boolean {
  return (
    typeof clockDriftPpm === 'number' &&
    Number.isFinite(clockDriftPpm) &&
    usesChirpStart(stimulus)
  )
}

/**
 * What a run should send. The decision only ever reaches the wire where it could be acted on, which
 * is the rule `extractHarmonics` already follows on these two screens: a request that asks for
 * something the stimulus cannot carry is a request the server has to reason about, and the panel
 * already knows the answer.
 *
 * <p>It is not gated on the figure, only on the stimulus — the server resolves a request from a
 * bench with no figure to no correction on its own, and gating here as well would make a stale
 * `ranges` the thing that decides whether a correction happens.</p>
 */
export function sendsClockDriftCorrection(
  requested: boolean,
  stimulus: ResponseStimulusName,
): boolean {
  return requested && usesChirpStart(stimulus)
}

/**
 * A stored decision, or the default.
 *
 * <p><b>Absent reads as on</b>, which is the opposite of how most of these fields are read and is
 * worth the sentence. A `chart*` default that flipped would change what an existing measurement
 * *looks* like; this one changes what it *measures*. What makes it safe is that the correction can
 * only do anything on a bench that has deliberately run the drift test — before that there is no
 * figure, so every existing measurement is unaffected whatever this answers — and on a bench that
 * has one, the uncorrected run is wrong in a way nothing on screen reveals. A default of off would
 * mean measuring the drift and then having to tick a box per measurement to get any benefit from
 * it, which is the trap the whole feature exists to avoid.</p>
 */
export function readClockDriftCorrection(stored: boolean | null | undefined): boolean {
  return stored !== false
}

/** The checkbox's label, with the figure in it so a correction is never applied anonymously. */
export function clockDriftCorrectionLabel(clockDriftPpm: number | null | undefined): string {
  return typeof clockDriftPpm === 'number' && Number.isFinite(clockDriftPpm)
    ? `Correct for clock drift (${clockDriftPpm >= 0 ? '+' : '−'}${Math.abs(clockDriftPpm).toFixed(1)} ppm)`
    : 'Correct for clock drift'
}

/**
 * The line under it: what the correction is doing, in this record's own terms, and where the figure
 * came from.
 *
 * <p>The slip is quoted in samples and microseconds rather than in ppm, because ppm is the figure
 * on the checkbox and says nothing by itself — what a reader can act on is that the two clocks
 * separate by this much over the record being analysed, which is exactly the error being removed.
 * The record length is `fftSize / sampleRateHz`, i.e. the chirp's own period: that equality is what
 * the whole measurement rests on.</p>
 */
export function clockDriftCorrectionNotice(
  clockDriftPpm: number | null | undefined,
  sampleRateHz: number,
  fftSize: number,
  measuredUtc: string | null | undefined,
  now = new Date(),
): string | null {
  if (typeof clockDriftPpm !== 'number' || !Number.isFinite(clockDriftPpm)) return null
  if (!(sampleRateHz > 0) || !(fftSize > 0)) return null

  const recordSeconds = fftSize / sampleRateHz
  const slipSeconds = Math.abs(clockDriftPpm) * 1e-6 * recordSeconds
  const slipSamples = slipSeconds * sampleRateHz
  const when = readMeasuredDate(measuredUtc)

  const age = clockDriftAgeDays(measuredUtc, now)
  const ago =
    age === null || age < 1 ? '' : ` (${Math.round(age)} day${Math.round(age) === 1 ? '' : 's'} ago)`
  const measured = when ? ` Measured ${when}${ago}.` : ''
  return (
    `The two clocks separate by ${slipSamples.toFixed(1)} samples ` +
    `(${(slipSeconds * 1e6).toFixed(0)} µs) over one ${recordSeconds.toFixed(2)} s record, which a ` +
    `sweep reads as the DUT's own delay. This takes that ramp off the phase and the impulse.` +
    measured
  )
}

/**
 * How old a stored figure may be before the panel says so, in days.
 *
 * <p><b>Why a nudge and not a gate.</b> A crystal's frequency moves with temperature — a few ppm
 * per degree on ordinary parts, so the *difference* between two of them can move by tens of ppm
 * across a room's day. But a correction made with a figure that is wrong by Δ leaves Δ/|ε| of the
 * original error, so a figure 40 % stale still removes 60 % of the ramp: staleness degrades
 * gracefully, and the correction is only worse than nothing if the stored figure has the wrong
 * *sign*, which needs it to be more than twice out. So the right handling is to say how old the
 * figure is and let the reader judge, never to withhold the correction or refuse the run.</p>
 *
 * <p>Thirty days is chosen to be rare enough not to nag. It is not the real variable — what moves
 * the figure is the bench warming up, which happens within an hour — so the wording points at
 * warm-up rather than at the calendar, and the dialog says the same.</p>
 */
export const CLOCK_DRIFT_STALE_DAYS = 30

/** How many days ago the figure was measured, or null when that cannot be read. */
export function clockDriftAgeDays(measuredUtc: string | null | undefined, now = new Date()): number | null {
  if (!measuredUtc) return null
  const when = new Date(measuredUtc)
  if (Number.isNaN(when.getTime())) return null
  return Math.max(0, (now.getTime() - when.getTime()) / 86_400_000)
}

/**
 * The amber line under the checkbox when the figure is old, or null while it is fresh.
 *
 * <p>It names warm-up rather than the date, because that is the lever: the same bench measured cold
 * and used warm has already moved, where one measured a month ago and left running has not.</p>
 */
export function clockDriftStaleNotice(
  measuredUtc: string | null | undefined,
  now = new Date(),
): string | null {
  const age = clockDriftAgeDays(measuredUtc, now)
  if (age === null || age < CLOCK_DRIFT_STALE_DAYS) return null

  const days = Math.round(age)
  return (
    `This figure is ${days} days old. Clocks move with temperature, so run the Clock drift test ` +
    `again once the bench has warmed up — it needs the output looped back to the input, as the ` +
    `calibration does. A stale figure still removes most of the error; it is never worse than ` +
    `leaving the box clear.`
  )
}

/** The drift test's date, in the browser's own locale, or null when there is nothing usable to show. */
export function readMeasuredDate(measuredUtc: string | null | undefined): string | null {
  if (!measuredUtc) return null
  const when = new Date(measuredUtc)
  return Number.isNaN(when.getTime()) ? null : when.toLocaleDateString()
}

/**
 * A line the panel prints under the correction, and how loudly.
 *
 * <p>`ok` is a grey confirmation and `warn` is amber. Two tones rather than a boolean because the
 * useful cases are not all faults: "the stored figure is current" is worth saying, and it is the
 * only thing that can retire the calendar nag below.</p>
 */
export interface ClockDriftNotice {
  tone: 'ok' | 'warn'
  text: string
}

/**
 * How far out a drift figure has to be before saying so is worth a reader's attention, in ppm.
 *
 * <p>Measured, not chosen: a realistic analyzer path — linear-phase converter filters, a gentle
 * analog reconstruction pole, DC coupling — makes the residual read about 2.5 ppm when the stored
 * figure is exact, so anything below that is the bench's own signature rather than staleness. Five
 * leaves a margin of two on it.</p>
 */
export const CLOCK_DRIFT_ACTIONABLE_PPM = 5

/**
 * And how far out *relative to the correction being made*, because the absolute figure alone is the
 * wrong question: 8 ppm left over after correcting 104 is a figure doing 92 % of its job, and
 * telling someone to re-measure for that is a nag. A tenth is where re-measuring buys a visible
 * improvement.
 */
export const CLOCK_DRIFT_ACTIONABLE_FRACTION = 0.1

/**
 * And how much it has to cost *this* measurement, in samples of slip across one record. The same
 * residual matters more at a long record than a short one — the slip is proportional to it — so
 * this is the gate that keeps the panel quiet about a figure whose error nothing on screen can
 * show. A tenth of a sample is well under the estimator's own peak interpolation.
 */
export const CLOCK_DRIFT_ACTIONABLE_SLIP_SAMPLES = 0.1

/**
 * Above this the fitted model did not describe the measured phase at all, so its drift coefficient
 * is not a reading of anything — say nothing rather than something confident.
 *
 * <p>Generous, because the miss is a weak discriminator and not a strong one: a clean path reads
 * well under a degree, but a DUT's own phase curve can read a few degrees while being wrong about
 * the drift by more than the whole quantity. What this catches is the gross case — a reference input
 * with something unexpected on it. The real protection is that the reading is only ever taken off
 * the reference channel; see `ResponseCaptureSupport.EstimateDriftResidual`.</p>
 */
export const CLOCK_DRIFT_MAX_FIT_MISS_DEG = 30

const ppm = (value: number) => `${value >= 0 ? '+' : '\u2212'}${Math.abs(value).toFixed(1)} ppm`

/**
 * What the measurement on screen says about the stored drift figure, or null when it says nothing.
 *
 * <p><b>This is the staleness detector.</b> A crystal's frequency moves with temperature and the
 * clock-drift test cannot be re-run without re-wiring the bench into a loopback, so the only thing
 * that can notice a figure going out of date is the measurement itself: a sweep corrected by an
 * exact figure has no delay ramp left on it, and whatever ramp *is* left is the error in that
 * figure. The server reads it off the reference channel — see
 * `ResponseCaptureSupport.EstimateDriftResidual`, which is also where the reasons it sometimes
 * cannot are.</p>
 *
 * <p>Four states and only three of them speak. A correction that is doing its job earns a quiet
 * confirmation, because it is the one thing that can retire {@link clockDriftStaleNotice}'s
 * calendar nag. One that is not earns amber. An <i>uncorrected</i> run that is carrying drift earns
 * amber too — that bench has an error on screen it could simply remove, and until now nothing could
 * tell it so. And a run with no drift worth the name says nothing at all: that is every ordinary
 * single-clock analyzer, and a line on every measurement confirming the absence of a fault nobody
 * suspected is the definition of noise.</p>
 */
export function clockDriftResidualNotice(
  residual: DriftResidualJson | null | undefined,
): ClockDriftNotice | null {
  if (!residual) return null
  if (!Number.isFinite(residual.residualPpm) || !Number.isFinite(residual.appliedPpm)) return null
  if (!(residual.recordSeconds > 0) || !(residual.slipSamples >= 0)) return null
  if (Number.isFinite(residual.fitRmsDeg) && residual.fitRmsDeg > CLOCK_DRIFT_MAX_FIT_MISS_DEG) return null

  const { recordSeconds, slipSamples } = residual
  const applied = residual.appliedPpm
  const left = Math.abs(residual.residualPpm)

  const actionable =
    slipSamples >= CLOCK_DRIFT_ACTIONABLE_SLIP_SAMPLES &&
    left >= Math.max(CLOCK_DRIFT_ACTIONABLE_PPM, CLOCK_DRIFT_ACTIONABLE_FRACTION * Math.abs(applied))

  const cost =
    `${slipSamples.toFixed(1)} samples over one ${recordSeconds.toFixed(2)} s record`

  if (applied === 0) {
    if (!actionable) return null

    return {
      tone: 'warn',
      text:
        `This run is carrying ${ppm(residual.residualPpm)} of clock drift — ${cost} — read off the ` +
        `reference channel. Run the Clock drift test to measure it, and correcting for it takes ` +
        `that ramp off the phase and the impulse.`,
    }
  }

  if (!actionable) {
    return {
      tone: 'ok',
      text:
        `The stored figure is still current: this run has ${ppm(residual.residualPpm)} left on it ` +
        `after correcting ${ppm(applied)}, measured from its own reference channel.`,
    }
  }

  return {
    tone: 'warn',
    text:
      `This run still has ${ppm(residual.residualPpm)} on it after correcting ${ppm(applied)}, so ` +
      `the stored figure looks about ${left.toFixed(0)} ppm out — ${cost} left uncorrected. Run the ` +
      `Clock drift test again once the bench has warmed up; it needs the output looped back to the ` +
      `input. Leave the box ticked meanwhile: a stale figure still removes most of the error.`,
  }
}

/**
 * The one line the panel prints: what the measurement says if it said anything, and the calendar
 * otherwise.
 *
 * <p>In that order, because a measurement beats a date. A figure a month old that a run has just
 * confirmed to within a ppm does not need re-measuring, and telling its owner to go and do it would
 * teach them to ignore the line.</p>
 */
export function clockDriftFreshnessNotice(
  residual: DriftResidualJson | null | undefined,
  measuredUtc: string | null | undefined,
  now = new Date(),
): ClockDriftNotice | null {
  const measured = clockDriftResidualNotice(residual)
  if (measured) return measured

  const stale = clockDriftStaleNotice(measuredUtc, now)
  return stale ? { tone: 'warn', text: stale } : null
}
