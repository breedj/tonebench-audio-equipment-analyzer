import { describe, expect, it } from 'vitest'
import {
  denormalise,
  evaluateForm,
  evaluateReal,
  transferFunctionForm,
  DC_FORM_FLOOR_DB,
} from './transferFunction'
import { evaluateModel, fitRational, type FitResult, type MeasuredBand } from './rationalFit'
import { cAbs } from './poly'
import type { ComplexPoint } from './complexPlane'

function logGrid(fromHz: number, toHz: number, count: number): number[] {
  const ratio = Math.log(toHz / fromHz)
  return Array.from({ length: count }, (_, i) => fromHz * Math.exp((ratio * i) / (count - 1)))
}

function sample(h: (f: number) => { re: number; im: number }, freqs: number[]): ComplexPoint[] {
  return freqs.map((freqHz) => {
    const v = h(freqHz)
    return {
      freqHz,
      re: v.re,
      im: v.im,
      magDb: 20 * Math.log10(Math.hypot(v.re, v.im)),
      phaseDeg: (Math.atan2(v.im, v.re) * 180) / Math.PI,
    }
  })
}

/** 1/(1 + jf/corner), scaled by `gain` (negative for an inverting stage). */
const onePole = (corner: number, gain = 1) => (f: number) => {
  const x = f / corner
  const d = 1 + x * x
  return { re: (gain * 1) / d, im: (gain * -x) / d }
}

/** A conjugate pair: 1/(1 − (f/f0)² + j·f/(f0·Q)). */
const twoPole = (f0: number, q: number) => (f: number) => {
  const x = f / f0
  const re = 1 - x * x
  const im = x / q
  const d = re * re + im * im
  return { re: re / d, im: -im / d }
}

/** A notch: a zero pair on the jω axis over a pole pair at the same frequency. */
const notch = (f0: number, q: number) => (f: number) => {
  const x = f / f0
  const nRe = 1 - x * x
  const dRe = 1 - x * x
  const dIm = x / q
  const d = dRe * dRe + dIm * dIm
  return { re: (nRe * dRe) / d, im: (-nRe * dIm) / d }
}

/** A one-pole high pass: jf/corner / (1 + jf/corner) — a zero at the origin. */
const highPass = (corner: number) => (f: number) => {
  const x = f / corner
  const d = 1 + x * x
  // jx/(1+jx) = (x² + jx)/(1+x²)
  return { re: (x * x) / d, im: x / d }
}

const GRID = logGrid(20, 20000, 80)
const BAND: MeasuredBand = { lowHz: GRID[0]!, highHz: GRID[GRID.length - 1]! }

function fitOf(h: (f: number) => { re: number; im: number }, zeros: number, poles: number): FitResult {
  const fit = fitRational(sample(h, GRID), zeros, poles)
  expect(fit).not.toBeNull()
  return fit!
}

const FIXTURES: { name: string; fit: () => FitResult }[] = [
  { name: 'a single real pole', fit: () => fitOf(onePole(2000), 0, 1) },
  { name: 'a Q = 4 conjugate pair', fit: () => fitOf(twoPole(1000, 4), 0, 2) },
  { name: 'an 800 Hz Q = 4 notch', fit: () => fitOf(notch(800, 4), 2, 2) },
  { name: 'an inverting one-pole stage', fit: () => fitOf(onePole(2000, -1), 0, 1) },
  { name: 'a one-pole high pass', fit: () => fitOf(highPass(100), 1, 1) },
  { name: 'a third-order low pass', fit: () => fitOf((f) => {
      const a = onePole(1500)(f)
      const b = twoPole(2500, 0.8)(f)
      return { re: a.re * b.re - a.im * b.im, im: a.re * b.im + a.im * b.re }
    }, 0, 3) },
]

describe('the formula and the model are the same function', () => {
  // The single most important test here. A dropped or doubled omegaScale power, a section written
  // with ζ where it wanted 2ζ, or a sign convention slip all die on it — and every one of those
  // produces a formula that looks perfectly reasonable on screen.
  for (const fixture of FIXTURES) {
    it(`reconstructs ${fixture.name} from both the factored form and the coefficients`, () => {
      const fit = fixture.fit()
      // omegaScale must not be 1, or a missing power of it would go unnoticed.
      expect(fit.model.omegaScale).toBeGreaterThan(100)

      const form = transferFunctionForm(fit, BAND)
      const coefficients = denormalise(fit.model)

      for (const f of logGrid(BAND.lowHz, BAND.highHz, 64)) {
        const model = evaluateModel(fit.model, f)
        const scale = Math.max(1e-30, cAbs(model))

        const fromForm = evaluateForm(form, f)
        expect(Math.hypot(fromForm.re - model.re, fromForm.im - model.im) / scale).toBeLessThan(1e-8)

        const fromReal = evaluateReal(coefficients, f)
        expect(Math.hypot(fromReal.re - model.re, fromReal.im - model.im) / scale).toBeLessThan(1e-8)
      }
    })
  }
})

describe('denormalise', () => {
  it('normalises the denominator constant to 1', () => {
    for (const fixture of FIXTURES) {
      expect(denormalise(fixture.fit().model).den[0]).toBeCloseTo(1, 12)
    }
  })

  it('agrees with the fit’s own dcGainDb in magnitude', () => {
    const fit = fitOf(onePole(2000, 1), 0, 1)
    const c = denormalise(fit.model)
    expect(20 * Math.log10(Math.abs(c.num[0]! / c.den[0]!))).toBeCloseTo(fit.dcGainDb, 9)
  })

  it('carries the omegaScale power per coefficient, not once for the whole polynomial', () => {
    // A second-order denominator: den[2] must be divided by omegaScale², not by omegaScale.
    const fit = fitOf(twoPole(1000, 4), 0, 2)
    const c = denormalise(fit.model)
    const ws = fit.model.omegaScale
    expect(c.den[1]!).toBeCloseTo(fit.model.den[1]! / fit.model.den[0]! / ws, 12)
    expect(c.den[2]!).toBeCloseTo(fit.model.den[2]! / fit.model.den[0]! / (ws * ws), 12)
  })

  it('drops round-off leading numerator coefficients, as the fit does before reporting zeros', () => {
    const fit = fitOf(onePole(2000), 0, 1)
    // An all-pole fit has no finite zeros, so the exported numerator is a bare constant.
    expect(fit.zeros).toHaveLength(0)
    expect(denormalise(fit.model).num).toHaveLength(1)
  })
})

describe('the gain sign', () => {
  it('renders an inverting stage with its minus sign, which dcGainDb cannot express', () => {
    const inverting = fitOf(onePole(2000, -1), 0, 1)
    const plain = fitOf(onePole(2000, 1), 0, 1)

    // dcGainDb is a magnitude, so the two fits are indistinguishable by it...
    expect(inverting.dcGainDb).toBeCloseTo(plain.dcGainDb, 6)
    // ...and the formula must still tell them apart.
    expect(transferFunctionForm(inverting, BAND).gain).toBeLessThan(0)
    expect(transferFunctionForm(plain, BAND).gain).toBeGreaterThan(0)
  })

  it('puts the DC gain itself in the leading number, because every section is 1 at s = 0', () => {
    const fit = fitOf(onePole(2000, -4), 0, 1)
    const form = transferFunctionForm(fit, BAND)
    expect(form.form).toBe('DcNormalised')
    expect(form.gain).toBeCloseTo(-4, 4)
  })
})

describe('the root-form fallback', () => {
  it('is taken for a high pass, whose zero at the origin has no DC gain to normalise on', () => {
    const form = transferFunctionForm(fitOf(highPass(100), 1, 1), BAND)
    expect(form.form).toBe('RootGain')
    expect(form.rootFormReason).toMatch(/origin/)
    expect(Number.isFinite(form.gain)).toBe(true)
    expect(form.gain).not.toBe(0)
  })

  it('is not taken for any fixture with a real DC gain', () => {
    for (const name of ['a single real pole', 'a Q = 4 conjugate pair', 'an 800 Hz Q = 4 notch']) {
      const fixture = FIXTURES.find((f) => f.name === name)!
      expect(transferFunctionForm(fixture.fit(), BAND).form).toBe('DcNormalised')
    }
  })

  it('keeps the DC form for a pole far below the band, which does not touch H(0) at all', () => {
    // The trap an earlier rule fell into: judging "near the origin" by a root's frequency against the
    // band condemned this fit, although H(0) of a plain low pass is its passband gain and the DC form
    // is exactly what a reader wants. A pole does not zero DC — only a numerator root at s = 0 does.
    const fit = fitOf(onePole(20), 0, 1)
    expect(fit.poles[0]!.freqHz).toBeLessThan(BAND.lowHz * 2)
    expect(transferFunctionForm(fit, BAND).form).toBe('DcNormalised')
  })

  it('is a dynamic range against the passband, deep enough that no real response trips it', () => {
    // A high pass is hundreds of dB down at DC; nothing with a real passband-to-DC ratio comes near.
    expect(DC_FORM_FLOOR_DB).toBeGreaterThanOrEqual(100)
    const highPassFit = fitOf(highPass(100), 1, 1)
    const c = denormalise(highPassFit.model)
    const dcDb = 20 * Math.log10(Math.abs(c.num[0]! / c.den[0]!))
    expect(dcDb).toBeLessThan(-DC_FORM_FLOOR_DB)
  })
})

describe('sections', () => {
  it('reports a real root as real and a pair as a pair, with the fit’s own ζ', () => {
    const fit = fitOf(twoPole(1000, 4), 0, 2)
    const form = transferFunctionForm(fit, BAND)
    expect(form.denominatorSections).toHaveLength(1)
    const section = form.denominatorSections[0]!
    expect(section.kind).toBe('pair')
    expect(section.freqHz).toBeCloseTo(1000, 0)
    // Q = 1/(2ζ) for a pair.
    expect(1 / (2 * section.zeta)).toBeCloseTo(4, 1)
    expect(section.omegaN).toBeCloseTo(2 * Math.PI * section.freqHz, 9)
  })

  it('leaves the numerator empty for an all-pole filter', () => {
    expect(transferFunctionForm(fitOf(onePole(2000), 0, 1), BAND).numeratorSections).toEqual([])
  })

  it('marks a right-half-plane root rather than rendering it as a stable one', () => {
    // A non-minimum-phase response: the wrong phase sign on a one-pole, which the fit reports as RHP.
    const h = (f: number) => {
      const x = f / 2000
      const d = 1 + x * x
      return { re: 1 / d, im: x / d }
    }
    const fit = fitOf(h, 0, 1)
    const rhp = [...fit.poles, ...fit.zeros].filter((s) => s.isRhp)
    expect(rhp.length).toBeGreaterThan(0)
    const form = transferFunctionForm(fit, BAND)
    const sections = [...form.numeratorSections, ...form.denominatorSections]
    expect(sections.some((s) => s.isRhp)).toBe(true)
    // An RHP real root has ζ = −1, which is what flips the sign of its section's s term.
    expect(sections.some((s) => s.zeta < 0)).toBe(true)
  })

  it('reconstructs a right-half-plane fit as faithfully as a stable one', () => {
    const h = (f: number) => {
      const x = f / 2000
      const d = 1 + x * x
      return { re: 1 / d, im: x / d }
    }
    const fit = fitOf(h, 0, 1)
    const form = transferFunctionForm(fit, BAND)
    for (const f of logGrid(BAND.lowHz, BAND.highHz, 32)) {
      const model = evaluateModel(fit.model, f)
      const rebuilt = evaluateForm(form, f)
      expect(Math.hypot(rebuilt.re - model.re, rebuilt.im - model.im) / cAbs(model)).toBeLessThan(1e-8)
    }
  })
})
