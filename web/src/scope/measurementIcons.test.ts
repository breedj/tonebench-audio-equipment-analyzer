import { describe, expect, it } from 'vitest'
import {
  ICON_HEIGHT,
  ICON_WIDTH,
  SCOPE_MEASUREMENT_ICONS,
  scopeMeasurementIcon,
  type IconShape,
} from './measurementIcons'

/**
 * How far a dimension line's end ticks reach either side of it — mirrored from the component, which
 * is where they are drawn.
 */
const TICK = 2

/** Every x and y a shape puts ink at, the dimension lines' own end ticks included. */
function extent(shape: IconShape): { xs: number[]; ys: number[] } {
  switch (shape.t) {
    case 'line':
      return { xs: [shape.x1, shape.x2], ys: [shape.y1, shape.y2] }
    case 'fill':
      return { xs: [shape.x, shape.x + shape.w], ys: [shape.y, shape.y + shape.h] }
    case 'spanH':
      return { xs: [shape.x1, shape.x2], ys: [shape.y - TICK, shape.y + TICK] }
    case 'spanV':
      return { xs: [shape.x - TICK, shape.x + TICK], ys: [shape.y1, shape.y2] }
    case 'path': {
      // Every number in the path data, as alternating x and y -- which is true of these, all of
      // which are written as absolute moves, curves, lines and H/V pairs.
      const numbers = [...shape.d.matchAll(/-?\d+(?:\.\d+)?/g)].map((m) => Number(m[0]))
      const xs: number[] = []
      const ys: number[] = []
      // H and V take a single coordinate, so the alternation has to follow the commands.
      let axis: 'x' | 'y' | 'alt' = 'alt'
      let next: 'x' | 'y' = 'x'
      for (const token of shape.d.match(/[A-Za-z]|-?\d+(?:\.\d+)?/g) ?? []) {
        if (/[A-Za-z]/.test(token)) {
          axis = token === 'H' ? 'x' : token === 'V' ? 'y' : 'alt'
          next = 'x'
          continue
        }
        const value = Number(token)
        const where = axis === 'alt' ? next : axis
        if (where === 'x') xs.push(value)
        else ys.push(value)
        if (axis === 'alt') next = next === 'x' ? 'y' : 'x'
      }
      void numbers
      return { xs, ys }
    }
  }
}

describe('SCOPE_MEASUREMENT_ICONS', () => {
  it('draws every figure inside the box it is given', () => {
    // The box is 24 x 16 and the SVG does not clip, so a mark outside it lands on the label beside
    // it -- which looks like a rendering fault and is invisible until somebody opens the picker.
    for (const [name, shapes] of Object.entries(SCOPE_MEASUREMENT_ICONS)) {
      for (const shape of shapes) {
        const { xs, ys } = extent(shape)
        for (const x of xs) {
          expect(x, `${name}: x ${x} is outside 0..${ICON_WIDTH}`).toBeGreaterThanOrEqual(0)
          expect(x, `${name}: x ${x} is outside 0..${ICON_WIDTH}`).toBeLessThanOrEqual(ICON_WIDTH)
        }
        for (const y of ys) {
          expect(y, `${name}: y ${y} is outside 0..${ICON_HEIGHT}`).toBeGreaterThanOrEqual(0)
          expect(y, `${name}: y ${y} is outside 0..${ICON_HEIGHT}`).toBeLessThanOrEqual(ICON_HEIGHT)
        }
      }
    }
  })

  it('gives every figure a mark as well as a backdrop', () => {
    // A figure that is only the faint trace is the same picture for every measurement in its
    // family, which is the one thing the figures exist not to be.
    for (const [name, shapes] of Object.entries(SCOPE_MEASUREMENT_ICONS)) {
      const marks = shapes.filter((s) => !('faint' in s && s.faint))
      expect(marks.length, `${name} is a backdrop and nothing else`).toBeGreaterThan(0)
    }
  })

  it('draws no two measurements the same way', () => {
    // The whole value of the figure is telling +Width from −Width at a glance.
    const seen = new Map<string, string>()
    for (const [name, shapes] of Object.entries(SCOPE_MEASUREMENT_ICONS)) {
      const key = JSON.stringify(shapes)
      const already = seen.get(key)
      expect(already, `${name} draws the same figure as ${already}`).toBeUndefined()
      seen.set(key, name)
    }
  })

  it('answers an empty figure for a name it does not know rather than throwing', () => {
    expect(scopeMeasurementIcon('nothing like this')).toEqual([])
  })
})
