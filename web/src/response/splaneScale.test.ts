import { describe, expect, it } from 'vitest'
import {
  axisTickHz,
  mappedRadius,
  mappedWindow,
  plainRadius,
  referenceHz,
  sPlaneMap,
  signedMappedRadius,
  surfaceViewport,
  toHz,
  toMapped,
  windowHz,
  MIN_REFERENCE_HZ,
  MIN_WINDOW_SPAN_HZ,
  type SPlaneMap,
} from './splaneScale'
import { rootPoints, MIN_ROOT_EXTENT_HZ, type RootPoint } from './poleZeroMap'
import type { MeasuredBand, Singularity } from './rationalFit'

const linear: SPlaneMap = { scale: 'Linear', refHz: 1 }
const log: SPlaneMap = { scale: 'LogModulus', refHz: 1 }

function singularity(over: Partial<Singularity>): Singularity {
  return { freqHz: 1000, q: null, zeta: 1, isPair: false, isRhp: false, ...over }
}

const band = (lowHz: number, highHz: number): MeasuredBand => ({ lowHz, highHz })

describe('referenceHz', () => {
  it('rounds down to a decade, so the warp does not shift with every window nudge', () => {
    expect(referenceHz(band(13, 90000))).toBe(10)
    expect(referenceHz(band(19.5, 20000))).toBe(10)
    expect(referenceHz(band(100, 20000))).toBe(100)
    // 13 Hz and 19.5 Hz are different windows of the same sweep and must warp identically.
    expect(referenceHz(band(13, 90000))).toBe(referenceHz(band(19.5, 20000)))
  })

  it('floors at MIN_REFERENCE_HZ and defaults to 1 Hz without a usable band', () => {
    expect(referenceHz(band(0.001, 100))).toBe(MIN_REFERENCE_HZ)
    expect(referenceHz(null)).toBe(1)
    expect(referenceHz(band(0, 100))).toBe(1)
    expect(referenceHz(band(Number.NaN, 100))).toBe(1)
  })
})

describe('Linear', () => {
  it('is exactly the identity, both ways', () => {
    for (const [x, y] of [
      [0, 0],
      [-2000, 1902],
      [1234.5, -98765],
    ] as const) {
      expect(toMapped(linear, x, y)).toEqual({ x, y })
      expect(toHz(linear, x, y)).toEqual({ reHz: x, imHz: y })
    }
    expect(mappedRadius(linear, 4321)).toBe(4321)
    expect(plainRadius(linear, 4321)).toBe(4321)
  })

  it('leaves sPlaneMap carrying a reference it does not use', () => {
    expect(sPlaneMap('Linear', band(13, 90000))).toEqual({ scale: 'Linear', refHz: 10 })
  })
})

describe('LogModulus round trip', () => {
  it('recovers the original point across nine decades and all four quadrants', () => {
    const m = sPlaneMap('LogModulus', band(10, 100000))
    for (const r of [0.5, 5, 50, 500, 5000, 50000, 5e6, 1e8]) {
      for (const theta of [0.3, 1.9, 3.0, 4.4, 5.9, Math.PI / 2, -Math.PI / 2, Math.PI]) {
        const reHz = r * Math.cos(theta)
        const imHz = r * Math.sin(theta)
        const mapped = toMapped(m, reHz, imHz)
        const back = toHz(m, mapped.x, mapped.y)
        // Relative, not absolute: an absolute tolerance is meaningless when r spans 0.5 Hz to 100 MHz.
        expect(Math.hypot(back.reHz - reHz, back.imHz - imHz) / r).toBeLessThan(1e-12)
      }
    }
  })

  it('round-trips the radius to full relative precision', () => {
    for (const r of [1e-6, 1e-3, 1, 137, 2000, 96000, 1e7]) {
      expect(Math.abs(plainRadius(log, mappedRadius(log, r)) - r) / r).toBeLessThan(1e-12)
    }
  })

  it('maps the origin to the origin without dividing by zero', () => {
    expect(toMapped(log, 0, 0)).toEqual({ x: 0, y: 0 })
    expect(toHz(log, 0, 0)).toEqual({ reHz: 0, imHz: 0 })
    expect(mappedRadius(log, 0)).toBe(0)
  })
})

describe('LogModulus geometry', () => {
  it('preserves the angle exactly, so ζ and Q still read as an angle from the origin', () => {
    // The outer pair of a fifth-order Butterworth at 2 kHz: ζ = cos(72°).
    const roots = rootPoints(singularity({ freqHz: 2000, zeta: 0.309016994, isPair: true }), 'pole')
    for (const root of roots) {
      const before = Math.atan2(root.im, root.re)
      const p = toMapped(log, root.re, root.im)
      expect(Math.atan2(p.y, p.x)).toBeCloseTo(before, 12)
    }
  })

  it('keeps the jω axis vertical and the real axis horizontal', () => {
    expect(toMapped(log, 0, 5000).x).toBe(0)
    expect(toMapped(log, 0, -5000).x).toBe(0)
    expect(toMapped(log, -5000, 0).y).toBe(0)
    // ...and the half-planes are not swapped.
    expect(toMapped(log, -5000, 0).x).toBeLessThan(0)
    expect(toMapped(log, 5000, 0).x).toBeGreaterThan(0)
    expect(toMapped(log, 0, 5000).y).toBeGreaterThan(0)
  })

  it('is smooth through the origin, unlike a bare log10(r/ref)', () => {
    // ρ → r/(refHz·ln10) as r → 0: finite, and linear in r. Approached from above with a −x/2 term,
    // so this has to be asserted near the limit and not at 1e-5, where the honest answer is already
    // 5e-6 away from it.
    const slope = 1 / (log.refHz * Math.LN10)
    for (const r of [1e-9, 1e-11, 1e-13]) {
      expect(mappedRadius(log, r) / r).toBeCloseTo(slope, 9)
    }
    expect(Number.isFinite(mappedRadius(log, 1e-300))).toBe(true)
  })

  it('still resolves a radius far below the float dust of 1 + x — which is why log1p is used', () => {
    // The naive form: 1 + 1e-17 === 1 in float64, so log10(1 + x) collapses to a hard zero and every
    // root nearer the origin than ~1e-16 of the reference lands exactly on it. log1p does not.
    expect(Math.log10(1 + 1e-17)).toBe(0)
    expect(mappedRadius(log, 1e-17)).toBeGreaterThan(0)
    expect(mappedRadius(log, 1e-17) / 1e-17).toBeCloseTo(1 / (log.refHz * Math.LN10), 9)
    // ...and the inverse holds up over the same range, so the round trip survives it.
    expect(plainRadius(log, mappedRadius(log, 1e-17))).toBeCloseTo(1e-17, 25)
  })

  it('is strictly increasing in radius', () => {
    let previous = -1
    for (const r of [0, 0.1, 1, 10, 100, 1000, 10000, 1e6]) {
      const rho = mappedRadius(log, r)
      expect(rho).toBeGreaterThan(previous)
      previous = rho
    }
  })

  it('spends a constant distance on a decade, well above the reference', () => {
    const m = sPlaneMap('LogModulus', band(10, 100000))
    const decade = (from: number) => mappedRadius(m, from * 10) - mappedRadius(m, from)
    // Exactly 1 only asymptotically — the +1 in log1p(r/ref) is what buys smoothness at the origin,
    // and it costs a vanishing amount of decade-linearity far from it.
    expect(decade(10000)).toBeCloseTo(1, 3)
    expect(decade(100000)).toBeCloseTo(1, 4)
    expect(decade(1e6)).toBeCloseTo(1, 5)
  })

  it('brings a tube amp’s two decades of roots into one picture, which is the whole point', () => {
    // The simulated tube amp: a 12 Hz coupling pole and a ~46 kHz transformer pole.
    const m = sPlaneMap('LogModulus', band(10, 96000))
    const linearFraction = 12 / 46000
    const logFraction = mappedRadius(m, 12) / mappedRadius(m, 46000)
    // Linear puts the low root at 0.026 % of the window — inside the origin marker.
    expect(linearFraction).toBeLessThan(0.0003)
    // Log-modulus puts it at ~9 %, i.e. visibly off the origin and separated from the high root.
    expect(logFraction).toBeGreaterThan(0.05)
    expect(logFraction / linearFraction).toBeGreaterThan(100)
  })
})

describe('surfaceViewport', () => {
  const roots = (): RootPoint[] => rootPoints(singularity({ freqHz: 2000, zeta: 0.309, isPair: true }), 'pole')

  it('is square and centred on the origin', () => {
    const v = surfaceViewport(linear, roots(), band(20, 20000))
    expect(v.xMin).toBe(-v.xMax)
    expect(v.yMin).toBe(-v.yMax)
    expect(v.xMax).toBe(v.yMax)
  })

  it('contains the top of the measured band, which poleZeroViewport deliberately does not', () => {
    // A 2 kHz DUT swept to 96 kHz: the roots alone would fit a 2.5 kHz window.
    const v = surfaceViewport(linear, roots(), band(10, 96000))
    expect(v.yMax).toBeGreaterThanOrEqual(96000)
  })

  it('contains every root', () => {
    const v = surfaceViewport(log, roots(), band(10, 96000))
    for (const root of roots()) {
      const p = toMapped(log, root.re, root.im)
      expect(Math.abs(p.x)).toBeLessThanOrEqual(v.xMax)
      expect(Math.abs(p.y)).toBeLessThanOrEqual(v.yMax)
    }
  })

  it('never collapses with no roots and no band', () => {
    expect(surfaceViewport(linear, [], null).xMax).toBeCloseTo(MIN_ROOT_EXTENT_HZ * 1.25, 6)
    expect(surfaceViewport(log, [], null).xMax).toBeGreaterThan(0)
  })
})

describe('axisTickHz', () => {
  it('returns 1/2/5 × a power of ten, inside the window', () => {
    const m = sPlaneMap('LogModulus', band(10, 100000))
    const extent = mappedRadius(m, 100000)
    const ticks = axisTickHz(m, extent)
    expect(ticks.length).toBeGreaterThan(3)
    for (const hz of ticks) {
      expect(hz).toBeLessThanOrEqual(plainRadius(m, extent) + 1e-6)
      const mantissa = hz / Math.pow(10, Math.floor(Math.log10(hz)))
      expect([1, 2, 5]).toContain(Math.round(mantissa))
    }
  })

  it('is ascending and capped, so six decades do not paint a solid band of labels', () => {
    const m = sPlaneMap('LogModulus', band(0.1, 1e6))
    const ticks = axisTickHz(m, mappedRadius(m, 1e6))
    expect(ticks.length).toBeLessThanOrEqual(12)
    for (let i = 1; i < ticks.length; i++) expect(ticks[i]!).toBeGreaterThan(ticks[i - 1]!)
  })

  it('gives nothing for a degenerate window', () => {
    expect(axisTickHz(log, 0)).toEqual([])
    expect(axisTickHz(log, Number.NaN)).toEqual([])
    expect(axisTickHz(log, -1)).toEqual([])
  })
})

describe('mappedWindow', () => {
  const linear = sPlaneMap('Linear', null)
  const log = sPlaneMap('LogModulus', { lowHz: 10, highHz: 100000 })

  it('is the identity under a linear radius, so the boxes are the coordinates', () => {
    expect(mappedWindow(linear, { xMinHz: -5000, xMaxHz: 1000, yMinHz: -200, yMaxHz: 200 })).toEqual({
      xMin: -5000,
      xMax: 1000,
      yMin: -200,
      yMax: 200,
    })
  })

  /**
   * The property that makes the boxes mean what they say: a bound typed in lands exactly where a
   * root at that frequency on the same axis lands — which is what `toMapped` does on an axis, since
   * the warp leaves the angle alone.
   */
  it('warps each bound to where a root at that frequency is drawn', () => {
    const v = mappedWindow(log, { xMinHz: -100000, xMaxHz: 100, yMinHz: -20000, yMaxHz: 20000 })

    expect(v.xMin).toBeCloseTo(toMapped(log, -100000, 0).x, 12)
    expect(v.xMax).toBeCloseTo(toMapped(log, 100, 0).x, 12)
    expect(v.yMax).toBeCloseTo(toMapped(log, 0, 20000).y, 12)
    expect(v.yMin).toBeCloseTo(-toMapped(log, 0, 20000).y, 12)
  })

  it('keeps the sign of a bound, so the origin stays inside a straddling window', () => {
    expect(signedMappedRadius(log, -1000)).toBeCloseTo(-mappedRadius(log, 1000), 12)
    expect(signedMappedRadius(log, 0)).toBe(0)
  })

  it('orders the bounds, whichever way round they were typed', () => {
    const v = mappedWindow(linear, { xMinHz: 1000, xMaxHz: -5000, yMinHz: 200, yMaxHz: -200 })

    expect(v.xMin).toBeLessThan(v.xMax)
    expect(v.yMin).toBeLessThan(v.yMax)
  })

  /** `fitTransform` divides by the span; a window closed to nothing paints an empty chart. */
  it('opens a degenerate axis to the minimum span rather than returning a zero-height window', () => {
    const v = mappedWindow(linear, { xMinHz: -100, xMaxHz: -100, yMinHz: 0, yMaxHz: 0 })

    expect(v.xMax - v.xMin).toBeCloseTo(MIN_WINDOW_SPAN_HZ, 12)
    expect(v.yMax - v.yMin).toBeCloseTo(MIN_WINDOW_SPAN_HZ, 12)
  })

  it('round-trips through windowHz, which is what seeds the boxes from the fit', () => {
    const asked = { xMinHz: -46000, xMaxHz: 120, yMinHz: -38000, yMaxHz: 38000 }
    const back = windowHz(log, mappedWindow(log, asked))

    expect(back.xMinHz).toBeCloseTo(asked.xMinHz, 6)
    expect(back.xMaxHz).toBeCloseTo(asked.xMaxHz, 9)
    expect(back.yMinHz).toBeCloseTo(asked.yMinHz, 6)
    expect(back.yMaxHz).toBeCloseTo(asked.yMaxHz, 6)
  })
})
