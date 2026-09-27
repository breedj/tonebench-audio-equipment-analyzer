import { describe, expect, it } from 'vitest'
import {
  formatTransferFunction,
  renderTransferFunction,
  TRANSFER_FUNCTION_FORMATS,
  type TransferFunctionFormat,
} from './transferFunctionText'
import { denormalise } from './transferFunction'
import { fitRational, type FitResult, type MeasuredBand } from './rationalFit'
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

const onePole = (corner: number, gain = 1) => (f: number) => {
  const x = f / corner
  const d = 1 + x * x
  return { re: (gain * 1) / d, im: (gain * -x) / d }
}
const twoPole = (f0: number, q: number) => (f: number) => {
  const x = f / f0
  const re = 1 - x * x
  const im = x / q
  const d = re * re + im * im
  return { re: re / d, im: -im / d }
}
const highPass = (corner: number) => (f: number) => {
  const x = f / corner
  const d = 1 + x * x
  return { re: (x * x) / d, im: x / d }
}

const GRID = logGrid(20, 20000, 80)
const BAND: MeasuredBand = { lowHz: GRID[0]!, highHz: GRID[GRID.length - 1]! }

function fitOf(h: (f: number) => { re: number; im: number }, zeros: number, poles: number): FitResult {
  const fit = fitRational(sample(h, GRID), zeros, poles)
  expect(fit).not.toBeNull()
  return fit!
}

const ALL: TransferFunctionFormat[] = ['Text', 'Latex', 'SciPy', 'Matlab', 'Coefficients']

const FIXTURES = () => [
  { name: 'one real pole', fit: fitOf(onePole(2000), 0, 1) },
  { name: 'a Q = 4 pair', fit: fitOf(twoPole(1000, 4), 0, 2) },
  { name: 'an inverting stage', fit: fitOf(onePole(2000, -4), 0, 1) },
  { name: 'a high pass on the root form', fit: fitOf(highPass(100), 1, 1) },
]

describe('SciPy and MATLAB arrays are descending', () => {
  // The second classic way to be wrong here, after 2π: `num[]`/`den[]` are lowest power first, and
  // both tools want highest first. Backwards, the export is a valid transfer function of something
  // else, so the numbers are parsed back out and compared rather than eyeballed.
  function numbersIn(text: string, label: 'num' | 'den' | 'first' | 'second'): number[] {
    const brackets = [...text.matchAll(/\[([^\]]*)\]/g)].map((m) => m[1]!)
    const pick = label === 'num' || label === 'first' ? brackets[0]! : brackets[1]!
    return pick
      .split(/[,\s]+/)
      .map((s) => s.trim())
      .filter((s) => s.length > 0)
      .map(Number)
  }

  it('emits the numerator highest power first for SciPy', () => {
    const fit = fitOf(twoPole(1000, 4), 0, 2)
    const expected = denormalise(fit.model)
    const text = formatTransferFunction(fit, BAND, 'SciPy')
    const den = numbersIn(text, 'den')
    expect(den).toHaveLength(expected.den.length)
    // Exact equality, not a tolerance: the export uses the shortest round-tripping representation, so
    // a parsed coefficient must be bit-identical to the one the fit produced. A tolerance here would
    // hide a precision loss that moves a high-Q pole.
    expect(den).toEqual([...expected.den].reverse())
    // The leading entry is the HIGHEST power, which for a second-order denominator is den[2].
    expect(den[0]!).toBeCloseTo(expected.den[2]!, 20)
    expect(den[den.length - 1]!).toBeCloseTo(expected.den[0]!, 9)
    // ...and den[0] is the normalised 1, so a reversed array would start with 1 instead.
    expect(expected.den[0]!).toBeCloseTo(1, 9)
    expect(den[0]!).not.toBeCloseTo(1, 6)
  })

  it('emits the same order for MATLAB and the bare coefficient listing', () => {
    const fit = fitOf(twoPole(1000, 4), 0, 2)
    const scipyDen = numbersIn(formatTransferFunction(fit, BAND, 'SciPy'), 'den')
    const matlabDen = numbersIn(formatTransferFunction(fit, BAND, 'Matlab'), 'den')
    const plainDen = numbersIn(formatTransferFunction(fit, BAND, 'Coefficients'), 'den')
    expect(matlabDen).toEqual(scipyDen)
    expect(plainDen).toEqual(scipyDen)
  })

  it('says which way round and in what units, since the numbers cannot say it themselves', () => {
    for (const format of ['SciPy', 'Matlab', 'Coefficients'] as const) {
      const text = formatTransferFunction(fitOf(onePole(2000), 0, 1), BAND, format)
      expect(text).toMatch(/rad\/s/)
      expect(text).toMatch(/highest power first/)
    }
  })
})

describe('every format', () => {
  it('is non-empty and free of undefined, NaN and Infinity for every fixture', () => {
    for (const { name, fit } of FIXTURES()) {
      for (const format of ALL) {
        const text = formatTransferFunction(fit, BAND, format)
        expect(text.length, `${name} / ${format}`).toBeGreaterThan(0)
        expect(text, `${name} / ${format}`).not.toMatch(/undefined|NaN|Infinity/)
      }
    }
  })

  it('survives a null band', () => {
    for (const format of ALL) {
      expect(formatTransferFunction(fitOf(onePole(2000), 0, 1), null, format).length).toBeGreaterThan(0)
    }
  })

  it('is listed once each in TRANSFER_FUNCTION_FORMATS', () => {
    expect(TRANSFER_FUNCTION_FORMATS.map((f) => f.id).sort()).toEqual([...ALL].sort())
  })
})

describe('LaTeX', () => {
  it('balances its braces', () => {
    for (const { name, fit } of FIXTURES()) {
      const text = formatTransferFunction(fit, BAND, 'Latex')
      let depth = 0
      for (const ch of text) {
        if (ch === '{') depth++
        if (ch === '}') depth--
        expect(depth, name).toBeGreaterThanOrEqual(0)
      }
      expect(depth, name).toBe(0)
    }
  })

  it('uses a fraction and states the frequency unit', () => {
    const text = formatTransferFunction(fitOf(onePole(2000), 0, 1), BAND, 'Latex')
    expect(text).toContain('\\frac')
    expect(text).toMatch(/\\mathrm\{k?Hz\}/)
  })
})

describe('plain text', () => {
  it('carries the fitted order and the residual, so the formula cannot be read without them', () => {
    const fit = fitOf(twoPole(1000, 4), 0, 2)
    const text = formatTransferFunction(fit, BAND, 'Text')
    expect(text).toContain(`order ${fit.numeratorOrder}/${fit.denominatorOrder}`)
    expect(text).toMatch(/dB rms error/)
    expect(text).toMatch(/Measured band/)
  })

  it('states the 2ζ convention, which is the one a reader would otherwise assume is ζ or 1/Q', () => {
    const text = formatTransferFunction(fitOf(twoPole(1000, 4), 0, 2), BAND, 'Text')
    expect(text).toContain('2ζ')
  })

  it('renders an inverting stage with a negative leading number', () => {
    const text = formatTransferFunction(fitOf(onePole(2000, -4), 0, 1), BAND, 'Text')
    expect(text).toMatch(/H\(s\) = -/)
  })
})

describe('renderTransferFunction', () => {
  it('splits superscripts out as parts rather than leaving markup to be parsed', () => {
    const rendered = renderTransferFunction(fitOf(twoPole(1000, 4), 0, 2), BAND)
    expect(rendered.denominator).toHaveLength(1)
    const parts = rendered.denominator[0]!.parts
    expect(parts.some((p) => p.sup === true && p.text === '2')).toBe(true)
    // Nothing in the parts should contain raw markup.
    for (const part of parts) expect(part.text).not.toMatch(/[<>]/)
  })

  it('leaves the numerator empty for an all-pole fit, rather than printing an empty pair of brackets', () => {
    expect(renderTransferFunction(fitOf(onePole(2000), 0, 1), BAND).numerator).toEqual([])
  })

  it('names the normalisation in force, and why when it is the root form', () => {
    expect(renderTransferFunction(fitOf(onePole(2000), 0, 1), BAND).note).toMatch(/H\(0\)/)
    const rootForm = renderTransferFunction(fitOf(highPass(100), 1, 1), BAND)
    expect(rootForm.note).toMatch(/Root form/)
    expect(rootForm.note).toMatch(/origin/)
  })

  it('states the hertz-versus-rad/s split on screen, where the 2π mistakes happen', () => {
    const rendered = renderTransferFunction(fitOf(onePole(2000), 0, 1), BAND)
    expect(rendered.conventions).toMatch(/hertz/)
    expect(rendered.conventions).toMatch(/rad\/s/)
  })

  it('shows a right-half-plane real root with a minus sign in its own section', () => {
    // The wrong phase sign on a one-pole, which the fit reports as a right-half-plane root.
    const fit = fitOf((f) => {
      const x = f / 2000
      const d = 1 + x * x
      return { re: 1 / d, im: x / d }
    }, 0, 1)
    const rendered = renderTransferFunction(fit, BAND)
    const text = rendered.denominator.map((factor) => factor.parts.map((p) => p.text).join('')).join('')
    expect(text).toContain('−')
  })
})
