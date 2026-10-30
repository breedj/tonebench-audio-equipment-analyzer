import { describe, expect, it } from 'vitest'
import {
  DECAY_KEY_INSET_PX,
  DECAY_KEY_MIN_HEIGHT_PX,
  DECAY_KEY_WIDTH_PX,
  decayLevelKeyLayout,
} from './decayLevelKey'

// The painting itself needs a canvas and this suite runs on `environment: 'node'`, so what is
// testable here is where the key goes — which is the whole of what the change was about, the
// library's own key being nailed to the top right.

describe('the 3D decay surface s colour key', () => {
  it('stands on the left, where the library s own could not be put', () => {
    const layout = decayLevelKeyLayout(680)
    expect(layout.x).toBe(DECAY_KEY_INSET_PX)
    expect(layout.y).toBe(DECAY_KEY_INSET_PX)
    expect(layout.width).toBe(DECAY_KEY_WIDTH_PX)
    // Comfortably inside the left edge rather than anywhere near the middle of any real plot.
    expect(layout.x + layout.width).toBeLessThan(100)
  })

  it('takes a quarter of the plot, which is the proportion it had on the right', () => {
    expect(decayLevelKeyLayout(680).height).toBe(170)
    expect(decayLevelKeyLayout(1200).height).toBe(300)
  })

  it('has a floor, so a short chart still gets a readable ramp', () => {
    expect(decayLevelKeyLayout(240).height).toBe(DECAY_KEY_MIN_HEIGHT_PX)
  })

  it('never reaches past the room there is, floor or no floor', () => {
    // The floor would otherwise hang the bar out of the bottom of a very short plot — reachable
    // on a laptop in a short window, and a bar drawn off the canvas is worse than a stubby one.
    // A plot with no room at all gets a bar of no height, which `drawDecayLevelKey` paints
    // nothing for; that is the one case where the top alone is already past the bottom.
    for (const height of [0, 10, 40, 100, 128, 129, 200]) {
      const layout = decayLevelKeyLayout(height)
      if (layout.height === 0) continue
      expect(layout.y + layout.height, `plot ${height}`).toBeLessThanOrEqual(height)
    }
  })

  it('gives a plot with no room for it no bar at all', () => {
    for (const height of [0, DECAY_KEY_INSET_PX, 2 * DECAY_KEY_INSET_PX]) {
      expect(decayLevelKeyLayout(height).height, `plot ${height}`).toBe(0)
    }
  })

  it('grows with the plot and never shrinks', () => {
    let previous = -1
    for (let height = 0; height <= 2000; height += 37) {
      const { height: bar } = decayLevelKeyLayout(height)
      expect(bar, `plot ${height}`).toBeGreaterThanOrEqual(previous)
      previous = bar
    }
  })
})
