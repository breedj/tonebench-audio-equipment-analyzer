/**
 * Geometry for the manual's wiring diagrams.
 *
 * The diagrams draw the QA403's own front panel and then run cables off it, so every page has to
 * agree about where the eight BNCs are. That table lives here rather than in the component, with
 * the routing helpers beside it, because a `.vue` file is not testable under the node environment
 * vitest runs in — the same reasoning as measurements/reorder.ts.
 *
 * The coordinate system is the figure's viewBox: 640 units wide, the panel occupying the top
 * PANEL_HEIGHT of it, and the wiring drawn underneath. 640 is the panel photo's 2000 px scaled by
 * 0.32, which is also where the connector centres come from, so the drawing stays in proportion
 * with the real thing.
 */

export interface Pt {
  x: number
  y: number
}

export const PANEL_WIDTH = 640
export const PANEL_HEIGHT = 150

/** The eight BNCs, by the name the silkscreen gives them. */
export const CONNECTORS = {
  'out-l+': { x: 263, y: 40 },
  'out-l-': { x: 263, y: 99 },
  'out-r+': { x: 353, y: 40 },
  'out-r-': { x: 353, y: 99 },
  'in-l+': { x: 466, y: 40 },
  'in-l-': { x: 466, y: 99 },
  'in-r+': { x: 555, y: 40 },
  'in-r-': { x: 555, y: 99 },
} as const satisfies Record<string, Pt>

export type ConnectorId = keyof typeof CONNECTORS

export const CONNECTOR_IDS = Object.keys(CONNECTORS) as ConnectorId[]

/** Half-width of the hex nut, i.e. how much room a connector takes on the panel. */
export const CONNECTOR_RADIUS = 14

/** How far a cable runs straight out of a connector before it may turn. Clears the nut. */
const EXIT_LENGTH = 24

/**
 * How far a cable steps sideways on its way off the panel.
 *
 * A cable leaving a **+** connector runs straight down over the **−** connector of the same pair
 * and hides it — including the shorting plug the diagram is there to show. So the top row steps
 * aside by one connector radius and a half, which is the only clear lane there is: further out it
 * would cross the group's own label (`Output` and `Input` sit in a break in the outline, 278–338
 * and 474–528), and further in it would clip the − connector's nut. The bottom row has nothing
 * below it and goes straight down.
 */
export const DODGE: Record<ConnectorId, number> = {
  'out-l+': -21,
  'out-l-': 0,
  'out-r+': 21,
  'out-r-': 0,
  'in-l+': -21,
  'in-l-': 0,
  'in-r+': 21,
  'in-r-': 0,
}

export function pin(id: ConnectorId): Pt {
  return CONNECTORS[id]
}

/** The six points of the hex nut drawn around a connector, as an SVG polygon `points` string. */
export function hexPoints(centre: Pt, radius = CONNECTOR_RADIUS): string {
  const out: string[] = []
  for (let i = 0; i < 6; i++) {
    const a = (Math.PI / 180) * (30 + i * 60)
    out.push(`${(centre.x + radius * Math.cos(a)).toFixed(2)},${(centre.y + radius * Math.sin(a)).toFixed(2)}`)
  }
  return out.join(' ')
}

function same(a: Pt, b: Pt): boolean {
  return Math.abs(a.x - b.x) < 1e-6 && Math.abs(a.y - b.y) < 1e-6
}

function dedupe(points: readonly Pt[]): Pt[] {
  const out: Pt[] = []
  for (const p of points) {
    const last = out[out.length - 1]
    if (last !== undefined && same(last, p)) continue
    out.push(p)
  }
  // A waypoint that is collinear with its neighbours has no corner to round, and leaving it in
  // makes orthPath shorten two real segments for a bend that is not there.
  const trimmed: Pt[] = []
  for (let i = 0; i < out.length; i++) {
    const prev = trimmed[trimmed.length - 1]
    const next = out[i + 1]
    const cur = out[i]!
    if (prev !== undefined && next !== undefined) {
      const collinearX = Math.abs(prev.x - cur.x) < 1e-6 && Math.abs(cur.x - next.x) < 1e-6
      const collinearY = Math.abs(prev.y - cur.y) < 1e-6 && Math.abs(cur.y - next.y) < 1e-6
      if (collinearX || collinearY) continue
    }
    trimmed.push(cur)
  }
  return trimmed
}

function distance(a: Pt, b: Pt): number {
  return Math.hypot(b.x - a.x, b.y - a.y)
}

function towards(from: Pt, to: Pt, by: number): Pt {
  const d = distance(from, to)
  if (d === 0) return from
  return { x: from.x + ((to.x - from.x) * by) / d, y: from.y + ((to.y - from.y) * by) / d }
}

function fmt(p: Pt): string {
  return `${p.x.toFixed(1)},${p.y.toFixed(1)}`
}

/** A polyline through `points` with each corner rounded, as an SVG path. */
export function orthPath(points: readonly Pt[], radius = 9): string {
  const pts = dedupe(points)
  if (pts.length === 0) return ''
  if (pts.length === 1) return `M${fmt(pts[0]!)}`
  let d = `M${fmt(pts[0]!)}`
  for (let i = 1; i < pts.length - 1; i++) {
    const prev = pts[i - 1]!
    const cur = pts[i]!
    const next = pts[i + 1]!
    const r = Math.min(radius, distance(prev, cur) / 2, distance(cur, next) / 2)
    d += `L${fmt(towards(cur, prev, r))}Q${fmt(cur)} ${fmt(towards(cur, next, r))}`
  }
  d += `L${fmt(pts[pts.length - 1]!)}`
  return d
}

export interface CableOptions {
  /** Overrides the sideways step off the panel. 0 sends the cable straight down. */
  exitDx?: number
  /**
   * Where the cable arrives. `lane` (the default) drops onto the target from above — the target's
   * port is on its top edge. `side` comes in horizontally at the target's own height.
   */
  arrive?: 'lane' | 'side'
}

/**
 * The route almost every diagram needs: out of the connector, sideways clear of the pair's other
 * half, down to a horizontal lane, along it, and into the target.
 *
 * Give the target the same x as the cable's own vertical run and the lane disappears: the whole
 * route collapses to a straight drop, which is what most diagrams want and what `orthPath` folds
 * the duplicate waypoints into.
 */
export function cableRoute(from: Pt, laneY: number, to: Pt, exitDx: number, options: CableOptions = {}): Pt[] {
  const exitY = from.y + EXIT_LENGTH
  const lane = from.x + exitDx
  const start: Pt[] = [from, { x: from.x, y: exitY }, { x: lane, y: exitY }]
  if (options.arrive === 'side') return [...start, { x: lane, y: to.y }, to]
  return [...start, { x: lane, y: laneY }, { x: to.x, y: laneY }, to]
}

/** A cable between two connectors: out of one, along a lane, and back into the other. */
export function linkRoute(a: Pt, b: Pt, laneY: number, dxA: number, dxB: number): Pt[] {
  return [
    a,
    { x: a.x, y: a.y + EXIT_LENGTH },
    { x: a.x + dxA, y: a.y + EXIT_LENGTH },
    { x: a.x + dxA, y: laneY },
    { x: b.x + dxB, y: laneY },
    { x: b.x + dxB, y: b.y + EXIT_LENGTH },
    { x: b.x, y: b.y + EXIT_LENGTH },
    b,
  ]
}
