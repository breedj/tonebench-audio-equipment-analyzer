import { describe, expect, it } from 'vitest'
import {
  MIN_STRIP_BOX_PX,
  STRIP_HANDLE_PX,
  stripBox,
  stripCentreOn,
  stripCoversAll,
  stripDrag,
  stripGrabAt,
  stripSeconds,
  stripX,
  type StripGeometry,
} from './overviewStrip'

/** A 20 ms record, a quarter of it before the trigger — the screen's own default. */
const BOUNDS = { fromS: -0.005, toS: 0.015 }
const GEO: StripGeometry = { left: 10, width: 400 }

describe('stripX and stripSeconds', () => {
  it('put the trigger where the record says it is, not in the middle', () => {
    // A quarter of the record is pre-trigger, so t = 0 sits a quarter across.
    expect(stripX(-0.005, BOUNDS, GEO)).toBe(10)
    expect(stripX(0, BOUNDS, GEO)).toBe(110)
    expect(stripX(0.015, BOUNDS, GEO)).toBe(410)
  })

  it('are each other inverse', () => {
    for (const x of [10, 110, 250, 410]) {
      expect(stripX(stripSeconds(x, BOUNDS, GEO), BOUNDS, GEO)).toBeCloseTo(x, 9)
    }
  })

  it('answer the left edge for a record with no span', () => {
    // Before anything is captured, and on a record of one sample.
    expect(stripX(0, { fromS: 0, toS: 0 }, GEO)).toBe(10)
    expect(stripSeconds(200, { fromS: 0, toS: 0 }, GEO)).toBe(0)
  })
})

describe('stripBox', () => {
  it('is the window drawn on the record own axis', () => {
    expect(stripBox({ fromS: 0, toS: 0.005 }, BOUNDS, GEO)).toEqual({ fromX: 110, toX: 210 })
  })

  it('fills the strip when the window is the whole record', () => {
    expect(stripBox(BOUNDS, BOUNDS, GEO)).toEqual({ fromX: 10, toX: 410 })
  })

  it('clamps a window reaching past the record rather than hiding it', () => {
    // A drag-zoom is not clamped to the record, so this is reachable -- and a box that vanished
    // would leave no way to see which part of the window is on the record.
    expect(stripBox({ fromS: -0.05, toS: 0.005 }, BOUNDS, GEO)).toEqual({ fromX: 10, toX: 210 })
  })

  it('is nothing with no record to draw on', () => {
    expect(stripBox({ fromS: 0, toS: 1 }, { fromS: 0, toS: 0 }, GEO)).toBeNull()
    expect(stripBox({ fromS: 0, toS: 1 }, BOUNDS, { left: 0, width: 0 })).toBeNull()
  })
})

describe('stripGrabAt', () => {
  const box = { fromX: 110, toX: 210 }

  it('takes an edge within the handle, and the body between them', () => {
    expect(stripGrabAt(110, box)).toBe('from')
    expect(stripGrabAt(110 + STRIP_HANDLE_PX, box)).toBe('from')
    expect(stripGrabAt(210, box)).toBe('to')
    expect(stripGrabAt(160, box)).toBe('body')
  })

  it('takes neither outside the box', () => {
    expect(stripGrabAt(50, box)).toBe('outside')
    expect(stripGrabAt(400, box)).toBe('outside')
  })

  it('gives a narrow box the nearer edge, so both ends stay reachable', () => {
    // Narrower than two handles, both tests pass -- preferring one end silently would make the box
    // resizable from one side only, which reads as a broken handle.
    const narrow = { fromX: 200, toX: 204 }
    expect(stripGrabAt(200, narrow)).toBe('from')
    expect(stripGrabAt(204, narrow)).toBe('to')
  })

  it('takes nothing when there is no box', () => {
    expect(stripGrabAt(100, null)).toBe('outside')
  })
})

describe('stripDrag', () => {
  const window = { fromS: 0, toS: 0.005 }

  it('slides the window whole on the body, keeping its span', () => {
    const moved = stripDrag('body', window, 0.002, BOUNDS, 1e-6)
    expect(moved.fromS).toBeCloseTo(0.002, 9)
    expect(moved.toS - moved.fromS).toBeCloseTo(0.005, 9)
  })

  it('stops a body drag at the end of the record rather than shortening it', () => {
    // A window that narrowed as it was pushed off the end would be a timebase changed by a gesture
    // that never asked to change one.
    const moved = stripDrag('body', window, 1.0, BOUNDS, 1e-6)
    expect(moved.toS).toBeCloseTo(BOUNDS.toS, 9)
    expect(moved.toS - moved.fromS).toBeCloseTo(0.005, 9)

    const back = stripDrag('body', window, -1.0, BOUNDS, 1e-6)
    expect(back.fromS).toBeCloseTo(BOUNDS.fromS, 9)
    expect(back.toS - back.fromS).toBeCloseTo(0.005, 9)
  })

  it('moves one edge alone', () => {
    expect(stripDrag('from', window, -0.002, BOUNDS, 1e-6)).toEqual({ fromS: -0.002, toS: 0.005 })
    expect(stripDrag('to', window, 0.002, BOUNDS, 1e-6)).toEqual({ fromS: 0, toS: 0.007 })
  })

  it('stops an edge at the other one rather than turning the box inside out', () => {
    const min = 1e-4
    expect(stripDrag('from', window, 1.0, BOUNDS, min).fromS).toBeCloseTo(0.005 - min, 9)
    expect(stripDrag('to', window, -1.0, BOUNDS, min).toS).toBeCloseTo(0 + min, 9)
  })

  it('keeps an edge inside the record', () => {
    expect(stripDrag('from', window, -1.0, BOUNDS, 1e-6).fromS).toBeCloseTo(BOUNDS.fromS, 9)
    expect(stripDrag('to', window, 1.0, BOUNDS, 1e-6).toS).toBeCloseTo(BOUNDS.toS, 9)
  })

  it('leaves the window alone for a drag that grabbed nothing, or that is not a number', () => {
    expect(stripDrag('outside', window, 0.002, BOUNDS, 1e-6)).toEqual(window)
    expect(stripDrag('body', window, Number.NaN, BOUNDS, 1e-6)).toEqual(window)
  })
})

describe('stripCentreOn', () => {
  it('keeps the span and puts it round the pointer', () => {
    const next = stripCentreOn(0.01, 0.004, BOUNDS)
    expect(next.fromS).toBeCloseTo(0.008, 9)
    expect(next.toS).toBeCloseTo(0.012, 9)
  })

  it('clamps to the record, like a body drag', () => {
    expect(stripCentreOn(0.015, 0.004, BOUNDS).toS).toBeCloseTo(BOUNDS.toS, 9)
    expect(stripCentreOn(-0.005, 0.004, BOUNDS).fromS).toBeCloseTo(BOUNDS.fromS, 9)
  })

  it('never asks for a window wider than the record', () => {
    expect(stripCentreOn(0, 10, BOUNDS)).toEqual({ fromS: BOUNDS.fromS, toS: BOUNDS.toS })
  })
})

describe('stripCoversAll', () => {
  it('is true for the whole record and for anything reaching past it', () => {
    expect(stripCoversAll(BOUNDS, BOUNDS)).toBe(true)
    expect(stripCoversAll({ fromS: -1, toS: 1 }, BOUNDS)).toBe(true)
  })

  it('is false for a window inside it', () => {
    expect(stripCoversAll({ fromS: 0, toS: 0.005 }, BOUNDS)).toBe(false)
  })
})

describe('the constants', () => {
  it('leave a box the minimum gesture width can still grab', () => {
    // The two bound different things -- the handle is how near a press has to be, the minimum is
    // how narrow a drag may leave the box -- but a minimum narrower than a handle would make a box
    // at its own limit grab as an edge from either side and never as a body, which is fine, and
    // a minimum *wider* than two handles would leave an ungrabbable gap. Neither is an error; what
    // would be is a non-positive one.
    expect(MIN_STRIP_BOX_PX).toBeGreaterThan(0)
    expect(STRIP_HANDLE_PX).toBeGreaterThan(0)
  })
})
