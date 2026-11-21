import { describe, expect, it } from 'vitest'
import {
  buildStepperGrid,
  decadeExponent,
  ladderNeighbours,
  stepLadder,
  tidyFormat,
  tidyNumber,
} from './stepperGrid'
import { SCOPE_TIME_PER_DIVISION_LADDER, formatScopeTime } from '../scope/timeAxis'
import { VOLTS_PER_DIVISION_LADDER, formatVoltsPerDivision } from '../scope/voltAxis'

/** The two controls' own formatters, exactly as the panels pass them. */
const time = (seconds: number) => formatScopeTime(seconds * 1000, seconds * 1000)
const volts = (v: number) => formatVoltsPerDivision(v).replace('/div', '').trim()

describe('the decade a rung sits on', () => {
  it('puts every 1-2-5 rung on the power of ten below it', () => {
    expect(decadeExponent(2e-6)).toBe(-6)
    expect(decadeExponent(5e-6)).toBe(-6)
    expect(decadeExponent(1e-5)).toBe(-5)
  })

  /**
   * The case the nudge exists for: `Math.log10(0.001)` is not always exactly −3, and a bare floor
   * would file 1 ms under 100 µs with a mantissa of 10 — a grid that is right for most of its
   * rungs and silently wrong for the round ones.
   */
  it('is not fooled by the rungs whose logarithm lands just short', () => {
    for (const exp of [-6, -5, -4, -3, -2, -1, 0, 1]) {
      expect(decadeExponent(10 ** exp), `10^${exp}`).toBe(exp)
    }
  })
})

describe('tidying', () => {
  it('writes a cell the way a cell wants it', () => {
    expect(tidyNumber(0.1)).toBe('0.1')
    expect(tidyNumber(2)).toBe('2')
    expect(tidyNumber(500.0000000001)).toBe('500')
  })

  it('keeps the formatter’s unit and drops its padding', () => {
    // The tile reads "1.000 ms/div", which is right above a trace and three characters too long
    // in a cell.
    expect(time(1e-3)).toContain('1.000')
    expect(tidyFormat(1e-3, time)).toBe('1 ms')
    expect(tidyFormat(1e-6, time)).toBe('1 µs')
  })
})

describe('the time/div grid', () => {
  const grid = buildStepperGrid(SCOPE_TIME_PER_DIVISION_LADDER, time, 'orange')

  it('reads as the design draws it, row for row', () => {
    expect(grid.rows.map((r) => r.label)).toEqual([
      '1 µs',
      '10 µs',
      '100 µs',
      '1 ms',
      '10 ms',
      '100 ms',
    ])
    expect(grid.rows.map((r) => r.cells.map((c) => c.text))).toEqual([
      ['—', '2', '5'],
      ['10', '20', '50'],
      ['100', '200', '500'],
      ['1', '2', '5'],
      ['10', '20', '50'],
      ['100', '—', '—'],
    ])
  })

  /**
   * The shape of the ladder is half of what the grid is for: a reader can see that there is
   * nothing below 2 µs and nothing above 100 ms without pressing anything.
   */
  it('marks the rungs the ladder does not reach rather than leaving a gap', () => {
    expect(grid.rows[0].cells[0].value).toBeNull()
    expect(grid.rows[5].cells[1].value).toBeNull()
    expect(grid.rows[5].cells[2].value).toBeNull()
    const offered = grid.rows.flatMap((r) => r.cells.map((c) => c.value)).filter((v) => v !== null)
    expect(offered.length).toBe(SCOPE_TIME_PER_DIVISION_LADDER.length)
  })

  it('names the whole span in its header', () => {
    expect(grid.range).toBe('2 µs – 100 ms')
  })

  it('gives the control’s own unit the control’s own hue, and steps back below it', () => {
    expect(grid.rows.map((r) => r.tone)).toEqual([
      'green',
      'green',
      'green',
      'orange',
      'orange',
      'orange',
    ])
  })
})

describe('the V/div grid', () => {
  const grid = buildStepperGrid(VOLTS_PER_DIVISION_LADDER, volts, 'violet')

  it('reads as the design draws it, row for row', () => {
    expect(grid.rows.map((r) => r.label)).toEqual([
      '10 µV',
      '100 µV',
      '1 mV',
      '10 mV',
      '0.1 V',
      '1 V',
      '10 V',
    ])
    expect(grid.rows.map((r) => r.cells.map((c) => c.text))).toEqual([
      ['10', '20', '50'],
      ['100', '200', '500'],
      ['1', '2', '5'],
      ['10', '20', '50'],
      ['0.1', '0.2', '0.5'],
      ['1', '2', '5'],
      ['10', '20', '50'],
    ])
    expect(grid.range).toBe('10 µV – 50 V')
  })

  /** Three units here against the time ladder's two, which is what the stepping-back rule is for. */
  it('reads green, blue, violet as the units change', () => {
    expect(grid.rows.map((r) => r.tone)).toEqual([
      'green',
      'green',
      'blue',
      'blue',
      'violet',
      'violet',
      'violet',
    ])
  })

  it('offers every rung of the ladder exactly once', () => {
    const offered = grid.rows.flatMap((r) => r.cells.map((c) => c.value)).filter((v) => v !== null)
    expect(offered.length).toBe(VOLTS_PER_DIVISION_LADDER.length)
    expect(new Set(offered).size).toBe(VOLTS_PER_DIVISION_LADDER.length)
  })
})

describe('stepping', () => {
  const ladder = SCOPE_TIME_PER_DIVISION_LADDER

  it('moves one rung', () => {
    expect(stepLadder(ladder, 1e-3, 1)).toBeCloseTo(2e-3, 12)
    expect(stepLadder(ladder, 1e-3, -1)).toBeCloseTo(5e-4, 12)
  })

  /** A press at the end is a no-op rather than a wrap: a timebase that jumped from 100 ms to 2 µs
   *  would throw the capture away on a mis-click. */
  it('stops at either end', () => {
    expect(stepLadder(ladder, 2e-6, -1)).toBe(2e-6)
    expect(stepLadder(ladder, 1e-1, 1)).toBe(1e-1)
  })

  /**
   * The rule that matters for a reopened measurement: its record length need not be on the ladder,
   * and from there a press has to mean the nearest rung in that direction rather than nothing.
   */
  it('works from a value that is not on the ladder at all', () => {
    expect(stepLadder(ladder, 1.37e-3, 1)).toBeCloseTo(2e-3, 12)
    expect(stepLadder(ladder, 1.37e-3, -1)).toBeCloseTo(1e-3, 12)
  })

  it('names the two neighbours for the tile’s hint, and nothing past an end', () => {
    expect(ladderNeighbours(ladder, 1e-3, time)).toEqual({ down: '500 µs', up: '2 ms' })
    expect(ladderNeighbours(ladder, 2e-6, time).down).toBeNull()
    expect(ladderNeighbours(ladder, 1e-1, time).up).toBeNull()
  })
})
