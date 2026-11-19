import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  SCOPE_TOGGLE_ICONS,
  TOGGLE_DOT_RADIUS,
  TOGGLE_ICON_SIZE,
  scopeToggleIcon,
  type ScopeToggleShape,
} from './displayIcons'

/** Every x and y a shape puts ink at, a marker's own radius included. */
function extent(shape: ScopeToggleShape): { xs: number[]; ys: number[] } {
  if (shape.t === 'dot') {
    const r = shape.r ?? TOGGLE_DOT_RADIUS
    return { xs: [shape.x - r, shape.x + r], ys: [shape.y - r, shape.y + r] }
  }

  // Every number in the path data, as alternating x and y — true of these, which are written as
  // absolute moves, curves and lines, with H and V pairs and one relative run.
  const numbers = [...shape.d.matchAll(/-?\d+(?:\.\d+)?/g)].map((m) => Number(m[0]))
  const xs: number[] = []
  const ys: number[] = []

  if (/[hv]/i.test(shape.d) || /\sl/.test(shape.d)) {
    // H, V and relative runs are walked rather than read off, since a lone number in them is one
    // coordinate and the other is carried over.
    let x = 0
    let y = 0
    for (const token of shape.d.match(/[A-Za-z][^A-Za-z]*/g) ?? []) {
      const op = token[0]
      const n = [...token.matchAll(/-?\d+(?:\.\d+)?/g)].map((m) => Number(m[0]))
      if (op === 'M' || op === 'L') {
        for (let i = 0; i + 1 < n.length; i += 2) {
          x = n[i]
          y = n[i + 1]
          xs.push(x)
          ys.push(y)
        }
      } else if (op === 'l') {
        for (let i = 0; i + 1 < n.length; i += 2) {
          x += n[i]
          y += n[i + 1]
          xs.push(x)
          ys.push(y)
        }
      } else if (op === 'H') {
        for (const v of n) {
          x = v
          xs.push(x)
          ys.push(y)
        }
      } else if (op === 'V') {
        for (const v of n) {
          y = v
          xs.push(x)
          ys.push(y)
        }
      } else if (op === 'C') {
        for (let i = 0; i + 1 < n.length; i += 2) {
          xs.push(n[i])
          ys.push(n[i + 1])
          x = n[i]
          y = n[i + 1]
        }
      }
    }
    return { xs, ys }
  }

  for (let i = 0; i + 1 < numbers.length; i += 2) {
    xs.push(numbers[i])
    ys.push(numbers[i + 1])
  }
  return { xs, ys }
}

describe('the display toggles’ figures', () => {
  it('draws every one inside its own box', () => {
    for (const [name, shapes] of Object.entries(SCOPE_TOGGLE_ICONS)) {
      expect(shapes.length, `${name} has no shapes`).toBeGreaterThan(0)
      for (const shape of shapes) {
        const { xs, ys } = extent(shape)
        for (const x of xs) {
          expect(x, `${name} puts ink at x=${x}`).toBeGreaterThanOrEqual(0)
          expect(x, `${name} puts ink at x=${x}`).toBeLessThanOrEqual(TOGGLE_ICON_SIZE)
        }
        for (const y of ys) {
          expect(y, `${name} puts ink at y=${y}`).toBeGreaterThanOrEqual(0)
          expect(y, `${name} puts ink at y=${y}`).toBeLessThanOrEqual(TOGGLE_ICON_SIZE)
        }
      }
    }
  })

  it('answers nothing for a name it does not have, rather than throwing', () => {
    expect(scopeToggleIcon('nonesuch')).toEqual([])
  })

  /**
   * The audit that matters: a button whose figure is missing draws an empty box, which looks
   * deliberate. The rail is a `.vue` against a real DOM and the suite runs on `environment: node`,
   * so this reads it as text — the technique the persistence and v-model audits already use.
   */
  it('has a figure for every name the rail asks for', () => {
    const rail = readFileSync(
      resolve(import.meta.dirname, '..', 'components/ScopeDisplayPanel.vue'),
      'utf8',
    )

    const asked = new Set<string>()
    // The three Drawing modes, from the map the buttons read.
    for (const m of rail.matchAll(/^\s*(?:Linear|Sinc|Dots):\s*'([a-zA-Z]+)',/gm)) asked.add(m[1])
    // And the plain on/off toggles, from the list they are declared in.
    for (const m of rail.matchAll(/^\s*icon:\s*'([a-zA-Z]+)',/gm)) asked.add(m[1])

    expect(asked.size).toBe(Object.keys(SCOPE_TOGGLE_ICONS).length)
    for (const name of asked) {
      expect(scopeToggleIcon(name), `the rail asks for ${name}`).not.toEqual([])
    }
  })

  /**
   * And the other half: every toggle carries the name a label used to. These buttons have no text,
   * so the aria-label is the whole of what a screen reader gets.
   */
  it('gives every on/off toggle a name and a sentence', () => {
    const rail = readFileSync(
      resolve(import.meta.dirname, '..', 'components/ScopeDisplayPanel.vue'),
      'utf8',
    )

    const icons = [...rail.matchAll(/^\s*icon:\s*'([a-zA-Z]+)',/gm)].length
    const labels = [...rail.matchAll(/^\s*label:\s*'[^']+',/gm)].length
    const titles = [...rail.matchAll(/^\s*title:\s*$|^\s*title:\s*'[^']+',/gm)].length

    expect(icons).toBeGreaterThanOrEqual(4)
    expect(labels).toBe(icons)
    expect(titles).toBe(icons)
  })
})
