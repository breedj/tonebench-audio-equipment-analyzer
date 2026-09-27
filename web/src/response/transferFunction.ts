import { cDiv, cx, polyEval, trimLeading, type Cx } from './poly'
import type { FitResult, MeasuredBand, RationalModel, Singularity } from './rationalFit'
import { passbandReferenceDb } from './surfaceField'

/**
 * The fitted model written out as a transfer function: `H(s)` in real s, both factored and as
 * coefficients.
 *
 * `rationalFit` works in *normalised* s — frequency divided by the band's geometric mean, which is
 * what keeps the least-squares conditioned — so its `num`/`den` are not coefficients anyone can use.
 * Everything here is about converting that back into the two forms a reader wants: a product of
 * first- and second-order sections they can check against the pole/zero table, and the coefficient
 * arrays scipy and MATLAB consume.
 *
 * **The two ways this goes silently wrong, both pinned by tests.** A dropped or doubled `omegaScale`
 * power gives a formula that is a factor of 2π out in frequency and looks completely reasonable; and
 * `FitResult.dcGainDb` is a *magnitude*, so a formula that takes its gain from there renders an
 * inverting stage as a non-inverting one. The gain is therefore read from `num[0]/den[0]` directly,
 * and `transferFunction.test.ts` reconstructs H from both forms and compares them against
 * `evaluateModel` across the band.
 */

/** Coefficients in real s (rad/s): `num[k]`/`den[k]` multiply `s^k`, and `den[0]` is 1. */
export interface RealCoefficients {
  num: number[]
  den: number[]
}

/**
 * Undo the fit's frequency normalisation.
 *
 * `num[k]` multiplies `(s/ωs)^k`, so the real-s coefficient is `num[k]/ωs^k`. The numerator is
 * trimmed first, for the same reason `fitRational` trims it before extracting zeros: a low-pass fit
 * carries round-off in its top coefficients, and an untrimmed degree-4 numerator whose top two
 * coefficients are 1e-17 exports as a fourth-order numerator with two enormous meaningless roots.
 */
export function denormalise(model: RationalModel): RealCoefficients {
  const scale = model.omegaScale
  const den0 = model.den[0] ?? 1
  const convert = (coeffs: readonly number[]) => coeffs.map((c, k) => c / den0 / Math.pow(scale, k))
  return { num: convert(trimLeading([...model.num])), den: convert([...model.den]) }
}

/** A first- or second-order section of the factored form. */
export interface Section {
  kind: 'real' | 'pair'
  /** Natural frequency in rad/s: 2π·freqHz. */
  omegaN: number
  freqHz: number
  /** Damping ratio; ±1 for a real root, its sign carrying the half-plane. */
  zeta: number
  isRhp: boolean
}

/**
 * Which normalisation the factored form uses.
 *
 * `DcNormalised` writes every section as 1 at s = 0, so the leading number *is* H(0) — the form a
 * reader can check against the panel's DC gain. `RootGain` writes bare `(s + …)` factors with a
 * leading K, and exists because the first form divides by each root: a high-pass fit has a zero at
 * or near the origin, which drives H(0) to zero and the sections to infinity.
 */
export type FormulaForm = 'DcNormalised' | 'RootGain'

export interface TransferFunctionForm {
  form: FormulaForm
  /** `DcNormalised`: H(0), **signed**. `RootGain`: the leading-coefficient ratio, also signed. */
  gain: number
  numeratorSections: Section[]
  denominatorSections: Section[]
  /** Why `RootGain` was picked, for the panel to print. Null under `DcNormalised`. */
  rootFormReason: string | null
}

/**
 * H(0) this far below the model's own passband level forces the root form.
 *
 * **The test is a dynamic range against the passband, not a root's frequency.** Two wrong rules were
 * tried first. Judging by "is the numerator constant zero" fails because a fitted high pass does not
 * produce an exact zero — it produces 1e-17, which leaves the DC form technically exact and
 * numerically useless, a gain of 1e-17 multiplied through sections carrying 1e17. Judging by "is the
 * lowest root near the origin relative to the band" fails the other way, and more embarrassingly: a
 * *pole* below the band does not touch H(0) at all — H(0) of a 12 Hz coupling pole is 1 — so that
 * rule threw away a perfectly good DC form for any DUT measured well above its lowest pole.
 *
 * What actually breaks the DC form is H(0) being zero or infinite, and the only honest numerical
 * version of "zero" is "vanishing beside the level the DUT actually runs at". 120 dB because a
 * genuine DC-blocked response lands hundreds of dB down while no real passband-to-DC ratio comes
 * close — a 20 Hz high pass measured from 20 Hz reads about −0.0 dB at DC by comparison.
 */
export const DC_FORM_FLOOR_DB = 120

function sectionOf(s: Singularity): Section {
  return {
    kind: s.isPair ? 'pair' : 'real',
    omegaN: 2 * Math.PI * s.freqHz,
    freqHz: s.freqHz,
    zeta: s.zeta,
    isRhp: s.isRhp,
  }
}

/**
 * The fitted model as a product of sections.
 *
 * Under `DcNormalised` a real root contributes `(1 − s/s_root)` with `s_root = −ζ·2πf` — so
 * `(1 + s/2πf)` in the left half-plane and `(1 − s/2πf)` in the right — and a conjugate pair
 * contributes `(1 + 2ζ·s/ωn + s²/ωn²)`, which is exactly `(1 − s/r)(1 − s/r̄)` for
 * `r = −ζωn ± jωn√(1−ζ²)`. Each is 1 at s = 0, so `H(s) = H(0)·Π(numerator)/Π(denominator)` is an
 * identity rather than an approximation, and the reconstruction test holds to round-off.
 */
export function transferFunctionForm(fit: FitResult, band: MeasuredBand | null): TransferFunctionForm {
  const coefficients = denormalise(fit.model)
  const numeratorSections = fit.zeros.map(sectionOf)
  const denominatorSections = fit.poles.map(sectionOf)

  const dcGain = (coefficients.num[0] ?? 0) / (coefficients.den[0] ?? 1)
  const dcGainDb = 20 * Math.log10(Math.abs(dcGain))
  const referenceDb = passbandReferenceDb(fit.model, band)
  const vanishes = Number.isFinite(referenceDb) && dcGainDb < referenceDb - DC_FORM_FLOOR_DB

  if (!Number.isFinite(dcGain) || dcGain === 0 || vanishes) {
    const numLead = coefficients.num[coefficients.num.length - 1] ?? 0
    const denLead = coefficients.den[coefficients.den.length - 1] ?? 1
    return {
      form: 'RootGain',
      gain: denLead === 0 ? 0 : numLead / denLead,
      numeratorSections,
      denominatorSections,
      rootFormReason: Number.isFinite(dcGain)
        ? 'the model has a zero at the origin, so H(0) vanishes and there is no DC gain to normalise on'
        : 'the model has a pole at the origin, so H(0) is unbounded',
    }
  }

  return { form: 'DcNormalised', gain: dcGain, numeratorSections, denominatorSections, rootFormReason: null }
}

/** One section's value at a complex s (rad/s), under the form's own convention. */
function sectionValue(section: Section, form: FormulaForm, s: Cx): Cx {
  const { omegaN, zeta } = section
  if (form === 'DcNormalised') {
    if (section.kind === 'real') {
      // (1 − s/s_root) with s_root = −ζ·ωn.
      const root = -zeta * omegaN
      return root === 0 ? cx(1) : cx(1 - s.re / root, -s.im / root)
    }
    // 1 + 2ζ·s/ωn + s²/ωn².
    const a = (2 * zeta) / omegaN
    const b = 1 / (omegaN * omegaN)
    const s2re = s.re * s.re - s.im * s.im
    const s2im = 2 * s.re * s.im
    return cx(1 + a * s.re + b * s2re, a * s.im + b * s2im)
  }

  if (section.kind === 'real') {
    // (s + ζ·ωn) — the monic factor (s − s_root).
    return cx(s.re + zeta * omegaN, s.im)
  }
  // (s² + 2ζωn·s + ωn²).
  const s2re = s.re * s.re - s.im * s.im
  const s2im = 2 * s.re * s.im
  return cx(s2re + 2 * zeta * omegaN * s.re + omegaN * omegaN, s2im + 2 * zeta * omegaN * s.im)
}

/** H(jω) rebuilt from the factored form — what a reader of the formula would compute. */
export function evaluateForm(form: TransferFunctionForm, freqHz: number): Cx {
  const s = cx(0, 2 * Math.PI * freqHz)
  let numerator = cx(form.gain)
  for (const section of form.numeratorSections) {
    const v = sectionValue(section, form.form, s)
    numerator = cx(numerator.re * v.re - numerator.im * v.im, numerator.re * v.im + numerator.im * v.re)
  }
  let denominator = cx(1)
  for (const section of form.denominatorSections) {
    const v = sectionValue(section, form.form, s)
    denominator = cx(
      denominator.re * v.re - denominator.im * v.im,
      denominator.re * v.im + denominator.im * v.re,
    )
  }
  return cDiv(numerator, denominator)
}

/** H(jω) rebuilt from the real-s coefficient arrays — what scipy would compute. */
export function evaluateReal(coefficients: RealCoefficients, freqHz: number): Cx {
  const s = cx(0, 2 * Math.PI * freqHz)
  return cDiv(polyEval(coefficients.num, s), polyEval(coefficients.den, s))
}
