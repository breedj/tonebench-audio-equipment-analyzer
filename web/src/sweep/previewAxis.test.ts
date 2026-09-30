import { describe, expect, it } from 'vitest'
import type { SweepPreviewSeries } from '../api/client'
import {
  fitPreviewAxis,
  fitScopePreviewAxis,
  previewAxisSplits,
  previewAxisTickLabels,
  scopeAxisTickLabels,
} from './previewAxis'

function preview(max: number[]): SweepPreviewSeries {
  return { x: max.map((_, i) => i + 1), min: max.map((v) => v - 10), max }
}

describe('fitPreviewAxis', () => {
  it('covers every preview it is given, snapped outwards to the grid', () => {
    const range = fitPreviewAxis([preview([-18.2, -95, -130.4]), preview([-42, -151.7])])

    // The union is -151.7 .. -18.2, so both ends step out to the enclosing 20 dB lines.
    expect(range).toEqual({ min: -160, max: 0 })
  })

  it('starts and ends on a grid line whichever step it is asked for', () => {
    const range = fitPreviewAxis([preview([-3.5, -47])], 10)
    expect(range).toEqual({ min: -50, max: 0 })
  })

  it('ignores the previews that are not there', () => {
    // A channel disabled in the generator contributes no series at all -- the other channel still
    // has to decide the window, rather than the pair reading as no data.
    expect(fitPreviewAxis([null, undefined, preview([-33])])).toEqual({ min: -40, max: -20 })
  })

  it('is null when nothing usable was given', () => {
    expect(fitPreviewAxis([])).toBeNull()
    expect(fitPreviewAxis([null])).toBeNull()
    expect(fitPreviewAxis([preview([])])).toBeNull()
  })

  /**
   * SpectrumPipeline.BuildFftPreview floors an all-zero bucket at double.Epsilon, which arrives as
   * about -3230 dB. One of those in the union would stretch the axis over a hundred grid lines of
   * nothing, which is the whole of the plot spent on a dead column.
   */
  it('ignores a dead column and a non-finite value rather than ranging to it', () => {
    expect(fitPreviewAxis([preview([-3233, -110, Number.NEGATIVE_INFINITY, Number.NaN, -25])])).toEqual({
      min: -120,
      max: -20,
    })
  })

  it('never returns a zero-height window', () => {
    // A trace sitting exactly on a line: floor and ceil agree, and uPlot would draw the whole
    // record as one horizontal line through the middle of the plot.
    expect(fitPreviewAxis([preview([-40, -40])])).toEqual({ min: -40, max: -20 })
  })
})

describe('previewAxisSplits', () => {
  it('lists every grid line including both ends', () => {
    expect(previewAxisSplits(-60, 0)).toEqual([-60, -40, -20, 0])
  })

  it('keeps a line on either edge that is not exactly representable', () => {
    // Neither -0.3/0.1 nor the running sum of 0.1s is exact in binary, so both ends of this window
    // need their slack: without it the lines at -0.3 and at +0.3 are both dropped.
    expect(previewAxisSplits(-0.3, 0.3, 0.1).length).toBe(7)
  })

  it('starts inside the window when its ends are not on the grid', () => {
    expect(previewAxisSplits(-55, 5)).toEqual([-40, -20, 0])
  })

  it('is empty for a window that makes no sense', () => {
    expect(previewAxisSplits(0, -20)).toEqual([])
    expect(previewAxisSplits(Number.NaN, 0)).toEqual([])
  })
})

describe('previewAxisTickLabels', () => {
  it('numbers every line when they are far enough apart', () => {
    expect(previewAxisTickLabels(previewAxisSplits(-60, 0), 120)).toEqual(['-60', '-40', '-20', '0'])
  })

  /**
   * The simulator's floor reaches -220 dBV, which is thirteen lines in a 150 px chart -- they
   * overlapped into an unreadable stack. The grid itself is untouched; only the numbering thins.
   */
  it('thins the numbering on a deep window, and keeps it on round multiples', () => {
    const splits = previewAxisSplits(-220, 20)
    const labels = previewAxisTickLabels(splits, 120)

    expect(splits.length).toBe(13)
    // 13 lines in 120 px is 10 px apart, so every third one is numbered.
    expect(labels.filter((l) => l !== null)).toEqual(['-180', '-120', '-60', '0'])
    // Whatever the stride, zero is one of the numbers a reader can hang the rest off.
    expect(labels[splits.indexOf(0)]).toBe('0')
  })

  it('survives a plot that has not been laid out yet', () => {
    expect(previewAxisTickLabels([-20, 0], 0)).toEqual(['-20', '0'])
    expect(previewAxisTickLabels([], 150)).toEqual([])
  })
})

describe('fitScopePreviewAxis', () => {
  function waveform(peak: number): SweepPreviewSeries {
    return { x: [0, 1, 2], min: [0, -peak, 0], max: [0, peak * 0.9, 0] }
  }

  it('is symmetric about zero and lands on a 1-2-5 grid', () => {
    // 0.145 V peak: 0.05 V steps, three of them, so the axis is +/-0.15.
    expect(fitScopePreviewAxis([waveform(0.145)])).toEqual({ min: -0.15, max: 0.15, stepV: 0.05 })
  })

  it('keeps two to four divisions a side across the levels a sweep spans', () => {
    for (const peak of [0.0007, 0.02, 0.35, 1, 3.4, 7.9]) {
      const range = fitScopePreviewAxis([waveform(peak)])!
      expect(range.max / range.stepV).toBeGreaterThanOrEqual(2)
      expect(range.max / range.stepV).toBeLessThanOrEqual(4.0001)
      expect(range.max).toBeGreaterThanOrEqual(peak)
      expect(range.min).toBe(-range.max)
    }
  })

  it('covers every point given, not just the loudest series in one of them', () => {
    const range = fitScopePreviewAxis([waveform(0.2), null, waveform(1.9), undefined])!
    expect(range.max).toBeGreaterThanOrEqual(1.9)
  })

  /** The trough is drawn too -- the preview is a min/max envelope per channel, unlike the spectrum's. */
  it('scans the min series as well as the max', () => {
    const clippedBelow: SweepPreviewSeries = { x: [0, 1], min: [-3, -3], max: [0.5, 0.5] }
    expect(fitScopePreviewAxis([clippedBelow])!.max).toBeGreaterThanOrEqual(3)
  })

  it('still gives a drawable axis for a channel that measured silence', () => {
    const range = fitScopePreviewAxis([{ x: [0, 1], min: [0, 0], max: [0, 0] }])!
    expect(range.max).toBeGreaterThan(0)
    expect(range.min).toBe(-range.max)
  })

  it('is null when nothing usable was given', () => {
    expect(fitScopePreviewAxis([])).toBeNull()
    expect(fitScopePreviewAxis([null, undefined])).toBeNull()
  })
})

describe('scopeAxisTickLabels', () => {
  it('prints as many decimals as the step needs, and no more', () => {
    // The splits accumulate, so the 0.15 line arrives as 0.15000000000000002.
    expect(scopeAxisTickLabels(previewAxisSplits(-0.15, 0.15, 0.05), 200, 0.05)).toEqual([
      '-0.15', '-0.10', '-0.05', '0.00', '0.05', '0.10', '0.15',
    ])
    expect(scopeAxisTickLabels(previewAxisSplits(-4, 4, 1), 200, 1)).toEqual([
      '-4', '-3', '-2', '-1', '0', '1', '2', '3', '4',
    ])
  })

  it('thins the numbering when the lines crowd, keeping zero', () => {
    // 9 lines in 60 px is 7.5 px apart, so every third one is numbered.
    const labels = scopeAxisTickLabels(previewAxisSplits(-4, 4, 1), 60, 1)
    expect(labels.filter((l) => l !== null)).toEqual(['-3', '0', '3'])
  })
})
