import { describe, expect, it } from 'vitest'
import {
  MESH_LINE_COLOR,
  NO_SURFACE_MESH,
  surfaceMeshIsEmpty,
  surfaceMeshIsWhole,
  surfaceMeshSegments,
  type SurfaceMesh,
} from './surfaceMesh'

function mesh(alongX: boolean, alongY: boolean): SurfaceMesh {
  return { alongX, alongY, color: MESH_LINE_COLOR }
}

describe('surfaceMeshSegments', () => {
  // The corners are [point, right, cross, top] = (x,y), (x+1,y), (x+1,y+1), (x,y+1). An edge runs
  // along x when its two corners share a y, and along y when they share an x — which is the whole
  // of the mapping, and the one thing that would silently swap the two checkboxes if it were wrong.
  it('rules the two edges at constant y when it rules along x', () => {
    expect(surfaceMeshSegments(mesh(true, false))).toEqual([
      [0, 1],
      [3, 2],
    ])
  })

  it('rules the two edges at constant x when it rules along y', () => {
    expect(surfaceMeshSegments(mesh(false, true))).toEqual([
      [0, 3],
      [1, 2],
    ])
  })

  it('rules every edge of the cell when it rules both ways', () => {
    const segments = surfaceMeshSegments(mesh(true, true))
    expect(segments).toHaveLength(4)
    // Each corner is an endpoint of exactly two of them, which is what makes it a closed outline.
    for (const corner of [0, 1, 2, 3]) {
      expect(segments.filter((s) => s.includes(corner))).toHaveLength(2)
    }
  })

  it('rules nothing when neither direction is asked for', () => {
    expect(surfaceMeshSegments(mesh(false, false))).toEqual([])
  })

  it('never names a corner the cell does not have, or joins one to itself', () => {
    for (const m of [mesh(true, false), mesh(false, true), mesh(true, true)]) {
      for (const [from, to] of surfaceMeshSegments(m)) {
        expect(from).toBeGreaterThanOrEqual(0)
        expect(to).toBeLessThanOrEqual(3)
        expect(from).not.toBe(to)
      }
    }
  })
})

describe('the two shortcuts the painter takes', () => {
  it('calls neither direction empty and both directions whole', () => {
    expect(surfaceMeshIsEmpty(mesh(false, false))).toBe(true)
    expect(surfaceMeshIsWhole(mesh(false, false))).toBe(false)
    expect(surfaceMeshIsEmpty(mesh(true, true))).toBe(false)
    expect(surfaceMeshIsWhole(mesh(true, true))).toBe(true)
  })

  it('calls one direction neither, because that is the case needing a pass of its own', () => {
    for (const m of [mesh(true, false), mesh(false, true)]) {
      expect(surfaceMeshIsEmpty(m)).toBe(false)
      expect(surfaceMeshIsWhole(m)).toBe(false)
    }
  })
})

describe('the mesh ink', () => {
  // It is drawn on the palette rather than on the page, so it does not follow the theme — the rule
  // DecaySurfaceChart's filled stack has carried since 2026-10-25, and now one constant for both.
  it('is white, and is what a chart with no mesh still carries', () => {
    expect(MESH_LINE_COLOR).toBe('#ffffff')
    expect(NO_SURFACE_MESH.color).toBe(MESH_LINE_COLOR)
    expect(surfaceMeshIsEmpty(NO_SURFACE_MESH)).toBe(true)
  })
})
