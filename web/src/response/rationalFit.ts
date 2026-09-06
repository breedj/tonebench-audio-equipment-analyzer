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
   * Pairs of a pole and a zero within `CANCELLATION_FRACTION` of each other in frequency. They are
   * the fit spending order on noise, and the honest response is to lower the order rather than to
   * report them as features.
   */
  cancellations: { poleHz: number; zeroHz: number }[]
}

/** A pole and zero closer than this in relative frequency are treated as cancelling. */
export const CANCELLATION_FRACTION = 0.05

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

  const cancellations: { poleHz: number; zeroHz: number }[] = []
  for (const pole of poles) {
    for (const zero of zeros) {
      const scale = Math.max(pole.freqHz, zero.freqHz)
      if (scale > 0 && Math.abs(pole.freqHz - zero.freqHz) / scale < CANCELLATION_FRACTION) {
        cancellations.push({ poleHz: pole.freqHz, zeroHz: zero.freqHz })
      }
    }
  }

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

/** Discounting cancelling poles may not cost more than this factor of fit accuracy. */
export const KNEE_MAX_DISCOUNT_COST = 10

export interface OrderFit {
  order: number
  rmsErrorDb: number
  cancellations: number
}

/**
 * The order to fit at. Three steps, and each one exists because the previous rule failed on measured
 * data from the GM70 driver stage (5 Hz–80 kHz, −25 dBV, 65 points).
 *
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
 * Falls back to the lowest order tried when nothing improves by `KNEE_SIGNIFICANT_FACTOR`: a
 * first-order system is fitted exactly at order 1, and every step above it is then a ratio of
 * round-off to round-off. Under-fitting is the safe failure — it shows in the error readout and in
 * the fitted curve visibly leaving the measurement, whereas over-fitting looks like success.
 */
export function kneeOrder(fits: OrderFit[]): number {
  if (fits.length === 0) return 2
  if (fits.length === 1) return fits[0].order

  // Errors are floored before dividing: a synthetic fixture fits to round-off, and 0/0 would make
  // every ratio NaN and the rule silently return the first order.
  const floor = 1e-12
  const err = (fit: OrderFit) => Math.max(fit.rmsErrorDb, floor)

  const ratios = fits.slice(1).map((fit, i) => ({ fit, ratio: err(fits[i]) / err(fit) }))
  const significant = ratios.filter((r) => r.ratio >= KNEE_SIGNIFICANT_FACTOR)
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
