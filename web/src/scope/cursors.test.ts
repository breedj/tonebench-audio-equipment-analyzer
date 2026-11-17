import { describe, expect, it } from 'vitest'
import {
  cursorGate,
  cursorPairNeedsPlacing,
  cursorReadings,
  DEFAULT_SCOPE_CURSORS,
  formatCursorFrequency,
  placeCursorPair,
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
