import { describe, expect, it } from 'vitest'
import { toComplexCurve } from './complexPlane'
import { crossingFreqHz, stabilityMargins, wrap180 } from './margins'
import { logGrid, makeTrace, threePole, threePoleExpected } from './testTrace'

const WIDE = { fromHz: 0, toHz: Infinity }

/**
 * `K` is chosen so the two margins come out numerically distinct — GM is exactly 10.00 dB and PM is
 * around 51°. That distinctness is load-bearing: with GM ≈ PM a swapped return pair passes every
 * assertion below.
 */
const K = 8 / Math.pow(10, 0.5)
const FC = 100

const marginsOf = (trace: Parameters<typeof toComplexCurve>[0], opts = WIDE) =>
  stabilityMargins(toComplexCurve(trace, opts))

describe('wrap180', () => {
  it('folds whole turns away, keeping the half-open interval', () => {
    expect(wrap180(-129)).toBeCloseTo(-129, 12)
    expect(wrap180(-129 - 720)).toBeCloseTo(-129, 12)
    expect(wrap180(190)).toBeCloseTo(-170, 12)
    expect(wrap180(180)).toBe(180)
    expect(wrap180(-180)).toBe(180)
  })
})

/**
 * Asserted directly rather than through a sampled fixture, and that is the point.
 *
 * The first version of this suite checked the interpolation end-to-end on a 6 points/octave grid and
 * **passed with linear-f interpolation substituted in** — the two agree to 0.17% across a segment
 * that narrow, well inside the tolerance the closed-form margins need. Half way between 100 Hz and
 * 400 Hz is 200 Hz in log f and 250 Hz in linear f, which no tolerance can confuse.
 */
describe('crossingFreqHz', () => {
  it('interpolates in log frequency, not linear', () => {
    expect(crossingFreqHz(100, 400, 0.5)).toBeCloseTo(200, 9)
    expect(crossingFreqHz(100, 1000, 1 / 3)).toBeCloseTo(215.443469, 6)
  })

  it('returns the endpoints untouched', () => {
    expect(crossingFreqHz(100, 400, 0)).toBeCloseTo(100, 9)
    expect(crossingFreqHz(100, 400, 1)).toBeCloseTo(400, 9)
  })

  it('falls back to linear when a frequency is not positive', () => {
    expect(crossingFreqHz(0, 400, 0.5)).toBeCloseTo(200, 9)
  })
})

describe('stabilityMargins', () => {
  it('finds both margins of a three-pole loop on a dense grid', () => {
    const expected = threePoleExpected(K, FC)
    const trace = threePole(K, FC, logGrid(1, 10000, 48))

    const margins = marginsOf(trace)

    expect(margins.outOfBand).toBe(false)
    expect(margins.gainMarginDb).toBeCloseTo(10, 2)
    expect(margins.gainMarginDb!).toBeCloseTo(expected.gainMarginDb, 2)
    expect(margins.phaseMarginDeg!).toBeCloseTo(expected.phaseMarginDeg, 1)
    expect(margins.gainCrossoverHz!).toBeCloseTo(expected.gainCrossoverHz, 0)
    expect(margins.phaseCrossoverHz!).toBeCloseTo(expected.phaseCrossoverHz, 0)
  })

  it('still recovers the closed form on a coarse grid', () => {
    const expected = threePoleExpected(K, FC)
    const trace = threePole(K, FC, logGrid(1, 10000, 6))

    const margins = marginsOf(trace)

    expect(margins.gainCrossoverHz! / expected.gainCrossoverHz).toBeCloseTo(1, 2)
    expect(margins.phaseCrossoverHz! / expected.phaseCrossoverHz).toBeCloseTo(1, 2)
    expect(margins.gainMarginDb!).toBeCloseTo(expected.gainMarginDb, 1)
    expect(margins.phaseMarginDeg!).toBeCloseTo(expected.phaseMarginDeg, 0)
  })

  /**
   * The estimator's unwrapper starts at bin 0 and accumulates turns through the sub-band junk, so
   * in-band phase carries an arbitrary k·360°. Every margin must be identical under that shift — a
   * `180 + φ` implementation fails this instantly, and this is the bug the unwrapper genuinely
   * produces rather than a hypothetical one.
   */
  it('is invariant to a whole-turn offset on the whole curve', () => {
    const grid = logGrid(1, 10000, 24)
    const plain = marginsOf(threePole(K, FC, grid))

    const wound = threePole(K, FC, grid)
    const shifted = makeTrace({
      freqHz: wound.freqHz,
      magnitudeMaxDb: wound.magnitudeMaxDb,
      phaseDeg: wound.phaseDeg.map((p) => p - 720),
    })

    const woundMargins = marginsOf(shifted)

    expect(woundMargins.phaseMarginDeg!).toBeCloseTo(plain.phaseMarginDeg!, 9)
    expect(woundMargins.gainMarginDb!).toBeCloseTo(plain.gainMarginDb!, 9)
    expect(woundMargins.gainCrossoverHz!).toBeCloseTo(plain.gainCrossoverHz!, 9)
    expect(woundMargins.phaseCrossoverHz!).toBeCloseTo(plain.phaseCrossoverHz!, 9)
  })

  /**
   * Raising the loop gain by 6 dB moves both margins in a direction the closed form fixes exactly.
   * A sign error in either formula survives a single-point assertion but not this one.
   */
  it('moves both margins the right way when the loop gain rises 6 dB', () => {
    const grid = logGrid(1, 10000, 48)
    const before = marginsOf(threePole(K, FC, grid))
    const after = marginsOf(threePole(K * 2, FC, grid))

    expect(after.gainMarginDb!).toBeCloseTo(before.gainMarginDb! - 6.0206, 2)
    expect(after.gainCrossoverHz!).toBeGreaterThan(before.gainCrossoverHz!)
    expect(after.phaseMarginDeg!).toBeLessThan(before.phaseMarginDeg!)
    // The phase crossover is set by the poles alone, so more gain must not move it.
    expect(after.phaseCrossoverHz!).toBeCloseTo(before.phaseCrossoverHz!, 6)
  })

  it('reports every gain crossing and takes the worst phase margin', () => {
    // Ripple around unity: three 0 dB crossings, and the phase is worst at the last of them.
    const trace = makeTrace({
      freqHz: [100, 200, 400, 800, 1600],
      magnitudeMaxDb: [6, -2, 2, -6, -12],
      phaseDeg: [-90, -120, -150, -170, -200],
    })

    const margins = marginsOf(trace)

    expect(margins.gainCrossings).toHaveLength(3)
    // The worst crossing is the third; a first-crossing implementation reports about 60°.
    expect(margins.phaseMarginDeg!).toBeLessThan(40)
    expect(margins.gainCrossoverHz!).toBeGreaterThan(400)
  })

  it('says out-of-band rather than zero when nothing crosses', () => {
    const trace = makeTrace({
      freqHz: [10, 100, 1000],
      magnitudeMaxDb: [-20, -30, -40],
      phaseDeg: [-10, -20, -30],
    })

    const margins = marginsOf(trace)

    expect(margins.outOfBand).toBe(true)
    expect(margins.phaseMarginDeg).toBeNull()
    expect(margins.gainMarginDb).toBeNull()
    expect(margins.gainCrossoverHz).toBeNull()
    expect(margins.phaseCrossoverHz).toBeNull()
  })

  /**
   * The window has to be applied before the search, not after: a crossing outside the plotted band
   * is not a crossing this measurement can speak for.
   */
  it('searches only inside the plotted window', () => {
    const trace = threePole(K, FC, logGrid(1, 10000, 24))
    const expected = threePoleExpected(K, FC)

    const inside = marginsOf(trace, { fromHz: 1, toHz: 10000 })
    const excluded = marginsOf(trace, { fromHz: 1, toHz: 20 })

    expect(inside.gainCrossoverHz!).toBeCloseTo(expected.gainCrossoverHz, 0)
    expect(excluded.outOfBand).toBe(true)
    expect(excluded.gainCrossoverHz).toBeNull()
  })

  it('has no margins to report when the run measured no phase', () => {
    const trace = makeTrace({
      freqHz: [100, 200, 400],
      magnitudeMaxDb: [6, 0, -6],
      phaseDeg: [0, 0, 0],
    })

    const margins = marginsOf(trace)

    // The magnitude does cross unity, so there is a gain crossing — but with a flat zero phase the
    // curve never approaches the critical point and there is no gain margin to quote.
    expect(margins.gainMarginDb).toBeNull()
    expect(margins.phaseCrossoverHz).toBeNull()
    expect(margins.phaseMarginDeg).toBeCloseTo(180, 6)
  })

  /**
   * A conditionally-stable loop crosses the critical phase more than once, and the margin that
   * matters is the smallest.
   */
  it('takes the worst of several phase crossings', () => {
    const trace = makeTrace({
      freqHz: [100, 200, 400, 800],
      magnitudeMaxDb: [-20, -4, -30, -40],
      phaseDeg: [-170, -190, -170, -190],
    })

    const margins = marginsOf(trace)

    expect(margins.phaseCrossings.length).toBeGreaterThanOrEqual(3)
    expect(margins.gainMarginDb!).toBeLessThan(20)
  })
})
