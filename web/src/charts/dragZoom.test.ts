import { describe, expect, it } from 'vitest'
import { dragZoomViewport, MIN_DRAG_PX } from './dragZoom'
import { fitTransform } from './planeTransform'

const transform = () =>
  fitTransform(
    { xMin: -100, xMax: 100, yMin: -100, yMax: 100 },
    400,
    400,
    { left: 0, right: 0, top: 0, bottom: 0 },
    true,
  )

describe('dragZoomViewport', () => {
  it('maps a box to the world window it covers', () => {
    const t = transform()
    const v = dragZoomViewport({ x: 100, y: 100 }, { x: 300, y: 300 }, t)!
    expect(v.xMin).toBeCloseTo(-50, 6)
    expect(v.xMax).toBeCloseTo(50, 6)
    expect(v.yMin).toBeCloseTo(-50, 6)
    expect(v.yMax).toBeCloseTo(50, 6)
  })

  it('gives the same window whichever corner the drag started from', () => {
    const t = transform()
    const forward = dragZoomViewport({ x: 100, y: 120 }, { x: 300, y: 320 }, t)!
    const backward = dragZoomViewport({ x: 300, y: 320 }, { x: 100, y: 120 }, t)!
    expect(backward).toEqual(forward)
    const mixed = dragZoomViewport({ x: 300, y: 120 }, { x: 100, y: 320 }, t)!
    expect(mixed).toEqual(forward)
  })

  it('always returns min below max', () => {
    const t = transform()
    for (const [a, b] of [
      [
        { x: 10, y: 10 },
        { x: 390, y: 390 },
      ],
      [
        { x: 390, y: 10 },
        { x: 10, y: 390 },
      ],
    ] as const) {
      const v = dragZoomViewport(a, b, t)!
      expect(v.xMin).toBeLessThan(v.xMax)
      expect(v.yMin).toBeLessThan(v.yMax)
    }
  })

  it('refuses a click, and a drag that is thin in either dimension alone', () => {
    const t = transform()
    expect(dragZoomViewport({ x: 100, y: 100 }, { x: 100, y: 100 }, t)).toBeNull()
    expect(dragZoomViewport({ x: 100, y: 100 }, { x: 104, y: 104 }, t)).toBeNull()
    // Wide but flat, and tall but narrow: both are refused, because either alone zooms to a sliver.
    expect(dragZoomViewport({ x: 100, y: 100 }, { x: 300, y: 104 }, t)).toBeNull()
    expect(dragZoomViewport({ x: 100, y: 100 }, { x: 104, y: 300 }, t)).toBeNull()
  })

  it('accepts a drag just past the threshold in both dimensions', () => {
    const t = transform()
    const past = MIN_DRAG_PX + 1
    expect(dragZoomViewport({ x: 100, y: 100 }, { x: 100 + past, y: 100 + past }, t)).not.toBeNull()
    // Exactly at the threshold is still a click — the comparison is strict.
    expect(dragZoomViewport({ x: 100, y: 100 }, { x: 100 + MIN_DRAG_PX, y: 100 + MIN_DRAG_PX }, t)).toBeNull()
  })

  it('honours a caller-supplied threshold', () => {
    const t = transform()
    expect(dragZoomViewport({ x: 100, y: 100 }, { x: 105, y: 105 }, t, 2)).not.toBeNull()
    expect(dragZoomViewport({ x: 100, y: 100 }, { x: 105, y: 105 }, t, 50)).toBeNull()
  })

  it('reproduces the arithmetic it was extracted from', () => {
    // The original, inline in ResponsePolarChart's onPointerUp: sorted world coordinates of the two
    // corners, gated on both deltas exceeding 8 px.
    const t = transform()
    const start = { x: 137, y: 211 }
    const end = { x: 332, y: 96 }
    const xs = [t.toWorldX(start.x), t.toWorldX(end.x)].sort((a, b) => a - b)
    const ys = [t.toWorldY(start.y), t.toWorldY(end.y)].sort((a, b) => a - b)
    expect(dragZoomViewport(start, end, t)).toEqual({ xMin: xs[0], xMax: xs[1], yMin: ys[0], yMax: ys[1] })
  })
})
