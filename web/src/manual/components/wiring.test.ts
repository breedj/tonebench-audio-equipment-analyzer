import { describe, expect, it } from 'vitest'
import {
  CONNECTORS,
  CONNECTOR_IDS,
  CONNECTOR_RADIUS,
  DODGE,
  PANEL_HEIGHT,
  PANEL_WIDTH,
  cableRoute,
  linkRoute,
  orthPath,
} from './wiring'

/** The two breaks in the group outlines that hold the `Output` and `Input` silkscreen. */
const LABEL_BREAKS: readonly (readonly [number, number])[] = [
  [278, 338],
  [474, 528],
]

describe('the connector table', () => {
  it('places all eight BNCs inside the panel', () => {
    expect(CONNECTOR_IDS).toHaveLength(8)
    for (const id of CONNECTOR_IDS) {
      const p = CONNECTORS[id]
      expect(p.x).toBeGreaterThan(0)
      expect(p.x).toBeLessThan(PANEL_WIDTH)
      expect(p.y).toBeGreaterThan(0)
      expect(p.y).toBeLessThan(PANEL_HEIGHT)
    }
  })

  it('keeps the outputs left of the inputs', () => {
    const outputs = CONNECTOR_IDS.filter((id) => id.startsWith('out'))
    const inputs = CONNECTOR_IDS.filter((id) => id.startsWith('in'))
    const rightmostOutput = Math.max(...outputs.map((id) => CONNECTORS[id].x))
    const leftmostInput = Math.min(...inputs.map((id) => CONNECTORS[id].x))
    expect(rightmostOutput).toBeLessThan(leftmostInput)
  })
})

describe('the dodge off the panel', () => {
  it('takes every + cable clear of the − connector under it', () => {
    for (const id of CONNECTOR_IDS.filter((c) => c.endsWith('+'))) {
      const partner = CONNECTORS[id.replace('+', '-') as keyof typeof CONNECTORS]
      const runX = CONNECTORS[id].x + DODGE[id]
      expect(Math.abs(runX - partner.x)).toBeGreaterThan(CONNECTOR_RADIUS)
    }
  })

  it('does not run a cable down through a group label', () => {
    for (const id of CONNECTOR_IDS) {
      const runX = CONNECTORS[id].x + DODGE[id]
      for (const [from, to] of LABEL_BREAKS) {
        expect(runX > from && runX < to).toBe(false)
      }
    }
  })

  it('leaves the bottom row alone — nothing sits under it', () => {
    for (const id of CONNECTOR_IDS.filter((c) => c.endsWith('-'))) expect(DODGE[id]).toBe(0)
  })
})

describe('cableRoute', () => {
  it('collapses to a straight drop when the target sits under the cable run', () => {
    const from = CONNECTORS['in-l+']
    const dodge = DODGE['in-l+']
    const d = orthPath(cableRoute(from, 300, { x: from.x + dodge, y: 300 }, dodge))
    // One corner out of the connector and nothing else: no lane jog at the far end.
    expect(d.match(/Q/g)).toHaveLength(2)
  })

  it('arrives from above by default and from the side when asked', () => {
    const to = { x: 120, y: 280 }
    const lane = cableRoute(CONNECTORS['in-l+'], 210, to, 0)
    const side = cableRoute(CONNECTORS['in-l+'], 210, to, 0, { arrive: 'side' })
    // Last leg vertical for a lane route, horizontal for a side one.
    expect(lane[lane.length - 2]!.x).toBe(to.x)
    expect(side[side.length - 2]!.y).toBe(to.y)
  })
})

describe('orthPath', () => {
  it('drops repeated and collinear waypoints rather than rounding a corner that is not there', () => {
    const straight = orthPath([
      { x: 0, y: 0 },
      { x: 0, y: 50 },
      { x: 0, y: 50 },
      { x: 0, y: 100 },
    ])
    expect(straight).toBe('M0.0,0.0L0.0,100.0')
  })

  it('rounds a real corner without overshooting a short segment', () => {
    // The corner radius is clamped to half of the shorter leg, so a 10-unit leg cannot be eaten.
    const d = orthPath([
      { x: 0, y: 0 },
      { x: 0, y: 10 },
      { x: 40, y: 10 },
    ])
    expect(d).toContain('Q')
    expect(d).toBe('M0.0,0.0L0.0,5.0Q0.0,10.0 5.0,10.0L40.0,10.0')
  })

  it('returns nothing for no points', () => {
    expect(orthPath([])).toBe('')
  })
})

describe('linkRoute', () => {
  it('goes down, across the lane and back up', () => {
    const a = CONNECTORS['out-l+']
    const b = CONNECTORS['in-l+']
    const route = linkRoute(a, b, 240, DODGE['out-l+'], DODGE['in-l+'])
    expect(route[0]).toEqual(a)
    expect(route[route.length - 1]).toEqual(b)
    // The two long verticals are the dodged ones, and both reach the lane.
    expect(route.filter((p) => p.y === 240).map((p) => p.x)).toEqual([
      a.x + DODGE['out-l+'],
      b.x + DODGE['in-l+'],
    ])
  })
})
