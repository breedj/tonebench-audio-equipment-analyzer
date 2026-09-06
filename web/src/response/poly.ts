/**
 * Just enough complex arithmetic and polynomial root-finding for the rational fit.
 *
 * Deliberately not a general library: it exists so `rationalFit.ts` can turn a fitted denominator
 * into poles, and it is kept pure and separate because root-finding is the step that fails silently
 * — a mis-scaled polynomial returns roots, they just are not the right ones.
 */

export interface Cx {
  re: number
  im: number
}

export const cx = (re: number, im = 0): Cx => ({ re, im })

export const cAdd = (a: Cx, b: Cx): Cx => ({ re: a.re + b.re, im: a.im + b.im })
export const cSub = (a: Cx, b: Cx): Cx => ({ re: a.re - b.re, im: a.im - b.im })
export const cMul = (a: Cx, b: Cx): Cx => ({
  re: a.re * b.re - a.im * b.im,
  im: a.re * b.im + a.im * b.re,
})

export function cDiv(a: Cx, b: Cx): Cx {
  // Smith's method: scaling by the larger component keeps |b|² from overflowing or, more relevantly
  // here, from underflowing to zero when both parts are small — which is exactly what happens near a
  // converged root.
  if (Math.abs(b.re) >= Math.abs(b.im)) {
    const r = b.im / b.re
    const d = b.re + b.im * r
    return { re: (a.re + a.im * r) / d, im: (a.im - a.re * r) / d }
  }
  const r = b.re / b.im
  const d = b.re * r + b.im
  return { re: (a.re * r + a.im) / d, im: (a.im * r - a.re) / d }
}

export const cAbs = (a: Cx): number => Math.hypot(a.re, a.im)

/** Horner evaluation. `coeffs[k]` multiplies `z^k`. */
export function polyEval(coeffs: number[], z: Cx): Cx {
  let out = cx(0)
  for (let k = coeffs.length - 1; k >= 0; k--) {
    out = cAdd(cMul(out, z), cx(coeffs[k]))
  }
  return out
}

/**
 * Drop leading coefficients that are negligible against the largest one.
 *
 * Load-bearing for this application, not hygiene. A low-pass filter has all its zeros at infinity,
 * so the fitted numerator's high-order coefficients come out at round-off — and a root finder handed
 * a degree-4 polynomial whose top two coefficients are 1e-17 does not report "degree 2", it reports
 * four roots, two of them enormous and meaningless. Trimming is what makes a 2nd-order low pass
 * report two poles and *no* zeros instead of two poles and two pieces of noise.
 */
export function trimLeading(coeffs: number[], relativeTolerance = 1e-9): number[] {
  const scale = Math.max(...coeffs.map(Math.abs))
  if (!(scale > 0)) return [0]

  let degree = coeffs.length - 1
  while (degree > 0 && Math.abs(coeffs[degree]) <= relativeTolerance * scale) degree--
  return coeffs.slice(0, degree + 1)
}

/**
 * All roots of a real polynomial, by Durand–Kerner.
 *
 * Chosen over an eigenvalue method because it needs no matrix machinery and converges reliably for
 * the degrees this fit produces (filters are order 2–8). It is sensitive to scaling, which is why
 * `rationalFit` normalises frequency before ever building the polynomial: over three decades of
 * un-normalised rad/s the coefficients span so many orders of magnitude that the roots are noise.
 */
export function polyRoots(coeffs: number[], maxIterations = 500, tolerance = 1e-14): Cx[] {
  const trimmed = trimLeading(coeffs)
  const degree = trimmed.length - 1
  if (degree < 1) return []

  // Monic: Durand–Kerner's update assumes a leading coefficient of 1.
  const lead = trimmed[degree]
  const monic = trimmed.map((c) => c / lead)

  // The classic seed. Off the real axis and not a root of unity, so no two starting points are
  // symmetric about a real polynomial's real axis — which would otherwise let a pair converge onto
  // the same root and leave another unfound.
  const seed = cx(0.4, 0.9)
  let roots: Cx[] = []
  let power = cx(1, 0)
  for (let i = 0; i < degree; i++) {
    roots.push(power)
    power = cMul(power, seed)
  }

  for (let iteration = 0; iteration < maxIterations; iteration++) {
    let maxStep = 0

    for (let i = 0; i < degree; i++) {
      let denominator = cx(1, 0)
      for (let j = 0; j < degree; j++) {
        if (j !== i) denominator = cMul(denominator, cSub(roots[i], roots[j]))
      }
      if (cAbs(denominator) === 0) continue

      const step = cDiv(polyEval(monic, roots[i]), denominator)
      roots[i] = cSub(roots[i], step)
      maxStep = Math.max(maxStep, cAbs(step))
    }

    if (maxStep < tolerance) break
  }

  return roots
}
