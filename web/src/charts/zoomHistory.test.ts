import { describe, expect, it } from 'vitest'
import { ZOOM_HISTORY_LIMIT, useZoomHistory, type ZoomWindow } from './zoomHistory'

const w = (xMin: number, xMax: number, yMin = 0, yMax = 1): ZoomWindow => ({ xMin, xMax, yMin, yMax })

describe('the zoom history', () => {
  it('walks back through the windows a chart left, newest first', () => {
    const h = useZoomHistory()
    expect(h.canGoBack.value).toBe(false)

    h.push(w(10, 100))
    h.push(w(20, 50))
    expect(h.canGoBack.value).toBe(true)

    expect(h.back()).toEqual(w(20, 50))
    expect(h.back()).toEqual(w(10, 100))
    expect(h.back()).toBeNull()
    expect(h.canGoBack.value).toBe(false)
  })

  // The chart pushes from setSelect, which can fire twice for one window (a drag that only moved on
  // one axis, a rebuild that re-applies the same scales); a second identical step is not a step.
  it('does not remember the same window twice in a row', () => {
    const h = useZoomHistory()
    h.push(w(10, 100))
    h.push(w(10, 100))
    expect(h.depth.value).toBe(1)

    // Tolerantly: a window may have been through a log/linear round trip, as useZoomBounds has it.
    h.push(w(10 + 1e-9, 100))
    expect(h.depth.value).toBe(1)
  })

  it('refuses a window an unlaid-out scale produced', () => {
    const h = useZoomHistory()
    h.push(w(Number.NaN, 100))
    h.push({ xMin: 1, xMax: 2, yMin: Number.NaN, yMax: 3 })
    expect(h.canGoBack.value).toBe(false)
  })

  it('forgets the oldest once it is full, rather than growing without bound', () => {
    const h = useZoomHistory(3)
    h.push(w(1, 10))
    h.push(w(2, 10))
    h.push(w(3, 10))
    h.push(w(4, 10))
    expect(h.depth.value).toBe(3)
    expect(h.back()).toEqual(w(4, 10))
    expect(h.back()).toEqual(w(3, 10))
    expect(h.back()).toEqual(w(2, 10))
    expect(h.back()).toBeNull()
  })

  it('clears every step — what Reset zoom does, since it goes all the way out', () => {
    const h = useZoomHistory()
    h.push(w(1, 10))
    h.push(w(2, 10))
    h.clear()
    expect(h.canGoBack.value).toBe(false)
    expect(h.back()).toBeNull()
  })

  it('keeps a sane default depth', () => {
    expect(ZOOM_HISTORY_LIMIT).toBeGreaterThanOrEqual(8)
    expect(useZoomHistory().depth.value).toBe(0)
  })
})
