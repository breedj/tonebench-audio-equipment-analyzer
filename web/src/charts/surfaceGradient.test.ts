import { describe, expect, it } from 'vitest'
import {
  MIN_GRADIENT_EXTENT_PX,
  surfaceQuadGradient,
  type SurfaceQuadCorner,
} from './surfaceGradient'

function corner(x: number, y: number, value: number): SurfaceQuadCorner {
  return { screen: { x, y }, value }
}

/** A cell as the library hands one over: four corners, in its own order. */
function cell(values: [number, number, number, number], spread = 40): SurfaceQuadCorner[] {
  // Screen y follows the level, which is what the projection does — the level axis is near enough
  // vertical — so a cell spanning more decibels is a taller face.
  return values.map((v, i) => corner(100 + (i % 2) * 6, 200 - v * spread, v))
}

describe('a 3D surface cell s colour gradient', () => {
  it('runs from the cell s lowest corner to its highest', () => {
    const ends = surfaceQuadGradient(cell([-1, -3, -2, -0.5]))
    expect(ends).not.toBeNull()
    expect(ends?.fromValue).toBe(-3)
    expect(ends?.toValue).toBe(-0.5)
    // The ends are the corners themselves, so the gradient covers the face rather than a guess at
    // where it is.
    expect(ends?.from.y).toBeGreaterThan(ends?.to.y ?? 0)
  })

  it('does not care which order the corners arrive in', () => {
    const values: [number, number, number, number] = [-1, -3, -2, -0.5]
    const straight = surfaceQuadGradient(cell(values))
    const shuffled = surfaceQuadGradient([...cell(values)].reverse())
    expect(shuffled?.fromValue).toBe(straight?.fromValue)
    expect(shuffled?.toValue).toBe(straight?.toValue)
  })

  it('leaves a cell lying flat to the flat fill it already had', () => {
    // Most of a quiet surface. Two equal stops would cost a gradient and draw one colour anyway.
    expect(surfaceQuadGradient(cell([-12, -12, -12, -12]))).toBeNull()
  })

  it('leaves a sliver alone, which is what bounds the cost', () => {
    // A dense surface is mostly cells a pixel or two across, where a gradient cannot be told from
    // the flat fill it replaces — and there are tens of thousands of them.
    const sliver = [corner(10, 10, -5), corner(11, 10, -5.01), corner(10, 11, -5.02), corner(11, 11, -5.03)]
    expect(surfaceQuadGradient(sliver)).toBeNull()
    expect(surfaceQuadGradient(sliver, 0.5)).not.toBeNull()
  })

  it('measures that extent along the gradient, not down the screen', () => {
    // A cell can be wide and short — a steep part of the surface seen from almost side on — and
    // its colour still runs across it.
    const wide = [corner(0, 50, -5), corner(40, 50, -20), corner(0, 51, -6), corner(40, 51, -19)]
    const ends = surfaceQuadGradient(wide)
    expect(ends?.fromValue).toBe(-20)
    expect(ends?.to.x).toBe(0)
  })

  it('refuses a corner that is not a number rather than painting a guess', () => {
    expect(surfaceQuadGradient([...cell([-1, -3, -2, -0.5]).slice(0, 3), corner(1, 1, Number.NaN)])).toBeNull()
  })

  it('refuses an empty cell', () => {
    expect(surfaceQuadGradient([])).toBeNull()
  })

  it('has a minimum extent of more than one pixel', () => {
    // One-pixel cells are the common case on a dense surface, so a threshold of one would gradient
    // nearly all of them and buy nothing.
    expect(MIN_GRADIENT_EXTENT_PX).toBeGreaterThan(1)
  })
})
