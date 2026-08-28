import { describe, expect, it } from 'vitest'
import { blendToward, sweepLineStyle, SWEEP_OVERLAY_DASH } from './sweepOverlayStyles'
import { MAX_VISIBLE_OVERLAYS } from '../composables/useMeasurements'

/** The eight TRACES entries from ThdSweepChart.vue, in the same order. */
const TRACES = [
  { key: 'magnitude', color: ['#be185d', '#f472b6'], width: 1 },
  { key: 'thd', color: ['#111827', '#e5e7eb'], width: 1 },
  { key: 'd2', color: ['#1d6fd0', '#60a5fa'], width: 0.8 },
  { key: 'd3', color: ['#e07b16', '#fbbf24'], width: 0.8 },
  { key: 'd4', color: ['#1a7a3c', '#4ade80'], width: 0.8 },
  { key: 'd5', color: ['#c62828', '#f87171'], width: 0.8 },
  { key: 'd6plus', color: ['#6b3fa0', '#c084fc'], width: 0.8 },
  { key: 'noise', color: ['#0d7d84', '#2dd4bf'], width: 0.8 },
] as const

/** Every line the chart can draw at once: 4 runs x 2 channels x 8 traces. */
function everyLine(thickLines: boolean, isDark: boolean) {
  const lines = []
  for (let runIdx = 0; runIdx <= MAX_VISIBLE_OVERLAYS; runIdx++)
    for (const channel of [0, 1])
      for (const trace of TRACES)
        lines.push({
          id: `run${runIdx}-ch${channel}-${trace.key}`,
          ...sweepLineStyle({
            runIdx,
            channel,
            color: trace.color,
            traceWidth: trace.width,
            thickLines,
            isDark,
          }),
        })
  return lines
}

const styleKey = (s: { stroke: string; width: number; dash: number[] | undefined }) =>
  `${s.stroke}|${s.width.toFixed(3)}|${(s.dash ?? []).join(',')}`

describe('sweepLineStyle', () => {
  // The bug this module exists to fix: dash used to encode run *and* channel by concatenation, so
  // the live right channel ([6,3]) and a history run ([20,5], [20,5,6,3]) drew as the same picture.
  it.each([
    ['thin light', false, false],
    ['thin dark', false, true],
    ['thick light', true, false],
    ['thick dark', true, true],
  ])('gives every simultaneously-drawn line a distinct style (%s)', (_name, thickLines, isDark) => {
    const lines = everyLine(thickLines, isDark)
    const seen = new Map<string, string>()

    for (const line of lines) {
      const key = styleKey(line)
      expect(seen.get(key), `${line.id} is indistinguishable from ${seen.get(key)}`).toBeUndefined()
      seen.set(key, line.id)
    }

    expect(seen.size).toBe(4 * 2 * TRACES.length)
  })

  it('never draws a line thinner than the sub-pixel floor', () => {
    for (const [thickLines, isDark] of [
      [false, false],
      [false, true],
      [true, false],
      [true, true],
    ] as const)
      for (const line of everyLine(thickLines, isDark)) expect(line.width).toBeGreaterThanOrEqual(0.9)
  })

  it('uses dash for the run and nothing else', () => {
    // Live is solid on both channels; a history run's two channels share one pattern, so dash alone
    // never distinguishes L from R -- that is lightness and width's job.
    expect(sweepLineStyle(base({ runIdx: 0, channel: 0 })).dash).toBeUndefined()
    expect(sweepLineStyle(base({ runIdx: 0, channel: 1 })).dash).toBeUndefined()

    for (let run = 1; run <= MAX_VISIBLE_OVERLAYS; run++) {
      const left = sweepLineStyle(base({ runIdx: run, channel: 0 })).dash
      const right = sweepLineStyle(base({ runIdx: run, channel: 1 })).dash
      expect(left).toEqual(SWEEP_OVERLAY_DASH[run - 1])
      expect(right).toEqual(left)
    }
  })

  it('has one dash pattern per overlay slot, all distinct', () => {
    expect(SWEEP_OVERLAY_DASH).toHaveLength(MAX_VISIBLE_OVERLAYS)
    expect(new Set(SWEEP_OVERLAY_DASH.map((d) => d.join(',')))).toHaveProperty('size', MAX_VISIBLE_OVERLAYS)
  })

  it('separates the channels by both lightness and width', () => {
    const left = sweepLineStyle(base({ runIdx: 0, channel: 0 }))
    const right = sweepLineStyle(base({ runIdx: 0, channel: 1 }))
    expect(right.stroke).not.toBe(left.stroke)
    expect(right.width).toBeLessThan(left.width)
  })

  // The blend ladder is deliberately not monotonic across runs -- history-left sits *between* the
  // two live channels. Lightness reads within a run; dash is what names the run.
  it('keeps history left darker than live right', () => {
    const liveRight = sweepLineStyle(base({ runIdx: 0, channel: 1 })).stroke
    const historyLeft = sweepLineStyle(base({ runIdx: 1, channel: 0 })).stroke
    expect(luminance(historyLeft)).toBeLessThan(luminance(liveRight))
  })
})

describe('blendToward', () => {
  it('returns the source unchanged at 0 and the target at 1', () => {
    expect(blendToward('#1d6fd0', '#ffffff', 0)).toBe('#1d6fd0')
    expect(blendToward('#1d6fd0', '#ffffff', 1)).toBe('#ffffff')
  })

  it('interpolates channelwise at the midpoint', () => {
    expect(blendToward('#000000', '#ffffff', 0.5)).toBe('#808080')
  })

  it('clamps out-of-range amounts rather than producing invalid hex', () => {
    expect(blendToward('#1d6fd0', '#ffffff', -1)).toBe('#1d6fd0')
    expect(blendToward('#1d6fd0', '#ffffff', 2)).toBe('#ffffff')
  })

  it('leaves a malformed colour alone instead of throwing', () => {
    expect(blendToward('not-a-colour', '#ffffff', 0.5)).toBe('not-a-colour')
  })
})

function base(over: { runIdx: number; channel: number }) {
  return {
    color: ['#1d6fd0', '#60a5fa'] as const,
    traceWidth: 0.8,
    thickLines: true,
    isDark: false,
    ...over,
  }
}

/** Rough perceptual weighting — enough to order two tints of one hue. */
function luminance(hex: string): number {
  const n = parseInt(hex.slice(1), 16)
  return 0.2126 * ((n >> 16) & 0xff) + 0.7152 * ((n >> 8) & 0xff) + 0.0722 * (n & 0xff)
}
