import { beforeAll, beforeEach, describe, expect, it } from 'vitest'
// eslint-disable-next-line @typescript-eslint/ban-ts-comment
// @ts-ignore -- the package's own declarations export `Graph3d` as `any`; see `graph3d.ts`.
import { Graph3d } from 'vis-graph3d/standalone'
import { patchSurfacePainting } from './graph3d'
import { MESH_LINE_COLOR, NO_SURFACE_MESH, type SurfaceMesh } from './surfaceMesh'

/*
 * How a surface cell is *stroked*, which is the whole of what the mesh change was about and the one
 * half of this chart that needs no canvas: the library's private painters are replaced with
 * recorders before `patchSurfacePainting` runs, so each wrapper delegates to a recorder and what is
 * under test is the wrapper's own decision rather than the library's drawing.
 *
 * The regression it exists for, reported 2026-11-01: `showSurfaceGrid` defaults to **true** in the
 * library, the chart had stopped passing it, and `strokeStyle = this.axisColor` then reached every
 * cell — so an unticked mesh drew a near-black grid and a ticked one drew white lines over it.
 * Nothing in 2010 client tests saw it, every one of them having checked the mesh's geometry rather
 * than what the painter does with the answer the library arrived at.
 */

const AXIS_INK = '#404040'
const FILL = '#e23900'

/** The corner shape the library hands `_polygon`, and all of it the patch reads. */
function corner(x: number, y: number, value: number) {
  return { screen: { x, y }, point: { value } }
}

/** A flat cell, so `surfaceQuadGradient` declines it and the fill stays the string it was given. */
const FLAT_CELL = [corner(0, 10, -6), corner(4, 10, -6), corner(4, 14, -6), corner(0, 14, -6)]

interface Segment {
  from: { x: number; y: number }
  to: { x: number; y: number }
  color: string
}

/** What the cell was filled and outlined with, and every line drawn over it afterwards. */
interface Painted {
  fill?: unknown
  stroke?: unknown
  segments: Segment[]
}

let painted: Painted = { segments: [] }

/** Records the pen, so a `lineTo` can be read back as the segment it drew. */
function stubContext() {
  let pen = { x: 0, y: 0 }
  const ctx = {
    strokeStyle: '' as string | CanvasGradient,
    fillStyle: '' as string | CanvasGradient,
    lineWidth: 1,
    beginPath() {},
    closePath() {},
    moveTo(x: number, y: number) {
      pen = { x, y }
    },
    lineTo(x: number, y: number) {
      painted.segments.push({ from: pen, to: { x, y }, color: String(ctx.strokeStyle) })
      pen = { x, y }
    },
    fill() {},
    stroke() {},
    createLinearGradient: () => ({ addColorStop() {} }),
  }
  return ctx
}

type Proto = Record<string, unknown>

function prototype(): Proto {
  return (Graph3d as unknown as { prototype: Proto }).prototype
}

/**
 * Paints one cell through both wrappers: the patched `_redrawSurfaceGraphPoint` marks it, the stub
 * standing in for the library's own body hands `_polygon` the fill and the stroke the library would
 * have chosen for the given `showSurfaceGrid`, and the patched `_polygon` decides what is drawn.
 */
function paintCell(mesh: SurfaceMesh | undefined, libraryGrid: boolean): Painted {
  painted = { segments: [] }
  // On the prototype, because the library's own body reaches `_polygon` through `this`.
  const chart = Object.assign(Object.create(prototype()), {
    valueRange: { min: -60 },
    scale: { value: 1 / 60 },
    axisColor: AXIS_INK,
    _surfaceMesh: mesh,
    // The brightness the real body passes to `_colormap`; the patch notes it in passing.
    _surfaceShade: 1,
    _libraryGrid: libraryGrid,
  })
  const drawCell = prototype()._redrawSurfaceGraphPoint as (
    this: typeof chart,
    ctx: unknown,
    point: unknown,
  ) => void
  drawCell.call(chart, stubContext(), FLAT_CELL)
  return painted
}

describe('a patched surface cell', () => {
  beforeAll(() => {
    const proto = prototype()
    // Must be in place *before* the patch, which captures what it wraps in a closure. Both are
    // real functions on the prototype, so the patch's own name check still passes.
    proto._polygon = function (
      _ctx: unknown,
      _points: unknown,
      fill?: unknown,
      stroke?: unknown,
    ) {
      painted.fill = fill
      painted.stroke = stroke
    }
    proto._redrawSurfaceGraphPoint = function (
      this: { _libraryGrid: boolean; _polygon: CallableFunction },
      ctx: unknown,
      points: unknown,
    ) {
      // What the library's own body does on its last two lines, and the only part that matters
      // here: it hands `_polygon` the axis ink under `showSurfaceGrid` and the fill without it.
      this._polygon(ctx, points, FILL, this._libraryGrid ? AXIS_INK : FILL)
    }
    patchSurfacePainting()
  })

  beforeEach(() => {
    painted = { segments: [] }
  })

  it('is never stroked in the axis ink, whatever the library decided', () => {
    // The report itself: with the library's grid on and no mesh wanted, every cell edge was ruled
    // in `axisColor`. The cell's own fill closes the hairline seams instead.
    const cell = paintCell(NO_SURFACE_MESH, true)
    expect(cell.stroke).not.toBe(AXIS_INK)
    expect(cell.stroke).toBe(FILL)
    expect(cell.segments).toEqual([])
  })

  it('draws no mesh at all when neither direction is wanted', () => {
    for (const libraryGrid of [false, true]) {
      const cell = paintCell(NO_SURFACE_MESH, libraryGrid)
      expect(cell.segments, `grid ${libraryGrid}`).toEqual([])
      expect(cell.stroke, `grid ${libraryGrid}`).toBe(FILL)
    }
  })

  it('draws none for a chart that was never given a mesh', () => {
    const cell = paintCell(undefined, true)
    expect(cell.segments).toEqual([])
    expect(cell.stroke).toBe(FILL)
  })

  it('takes the whole outline when both directions are wanted, and adds no pass of its own', () => {
    const cell = paintCell({ alongX: true, alongY: true, color: MESH_LINE_COLOR }, false)
    expect(cell.stroke).toBe(MESH_LINE_COLOR)
    expect(cell.segments).toEqual([])
  })

  it('rules one direction over the fill, in its own ink', () => {
    const cell = paintCell({ alongX: true, alongY: false, color: MESH_LINE_COLOR }, true)
    // The outline still closes the seams in the fill colour; the two chosen edges go over it.
    expect(cell.stroke).toBe(FILL)
    expect(cell.segments).toHaveLength(2)
    for (const segment of cell.segments) {
      expect(segment.color).toBe(MESH_LINE_COLOR)
      // `alongX` is the two edges at constant y — the pair running along the first floor axis.
      expect(segment.from.y).toBe(segment.to.y)
    }
    expect(cell.segments[0].from.y).not.toBe(cell.segments[1].from.y)
  })

  it('rules the other direction the other way', () => {
    const cell = paintCell({ alongX: false, alongY: true, color: MESH_LINE_COLOR }, false)
    expect(cell.segments).toHaveLength(2)
    for (const segment of cell.segments) expect(segment.from.x).toBe(segment.to.x)
    expect(cell.segments[0].from.x).not.toBe(cell.segments[1].from.x)
  })

  it('leaves anything that is not a surface cell alone', () => {
    // The axes, the legend and the tick marks go through `_polygon` too, and they are the one
    // thing that should still be drawn in the ink the library chose — mesh on the instance or not.
    const polygon = prototype()._polygon as (
      this: unknown,
      ctx: unknown,
      points: unknown,
      fill?: unknown,
      stroke?: unknown,
    ) => void
    polygon.call(
      { _surfaceMesh: { alongX: true, alongY: true, color: MESH_LINE_COLOR } },
      stubContext(),
      FLAT_CELL,
      '#ffffff',
      AXIS_INK,
    )
    expect(painted.stroke).toBe(AXIS_INK)
    expect(painted.segments).toEqual([])
  })
})
