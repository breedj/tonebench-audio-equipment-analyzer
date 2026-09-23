import type { ResponseTrace } from '../composables/useFrequencyResponse'

/**
 * One measured frequency as a point in the complex plane.
 *
 * `magDb` and `phaseDeg` are kept alongside `re`/`im` because every readout wants them back and
 * recovering them from the cartesian pair loses the unwrapped turn count — `Math.atan2` can only
 * ever answer in (−180°, 180°], and the whole point of the unwrapper is that a DUT which
 * accumulates several turns reads as a continuous curve.
 */
export interface ComplexPoint {
  freqHz: number
  re: number
  im: number
  magDb: number
  phaseDeg: number
}

export interface ComplexCurveOptions {
  /** The plotted frequency window — the display panel's From/To. */
  fromHz: number
  toHz: number
  /**
   * The chirp's removed bulk delay, to be put back. `FrequencyResponseEstimator` de-rotates every
   * record by an estimated latency before accumulating (its own remarks say why), so what the trace
   * carries is `φ_true + 360·f·τ/fs`. That is a *frequency-dependent* rotation and it is exactly
   * what a phase margin is made of, so it has to be undone rather than warned about.
   *
   * Null on a stepped run — `arg(L/R)` comes from two channels of one capture, so the record's
   * arbitrary start offset is common-mode and cancels, and there is nothing to restore.
   */
  latencySamples?: number | null
  sampleRateHz?: number | null
  /**
   * Drop points below this coherence. Where a chirp's coherence is low the estimate whips around
   * the origin, which manufactures phantom crossings of both 0 dB and −180°. Stepped coherence is
   * a constant 1.0 by construction, so this only ever bites on the chirp.
   */
  minCoherence?: number
  /**
   * Add 180° — the summing-junction inversion a loop-gain injection may or may not include.
   *
   * The measurement cannot know: `arg(L/R)` depends on where the injection sits and which way round
   * the probes went, so `PM = 180° + ∠L` is right or 180° out depending on wiring the analyzer
   * never sees. Applied here rather than in the margin maths so the contour and the numbers can
   * never disagree about which convention is in force.
   */
  invertSign?: boolean
}

/**
 * Below this a column measured nothing, and its phase is not a measurement either.
 *
 * `FrequencyResponseEstimator.ToDb` floors an exactly-zero bin at 1e-30, i.e. −600 dB, and a bin
 * *is* exactly zero where a noise-free simulation's DUT is below the ADC's quantisation: the Bessel
 * measured over a 96 kHz span reads −600 dB above 50 kHz, with coherence 1.0 (no noise means no
 * disagreement between averages) and a phase that is whatever the unwrapper made of atan2(0, 0)
 * and the removed-delay ramp — measured, −332 878° at 71 kHz. Unfiltered, that put the Nichols
 * phase axis at −1e6°, drew three thousand −180° markers as a solid red band, and handed the margin
 * search phantom crossings. Coherence cannot catch it, so this does: nothing physical is 300 dB
 * down from anything, and a real device's floor is a hundred dB above this line.
 */
export const MIN_MEASURED_DB = -300

/**
 * And below this far under the trace's own peak, the phase was never measured either — it was
 * *held*.
 *
 * `FrequencyResponseEstimator.PhaseFloorRelative` is 1e-6, i.e. 120 dB: under it the server stops
 * unwrapping and hands out the last unwrapped value for every bin, because what such a bin carries
 * is the arithmetic's phase rather than the DUT's. The magnitude there is still a number worth
 * plotting on a Bode chart — it is the floor, and the floor is a measurement — but a point in the
 * complex plane needs *both* parts, and half of this one is a placeholder.
 *
 * Leaving them in is what stretches a Nichols plot. Measured on the simulated Butterworth over a
 * 96 kHz span with the delay restored: the response reaches −121 dB at 29.9 kHz and the phase is
 * held from there, while the client's delay restoration keeps adding 360·f·τ to that frozen value —
 * so the held tail ramped on to −6054° at 83 kHz, took the phase axis with it, and filled the 0 dB
 * line with nineteen critical-phase markers. Dropped, the curve ends where the measurement does.
 *
 * Relative rather than absolute because that is how the server's floor is defined; the client
 * reproduces the decision from the same information (a magnitude against the trace's peak) rather
 * than needing a new column on the wire to say which bins were held.
 */
export const MIN_MEASURED_RELATIVE_DB = -120

/** dB → linear voltage ratio. Named because `/20` versus `/10` is the single easiest thing to get wrong here. */
export function dbToRatio(db: number): number {
  return Math.pow(10, db / 20)
}

const DEG_TO_RAD = Math.PI / 180

/**
 * The magnitude that pairs with a decimated column's phase.
 *
 * **The mid of the min/max column, not the max.** For a stepped run the two are equal
 * (`SteppedResponseRunner.BuildTrace` clones one array into both), so this only affects the chirp —
 * and there it matters. `FrequencyResponseRunner` fills `magMax[c]` with the column's *maximum*
 * while `phaseDeg[c]` is the bin at the column's *centre*: pairing a max with a centre-bin phase
 * inflates |G| exactly where the trace is ripply, which on a loop gain is where it approaches unity.
 * The bias then lands straight on the gain crossover and the gain margin.
 *
 * This is the same aggregation bias the decimation rule in CLAUDE.md describes for the noise floor,
 * one layer up: max-over-N-bins is not the value at any particular bin.
 */
export function columnMagnitudeDb(trace: ResponseTrace, i: number): number {
  const max = trace.magnitudeMaxDb[i]
  const min = trace.magnitudeMinDb[i]
  if (!Number.isFinite(min)) return max
  if (!Number.isFinite(max)) return min
  return (min + max) / 2
}

/**
 * A trace's gain curve as points in the complex plane, `G = 10^(dB/20)·e^{jφ}`.
 *
 * Filtering to the window happens *first* and is not cosmetic: a chirp gain trace spans 2 Hz–96 kHz
 * including bins where |G| is nothing but noise, and unfiltered those draw as spaghetti across the
 * whole plane and put phantom crossings into every margin.
 */
export function toComplexCurve(trace: ResponseTrace, opts: ComplexCurveOptions): ComplexPoint[] {
  const { fromHz, toHz, minCoherence } = opts
  const derotate = delayDegPerHz(opts)
  const signDeg = opts.invertSign ? 180 : 0
  const out: ComplexPoint[] = []

  // The peak is taken over the whole trace, not over the plotted window, because the server's own
  // phase floor is relative to the whole trace — see MIN_MEASURED_RELATIVE_DB. A window sitting
  // entirely in a DUT's stopband would otherwise raise its own floor and keep the held bins.
  let peakDb = Number.NEGATIVE_INFINITY
  for (let i = 0; i < trace.freqHz.length; i++) {
    const columnDb = columnMagnitudeDb(trace, i)
    if (Number.isFinite(columnDb) && columnDb > peakDb) peakDb = columnDb
  }
  const floorDb = Number.isFinite(peakDb)
    ? Math.max(MIN_MEASURED_DB, peakDb + MIN_MEASURED_RELATIVE_DB)
    : MIN_MEASURED_DB

  for (let i = 0; i < trace.freqHz.length; i++) {
    const freqHz = trace.freqHz[i]
    if (!Number.isFinite(freqHz) || freqHz < fromHz || freqHz > toHz) continue

    if (minCoherence != null) {
      const coherence = trace.coherence[i]
      // A missing coherence array is not a failed test — the stepped path writes a constant 1.0 and
      // an overlay from an older build may have none at all. Only a value that is present and low
      // is a reason to drop the point.
      if (Number.isFinite(coherence) && coherence < minCoherence) continue
    }

    const magDb = columnMagnitudeDb(trace, i)
    const rawPhaseDeg = trace.phaseDeg[i]
    if (!Number.isFinite(magDb) || !Number.isFinite(rawPhaseDeg)) continue
    if (magDb < floorDb) continue

    const phaseDeg = rawPhaseDeg - derotate * freqHz + signDeg
    const ratio = dbToRatio(magDb)
    const rad = phaseDeg * DEG_TO_RAD

    out.push({
      freqHz,
      re: ratio * Math.cos(rad),
      im: ratio * Math.sin(rad),
      magDb,
      phaseDeg,
    })
  }

  return out
}

/**
 * Degrees of added phase per Hz from the estimator's removed delay — `360·τ/fs`, zero when there is
 * no delay to restore. Split out so the per-point loop does one multiply and the arithmetic can be
 * asserted on its own.
 */
function delayDegPerHz(opts: ComplexCurveOptions): number {
  const { latencySamples, sampleRateHz } = opts
  if (latencySamples == null || sampleRateHz == null) return 0
  if (!Number.isFinite(latencySamples) || !(sampleRateHz > 0)) return 0
  return (360 * latencySamples) / sampleRateHz
}
