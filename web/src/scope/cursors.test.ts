import { describe, expect, it } from 'vitest'
import {
  cursorGate,
  cursorLevelsRead,
  cursorPairNeedsPlacing,
  cursorReadings,
  cursorTimeBBox,
  cursorTimeBFromBox,
  cursorsTrack,
  DEFAULT_SCOPE_CURSORS,
  formatCursorFrequency,
  isScopeCursorMode,
  placeCursorPair,
  trackLevelAt,
  type ScopeCursors,
} from './cursors'

function cursors(over: Partial<ScopeCursors> = {}): ScopeCursors {
  return { ...DEFAULT_SCOPE_CURSORS, ...over }
}

describe('cursorGate', () => {
  it('is the ordered time pair, whichever way round they were dragged', () => {
    // A cursor pair has two edges and no direction.
    const forward = cursorGate(cursors({ timeOn: true, timeAS: -0.001, timeBS: 0.004 }))
    const back = cursorGate(cursors({ timeOn: true, timeAS: 0.004, timeBS: -0.001 }))
    expect(forward).toEqual({ fromS: -0.001, toS: 0.004 })
    expect(back).toEqual(forward)
  })

  it('is nothing when the time pair is off, however it is placed', () => {
    // The level pair names no stretch of the record, so it cannot gate a measurement.
    expect(cursorGate(cursors({ timeAS: -0.001, timeBS: 0.004, voltOn: true, voltAV: -1, voltBV: 1 }))).toBeNull()
  })

  it('is nothing when the two cursors are on the same sample', () => {
    // A window of zero width holds nothing to measure -- the server refuses it too, rather than
    // answering with the whole record under a heading saying it is the cursors'.
    expect(cursorGate(cursors({ timeOn: true, timeAS: 0.002, timeBS: 0.002 }))).toBeNull()
  })

  it('is nothing for a cursor that is not a number', () => {
    // An emptied number box is '' through Vue's .number modifier, i.e. NaN here, and a NaN on the
    // query string is a window the server cannot read.
    expect(cursorGate(cursors({ timeOn: true, timeAS: Number.NaN, timeBS: 0.004 }))).toBeNull()
  })
})

describe('cursorPairNeedsPlacing', () => {
  it('says so for the default pair, which is both cursors on the trigger', () => {
    // Switched on as they are, the two would stack on t = 0 with nothing to read and nothing to
    // grab -- which is why the defaults are allowed to be zero at all.
    expect(cursorPairNeedsPlacing(DEFAULT_SCOPE_CURSORS.timeAS, DEFAULT_SCOPE_CURSORS.timeBS, -0.005, 0.005)).toBe(true)
  })

  it('says so for a pair left over from another window', () => {
    // A pair placed on a 20 ms record is off the screen entirely on a 1 s one.
    expect(cursorPairNeedsPlacing(0.002, 0.004, -0.5, 0.5)).toBe(false)
    expect(cursorPairNeedsPlacing(0.002, 0.9, -0.5, 0.5)).toBe(true)
    expect(cursorPairNeedsPlacing(-0.9, 0.004, -0.5, 0.5)).toBe(true)
  })

  it('leaves a pair that is already in the window alone', () => {
    expect(cursorPairNeedsPlacing(-0.001, 0.003, -0.005, 0.005)).toBe(false)
  })

  it('says so for a cursor that is not a number', () => {
    expect(cursorPairNeedsPlacing(Number.NaN, 0.003, -0.005, 0.005)).toBe(true)
  })
})

describe('placeCursorPair', () => {
  it('puts them a quarter and three quarters across the window', () => {
    expect(placeCursorPair(-0.004, 0.004)).toEqual({ a: -0.002, b: 0.002 })
  })

  it('places them the same way whichever end it is given first', () => {
    expect(placeCursorPair(0.004, -0.004)).toEqual(placeCursorPair(-0.004, 0.004))
  })
})

describe('cursorReadings', () => {
  it('reads the time pair with its span and its reciprocal', () => {
    const rows = cursorReadings(cursors({ timeOn: true, timeAS: 0, timeBS: 0.001 }), 0.5, 10)
    expect(rows.map((r) => r.label)).toEqual(['Time A', 'Time B', 'Δt', '1/Δt'])
    expect(rows.find((r) => r.label === 'Δt')?.value).toBe('1.000 ms')
    expect(rows.find((r) => r.label === '1/Δt')?.value).toBe('1.000 kHz')
  })

  it('leaves out 1/Δt when there is no span to take a reciprocal of', () => {
    const rows = cursorReadings(cursors({ timeOn: true, timeAS: 0.002, timeBS: 0.002 }), 0.5, 10)
    expect(rows.map((r) => r.label)).toEqual(['Time A', 'Time B', 'Δt'])
  })

  it('reads the level pair in the graticule own unit', () => {
    const rows = cursorReadings(cursors({ voltOn: true, voltAV: -0.25, voltBV: 0.75 }), 0.5, 10)
    expect(rows.map((r) => r.label)).toEqual(['Level A', 'Level B', 'ΔV'])
    expect(rows.find((r) => r.label === 'ΔV')?.value).toBe('1.000 V')
    // The unit follows the division, as every volts figure on this screen does.
    expect(cursorReadings(cursors({ voltOn: true, voltAV: 0, voltBV: 0.001 }), 0.001, 10)
      .find((r) => r.label === 'ΔV')?.value).toBe('1.00 mV')
  })

  it('reads both pairs together, time first', () => {
    const rows = cursorReadings(cursors({ timeOn: true, timeBS: 0.001, voltOn: true, voltBV: 1 }), 0.5, 10)
    expect(rows.map((r) => r.label)).toEqual(['Time A', 'Time B', 'Δt', '1/Δt', 'Level A', 'Level B', 'ΔV'])
  })

  it('answers nothing with both pairs off', () => {
    expect(cursorReadings(DEFAULT_SCOPE_CURSORS, 0.5, 10)).toEqual([])
  })
})

describe('formatCursorFrequency', () => {
  it('spans a one-second record to two adjacent samples', () => {
    // 1/Δt reaches from a fraction of a hertz across a whole record to the better part of a hundred
    // kilohertz between two samples at 192 kS/s, which is why it has a formatter of its own.
    expect(formatCursorFrequency(0.5)).toBe('500.00 mHz')
    expect(formatCursorFrequency(50)).toBe('50.000 Hz')
    expect(formatCursorFrequency(1000)).toBe('1.000 kHz')
    expect(formatCursorFrequency(96_000)).toBe('96.000 kHz')
    expect(formatCursorFrequency(150_000)).toBe('150.00 kHz')
    expect(formatCursorFrequency(Number.POSITIVE_INFINITY)).toBe('—')
  })
})


describe('Track mode', () => {
  it('is off by default, with the level pair placed by hand', () => {
    expect(DEFAULT_SCOPE_CURSORS.mode).toBe('Manual')
    expect(cursorsTrack(cursors())).toBe(false)
  })

  it('needs the time pair, which is the thing it follows', () => {
    // Track with nothing to follow is not tracking: the level cursors would have no time to read
    // the trace at, so the honest answer is that the mode is simply not doing anything yet.
    expect(cursorsTrack(cursors({ mode: 'Track' }))).toBe(false)
    expect(cursorsTrack(cursors({ mode: 'Track', timeOn: true }))).toBe(true)
  })

  it('reads the levels whether or not their own lines are drawn', () => {
    // voltOn draws the two horizontals -- the Siglent's Show CursorY. Under Track the squares on
    // the trace are already saying where the levels are, so the figures are worth printing either
    // way; in Manual there is nothing to print until the pair is switched on.
    expect(cursorLevelsRead(cursors({ mode: 'Track', timeOn: true }))).toBe(true)
    expect(cursorLevelsRead(cursors())).toBe(false)
    expect(cursorLevelsRead(cursors({ voltOn: true }))).toBe(true)
  })

  it('prints the level rows under Track with the time pair alone', () => {
    const rows = cursorReadings(
      cursors({ mode: 'Track', timeOn: true, timeAS: 0, timeBS: 0.001, voltAV: 0.5, voltBV: -0.25 }),
      0.5,
      10,
    )
    const labels = rows.map((r) => r.label)
    expect(labels).toContain('Level A')
    expect(labels).toContain('ΔV')
    // And says they were read off the trace rather than placed, which is the whole difference.
    expect(rows.find((r) => r.label === 'Level A')?.title).toContain('the trace')
  })

  it('answers for a mode it does not know, rather than taking it', () => {
    expect(isScopeCursorMode('Track')).toBe(true)
    expect(isScopeCursorMode('Follow')).toBe(false)
  })
})

describe('trackLevelAt', () => {
  const xs = [0, 1, 2, 3]
  const ys = [0, 10, 20, 30]

  it('interpolates between the two samples either side', () => {
    expect(trackLevelAt(xs, ys, 1.5)).toBeCloseTo(15, 12)
    expect(trackLevelAt(xs, ys, 2.25)).toBeCloseTo(22.5, 12)
  })

  it('is the sample itself where it lands on one, and at either end', () => {
    expect(trackLevelAt(xs, ys, 2)).toBe(20)
    expect(trackLevelAt(xs, ys, 0)).toBe(0)
    expect(trackLevelAt(xs, ys, 3)).toBe(30)
  })

  it('answers nothing outside the trace, rather than clamping to its ends', () => {
    // A level cursor clamped to the last sample would claim the trace is still there, which on a
    // window wider than the record is exactly what it is not.
    expect(trackLevelAt(xs, ys, -0.5)).toBeNull()
    expect(trackLevelAt(xs, ys, 3.5)).toBeNull()
    expect(trackLevelAt([], [], 0)).toBeNull()
    expect(trackLevelAt(xs, ys, Number.NaN)).toBeNull()
  })

  it('does not interpolate across a gap, which is a stretch nothing measured', () => {
    const gappy = [0, null, 20, 30]
    expect(trackLevelAt(xs, gappy, 1.5)).toBeNull()
    // The sample before a gap still reads itself: what is absent is the stretch, not the sample.
    expect(trackLevelAt(xs, gappy, 0.5)).toBe(0)
  })

  it('finds the right interval on a long trace, which is what the search is for', () => {
    const longXs = Array.from({ length: 5000 }, (_, i) => i * 0.25)
    const longYs = longXs.map((x) => x * 2)
    expect(trackLevelAt(longXs, longYs, 613.375)).toBeCloseTo(1226.75, 9)
  })
})

describe('the B box as a span', () => {
  it('shows a position or the span from A, and reads back as a position either way', () => {
    const absolute = cursors({ timeAS: -0.000065, timeBS: 0.000335 })
    expect(cursorTimeBBox(absolute)).toBeCloseTo(0.000335, 12)
    const span = { ...absolute, timeBIsSpan: true }
    expect(cursorTimeBBox(span)).toBeCloseTo(0.0004, 12)
    // The Siglent's own figures: X1 335 µs, X2 −65 µs, ΔX 400 µs.
    expect(cursorTimeBFromBox(span, 0.0004)).toBeCloseTo(0.000335, 12)
    expect(cursorTimeBFromBox(absolute, 0.0004)).toBeCloseTo(0.0004, 12)
  })

  it('leaves timeBS absolute, so nothing downstream has to know how it was typed', () => {
    const span = cursors({ timeOn: true, timeAS: 0.001, timeBS: 0.003, timeBIsSpan: true })
    expect(cursorGate(span)).toEqual({ fromS: 0.001, toS: 0.003 })
  })
})
