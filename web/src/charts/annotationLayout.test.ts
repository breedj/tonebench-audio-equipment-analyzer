import { describe, expect, it } from 'vitest'
import {
  annotationFontPx,
  annotationMetrics,
  boxFromDrag,
  boxHandles,
  clampBoxToWindow,
  clampLabelOffset,
  layoutAnnotations,
  leaderBetween,
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
    freqHz2: null,
    levelDb2: null,
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

/** A region from 1 to 4 kHz between −40 and −80 dB, stored top-left first. */
function boxItem(partial: Partial<AnnotationLayoutItem> = {}): AnnotationLayoutItem {
  return item({
    style: 'box',
    text: 'Hum band',
    freqHz: 1000,
    levelDb: -40,
    freqHz2: 4000,
    levelDb2: -80,
    labelOffsetOctaves: 0.3,
    labelOffsetDb: 12,
    ...partial,
  })
}

describe('a box placement', () => {
  it('draws a box at the corners it stores, and in the same place at export size', () => {
    const k = 2
    const small = layoutAnnotations([boxItem()], plotOf(700, 400))[0]
    const large = layoutAnnotations([boxItem()], plotOf(700 * k, 400 * k))[0]

    const plot = plotOf(700, 400)
    expect(small.box!.x).toBeCloseTo(plot.xToPos(1000), 6)
    expect(small.box!.x + small.box!.w).toBeCloseTo(plot.xToPos(4000), 6)
    expect(small.box!.y).toBeCloseTo(plot.yToPos(-40), 6)
    expect(small.box!.y + small.box!.h).toBeCloseTo(plot.yToPos(-80), 6)

    expect(large.box!.x).toBeCloseTo(small.box!.x * k, 6)
    expect(large.box!.w).toBeCloseTo(small.box!.w * k, 6)
    expect(large.box!.h).toBeCloseTo(small.box!.h * k, 6)
  })

  it('clamps a box that runs past the window to the plot edge, where a point would be skipped', () => {
    const placed = layoutAnnotations([boxItem({ freqHz: 1, freqHz2: 4000, levelDb: 200 })], plotOf(700, 400))[0]
    expect(placed.visible).toBe(true)
    expect(placed.box!.x).toBe(0)
    expect(placed.box!.y).toBe(0)
    expect(placed.box!.x + placed.box!.w).toBeLessThanOrEqual(700.0001)
  })

  it('lays out a box although its low edge is left of the window', () => {
    // The early-return guard tests item.freqHz alone, so a box starting below the window would
    // vanish on the first zoom if it were laid out after it.
    const placed = layoutAnnotations([boxItem({ freqHz: 0.5, freqHz2: 2000 })], plotOf(700, 400))[0]
    expect(placed.visible).toBe(true)
    expect(placed.box!.w).toBeGreaterThan(0)
  })

  it('leaves a box entirely outside the window undrawn rather than as a sliver on the axis', () => {
    const placed = layoutAnnotations([boxItem({ freqHz: 40000, freqHz2: 80000 })], plotOf(700, 400))[0]
    expect(placed.visible).toBe(false)
    expect(placed.box).toBeNull()
  })

  it('runs the leader between the nearest edges of the region and the label', () => {
    const placed = layoutAnnotations([boxItem({ labelOffsetOctaves: 0.2, labelOffsetDb: 40 })], plotOf(700, 400))[0]
    expect(placed.leader).not.toBeNull()
    // The label is above the box, so the connector leaves the box's top edge.
    expect(placed.leader!.y1).toBeCloseTo(placed.box!.y, 6)
    expect(placed.leader!.y2).toBeCloseTo(placed.label.y + placed.label.h, 6)
  })

  it('drops the leader when the label has been dragged onto the box', () => {
    const placed = layoutAnnotations([boxItem({ labelOffsetOctaves: 0.5, labelOffsetDb: -20 })], plotOf(700, 400))[0]
    expect(placed.leader).toBeNull()
  })

  it('keeps a box label inside the plot at every corner', () => {
    const plot = plotOf(700, 400)
    for (const [octaves, db] of [
      [-6, 80],
      [-6, -300],
      [6, 80],
      [6, -300],
    ]) {
      const placed = layoutAnnotations([boxItem({ labelOffsetOctaves: octaves, labelOffsetDb: db })], plot)[0]
      expect(placed.label.x).toBeGreaterThanOrEqual(0)
      expect(placed.label.y).toBeGreaterThanOrEqual(0)
      expect(placed.label.x + placed.label.w).toBeLessThanOrEqual(700.0001)
      expect(placed.label.y + placed.label.h).toBeLessThanOrEqual(400.0001)
    }
  })
})

describe('leaderBetween', () => {
  it('joins two rectangles edge to edge', () => {
    const joined = leaderBetween({ x: 0, y: 0, w: 10, h: 10 }, { x: 30, y: 0, w: 10, h: 10 })!
    expect(joined.x1).toBe(10)
    expect(joined.x2).toBe(30)
  })

  it('leaves a rectangle square where the two overlap on one axis', () => {
    // Overlapping in x, apart in y: the line comes straight down out of the overlap's middle.
    const joined = leaderBetween({ x: 0, y: 0, w: 10, h: 10 }, { x: 2, y: 40, w: 6, h: 10 })!
    expect(joined.x1).toBe(joined.x2)
    expect(joined.y1).toBe(10)
    expect(joined.y2).toBe(40)
  })

  it('answers nothing when the label is on the box', () => {
    expect(leaderBetween({ x: 0, y: 0, w: 50, h: 50 }, { x: 10, y: 10, w: 10, h: 10 })).toBeNull()
  })
})

describe('boxHandles', () => {
  const metrics = annotationMetrics(plotOf(700, 400))

  it('lays nine grips on a box big enough to carry them', () => {
    const kinds = boxHandles({ x: 100, y: 100, w: 200, h: 150 }, metrics).map((h) => h.kind)
    expect(kinds).toHaveLength(9)
    expect(new Set(kinds)).toEqual(new Set(['nw', 'ne', 'se', 'sw', 'n', 's', 'e', 'w', 'move']))
  })

  it('keeps only the corners on a box too small for the rest, so grips cannot overlap', () => {
    const kinds = boxHandles({ x: 100, y: 100, w: 12, h: 10 }, metrics).map((h) => h.kind)
    expect(kinds).toEqual(['nw', 'ne', 'se', 'sw'])
  })
})

describe('boxFromDrag', () => {
  const start = { freqHz: 1000, freqHz2: 4000, levelDb: -40, levelDb2: -80 }

  it('resizes the one edge its handle names and leaves the other three alone', () => {
    const next = boxFromDrag('e', start, { freqHz: 8000, levelDb: -10 })
    expect(next).toEqual({ ...start, freqHz2: 8000 })
  })

  it('moves both edges of a corner', () => {
    const next = boxFromDrag('se', start, { freqHz: 8000, levelDb: -90 })
    expect(next).toEqual({ ...start, freqHz2: 8000, levelDb2: -90 })
  })

  it('crosses an edge past its opposite rather than refusing, leaving the caller to normalise', () => {
    const next = boxFromDrag('w', start, { freqHz: 9000, levelDb: -40 })
    expect(next.freqHz).toBe(9000)
    expect(next.freqHz2).toBe(4000)
  })

  it("keeps a moved box's extent in octaves and dB rather than in pixels", () => {
    const moved = boxFromDrag('move', start, { freqHz: 10000, levelDb: -20 })
    expect(Math.log2(moved.freqHz2 / moved.freqHz)).toBeCloseTo(2, 10)
    expect(moved.levelDb - moved.levelDb2).toBeCloseTo(40, 10)
  })
})

describe('clampBoxToWindow', () => {
  const window = { loHz: 20, hiHz: 20000, bottomDb: -150, topDb: 20 }
  const minSpan = { octaves: 0.05, db: 1 }

  it('translates a box back into the window rather than squashing it', () => {
    const clamped = clampBoxToWindow({ freqHz: 5, freqHz2: 20, levelDb: 80, levelDb2: 40 }, window, minSpan)
    expect(clamped.freqHz).toBeGreaterThanOrEqual(window.loHz)
    expect(Math.log2(clamped.freqHz2 / clamped.freqHz)).toBeCloseTo(2, 6)
    expect(clamped.levelDb).toBeLessThanOrEqual(window.topDb)
    expect(clamped.levelDb - clamped.levelDb2).toBeCloseTo(40, 6)
  })

  it('refuses to shrink a box below the size its own grips need', () => {
    const clamped = clampBoxToWindow({ freqHz: 1000, freqHz2: 1000.0001, levelDb: -40, levelDb2: -40 }, window, minSpan)
    expect(Math.log2(clamped.freqHz2 / clamped.freqHz)).toBeGreaterThanOrEqual(minSpan.octaves - 1e-9)
    expect(clamped.levelDb - clamped.levelDb2).toBeGreaterThanOrEqual(minSpan.db - 1e-9)
  })

  it('hands a nonsense window back the box it was given rather than a NaN', () => {
    const box = { freqHz: 1000, freqHz2: 4000, levelDb: -40, levelDb2: -80 }
    expect(clampBoxToWindow(box, { loHz: 0, hiHz: 0, bottomDb: 0, topDb: 0 }, minSpan)).toEqual(box)
  })
})
