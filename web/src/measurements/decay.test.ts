import { describe, expect, it } from 'vitest'
import type { DecaySurface } from '../api/client'
import {
  acrossX,
  axisFraction,
  axisFractions,
  decayLayout,
  drawnSliceIndices,
  DEFAULT_CSD_ANALYSIS,
  formatHz,
  levelAt,
  levelIndex,
  levelTicks,
  levelY,
  linearTicks,
  csdAchievedSpanMs,
  csdSliceStepSamples,
  csdSpanSamples,
  wallGradientStops,
  logTicks,
  lutColor,
  orientSurface,
  advancesTrailingEdge,
  furthestReached,
  paletteLut,
  pickSurfacePoint,
  sliceOrigin,
  sliceStride,
} from './decay'

describe('decayLayout', () => {
  it('splits the box between the level range and the depth axis', () => {
    const layout = decayLayout(1000, 400, 0.2, 0.5)

    expect(layout.depthX).toBe(200)
    expect(layout.depthY).toBe(200)
    expect(layout.curveWidth).toBe(800)
    expect(layout.levelHeight).toBe(200)
  })

  /**
   * The depth axis runs down and to the right, so the first slice — time zero — sits at the back,
   * top left, and each later one nearer the viewer. Reversing that direction would silently break
   * the hidden-line removal: the painter's algorithm relies on a nearer curve being drawn lower, so
   * that filling underneath it covers the slices behind.
   */
  it('puts the first slice at the back and the last one nearest', () => {
    const layout = decayLayout(1000, 400, 0.2, 0.5)

    expect(sliceOrigin(layout, 0)).toEqual({ x: 0, y: 0 })
    expect(sliceOrigin(layout, 1)).toEqual({ x: 200, y: 200 })
  })

  /**
   * A negative horizontal run sends the stack down and to the *left*, which is ARTA's projection and
   * the default. The back curve then has to start one whole run in from the left edge, or the front
   * one — the furthest left — would be drawn outside the box.
   */
  it('starts the back curve one depth run in when the stack recedes leftwards', () => {
    const layout = decayLayout(1000, 400, -0.2, 0.5)

    expect(layout.left).toBe(200)
    expect(layout.curveWidth).toBe(800)
    expect(sliceOrigin(layout, 0)).toEqual({ x: 200, y: 0 })
    expect(sliceOrigin(layout, 1)).toEqual({ x: 0, y: 200 })
  })

  /** Either way round, the two curves between them span the whole width and no more. */
  it('uses the whole width whichever way the stack recedes', () => {
    for (const fraction of [0.2, -0.2]) {
      const layout = decayLayout(1000, 400, fraction, 0.5)
      const back = sliceOrigin(layout, 0)
      const front = sliceOrigin(layout, 1)

      expect(Math.min(back.x, front.x)).toBe(0)
      expect(Math.max(back.x, front.x) + layout.curveWidth).toBe(1000)
    }
  })

  /** Together the two axes use the whole box: a full-scale front curve reaches the bottom exactly. */
  it('lets a front slice at the bottom of the range reach the foot of the box', () => {
    const layout = decayLayout(1000, 400, 0.2, 0.5)
    const front = sliceOrigin(layout, 1)

    expect(levelY(layout, front.y, -40, 0, -40)).toBe(400)
    expect(levelY(layout, front.y, 0, 0, -40)).toBe(200)
  })
})

describe('levelY', () => {
  /**
   * Clamped rather than left to run off. A curve below the range that kept going would be drawn
   * across the slices in front of it, and clipping the canvas cannot fix that — every slice is its
   * own parallelogram, so one shared clip rectangle is the wrong shape for all of them.
   */
  it('clamps a level outside the window to the window', () => {
    const layout = decayLayout(1000, 400, 0.2, 0.5)

    expect(levelY(layout, 0, -300, 0, -40)).toBe(200)
    expect(levelY(layout, 0, 12, 0, -40)).toBe(0)
  })
})

describe('axisFraction', () => {
  it('is logarithmic, so a decade is a decade wherever it falls', () => {
    expect(axisFraction(20, 20, 20000, true)).toBeCloseTo(0, 6)
    expect(axisFraction(20000, 20, 20000, true)).toBeCloseTo(1, 6)
    expect(axisFraction(200, 20, 20000, true)).toBeCloseTo(1 / 3, 6)
    expect(axisFraction(2000, 20, 20000, true)).toBeCloseTo(2 / 3, 6)
  })

  /** A time axis is linear, and that is the whole difference between the two waterfalls. */
  it('is linear when the axis is not a frequency one', () => {
    expect(axisFraction(0, 0, 30, false)).toBeCloseTo(0, 6)
    expect(axisFraction(15, 0, 30, false)).toBeCloseTo(0.5, 6)
    expect(axisFraction(30, 0, 30, false)).toBeCloseTo(1, 6)
  })

  it('takes an axis own ends from its own values', () => {
    expect(axisFractions([0, 1, 2, 3, 4], false)).toEqual([0, 0.25, 0.5, 0.75, 1])
    for (const [i, fraction] of axisFractions([20, 200, 2000, 20000], true).entries()) {
      expect(fraction).toBeCloseTo(i / 3, 6)
    }
  })

  it('places a point along a curve from that curve own origin', () => {
    const layout = decayLayout(1000, 400, -0.2, 0.5)

    expect(acrossX(layout, sliceOrigin(layout, 0).x, 0)).toBe(200)
    expect(acrossX(layout, sliceOrigin(layout, 1).x, 1)).toBe(800)
  })
})

describe('paletteLut', () => {
  it('runs cold to hot across the whole table', () => {
    const jet = paletteLut('Jet')

    expect(jet.length).toBe(256 * 3)
    // Coldest is dark blue, hottest dark red.
    expect(jet[2]).toBeGreaterThan(jet[0])
    expect(jet[255 * 3]).toBeGreaterThan(jet[255 * 3 + 2])
  })

  /**
   * The one palette that has to follow the theme. A monochrome ramp says "loud" by contrast against
   * the page, so a fixed one drew the front slice — the loudest curve, and the whole point of the
   * chart — in near black on a near-black page.
   */
  it('reverses the grey ramp on a dark page so that loud stays visible', () => {
    const onLight = paletteLut('Grey', false)
    const onDark = paletteLut('Grey', true)

    // Loud is the top of the table in both, and it is dark on paper and light on a dark page.
    expect(onLight[255 * 3]).toBeLessThan(60)
    expect(onDark[255 * 3]).toBeGreaterThan(200)
  })

  it('leaves the coloured palettes alone whichever theme is on', () => {
    expect([...paletteLut('Jet', true)]).toEqual([...paletteLut('Jet', false)])
  })

  /**
   * The foot of the level scale is held a quarter of the way up the palette — see
   * PALETTE_FLOOR_FRACTION — so the quiet end of a surface is a readable blue rather than Jet's
   * near-black navy. The top of the scale is still the top of the palette.
   */
  it('maps the top of the range to the top of the table and the bottom to the palette floor', () => {
    expect(levelIndex(0, 0, -40)).toBe(255)
    expect(levelIndex(-40, 0, -40)).toBe(64)
    expect(levelIndex(-20, 0, -40)).toBe(159)
    // And anything past either end saturates rather than wrapping.
    expect(levelIndex(-300, 0, -40)).toBe(64)
    expect(levelIndex(10, 0, -40)).toBe(255)
  })

  /**
   * Stated the way it was asked for: on a 0 to -20 dB scale, the foot now reads what -15 dB used to.
   */
  it('gives the foot of the scale the colour that sat three quarters of the way down it', () => {
    const wasAtMinus15 = Math.round(((-15 - -20) / 20) * 255)
    expect(levelIndex(-20, 0, -20)).toBe(wasAtMinus15)
  })
})

describe('wallGradientStops', () => {
  /**
   * The bug this is here for: the wall used to hang each slice's own colour straight down from its
   * curve's end, which gradates it along *time* — red at the back where the response is loud, blue
   * at the front where it has decayed. That is a second key on the same picture, disagreeing with
   * the one printed beside it. The wall is the side face of the box and a point on it has a level.
   */
  it('runs from the top of the level axis to the foot of it', () => {
    const stops = wallGradientStops()

    expect(stops[0].offset).toBe(0)
    expect(stops[0].index).toBe(255)
    expect(stops[stops.length - 1].offset).toBe(1)
    expect(stops[stops.length - 1].index).toBe(levelIndex(-30, 0, -30))
  })

  it('is the same ramp the curves and the key are coloured from', () => {
    // Every stop is the colour levelIndex gives the level at that height, which is what makes the
    // wall and the curve that ends on it agree where they meet.
    for (const stop of wallGradientStops(8)) {
      const levelDb = 0 + stop.offset * -30
      expect(stop.index).toBe(levelIndex(levelDb, 0, -30))
    }
  })

  it('samples the palette finely enough to read as a gradient, and monotonically', () => {
    const stops = wallGradientStops()
    expect(stops.length).toBeGreaterThan(16)
    for (let i = 1; i < stops.length; i++) {
      expect(stops[i].offset).toBeGreaterThan(stops[i - 1].offset)
      expect(stops[i].index).toBeLessThan(stops[i - 1].index)
    }
    // And it never reaches the quarter of the palette the body fill keeps for itself.
    expect(stops[stops.length - 1].index).toBeGreaterThan(0)
  })
})

describe('csdSpanSamples', () => {
  /**
   * The point of saying the span in milliseconds: the same setting is the same measurement at every
   * rate, where ARTA's "shift 2" is 2.46 ms off a 48 kHz sound card and 0.61 ms at 192.
   */
  it('is the same length of time at every sample rate', () => {
    for (const rate of [48000, 96000, 192000]) {
      expect((csdSpanSamples(2.5, rate) / rate) * 1000).toBeCloseTo(2.5, 2)
    }
  })

  /**
   * The fault this replaced: the step between slices had to be a whole sample, so rounding *it*
   * made the span depend on a setting that has nothing to do with the axis — 2.46 ms asked for at
   * 96 kHz came out 2.063 ms over 100 slices and 3.104 over 150. Reported 2026-10-25.
   */
  it('does not move with the slice count', () => {
    for (const slices of [2, 20, 60, 100, 150, 200]) {
      const analysis = { ...DEFAULT_CSD_ANALYSIS, spanMs: 2.46, slices }
      expect(csdAchievedSpanMs(analysis, 96000)).toBeCloseTo(2.46, 2)
    }
  })

  it('never collapses to nothing', () => {
    expect(csdSpanSamples(0, 48000)).toBe(1)
    expect(csdSpanSamples(2.5, 0)).toBe(1)
  })

  /** The average gap, which is fractional on purpose: the slices land on whole samples one by one. */
  it('reports the gap between slices without pretending it is regular', () => {
    const analysis = { ...DEFAULT_CSD_ANALYSIS, spanMs: 2.46, slices: 100 }
    expect(csdSliceStepSamples(analysis, 96000)).toBeCloseTo(236 / 99, 3)
  })
})

describe('DEFAULT_CSD_ANALYSIS', () => {
  it("is ARTA's analysis, in units that do not move with the sample rate", () => {
    expect(DEFAULT_CSD_ANALYSIS).toEqual({
      fftLength: 1024,
      spanMs: 2.46,
      slices: 60,
      windowMode: 'Apodizing',
      riseTimeMs: 0.2,
      // Starts at the impulse, which is what every build before the head start existed did.
      startBeforePeakMs: 0,
    })
  })

  /**
   * ARTA's defaults off a 48 kHz sound card are a 5.33 ms window resolving 187.5 Hz over 2.46 ms of
   * decay. A new impulse measurement here opens at a 96 kHz span — a 192 kHz rate — and reads the
   * same three figures, which is the whole reason the span is written down in milliseconds and the
   * transform length is 1024 rather than ARTA's 256.
   */
  it('reads like ARTA at the rate a new measurement opens on', () => {
    const rate = 192000
    expect((DEFAULT_CSD_ANALYSIS.fftLength / rate) * 1000).toBeCloseTo(5.33, 2)
    expect(rate / DEFAULT_CSD_ANALYSIS.fftLength).toBeCloseTo(187.5, 1)
    // 472 samples at 192 kHz, which is ARTA's own 118 at 48 kHz to the microsecond.
    expect(csdAchievedSpanMs(DEFAULT_CSD_ANALYSIS, rate)).toBeCloseTo((118 / 48000) * 1000, 4)
  })
})

describe('sliceStride', () => {
  it('draws every slice when there is room, and thins when there is not', () => {
    expect(sliceStride(40, 300)).toBe(1)
    expect(sliceStride(900, 300)).toBe(3)
    // Never fewer than a readable handful, however little depth there is.
    expect(sliceStride(200, 10)).toBe(20)
  })

  /**
   * A filled stack wants to be dense — each curve hides what is behind it, so it is density that
   * makes the surface read as a surface. The burst decay's own defaults are the case that matters:
   * 30 periods at 3 points each is 91 slices, and a chart with a few hundred pixels of depth should
   * be drawing most of them rather than every third one.
   */
  it('draws every slice of a default burst decay on a chart of ordinary size', () => {
    expect(sliceStride(241, 254)).toBe(1)
    expect(drawnSliceIndices(241, 254).length).toBe(241)
  })
})

describe('levelAt', () => {
  const surface: DecaySurface = {
    sliceAxis: 'Periods',
    freqHz: [100, 200, 400, 800],
    resolutionLimitHz: 0,
    slices: [
      { value: 0, firstValidColumn: 0, levelDb: [0, -1, -2, -3] },
      { value: 1, firstValidColumn: 2, levelDb: [-10, -11] },
    ],
  }

  /**
   * A slice's array starts at its own `firstValidColumn`, not at column 0 — the deep slices of a
   * burst decay run off the end of the record at the low-frequency end. Indexing it as though it
   * started at zero reads the wrong frequency's level and draws a perfectly convincing surface of
   * nothing.
   */
  it('indexes a slice through its own valid span', () => {
    expect(levelAt(surface, 0, 3)).toBe(-3)
    expect(levelAt(surface, 1, 2)).toBe(-10)
    expect(levelAt(surface, 1, 3)).toBe(-11)
  })

  it('reports the columns a slice has no data for rather than inventing them', () => {
    expect(levelAt(surface, 1, 0)).toBeNull()
    expect(levelAt(surface, 1, 1)).toBeNull()
    expect(levelAt(surface, 5, 0)).toBeNull()
  })
})

describe('logTicks', () => {
  it('labels one, two and five per decade, inside the band and nowhere else', () => {
    expect(logTicks(20, 20000)).toEqual([20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000])
    expect(logTicks(200, 2000)).toEqual([200, 500, 1000, 2000])
  })

  it('has nothing to say about a band that is not one', () => {
    expect(logTicks(0, 1000)).toEqual([])
    expect(logTicks(1000, 1000)).toEqual([])
  })
})

describe('formatHz', () => {
  it('reads the way the other charts label the same axis', () => {
    expect(formatHz(20)).toBe('20')
    expect(formatHz(200)).toBe('200')
    expect(formatHz(1500)).toBe('1.5k')
    expect(formatHz(2000)).toBe('2k')
    expect(formatHz(20000)).toBe('20k')
  })
})

describe('levelTicks', () => {
  /**
   * The same list rules the dB axis, the back wall's grid and the colour key's labels, so it has to
   * end exactly on the foot of the range: a key whose last label was -25 beside an axis that reached
   * -30 would put every hue on the chart a step away from the number it is read as.
   */
  it('starts at 0 dB and ends on the foot of the range', () => {
    for (const range of [10, 20, 30, 40, 45, 60, 70]) {
      const ticks = levelTicks(range)
      expect(ticks[0]).toBe(0)
      expect(ticks[ticks.length - 1]).toBe(-range)
    }
  })

  it('keeps the axis to seven labels however deep the window is', () => {
    for (const range of [10, 20, 30, 40, 45, 60, 70]) {
      expect(levelTicks(range).length).toBeLessThanOrEqual(8)
    }
  })

  it('steps evenly, so the grid it rules is even', () => {
    expect(levelTicks(30)).toEqual([0, -5, -10, -15, -20, -25, -30])
    expect(levelTicks(60)).toEqual([0, -10, -20, -30, -40, -50, -60])
  })
})

describe('lutColor', () => {
  /** Index 0 is the palette's floor — what the area under every curve is filled with. */
  it('reads a palette entry as a CSS colour, clamped at both ends', () => {
    const lut = paletteLut('Jet')

    expect(lutColor(lut, 0)).toBe('rgb(8,12,64)')
    expect(lutColor(lut, -5)).toBe(lutColor(lut, 0))
    expect(lutColor(lut, 999)).toBe(lutColor(lut, 255))
  })
})

describe('drawnSliceIndices', () => {
  it('draws every slice when there is room', () => {
    expect(drawnSliceIndices(5, 600)).toEqual([0, 1, 2, 3, 4])
  })

  /**
   * The front slice is the near edge of the whole picture. Left out because the stride happened not
   * to land on it, the surface reads as one that stops short of the time it claims to reach — and
   * the hit test, which walks this same list, would then hand back the wrong slice's level for every
   * pixel the missing one owned.
   */
  it('always ends on the front slice, however the stride falls', () => {
    const indices = drawnSliceIndices(100, 60)

    expect(indices[0]).toBe(0)
    expect(indices[indices.length - 1]).toBe(99)
    expect(new Set(indices).size).toBe(indices.length)
  })

  it('has nothing to draw for a surface with no slices', () => {
    expect(drawnSliceIndices(0, 600)).toEqual([])
  })
})

describe('pickSurfacePoint', () => {
  // Two slices, four log-spaced columns, and a box whose arithmetic is round: the depth axis takes
  // half the height and a fifth of the width, so the front slice's origin is (200, 200) of a
  // 1000x400 box and its curve spans x 200..1000, y 200..400.
  const layout = decayLayout(1000, 400, 0.2, 0.5)
  const surface: DecaySurface = {
    sliceAxis: 'Milliseconds',
    freqHz: [100, 200, 400, 800],
    resolutionLimitHz: 0,
    slices: [
      { value: 0, firstValidColumn: 0, levelDb: [0, 0, 0, 0] },
      { value: 1, firstValidColumn: 2, levelDb: [-20, -20] },
    ],
  }
  const view = orientSurface(surface, false)
  const indices = [0, 1]

  /**
   * The whole body reports, not just the curve. Everything under a curve was filled as part of that
   * curve, so a reader pointing anywhere inside the solid gets the level of the curve above the
   * point — which is what makes the readout usable at all, a one-pixel line being something you
   * hunt for rather than something you point at.
   */
  it('reports the curve whose body covers the point, not only the curve itself', () => {
    const onTheCurve = pickSurfacePoint(view, layout, indices, 800, 300, 0, -40)
    const wellBelowIt = pickSurfacePoint(view, layout, indices, 800, 380, 0, -40)

    expect(onTheCurve?.depthIndex).toBe(1)
    expect(wellBelowIt?.depthIndex).toBe(1)
    expect(wellBelowIt?.levelDb).toBe(-20)
  })

  /**
   * The nearest curve wins, because the nearest curve is the one that was painted last. Above the
   * front one the curve behind it is what is actually on screen there.
   */
  it('gives a point above the front curve to the curve behind it', () => {
    const pick = pickSurfacePoint(view, layout, indices, 800, 120, 0, -40)

    expect(pick?.depthIndex).toBe(0)
    expect(pick?.levelDb).toBe(0)
  })

  /**
   * The values are evenly spaced in whatever the axis is, so the point along a curve falls out of
   * the projection rather than out of a search — but it is measured from that curve's *own* origin,
   * which is what the stack receding sideways means. Read it off the box instead and every curve
   * reports a value further off than the last.
   */
  it('reads the point along a curve from that curve own origin, not from the box', () => {
    const front = pickSurfacePoint(view, layout, indices, 600, 399, 0, -40)
    const back = pickSurfacePoint(view, layout, [0], 400, 399, 0, -40)

    expect(front?.depthIndex).toBe(1)
    expect(view.across[front!.acrossIndex]).toBe(400)
    expect(back?.depthIndex).toBe(0)
    expect(view.across[back!.acrossIndex]).toBe(400)
  })

  /**
   * A curve that has no data for a point is transparent there, exactly as it is drawn: the pick
   * falls through to whatever is behind. Inventing a level for it would report a decay the analysis
   * never measured.
   */
  it('falls through a curve that has no data for the point', () => {
    const pick = pickSurfacePoint(view, layout, indices, 300, 399, 0, -40)

    expect(pick?.depthIndex).toBe(0)
  })

  /** Past the last curve's own far end there is no surface, only the box's floor. */
  it('finds nothing beyond the ends of every curve', () => {
    expect(pickSurfacePoint(view, layout, indices, -50, 200, 0, -40)).toBeNull()
    expect(pickSurfacePoint(view, layout, indices, 1100, 399, 0, -40)).toBeNull()
  })

  /**
   * The hit test knows nothing about which view it is in — it is handed an oriented surface and the
   * transpose has already happened. The point that reads 400 Hz at 1 ms one way round has to read
   * the same thing the other way round, or the two waterfalls disagree about the same measurement.
   */
  it('reports the same reading whichever way the surface is oriented', () => {
    const transposed = orientSurface(surface, true)
    const depthCount = transposed.depth.length
    // Frequency index 2 of four, time index 1 of two: the same cell, now the third curve of four.
    const acrossFraction = 1
    const depth = 2 / (depthCount - 1)
    const origin = sliceOrigin(layout, depth)

    const pick = pickSurfacePoint(
      transposed,
      layout,
      [0, 1, 2, 3],
      acrossX(layout, origin.x, acrossFraction),
      // Above the 800 Hz curve in front of it, which would otherwise own this pixel and be right to.
      260,
      0,
      -40,
    )

    expect(pick?.depthIndex).toBe(2)
    expect(transposed.depth[pick!.depthIndex]).toBe(400)
    expect(transposed.across[pick!.acrossIndex]).toBe(1)
    expect(pick?.levelDb).toBe(-20)
  })
})

describe('linearTicks', () => {
  it('lands on round numbers, at most six of them', () => {
    expect(linearTicks(0, 30)).toEqual([0, 5, 10, 15, 20, 25, 30])
    expect(linearTicks(0, 1)).toEqual([0, 0.2, 0.4, 0.6000000000, 0.8, 1])
  })

  /**
   * Off the multiple rather than off an accumulated sum: adding 0.2 five times does not reach 1 in
   * binary floating point, and a tick labelled 0.6000000000000001 is the kind of thing that gets
   * through review.
   */
  it('does not accumulate floating-point drift along the axis', () => {
    for (const tick of linearTicks(0, 1)) {
      expect(String(tick).length).toBeLessThan(6)
    }
  })

  it('has nothing to say about an axis that is not one', () => {
    expect(linearTicks(5, 5)).toEqual([])
    expect(linearTicks(10, 1)).toEqual([])
  })
})

describe('orientSurface', () => {
  const surface: DecaySurface = {
    sliceAxis: 'Periods',
    freqHz: [100, 200, 400, 800],
    resolutionLimitHz: 0,
    slices: [
      { value: 0, firstValidColumn: 0, levelDb: [0, -1, -2, -3] },
      { value: 1, firstValidColumn: 0, levelDb: [-10, -11, -12, -13] },
      { value: 2, firstValidColumn: 2, levelDb: [-22, -23] },
    ],
  }

  it('reads frequency across the curves and time into the page', () => {
    const view = orientSurface(surface, false)

    expect(view.across).toEqual([100, 200, 400, 800])
    expect(view.depth).toEqual([0, 1, 2])
    expect(view.acrossIsFrequency).toBe(true)
    expect(view.level(1, 2)).toBe(-12)
  })

  it('reads time across the curves and frequency into the page', () => {
    const view = orientSurface(surface, true)

    expect(view.across).toEqual([0, 1, 2])
    expect(view.depth).toEqual([100, 200, 400, 800])
    expect(view.acrossIsFrequency).toBe(false)
    // The same reading as above, transposed: frequency index 2, time index 1.
    expect(view.level(2, 1)).toBe(-12)
  })

  /**
   * The missing data transposes with everything else, and it is worth naming what it looks like each
   * way round. Read frequency across, a slice is missing its low frequencies and its curve starts
   * late. Read time across, a low frequency runs out of record and its curve stops early. Inventing
   * a level for either would report a decay the analysis never measured.
   */
  it('keeps the absent columns absent, whichever way it is read', () => {
    expect(orientSurface(surface, false).level(2, 0)).toBeNull()
    expect(orientSurface(surface, true).level(0, 2)).toBeNull()
  })

  it('spaces a time axis linearly and a frequency axis logarithmically', () => {
    expect(orientSurface(surface, true).acrossFraction).toEqual([0, 0.5, 1])
    for (const [i, fraction] of orientSurface(surface, false).acrossFraction.entries()) {
      expect(fraction).toBeCloseTo(i / 3, 6)
    }
  })
})

describe('advancesTrailingEdge', () => {
  it('advances when a curve ends past everything before it', () => {
    expect(advancesTrailingEdge(90, 100, true)).toBe(true)
    expect(advancesTrailingEdge(110, 100, false)).toBe(true)
  })

  it('always takes the first curve, which has nothing before it', () => {
    expect(advancesTrailingEdge(100, null, true)).toBe(true)
  })

  /**
   * The case that made this a function. A curve that has run out of record ends early, folding back
   * inside the wall rather than extending it — ribbed at that point, the wall doubles back on itself
   * and its quads overlap. A burst decay is full of those, so it is not a corner case.
   */
  it('does not advance for a curve that ends short of the furthest reached', () => {
    expect(advancesTrailingEdge(105, 100, true)).toBe(false)
    expect(advancesTrailingEdge(95, 100, false)).toBe(false)
  })

  /**
   * And the reach only ever moves outwards, so one short curve does not let the next one rib the
   * wall over ground it already covers.
   */
  it('keeps the reach at the furthest, never at the latest', () => {
    expect(furthestReached(90, 100, true)).toBe(90)
    expect(furthestReached(105, 100, true)).toBe(100)
    expect(furthestReached(110, 100, false)).toBe(110)
    expect(furthestReached(95, 100, false)).toBe(100)
    expect(furthestReached(100, null, true)).toBe(100)
  })

  /**
   * The two together are what keeps the wall a function of the depth axis: ribs come out strictly
   * monotonic whatever the curves do, so no quad can ever overlap its neighbour.
   */
  it('builds a strictly monotonic wall out of ends that wander', () => {
    const ends = [100, 96, 99, 92, 94, 88]
    const ribs: number[] = []
    let reached: number | null = null

    for (const x of ends) {
      if (advancesTrailingEdge(x, reached, true)) {
        ribs.push(x)
        reached = furthestReached(x, reached, true)
      }
    }

    expect(ribs).toEqual([100, 96, 92, 88])
  })
})
