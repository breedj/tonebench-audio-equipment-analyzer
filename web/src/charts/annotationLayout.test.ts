import { describe, expect, it } from 'vitest'
import {
  annotationFontPx,
  annotationMetrics,
  boxFromDrag,
  boxHandles,
  boxSpaces,
  clampBoxToWindow,
  clampLabelOffset,
  defaultLabelOffset,
  drawsAnnotation,
  layoutAnnotations,
  leaderBetween,
  type AnnotationLayoutItem,
  type AnnotationPlot,
} from './annotationLayout'
import { DEFAULT_LABEL_OFFSET_FRACTION } from '../measurements/chartAnnotations'

const WINDOW = { loHz: 20, hiHz: 20000, bottomDb: -150, topDb: 20 }

/**
 * A plot of the given size over the fixed window above, with a log frequency axis against a dB one —
 * the spectrum chart's own geometry, and the only thing the layout needs of a chart. `measureText`
 * is proportional to the font, which is what a real canvas does and what lets the scaling test below
 * mean anything.
 */
function plotOf(width: number, height: number, scale = 1, left = 0, top = 0): AnnotationPlot {
  const spanOctaves = Math.log2(WINDOW.hiHz / WINDOW.loHz)
  return {
    left,
    top,
    width,
    height,
    x: {
      quantity: 'hz',
      posOf: (freqHz) => left + (Math.log2(freqHz / WINDOW.loHz) / spanOctaves) * width,
      valOf: (pos) => WINDOW.loHz * Math.pow(2, ((pos - left) / width) * spanOctaves),
      window: { lo: WINDOW.loHz, hi: WINDOW.hiHz },
    },
    y: {
      quantity: 'dbv',
      posOf: (db) => top + ((WINDOW.topDb - db) / (WINDOW.topDb - WINDOW.bottomDb)) * height,
      valOf: (pos) => WINDOW.topDb - ((pos - top) / height) * (WINDOW.topDb - WINDOW.bottomDb),
      window: { lo: WINDOW.bottomDb, hi: WINDOW.topDb },
    },
    scale,
    measureText: (text, fontPx) => text.length * 0.6 * fontPx,
  }
}

/** The impulse chart's geometry: a linear time axis that crosses zero, against volts. */
function timePlotOf(width = 700, height = 400): AnnotationPlot {
  const lo = -2
  const hi = 20
  return {
    left: 0,
    top: 0,
    width,
    height,
    x: {
      quantity: 'ms',
      posOf: (ms) => ((ms - lo) / (hi - lo)) * width,
      valOf: (pos) => lo + (pos / width) * (hi - lo),
      window: { lo, hi },
    },
    y: {
      quantity: 'volt',
      posOf: (v) => ((1 - v) / 2) * height,
      valOf: (pos) => 1 - (pos / height) * 2,
      window: { lo: -1, hi: 1 },
    },
    scale: 1,
    measureText: (text, fontPx) => text.length * 0.6 * fontPx,
  }
}

function item(partial: Partial<AnnotationLayoutItem> = {}): AnnotationLayoutItem {
  return {
    id: 1,
    number: 1,
    style: 'point',
    text: 'Mains hum',
    x: 1000,
    y: -60,
    x2: null,
    y2: null,
    xQuantity: 'hz',
    yQuantity: 'dbv',
    labelOffsetX: 0.45,
    labelOffsetY: 14,
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

describe('drawsAnnotation', () => {
  it('draws an annotation stored in the quantities this plot is showing', () => {
    expect(drawsAnnotation(item(), plotOf(700, 400))).toBe(true)
  })

  it('refuses one stored in another x quantity, which is what a sweep axis switch produces', () => {
    expect(drawsAnnotation(item({ xQuantity: 'outputWatt' }), plotOf(700, 400))).toBe(false)
  })

  it('refuses one stored against another y quantity, which is what a response view switch produces', () => {
    expect(drawsAnnotation(item({ yQuantity: 'db' }), plotOf(700, 400))).toBe(false)
  })

  it('judges a rule on its x alone, since it names no level at all', () => {
    const rule = item({ style: 'frequency', y: null, yQuantity: 'db' })
    expect(drawsAnnotation(rule, plotOf(700, 400))).toBe(true)
  })
})

describe('layoutAnnotations', () => {
  it('lays a label out at export size exactly where it lies on screen, scaled', () => {
    // The whole reason the offset is stored in the axis's own space: composeChartPng scales the plot
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

  it('skips an annotation whose stored quantity this axis cannot draw, rather than placing its number somewhere', () => {
    // The central rule of the whole generalisation: switch a sweep's x axis from hertz to watts and
    // the hertz notes stop being drawn. Nothing moves them and nothing deletes them.
    const placed = layoutAnnotations(
      [item({ xQuantity: 'outputWatt' }), item({ id: 2, yQuantity: 'pct' }), item({ id: 3 })],
      plotOf(700, 400),
    )
    expect(placed.map((p) => p.visible)).toEqual([false, false, true])
    expect(placed[0].anchor).toBeNull()
    expect(placed[0].box).toBeNull()
  })

  it('applies a label offset as a ratio on a ratio axis', () => {
    // 1 octave of x, 20 dB of y, on a frequency-against-level plot: the label lands two thousand
    // hertz along and twenty decibels up.
    const plot = plotOf(700, 400)
    const placed = layoutAnnotations([item({ labelOffsetX: 1, labelOffsetY: 20 })], plot)[0]
    expect(placed.label.x + placed.label.w / 2).toBeCloseTo(plot.x.posOf(2000), 6)
    expect(placed.label.y + placed.label.h / 2).toBeCloseTo(plot.y.posOf(-40), 6)
  })

  it('applies a label offset as a difference on a linear axis', () => {
    // The same field means 4 *milliseconds* here, not 4 octaves of time -- which is the whole reason
    // the space belongs to the quantity rather than being log2 everywhere.
    const plot = timePlotOf()
    const placed = layoutAnnotations(
      [item({ x: 5, y: 0.2, xQuantity: 'ms', yQuantity: 'volt', labelOffsetX: 4, labelOffsetY: 0.3 })],
      plot,
    )[0]
    expect(placed.visible).toBe(true)
    expect(placed.label.x + placed.label.w / 2).toBeCloseTo(plot.x.posOf(9), 6)
    expect(placed.label.y + placed.label.h / 2).toBeCloseTo(plot.y.posOf(0.5), 6)
  })

  it('places an annotation across t = 0, where a log axis would have no position for it', () => {
    const plot = timePlotOf()
    const placed = layoutAnnotations(
      [
        item({ x: 0, y: 0.5, xQuantity: 'ms', yQuantity: 'volt', labelOffsetX: 1, labelOffsetY: 0.2 }),
        item({ id: 2, x: -1, y: 0, xQuantity: 'ms', yQuantity: 'volt', labelOffsetX: 1, labelOffsetY: 0.2 }),
      ],
      plot,
    )
    expect(placed.map((p) => p.visible)).toEqual([true, true])
    expect(placed[0].anchor!.x).toBeCloseTo(plot.x.posOf(0), 6)
    expect(placed[1].anchor!.x).toBeCloseTo(plot.x.posOf(-1), 6)
  })

  it('keeps a label inside the plot at every corner', () => {
    const plot = plotOf(700, 400)
    for (const [x, offsetX, offsetY] of [
      [25, -4, 60],
      [25, -4, -200],
      [19000, 4, 60],
      [19000, 4, -200],
    ]) {
      const placed = layoutAnnotations([item({ x, labelOffsetX: offsetX, labelOffsetY: offsetY })], plot)[0]
      expect(placed.visible).toBe(true)
      expect(placed.label.x).toBeGreaterThanOrEqual(0)
      expect(placed.label.y).toBeGreaterThanOrEqual(0)
      expect(placed.label.x + placed.label.w).toBeLessThanOrEqual(700.0001)
      expect(placed.label.y + placed.label.h).toBeLessThanOrEqual(400.0001)
    }
  })

  it('skips an annotation whose x is outside the window rather than pinning it to the axis', () => {
    const outside = layoutAnnotations([item({ x: 5 }), item({ id: 2, x: 90000 })], plotOf(700, 400))
    expect(outside.map((p) => p.visible)).toEqual([false, false])
  })

  it('skips a point whose level cannot be converted to the chart unit', () => {
    // The spectrum's density unit with no ENBW: its axis answers a non-finite position, and nothing
    // is drawn rather than something being drawn in the wrong place.
    const plot = plotOf(700, 400)
    const unconvertible: AnnotationPlot = { ...plot, y: { ...plot.y, posOf: () => Number.NaN } }
    expect(layoutAnnotations([item()], unconvertible)[0].visible).toBe(false)
    expect(layoutAnnotations([item({ y: null })], plot)[0].visible).toBe(false)
  })

  it('runs the leader from the marker to the nearest edge of the label', () => {
    const left = layoutAnnotations([item({ labelOffsetX: -1.5, labelOffsetY: 0 })], plotOf(700, 400))[0]
    expect(left.leader!.x2).toBeLessThan(left.anchor!.x)
    expect(left.leader!.x2).toBeCloseTo(left.label.x + left.label.w, 6)

    const right = layoutAnnotations([item({ labelOffsetX: 1.5, labelOffsetY: 0 })], plotOf(700, 400))[0]
    expect(right.leader!.x2).toBeCloseTo(right.label.x, 6)
  })

  it('stacks rule labels that would overlap and stops at the rows that fit', () => {
    const crowded = Array.from({ length: 12 }, (_, i) =>
      item({ id: i + 1, number: i + 1, style: 'frequency', y: null, x: 1000 + i }),
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

  it('puts two rule labels that are far apart in the same row', () => {
    const placed = layoutAnnotations(
      [item({ style: 'frequency', y: null, x: 30 }), item({ id: 2, style: 'frequency', y: null, x: 18000 })],
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

describe('defaultLabelOffset', () => {
  it('offers a fraction of the plot, in whatever space each axis measures', () => {
    // This plot's window is the FFT screen's own default -- 20 Hz to 20 kHz, 20 to -150 dBV -- which
    // is where the 0.45 octaves and 14 dB this feature shipped with were chosen. The two fractions
    // have to reproduce them here, or the chart they were picked on has changed.
    const log = defaultLabelOffset(plotOf(700, 400), DEFAULT_LABEL_OFFSET_FRACTION)
    expect(log.labelOffsetX).toBeCloseTo(0.45, 1)
    expect(log.labelOffsetY).toBeCloseTo(14, 0)

    // The same two fractions on a linear axis are a fraction of its own span: milliseconds and volts.
    const linear = defaultLabelOffset(timePlotOf(), DEFAULT_LABEL_OFFSET_FRACTION)
    expect(linear.labelOffsetX).toBeCloseTo(22 * DEFAULT_LABEL_OFFSET_FRACTION.x, 6)
    expect(linear.labelOffsetY).toBeCloseTo(2 * DEFAULT_LABEL_OFFSET_FRACTION.y, 6)
  })

  it('answers zero rather than a NaN for a window its axis has no space for', () => {
    const plot = plotOf(700, 400)
    const broken: AnnotationPlot = { ...plot, x: { ...plot.x, window: { lo: -5, hi: 10 } } }
    expect(defaultLabelOffset(broken, DEFAULT_LABEL_OFFSET_FRACTION).labelOffsetX).toBe(0)
  })
})

describe('clampLabelOffset', () => {
  const spaces = boxSpaces('hz', 'dbv')
  const window = { xLo: WINDOW.loHz, xHi: WINDOW.hiHz, yLo: WINDOW.bottomDb, yHi: WINDOW.topDb }

  it('leaves an offset that already keeps the label on the plot alone', () => {
    const kept = clampLabelOffset({
      x: 1000,
      y: -60,
      offsetX: 0.45,
      offsetY: 14,
      window,
      spaces,
      label: { w: 80, h: 22 },
      plot: { width: 700, height: 400 },
    })
    expect(kept.offsetX).toBeCloseTo(0.45, 6)
    expect(kept.offsetY).toBeCloseTo(14, 6)
  })

  it('clamps a drag past the edge back to one that keeps the label on the plot', () => {
    const clamped = clampLabelOffset({
      x: 1000,
      y: -60,
      offsetX: 20,
      offsetY: 400,
      window,
      spaces,
      label: { w: 80, h: 22 },
      plot: { width: 700, height: 400 },
    })
    expect(1000 * Math.pow(2, clamped.offsetX)).toBeLessThan(WINDOW.hiHz)
    expect(-60 + clamped.offsetY).toBeLessThan(WINDOW.topDb)
    expect(-60 + clamped.offsetY).toBeGreaterThan(WINDOW.bottomDb)
  })

  it('clamps in the stored quantity, so the same drag on a linear axis is a plain difference', () => {
    const clamped = clampLabelOffset({
      x: 5,
      y: 0,
      offsetX: 500,
      offsetY: 500,
      window: { xLo: -2, xHi: 20, yLo: -1, yHi: 1 },
      spaces: boxSpaces('ms', 'volt'),
      label: { w: 80, h: 22 },
      plot: { width: 700, height: 400 },
    })
    expect(5 + clamped.offsetX).toBeLessThanOrEqual(20)
    expect(clamped.offsetY).toBeLessThanOrEqual(1)
  })

  it('hands a nonsense window back the offset it was given rather than a NaN', () => {
    const same = clampLabelOffset({
      x: 1000,
      y: -60,
      offsetX: 0.5,
      offsetY: 10,
      window: { xLo: 0, xHi: 0, yLo: 0, yHi: 0 },
      spaces,
      label: { w: 80, h: 22 },
      plot: { width: 0, height: 0 },
    })
    expect(same).toEqual({ offsetX: 0.5, offsetY: 10 })
  })
})

/** A region from 1 to 4 kHz between −40 and −80 dB, stored top-left first. */
function boxItem(partial: Partial<AnnotationLayoutItem> = {}): AnnotationLayoutItem {
  return item({
    style: 'box',
    text: 'Hum band',
    x: 1000,
    y: -40,
    x2: 4000,
    y2: -80,
    labelOffsetX: 0.3,
    labelOffsetY: 12,
    ...partial,
  })
}

describe('a box placement', () => {
  it('draws a box at the corners it stores, and in the same place at export size', () => {
    const k = 2
    const small = layoutAnnotations([boxItem()], plotOf(700, 400))[0]
    const large = layoutAnnotations([boxItem()], plotOf(700 * k, 400 * k))[0]

    const plot = plotOf(700, 400)
    expect(small.box!.x).toBeCloseTo(plot.x.posOf(1000), 6)
    expect(small.box!.x + small.box!.w).toBeCloseTo(plot.x.posOf(4000), 6)
    expect(small.box!.y).toBeCloseTo(plot.y.posOf(-40), 6)
    expect(small.box!.y + small.box!.h).toBeCloseTo(plot.y.posOf(-80), 6)

    expect(large.box!.x).toBeCloseTo(small.box!.x * k, 6)
    expect(large.box!.w).toBeCloseTo(small.box!.w * k, 6)
    expect(large.box!.h).toBeCloseTo(small.box!.h * k, 6)
  })

  it('clamps a box that runs past the window to the plot edge, where a point would be skipped', () => {
    const placed = layoutAnnotations([boxItem({ x: 1, x2: 4000, y: 200 })], plotOf(700, 400))[0]
    expect(placed.visible).toBe(true)
    expect(placed.box!.x).toBe(0)
    expect(placed.box!.y).toBe(0)
    expect(placed.box!.x + placed.box!.w).toBeLessThanOrEqual(700.0001)
  })

  it('lays out a box although its low edge is left of the window', () => {
    // The early-return guard tests item.x alone, so a box starting below the window would vanish on
    // the first zoom if it were laid out after it.
    const placed = layoutAnnotations([boxItem({ x: 0.5, x2: 2000 })], plotOf(700, 400))[0]
    expect(placed.visible).toBe(true)
    expect(placed.box!.w).toBeGreaterThan(0)
  })

  it('leaves a box entirely outside the window undrawn rather than as a sliver on the axis', () => {
    const placed = layoutAnnotations([boxItem({ x: 40000, x2: 80000 })], plotOf(700, 400))[0]
    expect(placed.visible).toBe(false)
    expect(placed.box).toBeNull()
  })

  it('runs the leader between the nearest edges of the region and the label', () => {
    const placed = layoutAnnotations([boxItem({ labelOffsetX: 0.2, labelOffsetY: 40 })], plotOf(700, 400))[0]
    expect(placed.leader).not.toBeNull()
    // The label is above the box, so the connector leaves the box's top edge.
    expect(placed.leader!.y1).toBeCloseTo(placed.box!.y, 6)
    expect(placed.leader!.y2).toBeCloseTo(placed.label.y + placed.label.h, 6)
  })

  it('drops the leader when the label has been dragged onto the box', () => {
    const placed = layoutAnnotations([boxItem({ labelOffsetX: 0.5, labelOffsetY: -20 })], plotOf(700, 400))[0]
    expect(placed.leader).toBeNull()
  })

  it('keeps a box label inside the plot at every corner', () => {
    const plot = plotOf(700, 400)
    for (const [offsetX, offsetY] of [
      [-6, 80],
      [-6, -300],
      [6, 80],
      [6, -300],
    ]) {
      const placed = layoutAnnotations([boxItem({ labelOffsetX: offsetX, labelOffsetY: offsetY })], plot)[0]
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
  const start = { x: 1000, x2: 4000, y: -40, y2: -80 }
  const spaces = boxSpaces('hz', 'dbv')

  it('resizes the one edge its handle names and leaves the other three alone', () => {
    const next = boxFromDrag('e', start, { x: 8000, y: -10 }, spaces)
    expect(next).toEqual({ ...start, x2: 8000 })
  })

  it('moves both edges of a corner', () => {
    const next = boxFromDrag('se', start, { x: 8000, y: -90 }, spaces)
    expect(next).toEqual({ ...start, x2: 8000, y2: -90 })
  })

  it('crosses an edge past its opposite rather than refusing, leaving the caller to normalise', () => {
    const next = boxFromDrag('w', start, { x: 9000, y: -40 }, spaces)
    expect(next.x).toBe(9000)
    expect(next.x2).toBe(4000)
  })

  it("keeps a moved box's extent in each axis's own space rather than in pixels", () => {
    const moved = boxFromDrag('move', start, { x: 10000, y: -20 }, spaces)
    expect(Math.log2(moved.x2 / moved.x)).toBeCloseTo(2, 10)
    expect(moved.y - moved.y2).toBeCloseTo(40, 10)
  })

  it('keeps a moved box on a linear axis to its own width, which is a difference there', () => {
    const linear = boxFromDrag('move', { x: 1, x2: 5, y: 0.5, y2: 0.1 }, { x: 10, y: 0.9 }, boxSpaces('ms', 'volt'))
    expect(linear.x2 - linear.x).toBeCloseTo(4, 10)
    expect(linear.y - linear.y2).toBeCloseTo(0.4, 10)
  })
})

describe('clampBoxToWindow', () => {
  const window = { xLo: 20, xHi: 20000, yLo: -150, yHi: 20 }
  const spaces = boxSpaces('hz', 'dbv')
  const minSpan = { x: 0.05, y: 1 }

  it('translates a box back into the window rather than squashing it', () => {
    const clamped = clampBoxToWindow({ x: 5, x2: 20, y: 80, y2: 40 }, window, minSpan, spaces)
    expect(clamped.x).toBeGreaterThanOrEqual(window.xLo)
    expect(Math.log2(clamped.x2 / clamped.x)).toBeCloseTo(2, 6)
    expect(clamped.y).toBeLessThanOrEqual(window.yHi)
    expect(clamped.y - clamped.y2).toBeCloseTo(40, 6)
  })

  it('keeps a moved box’s extent in its axis’s own space, on a linear axis as on a ratio one', () => {
    const clamped = clampBoxToWindow(
      { x: -10, x2: -6, y: 4, y2: 3.5 },
      { xLo: -2, xHi: 20, yLo: -1, yHi: 1 },
      { x: 0.1, y: 0.05 },
      boxSpaces('ms', 'volt'),
    )
    expect(clamped.x).toBeCloseTo(-2, 6)
    expect(clamped.x2 - clamped.x).toBeCloseTo(4, 6)
    expect(clamped.y).toBeCloseTo(1, 6)
    expect(clamped.y - clamped.y2).toBeCloseTo(0.5, 6)
  })

  it('refuses to shrink a box below the size its own grips need', () => {
    const clamped = clampBoxToWindow({ x: 1000, x2: 1000.0001, y: -40, y2: -40 }, window, minSpan, spaces)
    expect(Math.log2(clamped.x2 / clamped.x)).toBeGreaterThanOrEqual(minSpan.x - 1e-9)
    expect(clamped.y - clamped.y2).toBeGreaterThanOrEqual(minSpan.y - 1e-9)
  })

  it('hands a nonsense window back the box it was given rather than a NaN', () => {
    const box = { x: 1000, x2: 4000, y: -40, y2: -80 }
    expect(clampBoxToWindow(box, { xLo: 0, xHi: 0, yLo: 0, yHi: 0 }, minSpan, spaces)).toEqual(box)
  })
})
