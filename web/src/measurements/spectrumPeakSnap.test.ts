import { describe, expect, it } from 'vitest'
import { PEAK_SNAP_RADIUS_PX, peakSnapIndex } from './spectrumPeakSnap'

/** One column every 5 px, so the search radius spans three columns either side. */
const posAt = (i: number) => i * 5

describe('peakSnapIndex', () => {
  it('snaps to the local peak within the search radius', () => {
    const levels = [-90, -90, -40, -90, -90]
    const snapped = peakSnapIndex({
      count: levels.length,
      posAt,
      valueAt: (i) => levels[i],
      cursorPx: posAt(1),
      startIdx: 1,
    })
    expect(snapped).toBe(2)
  })

  it('never snaps past the search radius', () => {
    const levels = [-90, -90, -90, -90, -90, -90, -40]
    const far = posAt(6) - posAt(0)
    expect(far).toBeGreaterThan(PEAK_SNAP_RADIUS_PX)
    expect(
      peakSnapIndex({ count: levels.length, posAt, valueAt: (i) => levels[i], cursorPx: posAt(0), startIdx: 0 }),
    ).toBe(0)
  })

  it("keeps the cursor's own column when every candidate is null", () => {
    expect(peakSnapIndex({ count: 5, posAt, valueAt: () => null, cursorPx: posAt(2), startIdx: 2 })).toBe(2)
  })

  it("keeps the cursor's own column on a tie", () => {
    const levels = [-50, -50, -50]
    expect(
      peakSnapIndex({ count: levels.length, posAt, valueAt: (i) => levels[i], cursorPx: posAt(1), startIdx: 1 }),
    ).toBe(1)
  })

  it('takes the first non-null column when the cursor is on one with no value', () => {
    // A hidden channel null-fills its series, so the column under the cursor can be empty while a
    // neighbour the user plainly aimed at is not.
    const levels: (number | null)[] = [null, -70, null]
    expect(
      peakSnapIndex({ count: levels.length, posAt, valueAt: (i) => levels[i], cursorPx: posAt(0), startIdx: 0 }),
    ).toBe(1)
  })

  it('answers the cursor column for a cursor that is off the plot', () => {
    const levels = [-90, -40]
    expect(
      peakSnapIndex({ count: levels.length, posAt, valueAt: (i) => levels[i], cursorPx: -1, startIdx: 0 }),
    ).toBe(0)
  })
})
