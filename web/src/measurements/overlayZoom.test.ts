import { describe, expect, it } from 'vitest'
import { overlaysNeedingFetch, sameWindow, shouldApplyFetch, type OverlayZoomState } from './overlayZoom'

const WHOLE_SPAN = null
const BASS = { loFreqHz: 20, hiFreqHz: 200 }
const TREBLE = { loFreqHz: 5000, hiFreqHz: 20000 }

function overlay(id: number, over: Partial<OverlayZoomState> = {}): OverlayZoomState {
  return { id, visible: true, fetchedWindow: WHOLE_SPAN, ...over }
}

describe('sameWindow', () => {
  it('compares by value, not identity', () => {
    expect(sameWindow({ ...BASS }, { ...BASS })).toBe(true)
    expect(sameWindow(BASS, TREBLE)).toBe(false)
  })

  it('treats the whole span as its own window', () => {
    expect(sameWindow(WHOLE_SPAN, WHOLE_SPAN)).toBe(true)
    expect(sameWindow(WHOLE_SPAN, BASS)).toBe(false)
    expect(sameWindow(BASS, WHOLE_SPAN)).toBe(false)
  })
})

describe('overlaysNeedingFetch', () => {
  it('fetches a visible overlay that holds a different window', () => {
    expect(overlaysNeedingFetch([overlay(1)], BASS)).toEqual([1])
  })

  it('fetches nothing when every overlay already holds that window', () => {
    expect(overlaysNeedingFetch([overlay(1, { fetchedWindow: { ...BASS } })], { ...BASS })).toEqual([])
  })

  /**
   * Unchecking leaves the overlay in the list. Fetching for it anyway would make the cost of a
   * zoom grow with how many results had ever been ticked, not how many are on the chart.
   */
  it('skips hidden overlays', () => {
    const overlays = [overlay(1, { visible: false }), overlay(2)]
    expect(overlaysNeedingFetch(overlays, BASS)).toEqual([2])
  })

  /**
   * Zooming back out is a re-fetch like any other. Keeping the zoomed columns would draw the
   * overlay as two flat lines outside the window, because resample clamps past its source range.
   */
  it('re-fetches the whole span when the zoom is reset', () => {
    expect(overlaysNeedingFetch([overlay(1, { fetchedWindow: BASS })], WHOLE_SPAN)).toEqual([1])
  })

  it('fetches nothing at all for an empty chart', () => {
    expect(overlaysNeedingFetch([], BASS)).toEqual([])
  })
})

describe('shouldApplyFetch', () => {
  it('applies a result that answers the window the chart is on', () => {
    expect(shouldApplyFetch([overlay(1)], 1, { ...BASS }, { ...BASS })).toBe(true)
  })

  /**
   * The race a drag creates: several windows are asked for in turn and the answers can arrive out
   * of order, so a stale one must be dropped rather than painted over the current zoom.
   */
  it('drops a result for a window the chart has already left', () => {
    expect(shouldApplyFetch([overlay(1)], 1, BASS, TREBLE)).toBe(false)
    expect(shouldApplyFetch([overlay(1)], 1, BASS, WHOLE_SPAN)).toBe(false)
  })

  it('drops a result for an overlay unchecked while it was in flight', () => {
    expect(shouldApplyFetch([overlay(1, { visible: false })], 1, BASS, BASS)).toBe(false)
  })

  it('drops a result for an overlay removed while it was in flight', () => {
    expect(shouldApplyFetch([overlay(2)], 1, BASS, BASS)).toBe(false)
    expect(shouldApplyFetch([], 1, BASS, BASS)).toBe(false)
  })

  it('applies a whole-span result once the chart is back at the whole span', () => {
    expect(shouldApplyFetch([overlay(1, { fetchedWindow: BASS })], 1, WHOLE_SPAN, WHOLE_SPAN)).toBe(true)
  })
})
