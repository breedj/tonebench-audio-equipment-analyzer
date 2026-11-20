import { describe, expect, it } from 'vitest'
import {
  clampCursorBox,
  cursorArrowFits,
  cursorBoxFraction,
  cursorBoxRows,
  cursorLevelSpanLabels,
  cursorSlopeReading,
  cursorTimeSpanLabels,
  cursorValueLabel,
  isScopeCursorStyle,
  layoutCursorBox,
  MIN_CURSOR_ARROW_PX,
  SCOPE_CURSOR_STYLES,
} from './cursorOverlay'
import { DEFAULT_SCOPE_CURSORS, type ScopeCursors } from './cursors'

function cursors(over: Partial<ScopeCursors> = {}): ScopeCursors {
  return { ...DEFAULT_SCOPE_CURSORS, ...over }
}

/** A measurer a test can reason about: one unit per character. */
const perChar = (text: string) => text.length

describe('SCOPE_CURSOR_STYLES', () => {
  it('offers exactly the two the chart can draw', () => {
    expect(SCOPE_CURSOR_STYLES.map((o) => o.value)).toEqual(['Fixed', 'Following'])
  })

  it('says what each one does, since neither name explains itself', () => {
    for (const option of SCOPE_CURSOR_STYLES) {
      expect(option.title && option.title.length > 30).toBe(true)
    }
  })

  it('recognises its own values and nothing else', () => {
    for (const option of SCOPE_CURSOR_STYLES) expect(isScopeCursorStyle(option.value)).toBe(true)
    expect(isScopeCursorStyle('Floating')).toBe(false)
    expect(isScopeCursorStyle(undefined)).toBe(false)
    expect(isScopeCursorStyle(null)).toBe(false)
  })
})

describe('cursorSlopeReading', () => {
  it('is a reading about a pair of pairs, so one pair alone has nothing to say', () => {
    expect(cursorSlopeReading(cursors({ timeOn: true, timeAS: 0, timeBS: 1e-3 }))).toBeNull()
    expect(cursorSlopeReading(cursors({ voltOn: true, voltAV: 0, voltBV: 1 }))).toBeNull()
  })

  it('is absent where the cursors name no stretch of time', () => {
    const both = { timeOn: true, voltOn: true, voltAV: 0, voltBV: 1 }
    expect(cursorSlopeReading(cursors({ ...both, timeAS: 2e-3, timeBS: 2e-3 }))).toBeNull()
  })

  it('scales the denominator, never the numerator', () => {
    // 510 mV over 25.1 µs is 20.3 V/ms. Written as V/s it would be 20318 — the figure every
    // amplifier datasheet writes the other way round.
    const reading = cursorSlopeReading(
      cursors({ timeOn: true, timeAS: -14.05e-6, timeBS: 11.05e-6, voltOn: true, voltAV: -257e-3, voltBV: 253e-3 }),
    )
    expect(reading?.label).toBe('ΔV/Δt')
    expect(reading?.value).toBe('20.32 V/ms')
    expect(reading?.value).not.toMatch(/V\/s$/)
  })

  it('keeps the sign, because a falling edge is not a rising one', () => {
    const falling = cursorSlopeReading(
      cursors({ timeOn: true, timeAS: 0, timeBS: 1e-3, voltOn: true, voltAV: 1, voltBV: 0 }),
    )
    expect(falling?.value.startsWith('-')).toBe(true)
  })
})

describe('cursorBoxRows', () => {
  it('is the rail’s own rows plus the slope, so the two cannot disagree about a figure', () => {
    const rows = cursorBoxRows(
      cursors({ timeOn: true, timeAS: 0, timeBS: 1e-3, voltOn: true, voltAV: 0, voltBV: 1 }),
      0.5,
      2,
    )
    expect(rows.map((r) => r.label)).toEqual([
      'Time A',
      'Time B',
      'Δt',
      '1/Δt',
      'Level A',
      'Level B',
      'ΔV',
      'ΔV/Δt',
    ])
  })

  it('has nothing to print with both pairs off', () => {
    expect(cursorBoxRows(cursors(), 0.5, 2)).toEqual([])
  })

  it('leaves the slope out when only one pair is on', () => {
    const rows = cursorBoxRows(cursors({ timeOn: true, timeAS: 0, timeBS: 1e-3 }), 0.5, 2)
    expect(rows.some((r) => r.label === 'ΔV/Δt')).toBe(false)
  })
})

describe('cursorValueLabel', () => {
  it('writes the value alone — the letter is already on the line', () => {
    const c = cursors({ timeOn: true, timeAS: 11.05e-6, timeBS: -14.05e-6 })
    expect(cursorValueLabel(c, 'timeA', 0.1, 0.05)).toBe('11.05 µs')
    expect(cursorValueLabel(c, 'timeA', 0.1, 0.05)).not.toMatch(/^A/)
  })

  it('is null for a pair that is off', () => {
    const c = cursors({ timeOn: true, timeAS: 1e-3, timeBS: 2e-3 })
    expect(cursorValueLabel(c, 'voltA', 0.1, 2)).toBeNull()
    expect(cursorValueLabel(c, 'voltB', 0.1, 2)).toBeNull()
  })

  it('writes levels in the graticule’s own unit, which moves with the V/div', () => {
    const c = cursors({ voltOn: true, voltAV: 0.253, voltBV: -0.257 })
    expect(cursorValueLabel(c, 'voltA', 0.1, 2)).toBe('0.253 V')
    expect(cursorValueLabel(c, 'voltB', 0.1, 2)).toBe('-0.257 V')
    // The same level on a finer graticule reads in millivolts -- the unit belongs to the squares
    // the reader is counting, not to the figure.
    expect(cursorValueLabel(c, 'voltA', 0.02, 2)).toBe('253.00 mV')
  })
})

describe('the span labels', () => {
  it('carry Δt, its reciprocal and the slope on the time arrow', () => {
    const labels = cursorTimeSpanLabels(
      cursors({ timeOn: true, timeAS: 0, timeBS: 1e-3, voltOn: true, voltAV: 0, voltBV: 1 }),
    )
    expect(labels).toHaveLength(3)
    expect(labels[0].startsWith('Δt ')).toBe(true)
    expect(labels[1].startsWith('1/Δt ')).toBe(true)
    expect(labels[2].startsWith('ΔV/Δt ')).toBe(true)
  })

  it('drop the slope when there is no level pair to make one from', () => {
    const labels = cursorTimeSpanLabels(cursors({ timeOn: true, timeAS: 0, timeBS: 1e-3 }))
    expect(labels).toHaveLength(2)
  })

  it('have nothing to say about a pair with no span', () => {
    expect(cursorTimeSpanLabels(cursors({ timeOn: true, timeAS: 1e-3, timeBS: 1e-3 }))).toEqual([])
    expect(cursorTimeSpanLabels(cursors())).toEqual([])
    expect(cursorLevelSpanLabels(cursors(), 0.5)).toEqual([])
  })

  it('write ΔV as a magnitude, the way the table does', () => {
    const labels = cursorLevelSpanLabels(cursors({ voltOn: true, voltAV: 0.253, voltBV: -0.257 }), 0.1)
    expect(labels).toEqual(['ΔV 0.510 V'])
  })
})

describe('layoutCursorBox', () => {
  it('is sized from its own content, so a long figure is never clipped', () => {
    const narrow = layoutCursorBox([{ label: 'Δt', value: '1 ms', title: '' }], perChar)
    const wide = layoutCursorBox([{ label: 'ΔV/Δt', value: '-1234.000 mV/µs', title: '' }], perChar)
    expect(wide.width).toBeGreaterThan(narrow.width)
    expect(wide.labelWidth).toBe(5)
  })

  it('grows a row at a time', () => {
    const one = layoutCursorBox([{ label: 'a', value: 'b', title: '' }], perChar)
    const two = layoutCursorBox(
      [
        { label: 'a', value: 'b', title: '' },
        { label: 'c', value: 'd', title: '' },
      ],
      perChar,
    )
    expect(two.height - one.height).toBe(one.lineHeight)
  })

  it('still has a size with nothing in it, so an empty box is never zero-height', () => {
    const empty = layoutCursorBox([], perChar)
    expect(empty.height).toBeGreaterThan(0)
    expect(empty.width).toBeGreaterThan(0)
  })

  it('takes the widest label and the widest value, not the widest row', () => {
    const metrics = layoutCursorBox(
      [
        { label: 'wwwwww', value: 'x', title: '' },
        { label: 'y', value: 'zzzzzzzz', title: '' },
      ],
      perChar,
    )
    expect(metrics.labelWidth).toBe(6)
    expect(metrics.width).toBe(metrics.padding * 2 + 6 + metrics.gap + 8)
  })
})

describe('clampCursorBox', () => {
  const plot = { left: 40, top: 10, width: 400, height: 200 }
  const box = { width: 120, height: 80 }

  it('puts 0,0 against the top-left corner and 1,1 against the bottom-right', () => {
    expect(clampCursorBox({ x: 0, y: 0 }, box, plot)).toEqual({ left: 40, top: 10 })
    expect(clampCursorBox({ x: 1, y: 1 }, box, plot)).toEqual({ left: 40 + 280, top: 10 + 120 })
  })

  it('keeps the whole box inside the plot from every corner', () => {
    for (const x of [-5, 0, 0.5, 1, 7]) {
      for (const y of [-5, 0, 0.5, 1, 7]) {
        const { left, top } = clampCursorBox({ x, y }, box, plot)
        expect(left).toBeGreaterThanOrEqual(plot.left)
        expect(top).toBeGreaterThanOrEqual(plot.top)
        expect(left + box.width).toBeLessThanOrEqual(plot.left + plot.width)
        expect(top + box.height).toBeLessThanOrEqual(plot.top + plot.height)
      }
    }
  })

  it('pins a box bigger than the plot rather than hiding it', () => {
    const huge = { width: 900, height: 900 }
    expect(clampCursorBox({ x: 1, y: 1 }, huge, plot)).toEqual({ left: 40, top: 10 })
  })

  it('reads a non-finite fraction as the corner', () => {
    expect(clampCursorBox({ x: Number.NaN, y: Number.NaN }, box, plot)).toEqual({ left: 40, top: 10 })
  })
})

describe('cursorBoxFraction', () => {
  const plot = { left: 40, top: 10, width: 400, height: 200 }
  const box = { width: 120, height: 80 }

  it('is the exact inverse of the clamp, so an untouched box cannot creep', () => {
    for (const f of [
      { x: 0, y: 0 },
      { x: 0.25, y: 0.75 },
      { x: 1, y: 1 },
    ]) {
      const position = clampCursorBox(f, box, plot)
      const back = cursorBoxFraction(position, box, plot)
      expect(back.x).toBeCloseTo(f.x, 12)
      expect(back.y).toBeCloseTo(f.y, 12)
    }
  })

  it('clamps a pointer dragged outside the plot', () => {
    expect(cursorBoxFraction({ left: -500, top: -500 }, box, plot)).toEqual({ x: 0, y: 0 })
    expect(cursorBoxFraction({ left: 5000, top: 5000 }, box, plot)).toEqual({ x: 1, y: 1 })
  })

  it('answers zero where there is no travel at all', () => {
    const exact = { width: plot.width, height: plot.height }
    expect(cursorBoxFraction({ left: 40, top: 10 }, exact, plot)).toEqual({ x: 0, y: 0 })
  })
})

describe('cursorArrowFits', () => {
  it('needs both ends on the plot — one on the edge would read as a measurement to the edge', () => {
    expect(cursorArrowFits(100, 300, 50, 400)).toBe(true)
    expect(cursorArrowFits(10, 300, 50, 400)).toBe(false)
    expect(cursorArrowFits(100, 900, 50, 400)).toBe(false)
  })

  it('is refused for a cursor that is drawn nowhere', () => {
    expect(cursorArrowFits(null, 300, 50, 400)).toBe(false)
    expect(cursorArrowFits(100, null, 50, 400)).toBe(false)
    expect(cursorArrowFits(Number.NaN, 300, 50, 400)).toBe(false)
  })

  it('is refused for a pair too close together to draw two heads between', () => {
    expect(cursorArrowFits(200, 200 + MIN_CURSOR_ARROW_PX - 1, 50, 400)).toBe(false)
    expect(cursorArrowFits(200, 200 + MIN_CURSOR_ARROW_PX, 50, 400)).toBe(true)
  })

  it('does not care which way round the pair is', () => {
    expect(cursorArrowFits(300, 100, 50, 400)).toBe(true)
  })
})
