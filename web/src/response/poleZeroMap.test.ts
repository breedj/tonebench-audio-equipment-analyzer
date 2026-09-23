import { describe, expect, it } from 'vitest'
import {
  fitRootPoints,
  measuredBand,
  nearestRoot,
  poleZeroViewport,
  rootPoints,
  visibleRoots,
  MIN_ROOT_EXTENT_HZ,
} from './poleZeroMap'
import { fitRational, type Singularity } from './rationalFit'
import type { ComplexPoint } from './complexPlane'

function singularity(over: Partial<Singularity>): Singularity {
  return { freqHz: 1000, q: null, zeta: 1, isPair: false, isRhp: false, ...over }
}

describe('rootPoints', () => {
  it('puts a Butterworth pair back where its (f, ζ) says: on the circle of radius f, at −ζf ± jf√(1−ζ²)', () => {
    // The outer pair of a fifth-order Butterworth at 2 kHz: ζ = cos(72°) = 0.309.
    const pair = singularity({ freqHz: 2000, zeta: 0.309016994, q: 1.618, isPair: true })
    const roots = rootPoints(pair, 'pole')
    expect(roots).toHaveLength(2)
    expect(roots[0]!.re).toBeCloseTo(-618.03, 1)
    expect(roots[0]!.im).toBeCloseTo(1902.11, 1)
    expect(roots[1]!.im).toBeCloseTo(-1902.11, 1)
    expect(Math.hypot(roots[0]!.re, roots[0]!.im)).toBeCloseTo(2000, 6)
  })

  it('puts a real pole on the negative real axis and a right-half-plane one on the positive', () => {
    expect(rootPoints(singularity({ freqHz: 2000, zeta: 1 }), 'pole')).toEqual([
      expect.objectContaining({ re: -2000, im: 0, kind: 'pole' }),
    ])
    expect(rootPoints(singularity({ freqHz: 500, zeta: -1, isRhp: true }), 'zero')).toEqual([
      expect.objectContaining({ re: 500, im: 0, kind: 'zero' }),
    ])
  })

  it('puts an elliptic zero on the jω axis', () => {
    const zero = singularity({ freqHz: 3481, zeta: 0, q: Infinity, isPair: true })
    const roots = rootPoints(zero, 'zero')
    expect(roots[0]!.re).toBeCloseTo(0, 9)
    expect(roots[0]!.im).toBeCloseTo(3481, 6)
  })
})

describe('poleZeroViewport', () => {
  it('is square, symmetric, and fitted to the furthest root with a margin', () => {
    const roots = rootPoints(singularity({ freqHz: 2000, zeta: 0.309, isPair: true }), 'pole')
    const v = poleZeroViewport(roots)
    expect(v.xMax).toBeCloseTo(1902.1 * 1.25, 0)
    expect(v.xMin).toBe(-v.xMax)
    expect(v.yMin).toBe(-v.yMax)
    expect(v.yMax).toBe(v.xMax)
  })

  it('never collapses on an empty fit', () => {
    expect(poleZeroViewport([]).xMax).toBe(MIN_ROOT_EXTENT_HZ * 1.25)
  })
})

describe('fitRootPoints', () => {
  it('recovers the roots of a fitted second-order low-pass', () => {
    // A 1 kHz, Q 0.7 low-pass sampled on a log grid: the fit should place one pair at 1 kHz.
    const f0 = 1000
    const q = 0.7
    const points: ComplexPoint[] = []
    for (let i = 0; i <= 60; i++) {
      const f = 100 * Math.pow(10, (2 * i) / 60)
      const w = f / f0
      const den = { re: 1 - w * w, im: w / q }
      const d = den.re * den.re + den.im * den.im
      const h = { re: den.re / d, im: -den.im / d }
      points.push({ freqHz: f, re: h.re, im: h.im, magDb: 20 * Math.log10(Math.hypot(h.re, h.im)), phaseDeg: (Math.atan2(h.im, h.re) * 180) / Math.PI })
    }
    const fit = fitRational(points, 2, 2)
    expect(fit).not.toBeNull()
    const roots = fitRootPoints(fit!).filter((r) => r.kind === 'pole')
    expect(roots).toHaveLength(2)
    expect(Math.hypot(roots[0]!.re, roots[0]!.im)).toBeCloseTo(f0, -1)
    expect(roots[0]!.re).toBeCloseTo(-f0 / (2 * q), -1)
  })
})

describe('nearestRoot', () => {
  it('picks by screen distance and gives up beyond the radius', () => {
    const roots = rootPoints(singularity({ freqHz: 2000, zeta: 0.309, isPair: true }), 'pole')
    const toScreen = (r: { re: number; im: number }) => ({ x: r.re / 10, y: -r.im / 10 })
    expect(nearestRoot(roots, -62, -190, toScreen)?.im).toBeGreaterThan(0)
    expect(nearestRoot(roots, -62, 190, toScreen)?.im).toBeLessThan(0)
    expect(nearestRoot(roots, 200, 200, toScreen)).toBeNull()
  })
})

describe('visibleRoots', () => {
  const band = { lowHz: 100, highHz: 10_000 }
  const inBand = rootPoints(singularity({ freqHz: 2000, zeta: 0.309, isPair: true }), 'pole')
  const outOfBand = rootPoints(singularity({ freqHz: 59_847_000, zeta: 1 }), 'zero')
  const all = [...inBand, ...outOfBand]

  it('keeps everything until asked, and then drops only what the table flags', () => {
    expect(visibleRoots(all, band, false)).toHaveLength(3)
    expect(visibleRoots(all, band, true)).toEqual(inBand)
  })

  it('keeps a conjugate pair together — in or out as a pair', () => {
    expect(visibleRoots(inBand, { lowHz: 1900, highHz: 2100 }, true)).toHaveLength(2)
    expect(visibleRoots(inBand, { lowHz: 3000, highHz: 4000 }, true)).toHaveLength(0)
  })

  it('keeps everything when there is no band to judge against', () => {
    expect(visibleRoots(all, null, true)).toHaveLength(3)
    expect(measuredBand([])).toBeNull()
    expect(measuredBand([{ freqHz: 20 }, { freqHz: 300 }, { freqHz: 9000 }])).toEqual({ lowHz: 20, highHz: 9000 })
  })

  it('rescales the window once the extrapolated root is gone', () => {
    expect(poleZeroViewport(all).xMax).toBeGreaterThan(1e7)
    expect(poleZeroViewport(visibleRoots(all, band, true)).xMax).toBeLessThan(3000)
  })
})
