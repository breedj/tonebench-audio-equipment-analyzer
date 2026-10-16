import { describe, expect, it } from 'vitest'
import {
  annotationFontPx,
  clampLabelOffset,
  layoutAnnotations,
  type AnnotationLayoutItem,
  type AnnotationPlot,
} from './annotationLayout'

const WINDOW = { loHz: 20, hiHz: 20000, bottomDb: -150, topDb: 20 }

/**
 * A plot of the given size over the fixed window above, with a log frequency axis — the spectrum
 * chart's own geometry, and the only thing the layout needs of uPlot. `measureText` is proportional
 * to the font, which is what a real canvas does and what lets the scaling test below mean anything.
 */
function plotOf(width: number, height: number, scale = 1, left = 0, top = 0): AnnotationPlot {
  const spanOctaves = Math.log2(WINDOW.hiHz / WINDOW.loHz)
  return {
    left,
    top,
    width,
    height,
    xToPos: (freqHz) => left + (Math.log2(freqHz / WINDOW.loHz) / spanOctaves) * width,
    yToPos: (db) => top + ((WINDOW.topDb - db) / (WINDOW.topDb - WINDOW.bottomDb)) * height,
    scale,
    measureText: (text, fontPx) => text.length * 0.6 * fontPx,
  }
}

function item(partial: Partial<AnnotationLayoutItem> = {}): AnnotationLayoutItem {
  return {
    id: 1,
    number: 1,
    style: 'point',
    text: 'Mains hum',
    freqHz: 1000,
    levelDb: -60,
    labelOffsetOctaves: 0.45,
    labelOffsetDb: 14,
    ...partial,
  }
}

describe('annotationFontPx', () => {
  it('scales the font with the plot width, within its bounds', () => {
    expect(annotationFontPx(700)).toBeCloseTo(11, 10)
    expect(annotationFontPx(1400)).toBeCloseTo(22, 10)
    expect(annotationFontPx(100)).toBe(10)
    expect(annotationFontPx(10000)).toBe(26)
  })
})

describe('layoutAnnotations', () => {
  it('lays a label out at export size exactly where it lies on screen, scaled', () => {
    // The whole reason the offset is stored in octaves and dB: composeChartPng scales the plot
    // canvas onto the requested size, so a layout that is a fixed fraction of the plot survives it.
    // k is deliberately inside the font's bounds at both ends (11 px at 700, 22 px at 1400).
    const k = 2
    const small = layoutAnnotations([item()], plotOf(700, 400))[0]
    const large = layoutAnnotations([item()], plotOf(700 * k, 400 * k))[0]

    expect(large.fontPx).toBeCloseTo(small.fontPx * k, 6)
    expect(large.anchor!.x).toBeCloseTo(small.anchor!.x * k, 6)
    expect(large.anchor!.y).toBeCloseTo(small.anchor!.y * k, 6)
    expect(large.label.x).toBeCloseTo(small.label.x * k, 6)
    expect(large.label.y).toBeCloseTo(small.label.y * k, 6)
    expect(large.label.w).toBeCloseTo(small.label.w * k, 6)
    expect(large.label.h).toBeCloseTo(small.label.h * k, 6)
    expect(large.textX).toBeCloseTo(small.textX * k, 6)
    expect(large.badge!.r).toBeCloseTo(small.badge!.r * k, 6)
  })

  it('takes the font from the CSS width, so a hidpi canvas is not laid out half size', () => {
    const css = layoutAnnotations([item()], plotOf(700, 400, 1))[0]
    const device = layoutAnnotations([item()], plotOf(1400, 800, 2))[0]
    expect(device.fontPx).toBeCloseTo(css.fontPx * 2, 6)
  })

  it('keeps a label inside the plot at every corner', () => {
    const plot = plotOf(700, 400)
    for (const [freqHz, octaves, db] of [
      [25, -4, 60],
      [25, -4, -200],
      [19000, 4, 60],
      [19000, 4, -200],
    ]) {
      const placed = layoutAnnotations([item({ freqHz, labelOffsetOctaves: octaves, labelOffsetDb: db })], plot)[0]
      expect(placed.visible).toBe(true)
      expect(placed.label.x).toBeGreaterThanOrEqual(0)
      expect(placed.label.y).toBeGreaterThanOrEqual(0)
      expect(placed.label.x + placed.label.w).toBeLessThanOrEqual(700.0001)
      expect(placed.label.y + placed.label.h).toBeLessThanOrEqual(400.0001)
    }
  })

  it('skips an annotation whose frequency is outside the window rather than pinning it to the axis', () => {
    const outside = layoutAnnotations([item({ freqHz: 5 }), item({ id: 2, freqHz: 90000 })], plotOf(700, 400))
    expect(outside.map((p) => p.visible)).toEqual([false, false])
  })

  it('skips a point whose level cannot be converted to the chart unit', () => {
    const placed = layoutAnnotations([item({ levelDb: null })], plotOf(700, 400))[0]
    expect(placed.visible).toBe(false)
  })

  it('runs the leader from the marker to the nearest edge of the label', () => {
    const left = layoutAnnotations([item({ labelOffsetOctaves: -1.5, labelOffsetDb: 0 })], plotOf(700, 400))[0]
    expect(left.leader!.x2).toBeLessThan(left.anchor!.x)
    expect(left.leader!.x2).toBeCloseTo(left.label.x + left.label.w, 6)

    const right = layoutAnnotations([item({ labelOffsetOctaves: 1.5, labelOffsetDb: 0 })], plotOf(700, 400))[0]
    expect(right.leader!.x2).toBeCloseTo(right.label.x, 6)
  })

  it('stacks frequency labels that would overlap and stops at the rows that fit', () => {
    const crowded = Array.from({ length: 12 }, (_, i) =>
      item({ id: i + 1, number: i + 1, style: 'frequency', levelDb: null, freqHz: 1000 + i }),
    )
    const placed = layoutAnnotations(crowded, plotOf(700, 400))

    const rows = placed.filter((p) => p.visible).map((p) => p.label.y)
    expect(new Set(rows).size).toBe(rows.length)
    expect(placed.some((p) => !p.visible)).toBe(true)
    for (const p of placed.filter((q) => q.visible)) {
      expect(p.rule!.y1).toBe(0)
      expect(p.rule!.y2).toBe(400)
    }
  })

  it('puts two frequency labels that are far apart in the same row', () => {
    const placed = layoutAnnotations(
      [
        item({ style: 'frequency', levelDb: null, freqHz: 30 }),
        item({ id: 2, style: 'frequency', levelDb: null, freqHz: 18000 }),
      ],
      plotOf(700, 400),
    )
    expect(placed[0].label.y).toBe(placed[1].label.y)
  })

  it('leaves room for the ordinal only when the numbers are drawn', () => {
    const numbered = layoutAnnotations([item()], plotOf(700, 400), true)[0]
    const bare = layoutAnnotations([item()], plotOf(700, 400), false)[0]
    expect(bare.badge).toBeNull()
    expect(bare.label.w).toBeLessThan(numbered.label.w)
  })
})

describe('clampLabelOffset', () => {
  it('leaves an offset that already keeps the label on the plot alone', () => {
    const kept = clampLabelOffset({
      freqHz: 1000,
      levelDb: -60,
      octaves: 0.45,
      db: 14,
      window: WINDOW,
      label: { w: 80, h: 22 },
      plot: { width: 700, height: 400 },
    })
    expect(kept.octaves).toBeCloseTo(0.45, 6)
    expect(kept.db).toBeCloseTo(14, 6)
  })

  it('clamps a drag past the edge back to one that keeps the label on the plot', () => {
    const clamped = clampLabelOffset({
      freqHz: 1000,
      levelDb: -60,
      octaves: 20,
      db: 400,
      window: WINDOW,
      label: { w: 80, h: 22 },
      plot: { width: 700, height: 400 },
    })
    expect(1000 * Math.pow(2, clamped.octaves)).toBeLessThan(WINDOW.hiHz)
    expect(-60 + clamped.db).toBeLessThan(WINDOW.topDb)
    expect(-60 + clamped.db).toBeGreaterThan(WINDOW.bottomDb)
  })

  it('hands a nonsense window back the offset it was given rather than a NaN', () => {
    const same = clampLabelOffset({
      freqHz: 1000,
      levelDb: -60,
      octaves: 0.5,
      db: 10,
      window: { loHz: 0, hiHz: 0, bottomDb: 0, topDb: 0 },
      label: { w: 80, h: 22 },
      plot: { width: 0, height: 0 },
    })
    expect(same).toEqual({ octaves: 0.5, db: 10 })
  })
})
