import { describe, expect, it } from 'vitest'
import { freqTickLabels, freqTickSplits } from './freqAxis'

/** Ticks plus their text, the way a chart actually renders them. */
function axis(min: number, max: number, widthPx = 900): string[] {
  return freqTickLabels(freqTickSplits(min, max, widthPx), min, max).filter((s): s is string => s != null)
}

describe('freqTickSplits', () => {
  it('keeps the familiar 1/2/5 ladder over the full audio span', () => {
    expect(freqTickSplits(20, 20000, 900)).toEqual([20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000])
  })

  it('goes finer when a few decades have room for it', () => {
    expect(freqTickSplits(200, 900, 900)).toEqual([200, 300, 400, 500, 600, 700, 800, 900])
  })

  it('falls back to bare decades on a narrow chart', () => {
    expect(freqTickSplits(10, 20000, 200)).toEqual([10, 100, 1000, 10000])
  })

  it('still produces ticks inside a window with no round mantissa', () => {
    // uPlot's own log splits yield exactly one here (scaleMin, then +100 overshoots the span).
    const ticks = freqTickSplits(990, 1010, 900)
    expect(ticks.length).toBeGreaterThan(2)
    expect(ticks[0]).toBeGreaterThanOrEqual(990)
    expect(ticks[ticks.length - 1]).toBeLessThanOrEqual(1010)
  })

  it('never starts on the ragged scale minimum', () => {
    // 5.859375 Hz is a real bin edge (48 kHz / 8192); it must not become a tick, let alone a label.
    expect(freqTickSplits(5.859375, 24000, 900)).not.toContain(5.859375)
  })

  it('survives a degenerate span', () => {
    expect(freqTickSplits(0, 1000, 900)).toEqual([0])
    expect(freqTickSplits(100, 100, 900)).toEqual([100])
  })
})

describe('freqTickLabels', () => {
  it('leaves unlabelled splits null', () => {
    expect(freqTickLabels([null, 100, null], 20, 20000)).toEqual([null, '100', null])
  })

  it('labels the full audio span the way it always has', () => {
    expect(axis(20, 20000)).toEqual(['20', '50', '100', '200', '500', '1.0k', '2.0k', '5.0k', '10k', '20k'])
  })

  it('commits to one unit under a decade of span', () => {
    // Not "998, 1.000k, 1.002k" — every tick in a tight window reads in the same unit.
    const labels = axis(990, 1010)
    expect(labels.every((s) => !s.endsWith('k'))).toBe(true)
    expect(labels[0]).toBe('990')
    expect(labels[labels.length - 1]).toBe('1010')
  })

  it('adds decimals only where neighbouring ticks would otherwise read the same', () => {
    expect(freqTickLabels([1000, 1020, 1040], 1000, 1050)).toEqual(['1.00k', '1.02k', '1.04k'])
    expect(freqTickLabels([19.9, 20, 20.1], 19.8, 20.2)).toEqual(['19.9', '20', '20.1'])
  })

  it('rounds a ragged span edge instead of spelling it out', () => {
    expect(freqTickLabels([5.859375], 5.859375, 24000)).toEqual(['5.86'])
  })
})
