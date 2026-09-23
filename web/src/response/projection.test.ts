import { describe, expect, it } from 'vitest'
import {
  MIN_DB_RADIUS,
  MIN_LINEAR_EXTENT,
  criticalPhases,
  MAX_CRITICAL_MARKERS,
  nicholsPoint,
  nyquistPoint,
  nyquistRadius,
  symmetricExtent,
} from './projection'
import type { ComplexPoint } from './complexPlane'

const at = (magDb: number, phaseDeg: number): ComplexPoint => ({
  freqHz: 1000,
  magDb,
  phaseDeg,
  re: 0,
  im: 0,
})

describe('nyquistRadius', () => {
  it('is the plain ratio under the linear map', () => {
    expect(nyquistRadius(0, 'Linear', -40).r).toBeCloseTo(1, 12)
    expect(nyquistRadius(20, 'Linear', -40).r).toBeCloseTo(10, 12)
    expect(nyquistRadius(-20, 'Linear', -40).r).toBeCloseTo(0.1, 12)
  })

  /**
   * The unit circle has to stay the unit circle under both maps, or the one decoration that says
   * where |G| = 1 stops meaning it — and with it every visual reading of the gain crossover.
   */
  it('puts 0 dB on the unit circle under both maps', () => {
    expect(nyquistRadius(0, 'Linear', -40).r).toBeCloseTo(1, 12)
    expect(nyquistRadius(0, 'Db', -40).r).toBeCloseTo(1, 12)
    expect(nyquistRadius(0, 'Db', -80).r).toBeCloseTo(1, 12)
  })

  it('maps the floor toward the origin and keeps rising above 0 dB', () => {
    expect(nyquistRadius(-20, 'Db', -40).r).toBeCloseTo(0.5, 12)
    expect(nyquistRadius(40, 'Db', -40).r).toBeCloseTo(2, 12)
  })

  it('is monotone in magnitude under the dB map', () => {
    let previous = -Infinity
    for (let db = -60; db <= 60; db += 3) {
      const { r } = nyquistRadius(db, 'Db', -40)
      expect(r).toBeGreaterThanOrEqual(previous)
      previous = r
    }
  })

  /**
   * Clamped to a small positive radius rather than to zero. Everything below the floor landing
   * exactly on the origin would change how many times the contour encircles −1, which is the single
   * thing the plot is for.
   */
  it('clamps below the floor without collapsing onto the origin', () => {
    const deep = nyquistRadius(-200, 'Db', -40)

    expect(deep.r).toBe(MIN_DB_RADIUS)
    expect(deep.r).toBeGreaterThan(0)
    expect(deep.clamped).toBe(true)
    expect(nyquistRadius(-20, 'Db', -40).clamped).toBe(false)
  })

  it('falls back to linear rather than inverting when the floor is not below 0 dB', () => {
    expect(nyquistRadius(6, 'Db', 0).r).toBeCloseTo(dbRatio(6), 12)
    expect(nyquistRadius(6, 'Db', 10).r).toBeCloseTo(dbRatio(6), 12)
  })
})

function dbRatio(db: number): number {
  return Math.pow(10, db / 20)
}

describe('nyquistPoint', () => {
  it('places the critical point at angle 180 on the unit circle', () => {
    const point = nyquistPoint(at(0, 180), 'Linear', -40)

    expect(point.x).toBeCloseTo(-1, 9)
    expect(point.y).toBeCloseTo(0, 9)
  })

  it('keeps the angle when the radius map changes', () => {
    const linear = nyquistPoint(at(-20, -135), 'Linear', -40)
    const db = nyquistPoint(at(-20, -135), 'Db', -40)

    expect(Math.atan2(db.y, db.x)).toBeCloseTo(Math.atan2(linear.y, linear.x), 9)
    expect(Math.hypot(db.x, db.y)).toBeCloseTo(0.5, 9)
    expect(Math.hypot(linear.x, linear.y)).toBeCloseTo(0.1, 9)
  })
})

describe('nicholsPoint', () => {
  it('is phase against gain, untransformed', () => {
    expect(nicholsPoint(at(-6, -200))).toEqual({ x: -200, y: -6, clamped: false })
  })
})

describe('criticalPhases', () => {
  /**
   * The phase axis is unwrapped, so a three-pole loop is already past −270°. A chart that drew only
   * −180° would put its critical marker outside the region the curve occupies.
   */
  it('marks every turn inside the window, not just minus 180', () => {
    expect(criticalPhases(-400, 0)).toEqual([-180])
    expect(criticalPhases(-1000, 100)).toEqual([-900, -540, -180])
    expect(criticalPhases(-560, -100)).toEqual([-540, -180])
  })

  it('returns nothing when no turn falls inside the window', () => {
    expect(criticalPhases(-170, -10)).toEqual([])
  })

  it('draws none at all once there are too many turns to mean anything', () => {
    expect(criticalPhases(-360 * MAX_CRITICAL_MARKERS + 10, 0)).toHaveLength(MAX_CRITICAL_MARKERS)
    expect(criticalPhases(-360 * (MAX_CRITICAL_MARKERS + 1), 0)).toEqual([])
  })
})

describe('symmetricExtent', () => {
  const at = (x: number, y: number) => ({ x, y, clamped: false })

  /**
   * The regression this exists for. A 26.8 dB amplifier measured input-to-output sits at |G| = 21.8,
   * and the window used to be pinned at ±2 — so every point was clipped and the chart drew nothing
   * but axes. Reported from real hardware; the simulator's unity loopback could never have shown it,
   * because |G| = 1 fits inside ±2.
   */
  it('grows to contain a curve far outside the floor', () => {
    const extent = symmetricExtent([at(21.8, 0), at(-15, 12)], MIN_LINEAR_EXTENT)

    expect(extent).toBeGreaterThan(21.8)
    expect(extent).toBeLessThan(21.8 * 1.1)
  })

  /**
   * The other half: a curve that lives near the unit circle must not be zoomed in so far that the
   * −1 marker and the unit circle fall off, which is what fitting alone would do.
   */
  it('never shrinks below the floor', () => {
    expect(symmetricExtent([at(0.2, -0.1)], MIN_LINEAR_EXTENT)).toBe(MIN_LINEAR_EXTENT)
    expect(symmetricExtent([], MIN_LINEAR_EXTENT)).toBe(MIN_LINEAR_EXTENT)
  })

  it('measures both axes and both signs', () => {
    expect(symmetricExtent([at(-30, 0)], MIN_LINEAR_EXTENT)).toBeGreaterThan(30)
    expect(symmetricExtent([at(0, -30)], MIN_LINEAR_EXTENT)).toBeGreaterThan(30)
  })

  it('ignores non-finite points rather than blowing the window to Infinity', () => {
    const extent = symmetricExtent([at(3, 0), at(Number.NaN, 0), at(0, Infinity)], MIN_LINEAR_EXTENT)

    expect(Number.isFinite(extent)).toBe(true)
    expect(extent).toBeGreaterThan(3)
    expect(extent).toBeLessThan(4)
  })

  /**
   * Under the dB map a huge |G| is already compressed to a small radius, so the window must be
   * measured in plane coordinates — not from the magnitudes the map was applied to.
   */
  it('follows the dB radius map rather than the raw magnitude', () => {
    const huge = { freqHz: 1000, magDb: 60, phaseDeg: 0, re: 0, im: 0 }
    const compressed = nyquistPoint(huge, 'Db', -40)

    // 60 dB against a −40 dB floor is r = (60 + 40)/40 = 2.5, against |G| = 1000 under the linear
    // map: the same curve, two windows nearly three orders of magnitude apart.
    expect(symmetricExtent([compressed], MIN_LINEAR_EXTENT)).toBeCloseTo(2.625, 6)
    expect(symmetricExtent([nyquistPoint(huge, 'Linear', -40)], MIN_LINEAR_EXTENT)).toBeGreaterThan(900)
  })
})
