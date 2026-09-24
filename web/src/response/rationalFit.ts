import type { ComplexPoint } from './complexPlane'
import { cAbs, cDiv, cMul, cx, polyEval, polyRoots, trimLeading, type Cx } from './poly'

/**
 * Fits a rational transfer function H(s) = N(s)/D(s) to a measured complex response, and reports its
 * poles and zeros.
 *
 * The method is Levy's linear least squares with Sanathanan–Koerner reweighting: minimise
 * |N(jω) − H·D(jω)|² over the coefficients, then re-solve weighting each point by 1/|D_prev(jω)|²,
 * which converges on the error that actually matters, |H − N/D|. Chosen over vector fitting because
 * filters are order 2–8 and, with the frequency normalisation below, SK is well conditioned there
 * and needs no eigensolver.
 *
 * **Frequency is normalised by the band's geometric mean, and that is not a refinement.** A sweep
 * spanning 20 Hz to 20 kHz is three decades, so an un-normalised s = jω raises the top coefficient
 * to (2π·20000)^n — around 1e26 at n = 5, against 1 at the bottom. The normal equations are then
 * numerically empty and the "poles" are round-off. Normalised, the same fit is conditioned like a
 * one-decade problem.
 *
 * The model is continuous-time (s-domain), which is what an analog filter is. Two things it cannot
 * represent, and which therefore come back as spurious pole/zero pairs rather than as an error: a
 * pure delay (e^{-sT} is not rational — for a chirp run leave the delay *removed*), and a
 * non-minimum-phase response measured with the wrong phase sign.
 */

export interface RationalModel {
  /** Numerator coefficients, `num[k]` multiplying `s^k`, in normalised s. */
  num: number[]
  /** Denominator coefficients, `den[k]` multiplying `s^k`, in normalised s. `den[0]` is 1. */
  den: number[]
  /** Radians/second that normalised s = 1 corresponds to. */
  omegaScale: number
}

export interface Singularity {
  /** Natural frequency, |s|/2π. */
  freqHz: number
  /** Q = |s| / (2|Re s|) for a complex pair; null for a real root, which has no Q. */
  q: number | null
  /** Damping ratio, −Re s / |s|. */
  zeta: number
  /** True for a conjugate pair (reported once), false for a real root. */
  isPair: boolean
  /** Right-half-plane: unstable for a pole, non-minimum-phase for a zero. */
  isRhp: boolean
}

/** The frequencies a fit speaks for: the plotted curve's first and last point. */
export interface MeasuredBand {
  lowHz: number
  highHz: number
}

export interface Cancellation {
  poleHz: number
  zeroHz: number
  /** Peak-to-peak dB the pair contributes across the measured band. */
  swingDb: number
}

export interface FitResult {
  model: RationalModel
  poles: Singularity[]
  zeros: Singularity[]
  /** |H| at DC in dB, from the fitted model. */
  dcGainDb: number
  /** RMS of |fitted − measured| in dB, over the fitted points. */
  rmsErrorDb: number
  maxErrorDb: number
  numeratorOrder: number
  denominatorOrder: number
  /**
   * Pairs of a pole and a zero that together do nothing the measurement can see — the fit spending
   * order on noise, whose honest answer is a lower order rather than two reported components.
   */
  cancellations: Cancellation[]
}

/**
 * A pole/zero pair that moves the response by less than this across the measured band cancels.
 *
 * **Judged by what the pair does, not by how close its two frequencies are.** The rule was a
 * relative frequency distance (5 %) until 2026-09-24, and proximity in frequency is not
 * cancellation: a notch *is* a zero pair sitting exactly on a pole pair, differing only in damping,
 * so the 800 Hz Q = 4 notch in this module's own tests — fitted at its true order 2/2, nothing
 * spurious anywhere — was reported as a cancellation and the panel told the user to lower the
 * order on it. Measured here with the pair evaluated instead: that notch swings **10.98 dB**, while
 * the genuine article, a second-order filter fitted at order 4/4, swings **0.00 dB**.
 *
 * The threshold is 1 dB because the two nearest honest cases measured are a gentle first-order
 * shelf (pole 300 Hz, zero 360 Hz — 17 % apart in frequency, and **1.58 dB** of real response) and
 * the 91 kHz doublet an order-3 fit of the EL84 amplifier invents at the very edge of its band
 * (**1.12 dB**). Those two are a third of a dB apart, so no threshold separates them; 1 dB keeps
 * the shelf, which is a component someone built, and lets the doublet through, which shows up in
 * the out-of-band flag instead.
 */
export const CANCELLATION_SWING_DB = 1

/**
 * At most this many frequencies are used to evaluate a pair's swing.
 *
 * The swing is smooth in log f, so a stride over the measured points answers the same question as
 * all 1600 of them: `suggestOrder` fits eight orders, each with up to 8 × 8 pole/zero combinations,
 * and the full grid would put a quarter of a million logarithms behind every automatic order.
 */
const SWING_SAMPLES = 256

/** Solves `A x = b` for a small dense system by Gaussian elimination with partial pivoting. */
function solve(a: number[][], b: number[]): number[] | null {
  const n = b.length
  const m = a.map((row, i) => [...row, b[i]])

  for (let col = 0; col < n; col++) {
    let pivot = col
    for (let row = col + 1; row < n; row++) {
      if (Math.abs(m[row][col]) > Math.abs(m[pivot][col])) pivot = row
    }
    if (Math.abs(m[pivot][col]) < 1e-300) return null
    ;[m[col], m[pivot]] = [m[pivot], m[col]]

    for (let row = 0; row < n; row++) {
      if (row === col) continue
      const factor = m[row][col] / m[col][col]
      if (factor === 0) continue
      for (let k = col; k <= n; k++) m[row][k] -= factor * m[col][k]
    }
  }

  // Full Gauss-Jordan above, so each row is already diagonal: x_i is simply rhs / pivot.
  return m.map((row, i) => row[n] / row[i])
}

/** Powers of `s` up to `order`, cached per point. */
function powers(s: Cx, order: number): Cx[] {
  const out: Cx[] = [cx(1, 0)]
  for (let k = 1; k <= order; k++) out.push(cMul(out[k - 1], s))
  return out
}

/**
 * One weighted Levy solve.
 *
 * Unknowns are `[b_0..b_m, a_1..a_n]`; `a_0` is pinned to 1, which is the normalisation that stops
 * the trivial all-zero solution. Each measured point contributes two real equations (real and
 * imaginary parts), so the coefficients come out real and the roots in conjugate pairs — which is
 * what makes them physically meaningful rather than an arbitrary complex factorisation.
 */
function levySolve(
  points: ComplexPoint[],
  omegaScale: number,
  numOrder: number,
  denOrder: number,
  weights: number[],
): RationalModel | null {
  const unknowns = numOrder + 1 + denOrder
  const ata: number[][] = Array.from({ length: unknowns }, () => new Array(unknowns).fill(0))
  const atb: number[] = new Array(unknowns).fill(0)

  points.forEach((point, index) => {
    const s = cx(0, (2 * Math.PI * point.freqHz) / omegaScale)
    const h = cx(point.re, point.im)
    const sp = powers(s, Math.max(numOrder, denOrder))
    const w = weights[index]
    if (!(w > 0) || !Number.isFinite(w)) return

    // row · x = h, with row split into its real and imaginary parts below.
    const row: Cx[] = []
    for (let k = 0; k <= numOrder; k++) row.push(sp[k])
    for (let k = 1; k <= denOrder; k++) row.push(cMul(cx(-h.re, -h.im), sp[k]))

    for (const part of ['re', 'im'] as const) {
      const r = row.map((c) => c[part])
      const y = h[part]
      for (let i = 0; i < unknowns; i++) {
        if (r[i] === 0) continue
        for (let j = 0; j < unknowns; j++) ata[i][j] += w * r[i] * r[j]
        atb[i] += w * r[i] * y
      }
    }
  })

  const x = solve(ata, atb)
  if (!x || x.some((v) => !Number.isFinite(v))) return null

  return {
    num: x.slice(0, numOrder + 1),
    den: [1, ...x.slice(numOrder + 1)],
    omegaScale,
  }
}

/** H(s) of a fitted model at one frequency. */
export function evaluateModel(model: RationalModel, freqHz: number): Cx {
  const s = cx(0, (2 * Math.PI * freqHz) / model.omegaScale)
  return cDiv(polyEval(model.num, s), polyEval(model.den, s))
}

function toSingularities(coeffs: number[], omegaScale: number): Singularity[] {
  const roots = polyRoots(coeffs)
  const out: Singularity[] = []
  const used = new Array(roots.length).fill(false)

  roots.forEach((root, i) => {
    if (used[i]) return
    used[i] = true

    // Conjugate pairs are reported once, as a resonance with an f and a Q — which is how a filter is
    // specified and read. Matched by value rather than assumed adjacent, since the root finder
    // returns them in no particular order.
    let pairIndex = -1
    if (Math.abs(root.im) > 1e-9 * Math.max(1, cAbs(root))) {
      let best = Infinity
      roots.forEach((other, j) => {
        if (used[j]) return
        const distance = Math.hypot(other.re - root.re, other.im + root.im)
        if (distance < best) {
          best = distance
          pairIndex = j
        }
      })
      if (pairIndex >= 0 && best < 1e-6 * Math.max(1, cAbs(root))) used[pairIndex] = true
      else pairIndex = -1
    }

    const magnitude = cAbs(root) * omegaScale
    const sigma = root.re * omegaScale
    const isPair = pairIndex >= 0

    out.push({
      freqHz: magnitude / (2 * Math.PI),
      q: isPair && Math.abs(sigma) > 0 ? magnitude / (2 * Math.abs(sigma)) : null,
      zeta: magnitude > 0 ? -sigma / magnitude : 0,
      isPair,
      isRhp: root.re > 0,
    })
  })

  return out.sort((a, b) => a.freqHz - b.freqHz)
}

/**
 * The s-plane roots a singularity stands for, in Hz (σ/2π along the real axis, ω/2π along the
 * imaginary one) — a conjugate pair contributes both of its roots, a real root only itself.
 *
 * `Singularity` reports a pair once as (f, ζ): the pair is |s| = 2πf at −ζ|s| ± j|s|√(1−ζ²), and a
 * real root is at −ζ|s| with ζ = ±1, its sign carrying which half-plane it is in. Shared with
 * `poleZeroMap.rootPoints`, which draws exactly these points, so the arithmetic exists once.
 */
export function singularityRoots(s: Singularity): { re: number; im: number }[] {
  const sigmaHz = -s.zeta * s.freqHz
  if (!s.isPair) return [{ re: sigmaHz, im: 0 }]

  const omegaHz = Math.sqrt(Math.max(0, s.freqHz * s.freqHz - sigmaHz * sigmaHz))
  return [
    { re: sigmaHz, im: omegaHz },
    { re: sigmaHz, im: -omegaHz },
  ]
}

/** A root's natural frequency lies inside the measured band. No band to judge against counts as in. */
export function singularityInBand(s: Singularity, band: MeasuredBand | null): boolean {
  if (!band) return true
  return s.freqHz >= band.lowHz && s.freqHz <= band.highHz
}

/**
 * Peak-to-peak dB the pole/zero pair contributes over `freqHz`: `Σ|jω − z| / Σ|jω − p|` in dB, at
 * its largest minus at its smallest. A flat contribution is a pair the measurement cannot see, and
 * the *swing* rather than the level is what says so — a pair that is flat but not unity is only a
 * gain, which the numerator's leading coefficient absorbs.
 */
function pairSwingDb(pole: Singularity, zero: Singularity, freqHz: number[]): number {
  const p = singularityRoots(pole)
  const z = singularityRoots(zero)
  let lowest = Infinity
  let highest = -Infinity

  for (const f of freqHz) {
    let db = 0
    for (const r of z) db += 20 * Math.log10(Math.hypot(r.re, f - r.im))
    for (const r of p) db -= 20 * Math.log10(Math.hypot(r.re, f - r.im))
    if (!Number.isFinite(db)) continue
    if (db < lowest) lowest = db
    if (db > highest) highest = db
  }

  return highest >= lowest ? highest - lowest : Infinity
}

/**
 * The pole/zero pairs that cancel, each zero spent at most once.
 *
 * Greedy on the smallest swing rather than every combination: with three poles and three zeros
 * there are nine pairings and only three of them can be real, so reporting all of the close ones
 * would count the same wasted order several times over.
 */
function findCancellations(poles: Singularity[], zeros: Singularity[], freqHz: number[]): Cancellation[] {
  const spent = new Array(zeros.length).fill(false)
  const out: Cancellation[] = []

  for (const pole of poles) {
    let bestIndex = -1
    let bestSwing = Infinity
    zeros.forEach((zero, index) => {
      if (spent[index]) return
      const swing = pairSwingDb(pole, zero, freqHz)
      if (swing < bestSwing) {
        bestSwing = swing
        bestIndex = index
      }
    })

    if (bestIndex >= 0 && bestSwing < CANCELLATION_SWING_DB) {
      spent[bestIndex] = true
      out.push({ poleHz: pole.freqHz, zeroHz: zeros[bestIndex].freqHz, swingDb: bestSwing })
    }
  }

  return out
}

/**
 * The root that makes a fit's DC gain an extrapolation rather than a reading, or null when nothing
 * does.
 *
 * `H(0)` is always evaluated at a frequency no sweep contains, but it is only *extrapolated* when
 * the model bends on the way down: with every root above the band, |H| is flat from the lowest one
 * to DC and H(0) is the passband gain the measurement already reported. A root below the band is
 * the opposite case, and the figure then describes a corner the sweep never reached.
 *
 * Measured on a push-pull EL84 amplifier (chirp 13 Hz–91 kHz, passband gain 26.58 dB): an order-6
 * fit placed a pole at 7.65 Hz against a zero at 3.00 Hz — the amplifier's coupling, half seen —
 * and reported **DC gain 18.43 dB**, eight decibels below the measurement, next to a fit error of
 * 0.008 dB rms. Forced to 2 poles and 1 zero, both above the band, the same curve gives 26.55 dB.
 * Neither fit is wrong; only one of them is quoting a number the sweep constrains.
 */
export function dcGainExtrapolation(fit: FitResult, band: MeasuredBand | null): { rootHz: number; kind: 'pole' | 'zero' } | null {
  if (!band) return null

  const roots = [
    ...fit.poles.map((p) => ({ rootHz: p.freqHz, kind: 'pole' as const })),
    ...fit.zeros.map((z) => ({ rootHz: z.freqHz, kind: 'zero' as const })),
  ].filter((r) => Number.isFinite(r.rootHz) && r.rootHz < band.lowHz)

  if (roots.length === 0) return null
  return roots.reduce((lowest, r) => (r.rootHz < lowest.rootHz ? r : lowest))
}

/**
 * Fit a model of the given order to a measured curve.
 *
 * `points` are the curve the chart is already drawing — windowed, sign-corrected and
 * coherence-filtered — so the table always describes exactly what is on screen.
 */
export function fitRational(
  points: ComplexPoint[],
  numeratorOrder: number,
  denominatorOrder: number,
  iterations = 5,
): FitResult | null {
  const usable = points.filter((p) => p.freqHz > 0 && Number.isFinite(p.re) && Number.isFinite(p.im))
  if (usable.length < numeratorOrder + denominatorOrder + 2) return null

  // The geometric mean of the band, so normalised s spans equally either side of 1.
  const lowHz = usable[0].freqHz
  const highHz = usable[usable.length - 1].freqHz
  const omegaScale = 2 * Math.PI * Math.sqrt(lowHz * highHz)

  // First pass is unweighted Levy, which is biased toward wherever |D| is large; the SK reweighting
  // below is what removes that bias, and without it a low-pass fit is dominated by its stopband.
  let weights = new Array(usable.length).fill(1)
  let model: RationalModel | null = null

  for (let iteration = 0; iteration < iterations; iteration++) {
    const next = levySolve(usable, omegaScale, numeratorOrder, denominatorOrder, weights)
    if (!next) break
    model = next

    weights = usable.map((point) => {
      const s = cx(0, (2 * Math.PI * point.freqHz) / omegaScale)
      const d = cAbs(polyEval(next.den, s))
      return d > 0 ? 1 / (d * d) : 0
    })
  }

  if (!model) return null

  let sumSquares = 0
  let maxErrorDb = 0
  let counted = 0
  for (const point of usable) {
    const fitted = evaluateModel(model, point.freqHz)
    const fittedAbs = cAbs(fitted)
    const measuredAbs = Math.hypot(point.re, point.im)
    if (!(fittedAbs > 0) || !(measuredAbs > 0)) continue
    const errorDb = 20 * Math.log10(fittedAbs / measuredAbs)
    sumSquares += errorDb * errorDb
    maxErrorDb = Math.max(maxErrorDb, Math.abs(errorDb))
    counted++
  }

  const poles = toSingularities(model.den, omegaScale)
  const zeros = toSingularities(trimLeading(model.num), omegaScale)

  const stride = Math.max(1, Math.ceil(usable.length / SWING_SAMPLES))
  const swingGrid = usable.filter((_, i) => i % stride === 0).map((p) => p.freqHz)
  const cancellations = findCancellations(poles, zeros, swingGrid)

  const dc = cAbs(cDiv(cx(model.num[0]), cx(model.den[0])))

  return {
    model,
    poles,
    zeros,
    dcGainDb: dc > 0 ? 20 * Math.log10(dc) : -Infinity,
    rmsErrorDb: counted > 0 ? Math.sqrt(sumSquares / counted) : Infinity,
    maxErrorDb,
    numeratorOrder,
    denominatorOrder,
    cancellations,
  }
}

/** A step must improve the error by at least this factor to count as a cliff rather than drift. */
export const KNEE_SIGNIFICANT_FACTOR = 1.5

/**
 * A cliff only counts when the order below it still *missed* the measurement by this much.
 *
 * Without it the rule reads a ratio and nothing else, and a ratio cannot tell structure from noise:
 * measured on a push-pull EL84 amplifier over 13 Hz–91 kHz, whose response is flat to 0.11 dB from
 * 100 Hz to 20 kHz, orders 1 through 5 all fit within **0.052–0.060 dB rms** — the chirp's own
 * ripple — and order 6 reaches 0.0081. That 6.4x step is the largest in the curve and the rule took
 * it, spending four extra roots (two of them right-half-plane pairs, on an unconditionally stable
 * amplifier) to buy five hundredths of a decibel, and moving the reported DC gain 8 dB in the
 * process. With the floor, no step on that curve counts and the fallback returns the lowest order
 * tried, which describes a featureless response as honestly as order 5 did.
 *
 * 0.1 dB is above what this instrument's own chirp repeatability leaves on a flat DUT and below
 * anything a person would call a feature; it is also well under `POOR_FIT_DB`, so an order the
 * floor stops at is still a fit the panel calls good. It gates the step's *starting* point, not its
 * end, so a synthetic filter — large errors below its true order, round-off at and above it — keeps
 * selecting exactly its own order.
 */
export const KNEE_NEGLIGIBLE_ERROR_DB = 0.1

/** Discounting cancelling poles may not cost more than this factor of fit accuracy. */
export const KNEE_MAX_DISCOUNT_COST = 10

export interface OrderFit {
  order: number
  rmsErrorDb: number
  cancellations: number
}

/**
 * The order to fit at. Four steps, and each one exists because the previous rule failed on measured
 * data — steps 1 to 3 on the GM70 driver stage (5 Hz–80 kHz, −25 dBV, 65 points), step 0 on the
 * EL84 amplifier described at `KNEE_NEGLIGIBLE_ERROR_DB`.
 *
 * 0. **A step out of an error that was already negligible is not a cliff.** See
 *    `KNEE_NEGLIGIBLE_ERROR_DB`: a featureless response is fitted to the measurement's own ripple at
 *    every order, and the largest ratio in such a curve is a ratio between two kinds of noise.
 * 1. **Walk to the last cliff.** Take the highest order whose step improved the error by
 *    `KNEE_SIGNIFICANT_FACTOR`. Not the *biggest* cliff: that curve has two comparable ones — 5.1x at
 *    order 2 and 4.6x at order 7 — with a plateau between, and taking the biggest returned order 2,
 *    whose 1.33 dB peak error is visibly wrong. A real system has structure at several scales and
 *    every cliff on the way up is a pole the model needs.
 * 2. **Discount the poles that cancel.** Order 7 fits that data best and two or three of its
 *    pole/zero pairs sit on top of each other — by this module's own definition, order spent on
 *    noise. (The original rule, "lowest order within 15% of the best error", returned 7 and reported
 *    those as components; when the error keeps improving, nothing but the top is ever within 15% of
 *    the top.)
 * 3. **Walk back up if the discount cost too much.** This is the step that makes the answer stable.
 *    Two identical sweeps of the same amplifier found 2 and 3 cancelling pairs at order 7, so plain
 *    subtraction returned 5 and 4 — a rule whose output moves with the noise realisation is not a
 *    rule. Requiring the discounted order to stay within `KNEE_MAX_DISCOUNT_COST` of the best error
 *    achieved returns **5 on both**.
 *
 * Falls back to the lowest order tried when no step qualifies: a first-order system is fitted
 * exactly at order 1, and every step above it is then a ratio of round-off to round-off, while a
 * flat amplifier is fitted about equally well at every order and the fallback says so. Under-fitting
 * is the safe failure — it shows in the error readout and in the fitted curve visibly leaving the
 * measurement, whereas over-fitting looks like success.
 */
export function kneeOrder(fits: OrderFit[]): number {
  if (fits.length === 0) return 2
  if (fits.length === 1) return fits[0].order

  // Errors are floored before dividing: a synthetic fixture fits to round-off, and 0/0 would make
  // every ratio NaN and the rule silently return the first order.
  const floor = 1e-12
  const err = (fit: OrderFit) => Math.max(fit.rmsErrorDb, floor)

  const ratios = fits.slice(1).map((fit, i) => ({ fit, ratio: err(fits[i]) / err(fit), from: err(fits[i]) }))
  const significant = ratios.filter((r) => r.ratio >= KNEE_SIGNIFICANT_FACTOR && r.from > KNEE_NEGLIGIBLE_ERROR_DB)
  if (significant.length === 0) return fits[0].order

  const cliff = significant[significant.length - 1].fit
  const lowest = fits[0].order

  const discounted = Math.min(cliff.order, Math.max(lowest, cliff.order - Math.max(0, cliff.cancellations)))

  // Cheapest acceptable error: the best anyone achieved, times the accuracy the discount may cost.
  const best = Math.min(...fits.map(err))
  const limit = best * KNEE_MAX_DISCOUNT_COST

  for (let order = discounted; order <= cliff.order; order++) {
    const fit = fits.find((f) => f.order === order)
    if (fit && err(fit) <= limit) return order
  }

  return cliff.order
}

/**
 * The order to fit at, chosen by the knee of the error curve.
 *
 * Order selection is the whole game for a filter: too low and real features are missed, too high and
 * the fit spends its extra poles on noise and reports them as if they were components.
 */
export function suggestOrder(points: ComplexPoint[], maxOrder = 8): { order: number; fits: OrderFit[] } {
  const fits: OrderFit[] = []

  for (let order = 1; order <= maxOrder; order++) {
    const fit = fitRational(points, order, order)
    if (fit && Number.isFinite(fit.rmsErrorDb)) {
      fits.push({ order, rmsErrorDb: fit.rmsErrorDb, cancellations: fit.cancellations.length })
    }
  }

  if (fits.length === 0) return { order: 2, fits }
  return { order: kneeOrder(fits), fits }
}
