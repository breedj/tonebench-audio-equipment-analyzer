import { describe, expect, it } from 'vitest'
import { fitTransform, niceTicks, thinPositions, type Viewport } from './planeTransform'

const NO_INSETS = { left: 0, right: 0, top: 0, bottom: 0 }
const UNIT: Viewport = { xMin: -1, xMax: 1, yMin: -1, yMax: 1 }

describe('fitTransform', () => {
  it('maps the viewport corners onto the plot area, y growing upward', () => {
    const t = fitTransform(UNIT, 200, 100, NO_INSETS, false)

    expect(t.toScreenX(-1)).toBeCloseTo(0, 9)
    expect(t.toScreenX(1)).toBeCloseTo(200, 9)
    // y is inverted: the top of the window is pixel 0.
    expect(t.toScreenY(1)).toBeCloseTo(0, 9)
    expect(t.toScreenY(-1)).toBeCloseTo(100, 9)
    expect(t.toScreenX(0)).toBeCloseTo(100, 9)
    expect(t.toScreenY(0)).toBeCloseTo(50, 9)
  })

  it('round-trips world to screen and back', () => {
    const t = fitTransform({ xMin: -3, xMax: 5, yMin: -20, yMax: 40 }, 640, 480, NO_INSETS, false)

    for (const [x, y] of [[-3, -20], [0, 0], [2.5, 17], [5, 40]]) {
      expect(t.toWorldX(t.toScreenX(x))).toBeCloseTo(x, 9)
      expect(t.toWorldY(t.toScreenY(y))).toBeCloseTo(y, 9)
    }
  })

  it('honours insets', () => {
    const t = fitTransform(UNIT, 220, 140, { left: 10, right: 10, top: 20, bottom: 20 }, false)

    expect(t.toScreenX(-1)).toBeCloseTo(10, 9)
    expect(t.toScreenX(1)).toBeCloseTo(210, 9)
    expect(t.toScreenY(1)).toBeCloseTo(20, 9)
    expect(t.toScreenY(-1)).toBeCloseTo(120, 9)
  })

  describe('equal aspect', () => {
    /**
     * The reason this mode exists: on a Nyquist plot the entire reading is how close the contour
     * passes to −1, and unequal pixels-per-unit misstate that distance with nothing on screen to
     * say so. A circle must come out a circle on any canvas shape.
     */
    it('gives both axes the same pixels per unit on a wide canvas', () => {
      const t = fitTransform(UNIT, 400, 100, NO_INSETS, true)

      expect(t.scaleX).toBeCloseTo(t.scaleY, 9)
      // The tighter axis is the one that fits exactly; here that is y.
      expect(t.scaleY).toBeCloseTo(50, 9)
    })

    it('gives both axes the same pixels per unit on a tall canvas', () => {
      const t = fitTransform(UNIT, 100, 400, NO_INSETS, true)

      expect(t.scaleX).toBeCloseTo(t.scaleY, 9)
      expect(t.scaleX).toBeCloseTo(50, 9)
    })

    /**
     * Letterboxing widens the window; it must never crop it. A Nyquist view that quietly dropped
     * part of the requested range would hide exactly the excursion the user zoomed out to find.
     */
    it('widens the roomier axis rather than cropping the requested one', () => {
      const t = fitTransform(UNIT, 400, 100, NO_INSETS, true)

      expect(t.viewport.xMin).toBeLessThanOrEqual(-1)
      expect(t.viewport.xMax).toBeGreaterThanOrEqual(1)
      expect(t.viewport.yMin).toBeCloseTo(-1, 9)
      expect(t.viewport.yMax).toBeCloseTo(1, 9)
      expect(t.viewport.xMin).toBeCloseTo(-4, 9)
      expect(t.viewport.xMax).toBeCloseTo(4, 9)
    })

    it('keeps the requested window centred', () => {
      const t = fitTransform({ xMin: 0, xMax: 2, yMin: 10, yMax: 12 }, 400, 100, NO_INSETS, true)

      expect((t.viewport.xMin + t.viewport.xMax) / 2).toBeCloseTo(1, 9)
      expect((t.viewport.yMin + t.viewport.yMax) / 2).toBeCloseTo(11, 9)
    })
  })

  it('does not divide by zero on a degenerate viewport', () => {
    const t = fitTransform({ xMin: 1, xMax: 1, yMin: 2, yMax: 2 }, 100, 100, NO_INSETS, false)

    expect(Number.isFinite(t.toScreenX(1))).toBe(true)
    expect(Number.isFinite(t.toScreenY(2))).toBe(true)
  })
})

describe('niceTicks', () => {
  it('picks 1/2/5 steps', () => {
    expect(niceTicks(0, 10, 5)).toEqual([0, 2, 4, 6, 8, 10])
    expect(niceTicks(-1, 1, 4)).toEqual([-1, -0.5, 0, 0.5, 1])
  })

  it('renders zero as zero rather than as float dust', () => {
    expect(niceTicks(-0.3, 0.3, 6)).toContain(0)
    for (const tick of niceTicks(-0.3, 0.3, 6)) {
      expect(String(tick)).not.toMatch(/0000000|9999999/)
    }
  })

  it('returns nothing for a degenerate span', () => {
    expect(niceTicks(1, 1, 5)).toEqual([])
    expect(niceTicks(0, Number.NaN, 5)).toEqual([])
  })
})

describe('thinPositions', () => {
  it('keeps the first of a crowd and drops the rest', () => {
    const kept = thinPositions(
      [
        { at: 0, label: '0' },
        { at: 3, label: 'a' },
        { at: 40, label: 'b' },
        { at: 45, label: 'c' },
        { at: 90, label: 'd' },
      ],
      20,
    )
    expect(kept.map((k) => k.label)).toEqual(['0', 'b', 'd'])
  })

  it('treats order as priority, so a caller can protect the label that matters', () => {
    // Zero passed first survives; passed last it is the one dropped.
    expect(thinPositions([{ at: 0 }, { at: 2 }], 10).map((k) => k.at)).toEqual([0])
    expect(thinPositions([{ at: 2 }, { at: 0 }], 10).map((k) => k.at)).toEqual([2])
  })

  it('keeps everything when nothing collides, and ignores a non-finite position', () => {
    expect(thinPositions([{ at: 0 }, { at: 50 }, { at: 100 }], 20)).toHaveLength(3)
    expect(thinPositions([{ at: 0 }, { at: Number.NaN }, { at: 50 }], 20)).toHaveLength(2)
  })

  it('handles an empty list', () => {
    expect(thinPositions([], 10)).toEqual([])
  })
})
