import { describe, expect, it } from 'vitest'
import {
  contourLevels,
  evaluateField,
  levelAtCell,
  marchingSquares,
  modelLevelDb,
  passbandReferenceDb,
  polyAbs2,
  surfaceWindow,
  type SurfaceField,
} from './surfaceField'
import { evaluateModel, fitRational, type RationalModel } from './rationalFit'
import { cAbs, cx, polyEval } from './poly'
import { sPlaneMap, toHz, type SPlaneMap } from './splaneScale'
import type { ComplexPoint } from './complexPlane'
import type { Viewport } from '../charts/planeTransform'

const linear: SPlaneMap = { scale: 'Linear', refHz: 1 }

/** A model built by hand rather than fitted, so the poles are known exactly. */
function model(num: number[], den: number[], omegaScale = 2 * Math.PI * 1000): RationalModel {
  return { num, den, omegaScale }
}

/** H(s) = 1/(1 + s/ω0) with ω0 the normalising scale, i.e. a real pole at exactly `omegaScale`. */
const onePoleModel = model([1], [1, 1])

/** A conjugate pair at |s| = omegaScale with ζ = 0.125, i.e. Q = 4. */
const resonantModel = model([1], [1, 2 * 0.125, 1])

function logGrid(fromHz: number, toHz: number, count: number): number[] {
  const ratio = Math.log(toHz / fromHz)
  return Array.from({ length: count }, (_, i) => fromHz * Math.exp((ratio * i) / (count - 1)))
}

/** Sample a designed analog response into the ComplexPoint curve `fitRational` consumes. */
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

describe('polyAbs2', () => {
  it('matches polyEval — the duplication that exists only for speed', () => {
    // Random coefficient sets and arguments: this is the test that stops the hand-rolled Horner
    // drifting away from the library one it copies.
    let seed = 12345
    const random = () => {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff
      return seed / 0x7fffffff - 0.5
    }
    for (let trial = 0; trial < 200; trial++) {
      const degree = 1 + (trial % 8)
      const coeffs = Array.from({ length: degree + 1 }, () => random() * 4)
      const sr = random() * 6
      const si = random() * 6
      const expected = cAbs(polyEval(coeffs, cx(sr, si))) ** 2
      const actual = polyAbs2(coeffs, sr, si)
      expect(Math.abs(actual - expected)).toBeLessThan(1e-12 * Math.max(1, Math.abs(expected)))
    }
  })

  it('evaluates a constant and a monomial the obvious way', () => {
    expect(polyAbs2([3], 5, 7)).toBeCloseTo(9, 12)
    // P(s) = s at s = 3 + 4j: |s|² = 25.
    expect(polyAbs2([0, 1], 3, 4)).toBeCloseTo(25, 12)
  })
})

describe('modelLevelDb', () => {
  it('IS the Bode magnitude on the jω axis — the claim the whole view rests on', () => {
    for (const m of [onePoleModel, resonantModel]) {
      for (const f of logGrid(10, 100000, 40)) {
        const bode = 20 * Math.log10(cAbs(evaluateModel(m, f)))
        expect(modelLevelDb(m, 0, f)).toBeCloseTo(bode, 9)
      }
    }
  })

  it('is symmetric about the real axis, because the coefficients are real', () => {
    for (const f of [20, 1000, 47000]) {
      expect(modelLevelDb(resonantModel, -300, f)).toBeCloseTo(modelLevelDb(resonantModel, -300, -f), 12)
    }
  })

  it('rises without bound approaching a pole and collapses approaching a zero', () => {
    // onePoleModel's pole is at s = −omegaScale, i.e. σ/2π = −1000 Hz on the real axis.
    let previous = -Infinity
    for (const distance of [500, 100, 10, 1, 0.1]) {
      const level = modelLevelDb(onePoleModel, -1000 + distance, 0)
      expect(level).toBeGreaterThan(previous)
      previous = level
    }
    expect(modelLevelDb(onePoleModel, -1000, 0)).toBe(Number.POSITIVE_INFINITY)

    // A zero at the same place: H(s) = 1 + s/ω0.
    const zeroModel = model([1, 1], [1])
    let previousZero = Infinity
    for (const distance of [500, 100, 10, 1]) {
      const level = modelLevelDb(zeroModel, -1000 + distance, 0)
      expect(level).toBeLessThan(previousZero)
      previousZero = level
    }
  })

  it('carries the 2π exactly: a pole placed at f reads its maximum at f, not at 2πf or f/2π', () => {
    // The resonance is at |s| = omegaScale = 2π·1000, so the peak of |H(jω)| sits at ~1000 Hz.
    let best = -Infinity
    let bestHz = 0
    for (const f of logGrid(100, 10000, 400)) {
      const level = modelLevelDb(resonantModel, 0, f)
      if (level > best) {
        best = level
        bestHz = f
      }
    }
    expect(bestHz).toBeGreaterThan(950)
    expect(bestHz).toBeLessThan(1050)
  })
})

describe('passbandReferenceDb', () => {
  it('returns the flat gain of a flat model', () => {
    expect(passbandReferenceDb(model([4], [1]), { lowHz: 20, highHz: 20000 })).toBeCloseTo(20 * Math.log10(4), 9)
  })

  it('reads the in-band median, not the DC gain, on a model whose corner is below the band', () => {
    // A 1 kHz low pass measured from 10 kHz up: H(0) is 0 dB, but nothing in the band is near it.
    const band = { lowHz: 10000, highHz: 100000 }
    const reference = passbandReferenceDb(onePoleModel, band)
    const dcGainDb = 20 * Math.log10(cAbs(evaluateModel(onePoleModel, 0)))
    expect(dcGainDb).toBeCloseTo(0, 9)
    expect(reference).toBeLessThan(-18)
    // ...and it really is the median of the band, not its edge.
    expect(reference).toBeCloseTo(modelLevelDb(onePoleModel, 0, Math.sqrt(band.lowHz * band.highHz)), 0)
  })

  it('is not dragged by a resonance the way a peak or a mean of magnitudes would be', () => {
    // Q = 4 at 1 kHz inside a wide band: the peak is +12 dB, and a median must stay near the flat part.
    const band = { lowHz: 20, highHz: 20000 }
    const reference = passbandReferenceDb(resonantModel, band)
    let peak = -Infinity
    for (const f of logGrid(band.lowHz, band.highHz, 400)) peak = Math.max(peak, modelLevelDb(resonantModel, 0, f))
    expect(peak).toBeGreaterThan(11)
    expect(Math.abs(reference)).toBeLessThan(peak - 6)
  })

  it('survives a missing or degenerate band', () => {
    expect(Number.isFinite(passbandReferenceDb(onePoleModel, null))).toBe(true)
    expect(Number.isFinite(passbandReferenceDb(onePoleModel, { lowHz: 0, highHz: 0 }))).toBe(true)
  })
})

describe('surfaceWindow', () => {
  it('places the offsets around the reference and keeps top above bottom', () => {
    const w = surfaceWindow(26, 40, -60)
    expect(w).toEqual({ referenceDb: 26, topDb: 66, bottomDb: -34 })
    // Offsets given the wrong way round are still a usable window rather than an inverted one.
    expect(surfaceWindow(26, -60, 40)).toEqual({ referenceDb: 26, topDb: 66, bottomDb: -34 })
  })

  it('never collapses to zero span', () => {
    const w = surfaceWindow(0, 5, 5)
    expect(w.topDb).toBeGreaterThan(w.bottomDb)
  })
})

describe('contourLevels', () => {
  it('is anchored on the reference, sorted, and includes the −3 dB line', () => {
    const levels = contourLevels(surfaceWindow(0, 40, -60))
    expect(levels).toContain(0)
    expect(levels).toContain(-3)
    for (let i = 1; i < levels.length; i++) expect(levels[i]!).toBeGreaterThan(levels[i - 1]!)
  })

  it('keeps the line count under the cap however deep the window', () => {
    for (const depth of [10, 60, 120, 400]) {
      expect(contourLevels(surfaceWindow(0, 20, -depth)).length).toBeLessThanOrEqual(13)
    }
  })

  it('stays inside the window', () => {
    const w = surfaceWindow(26.58, 40, -60)
    for (const level of contourLevels(w)) {
      expect(level).toBeGreaterThanOrEqual(w.bottomDb - 1e-9)
      expect(level).toBeLessThanOrEqual(w.topDb + 1e-9)
    }
  })

  it('anchors on an amplifier’s own passband, not on round absolute numbers', () => {
    // A +26.58 dB stage: the contours are 26.58, 20.58, … not 26, 20, …
    expect(contourLevels(surfaceWindow(26.58, 12, -36))).toContain(26.58)
  })

  it('gives nothing for a degenerate window', () => {
    expect(contourLevels({ referenceDb: 0, topDb: 0, bottomDb: 0 })).toEqual([])
  })
})

describe('evaluateField', () => {
  const viewport: Viewport = { xMin: -2000, xMax: 2000, yMin: -2000, yMax: 2000 }

  it('puts row 0 at the TOP, in screen order rather than complex-plane order', () => {
    // A window covering only positive ω, two rows tall: row 0 must sample the HIGHER frequency.
    const field = evaluateField(onePoleModel, linear, { xMin: -1, xMax: 1, yMin: 0, yMax: 100 }, 1, 2)
    expect(levelAtCell(field, 0, 0)).toBeCloseTo(modelLevelDb(onePoleModel, 0, 75), 6)
    expect(levelAtCell(field, 0, 1)).toBeCloseTo(modelLevelDb(onePoleModel, 0, 25), 6)
    // A low pass falls with frequency, so the top row is the quieter one — get the row order wrong
    // and the whole surface is mirrored about the real axis.
    expect(levelAtCell(field, 0, 0)).toBeLessThan(levelAtCell(field, 0, 1))
  })

  it('agrees with per-point modelLevelDb at sampled cells, under both scales', () => {
    const cases: { map: SPlaneMap; v: Viewport }[] = [
      { map: linear, v: viewport },
      { map: sPlaneMap('LogModulus', { lowHz: 10, highHz: 100000 }), v: { xMin: -5, xMax: 5, yMin: -5, yMax: 5 } },
    ]
    for (const { map, v } of cases) {
      const cols = 16
      const rows = 12
      const field = evaluateField(resonantModel, map, v, cols, rows)
      for (const [col, row] of [
        [0, 0],
        [7, 5],
        [15, 11],
        [3, 9],
      ] as const) {
        const u = v.xMin + ((v.xMax - v.xMin) * (col + 0.5)) / cols
        const w = v.yMax - ((v.yMax - v.yMin) * (row + 0.5)) / rows
        const s = toHz(map, u, w)
        const expected = modelLevelDb(resonantModel, s.reHz, s.imHz)
        // Relative, because `levelDb` is a Float32Array on purpose — a 700×500 grid is 1.4 MB rather
        // than 2.8 — and float32's ~1.2e-7 precision is relative: the same rounding that is 1e-6 dB
        // at −16 dB is 1e-5 dB down in a stopband at −182. Either is orders of magnitude below
        // anything a contour or a colour could show.
        expect(Math.abs(levelAtCell(field, col, row) - expected)).toBeLessThan(1e-6 * Math.max(1, Math.abs(expected)))
      }
    }
  })

  it('samples cell centres, so nothing lands exactly on a pole by accident of the window', () => {
    // The window is centred on the origin with an even cell count, so no cell centre is at (0,0).
    const field = evaluateField(onePoleModel, linear, viewport, 8, 8)
    for (let i = 0; i < field.levelDb.length; i++) expect(Number.isFinite(field.levelDb[i]!)).toBe(true)
  })

  it('returns a usable field for a degenerate request', () => {
    const field = evaluateField(onePoleModel, linear, viewport, 0, 0)
    expect(field.cols).toBe(1)
    expect(field.rows).toBe(1)
    expect(field.levelDb).toHaveLength(1)
  })
})

describe('marchingSquares', () => {
  /** A cone: level falls off with distance from the grid centre, so contours are circles. */
  function cone(size: number): SurfaceField {
    const levelDb = new Float32Array(size * size)
    const c = (size - 1) / 2
    for (let row = 0; row < size; row++) {
      for (let col = 0; col < size; col++) {
        levelDb[row * size + col] = -Math.hypot(col - c, row - c)
      }
    }
    return { cols: size, rows: size, levelDb, viewport: { xMin: 0, xMax: size, yMin: 0, yMax: size } }
  }

  it('traces a circle at the right radius', () => {
    const size = 41
    const field = cone(size)
    const c = (size - 1) / 2
    const radius = 12
    const segments = marchingSquares(field, -radius)
    expect(segments.length).toBeGreaterThan(40)
    for (let i = 0; i < segments.length; i += 2) {
      const d = Math.hypot(segments[i]! - c, segments[i + 1]! - c)
      // Every endpoint within half a cell of the true iso-radius.
      expect(Math.abs(d - radius)).toBeLessThan(0.5)
    }
  })

  it('returns nothing for a level the grid never reaches', () => {
    const field = cone(21)
    expect(marchingSquares(field, 10)).toEqual([])
    expect(marchingSquares(field, -1000)).toEqual([])
  })

  it('emits whole segments — four numbers each', () => {
    expect(marchingSquares(cone(21), -5).length % 4).toBe(0)
  })

  it('resolves a saddle without crossing its two segments', () => {
    // Diagonal corners above the level: the classic ambiguous cell.
    const levelDb = Float32Array.from([1, -1, -1, 1])
    const field: SurfaceField = {
      cols: 2,
      rows: 2,
      levelDb,
      viewport: { xMin: 0, xMax: 2, yMin: 0, yMax: 2 },
    }
    const segments = marchingSquares(field, 0)
    expect(segments).toHaveLength(8)
    // The two segments must not intersect. With corners nw=1, ne=-1, se=-1, sw=1 the centre average
    // is 0, so the connection joins the two above-corners' own edges.
    const a = segments.slice(0, 4)
    const b = segments.slice(4, 8)
    const crosses = segmentsIntersect(a as number[], b as number[])
    expect(crosses).toBe(false)
  })

  it('closes a contour around a pole rather than leaving a gap at the overflow', () => {
    // A cell that overflowed to +Infinity counts as above the level, so the ring still closes.
    const size = 11
    const levelDb = new Float32Array(size * size)
    const c = (size - 1) / 2
    for (let row = 0; row < size; row++) {
      for (let col = 0; col < size; col++) {
        const d = Math.hypot(col - c, row - c)
        levelDb[row * size + col] = d === 0 ? Number.POSITIVE_INFINITY : -20 * Math.log10(d)
      }
    }
    const field: SurfaceField = { cols: size, rows: size, levelDb, viewport: { xMin: 0, xMax: size, yMin: 0, yMax: size } }
    const segments = marchingSquares(field, 0)
    expect(segments.length).toBeGreaterThan(0)
    for (let i = 0; i < segments.length; i++) expect(Number.isFinite(segments[i]!)).toBe(true)
  })

  it('honours a stride, sampling fewer cells', () => {
    const field = cone(41)
    const fine = marchingSquares(field, -12, 1)
    const coarse = marchingSquares(field, -12, 4)
    expect(coarse.length).toBeGreaterThan(0)
    expect(coarse.length).toBeLessThan(fine.length)
  })
})

/** Do two segments [x0,y0,x1,y1] properly cross? Used only to assert the saddle rule. */
function segmentsIntersect(a: number[], b: number[]): boolean {
  const orient = (px: number, py: number, qx: number, qy: number, rx: number, ry: number) =>
    Math.sign((qx - px) * (ry - py) - (qy - py) * (rx - px))
  const d1 = orient(a[0]!, a[1]!, a[2]!, a[3]!, b[0]!, b[1]!)
  const d2 = orient(a[0]!, a[1]!, a[2]!, a[3]!, b[2]!, b[3]!)
  const d3 = orient(b[0]!, b[1]!, b[2]!, b[3]!, a[0]!, a[1]!)
  const d4 = orient(b[0]!, b[1]!, b[2]!, b[3]!, a[2]!, a[3]!)
  return d1 !== d2 && d3 !== d4 && d1 !== 0 && d2 !== 0
}

describe('against a fitted model rather than a designed one', () => {
  it('reads a fitted one-pole low pass on the jω axis as the Bode magnitude', () => {
    const corner = 2000
    const h = (f: number) => {
      const x = f / corner
      const denom = 1 + x * x
      return { re: 1 / denom, im: -x / denom }
    }
    const fit = fitRational(sample(h, logGrid(20, 20000, 60)), 0, 1)
    expect(fit).not.toBeNull()
    for (const f of logGrid(50, 15000, 20)) {
      const bode = 20 * Math.log10(cAbs(evaluateModel(fit!.model, f)))
      expect(modelLevelDb(fit!.model, 0, f)).toBeCloseTo(bode, 9)
      // ...and that Bode magnitude really is the designed filter's.
      expect(bode).toBeCloseTo(20 * Math.log10(Math.hypot(h(f).re, h(f).im)), 3)
    }
  })

  it('places the fitted pole where the field blows up', () => {
    const corner = 2000
    const h = (f: number) => {
      const x = f / corner
      const denom = 1 + x * x
      return { re: 1 / denom, im: -x / denom }
    }
    const fit = fitRational(sample(h, logGrid(20, 20000, 60)), 0, 1)!
    const pole = fit.poles[0]!
    expect(pole.freqHz).toBeCloseTo(corner, 0)
    // On the negative real axis at the pole's own frequency the level must tower over the passband.
    const atPole = modelLevelDb(fit.model, -pole.freqHz * 0.999, 0)
    expect(atPole).toBeGreaterThan(40)
  })
})
