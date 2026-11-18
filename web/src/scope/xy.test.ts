import { describe, expect, it } from 'vitest'
import type { ScopeXySlice } from '../api/client'
import {
  SCOPE_VIEWS,
  XY_DIVISIONS,
  correlationDescription,
  formatAmplitudeRatio,
  formatCorrelation,
  formatPhaseDeg,
  scopeViewIsXy,
  xyCursorText,
  xyPlotBox,
  xyReadings,
  xyTruncationNote,
  xyWindow,
} from './xy'

function slice(patch: Partial<ScopeXySlice> = {}): ScopeXySlice {
  return {
    x: [0, 1],
    y: [0, 1],
    frameCount: 3840,
    windowFrameCount: 3840,
    spanS: 0.02,
    correlation: 1,
    phaseDeg: 0,
    amplitudeRatio: 1,
    ...patch,
  }
}

describe('the view toggle', () => {
  it('offers every picture once, the waveform first', () => {
    expect(SCOPE_VIEWS.map((v) => v.value)).toEqual(['Waveform', 'XY', 'Eye'])
    for (const view of SCOPE_VIEWS) {
      expect(view.label.length).toBeGreaterThan(0)
      expect(view.title.length).toBeGreaterThan(30)
    }
  })

  it('knows which one is the goniometer', () => {
    expect(scopeViewIsXy('XY')).toBe(true)
    expect(scopeViewIsXy('Waveform')).toBe(false)
    expect(scopeViewIsXy('Eye')).toBe(false)
  })
})

describe('the window', () => {
  // Square, from one V/div: the whole content of the picture is its shape, and the first reading
  // anyone takes is whether the line sits at 45 degrees. Different scales would tilt it.
  it('is square and symmetric about zero, eight divisions across', () => {
    expect(xyWindow(0.5)).toEqual({ min: -2, max: 2 })
    expect(xyWindow(2)).toEqual({ min: -8, max: 8 })
    expect(XY_DIVISIONS).toBe(8)
  })

  it('never has zero span, whatever it is handed', () => {
    for (const vdiv of [0, -1, Number.NaN]) {
      const w = xyWindow(vdiv)
      expect(w.max).toBeGreaterThan(w.min)
    }
  })
})

describe('the readings', () => {
  it('reads nothing at all without a figure', () => {
    expect(xyReadings(null)).toEqual([])
    expect(xyReadings(undefined)).toEqual([])
  })

  it('always quotes the correlation, which is the figure that is always meaningful', () => {
    const rows = xyReadings(slice({ correlation: -0.5, phaseDeg: null, amplitudeRatio: null }))
    expect(rows.map((r) => r.label)).toEqual(['Correlation', 'Drawn from'])
    expect(rows[0].value).toBe('−0.500')
  })

  // The "absent rather than wrong" rule: the analyzer answers null where no single number is this
  // figure's phase, and a dashed row would invite reading one anyway.
  it('leaves a refused phase or ratio out rather than dashing it', () => {
    const rows = xyReadings(slice({ phaseDeg: null, amplitudeRatio: null }))
    expect(rows.some((r) => r.label === 'Phase')).toBe(false)
    expect(rows.some((r) => r.label === 'Right / left')).toBe(false)
  })

  it('quotes a phase and a ratio when there are any', () => {
    const rows = xyReadings(slice({ phaseDeg: -42.25, amplitudeRatio: 0.5 }))
    expect(rows.find((r) => r.label === 'Phase')?.value).toBe('−42.3°')
    expect(rows.find((r) => r.label === 'Right / left')?.value).toBe('0.500 · −6.02 dB')
  })

  it('says how much of the record the figure is drawn from', () => {
    const rows = xyReadings(slice({ frameCount: 8192, spanS: 0.0426667 }))
    // Through toLocaleString, as the row itself is: the thousands separator is the runner's and
    // asserting one of them would make this test fail on a machine in another locale.
    expect(rows.at(-1)?.value).toContain(`${(8192).toLocaleString()} pairs`)
  })

  it('gives every row something to say about itself', () => {
    for (const row of xyReadings(slice())) expect(row.title.length).toBeGreaterThan(30)
  })
})

describe('formatting', () => {
  // A sign that is never dropped: +0.000 and −0.000 are different readings of a stereo pair, and a
  // bare "0.000" hides which side of nothing the correlation fell.
  it('keeps the correlation signed and clamped', () => {
    expect(formatCorrelation(1)).toBe('+1.000')
    expect(formatCorrelation(-1)).toBe('−1.000')
    expect(formatCorrelation(0.5)).toBe('+0.500')
    expect(formatCorrelation(-0.0004)).toBe('−0.000')
    expect(formatCorrelation(1.2)).toBe('+1.000')
    expect(formatCorrelation(Number.NaN)).toBe('—')
  })

  it('keeps the phase signed, because a lead and a lag are different figures', () => {
    expect(formatPhaseDeg(90)).toBe('+90.0°')
    expect(formatPhaseDeg(-90)).toBe('−90.0°')
    expect(formatPhaseDeg(Number.NaN)).toBe('—')
  })

  // Both ways in one row: a ratio answers "how much of it is there", decibels answer "how far
  // down is it", and which one a reader wants depends on what they are checking.
  it('writes the amplitude ratio as a ratio and as decibels', () => {
    expect(formatAmplitudeRatio(1)).toBe('1.000 · +0.00 dB')
    expect(formatAmplitudeRatio(2)).toBe('2.000 · +6.02 dB')
    expect(formatAmplitudeRatio(0.5)).toBe('0.500 · −6.02 dB')
    expect(formatAmplitudeRatio(0)).toBe('—')
    expect(formatAmplitudeRatio(-1)).toBe('—')
  })

  it('writes the hover readout in both channels', () => {
    expect(xyCursorText(0.25, -0.5, 0.5)).toContain('Left')
    expect(xyCursorText(0.25, -0.5, 0.5)).toContain('right')
  })
})

describe('the plot box', () => {
  const insets = { left: 62, right: 18, top: 16, bottom: 34 }

  // Square by construction, not by letterboxing a viewport: an eight-by-eight graticule over an
  // oblong box has oblong cells, and a line drawn corner to corner is not the world line y = x --
  // and on this chart both of those are readings rather than decoration.
  it('is square whichever way the canvas is shaped', () => {
    for (const [w, h] of [[700, 600], [400, 900], [500, 500]] as const) {
      const box = xyPlotBox(w, h, insets)
      expect(box.size).toBeGreaterThan(0)
      expect(box.left + box.size).toBeLessThanOrEqual(w - insets.right + 1)
      expect(box.top + box.size).toBeLessThanOrEqual(h - insets.bottom + 1)
    }
  })

  it('takes the smaller of the two available spans', () => {
    // 700 - 62 - 18 = 620 across, 600 - 16 - 34 = 550 down.
    expect(xyPlotBox(700, 600, insets).size).toBe(550)
    // 300 - 62 - 18 = 220 across, which is now the smaller one.
    expect(xyPlotBox(300, 600, insets).size).toBe(220)
  })

  it('centres it horizontally in what the labels leave', () => {
    const box = xyPlotBox(700, 600, insets)
    const leftGap = box.left - insets.left
    const rightGap = 700 - insets.right - (box.left + box.size)
    expect(Math.abs(leftGap - rightGap)).toBeLessThanOrEqual(1)
  })

  it('never collapses, however little room it is given', () => {
    const box = xyPlotBox(10, 10, insets)
    expect(box.size).toBeGreaterThanOrEqual(1)
  })
})

describe('the correlation descriptions', () => {
  it('names a cause only for the band that has one', () => {
    expect(correlationDescription(-1)).toMatch(/inverted|out of phase/)
    expect(correlationDescription(1)).not.toMatch(/inverted/)
    expect(correlationDescription(0)).not.toMatch(/inverted/)
  })

  it('covers the whole range without a gap', () => {
    for (let r = -1; r <= 1.0001; r += 0.01) {
      expect(correlationDescription(r).length).toBeGreaterThan(0)
    }
    expect(correlationDescription(Number.NaN)).toBe('')
  })
})

describe('the truncation note', () => {
  it('says nothing when the whole window is drawn', () => {
    expect(xyTruncationNote(slice())).toBeNull()
    expect(xyTruncationNote(null)).toBeNull()
  })

  it('says how much is drawn, and how to move it', () => {
    const note = xyTruncationNote(slice({ frameCount: 8192, windowFrameCount: 192000, spanS: 0.0426667 }))
    expect(note).toContain((8192).toLocaleString())
    expect(note).toContain((192000).toLocaleString())
    expect(note).toMatch(/Narrow the window/)
  })
})
