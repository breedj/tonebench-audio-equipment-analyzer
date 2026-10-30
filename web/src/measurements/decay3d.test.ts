import { describe, expect, it } from 'vitest'
import type { DecaySurface } from '../api/client'
import {
  DECAY_DEPTH_PERCENT,
  DECAY_ROTATION_DEG,
  levelAt,
  orientSurface,
  paletteFloorFraction,
  paletteIndex,
  paletteLut,
} from './decay'
import { defaultImpulseDisplay } from './impulseDisplay'
import {
  DECAY_DEPTH_SIGN,
  DECAY_EXPLODE_Z,
  DECAY_VIEW_MAGNIFICATION,
  DECAY_VIEW_MOVE_X,
  DECAY_VIEW_MOVE_Y,
  decayAcrossLabel,
  decayAxisCoordinate,
  decayAxisValue,
  decayCameraAngles,
  decayCameraDistance,
  decayCenterPercent,
  decayColormap,
  decayDepthLabel,
  decayExportTitle,
  decayModeIs3d,
  decayOptionsKey,
  decayPointFrequencyHz,
  decayPointTime,
  decayPoints,
  decayVerticalRatio,
  decayViewDefaults,
  explodeZForAxisLengths,
  magnificationFromStored,
} from './decay3d'

/** Four slices over five log-spaced columns, the last two slices missing their low end. */
function ragged(): DecaySurface {
  return {
    sliceAxis: 'Periods',
    freqHz: [20, 63, 200, 632, 2000],
    resolutionLimitHz: 0,
    slices: [
      { value: 0, firstValidColumn: 0, levelDb: [0, -1, -2, -3, -4] },
      { value: 1, firstValidColumn: 0, levelDb: [-5, -6, -7, -8, -9] },
      { value: 2, firstValidColumn: 2, levelDb: [-10, -11, -12] },
      { value: 3, firstValidColumn: 4, levelDb: [-13] },
    ],
  }
}

function dense(): DecaySurface {
  return {
    sliceAxis: 'Milliseconds',
    freqHz: [100, 1000, 10000],
    resolutionLimitHz: 0,
    slices: [
      { value: 0, firstValidColumn: 0, levelDb: [0, -1, -2] },
      { value: 0.5, firstValidColumn: 0, levelDb: [-3, -4, -5] },
    ],
  }
}

const MODES = ['Waterfall', 'WaterfallPeriods'] as const

describe('decayPoints', () => {
  /**
   * The one test that matters: every vertex the library is handed must be the level the Canvas 2D
   * chart reads at the same point. Compared cell for cell rather than by a summary — a transposed
   * or offset cloud agrees on every summary and is wrong about every point.
   */
  it('agrees with levelAt cell for cell, on both orientations', () => {
    const surface = ragged()
    for (const mode of MODES) {
      const cloud = decayPoints(surface, mode, 30)
      const view = orientSurface(surface, mode === 'WaterfallPeriods')
      expect(cloud.rows).toBe(view.depth.length)
      expect(cloud.cols).toBe(view.across.length)

      // Rebuild the (row, column) the library will index each vertex at, which is what it does from
      // the distinct values of the two floor axes.
      const acrossCoords = view.across.map((v) => decayAxisCoordinate(v, view.acrossIsFrequency))
      const depthCoords = view.depth.map(
        (v) => DECAY_DEPTH_SIGN * decayAxisCoordinate(v, view.depthIsFrequency),
      )
      let seen = 0
      for (const point of cloud.points) {
        const c = acrossCoords.indexOf(point.x)
        const r = depthCoords.indexOf(point.y)
        expect(c, `${mode} column of ${point.x}`).toBeGreaterThanOrEqual(0)
        expect(r, `${mode} row of ${point.y}`).toBeGreaterThanOrEqual(0)
        expect(point.z, `${mode} ${r},${c}`).toBeCloseTo(view.level(r, c) as number, 12)
        seen++
      }
      expect(seen).toBe(cloud.rows * cloud.cols - cloud.omitted)
    }
  })

  /**
   * <p><b>The headline rule, and the one with the worst available failure.</b> A cell the analysis
   * has no answer for is left out of the array; the library reads every coordinate as
   * <c>data[i][col] || 0</c>, so a NaN or a sentinel written into it becomes <c>0</c> — and 0 dB is
   * the <em>top</em> of a decay scale. A burst decay's unmeasured low-frequency corner would come
   * out as the loudest thing on the surface.</p>
   *
   * <p>So this asserts both halves: the count of what was left out, and that nothing emitted sits
   * at a level <c>levelAt</c> refused to give.</p>
   */
  it('omits a cell it has no measurement for, rather than zeroing it', () => {
    const surface = ragged()
    const cloud = decayPoints(surface, 'Waterfall', 30)

    // Slice 2 starts at column 2, slice 3 at column 4.
    expect(cloud.omitted).toBe(2 + 4)
    expect(cloud.points).toHaveLength(4 * 5 - 6)

    const acrossCoords = surface.freqHz.map((f) => decayAxisCoordinate(f, true))
    for (const point of cloud.points) {
      const c = acrossCoords.indexOf(point.x)
      const r = surface.slices.findIndex((s) => DECAY_DEPTH_SIGN * s.value === point.y)
      expect(levelAt(surface, r, c), `${r},${c} was emitted`).not.toBeNull()
    }

    // And the cells it dropped really are the ragged ones, named the other way round.
    const emitted = new Set(cloud.points.map((p) => `${p.x},${p.y}`))
    for (const [r, slice] of surface.slices.entries()) {
      for (let c = 0; c < slice.firstValidColumn; c++) {
        const key = `${acrossCoords[c]},${DECAY_DEPTH_SIGN * slice.value}`
        expect(emitted.has(key), `${r},${c} should be absent`).toBe(false)
      }
    }
  })

  it('emits every cell of a surface with nothing missing', () => {
    const cloud = decayPoints(dense(), 'Waterfall', 30)
    expect(cloud.omitted).toBe(0)
    expect(cloud.points).toHaveLength(cloud.rows * cloud.cols)
    expect(cloud.points.every((p) => Number.isFinite(p.z))).toBe(true)
  })

  /** Clamped like levelY, not dropped — the two charts must agree about the foot of the scale. */
  it('clamps a level below the range to the foot of it rather than discarding it', () => {
    const surface: DecaySurface = {
      sliceAxis: 'Milliseconds',
      freqHz: [100, 1000],
      resolutionLimitHz: 0,
      slices: [{ value: 0, firstValidColumn: 0, levelDb: [-80, 5] }],
    }
    const cloud = decayPoints(surface, 'Waterfall', 30)
    expect(cloud.points.map((p) => p.z)).toEqual([-30, 0])
    expect(cloud.omitted).toBe(0)
  })

  /**
   * <p>A frequency axis carries its logarithm, because the library has no logarithmic scale — and
   * <b>either floor axis can be the frequency one</b>, which is what the by-period view is. An
   * implementation that logged only the across axis draws the by-period view's whole frequency
   * stack inside the last twentieth of its floor.</p>
   */
  it('puts the logarithm on whichever axis is the frequency', () => {
    const across = decayPoints(dense(), 'Waterfall', 30)
    expect(across.acrossIsFrequency).toBe(true)
    expect(across.depthIsFrequency).toBe(false)
    expect([...new Set(across.points.map((p) => p.x))].sort((a, b) => a - b)).toEqual([2, 3, 4])

    const byPeriod = decayPoints(dense(), 'WaterfallPeriods', 30)
    expect(byPeriod.acrossIsFrequency).toBe(false)
    expect(byPeriod.depthIsFrequency).toBe(true)
    // Time across, untouched; frequency in depth, logged and negated.
    expect([...new Set(byPeriod.points.map((p) => p.x))].sort((a, b) => a - b)).toEqual([0, 0.5])
    expect([...new Set(byPeriod.points.map((p) => p.y))].sort((a, b) => a - b)).toEqual([-4, -3, -2])
  })

  /**
   * <p><b>The depth axis is negated, and that is the library's handedness rather than a
   * preference.</b> Working its parallel projection through: at a horizontal angle of zero, screen
   * x follows the first floor axis and the second recedes <em>away</em> from the viewer as it
   * grows. Slice zero is the back of a waterfall, so the depth coordinate has to fall as the stack
   * comes forward. Turning the camera to the other side instead fixes the depth and mirrors the
   * frequency with it, the floor keeping its handedness however the camera moves — and this library
   * has no axis-inversion flag, so negating one coordinate is the only lever that moves one axis
   * alone.</p>
   */
  it('negates the depth axis, so slice zero sits at the back', () => {
    const cloud = decayPoints(dense(), 'Waterfall', 30)
    const depths = [...new Set(cloud.points.map((p) => p.y))].sort((a, b) => a - b)
    expect(depths).toEqual([-0.5, 0])
    expect(DECAY_DEPTH_SIGN).toBe(-1)
  })

  it('never calls both axes a frequency, since one of them is always the time', () => {
    for (const mode of MODES) {
      const cloud = decayPoints(ragged(), mode, 30)
      expect(cloud.acrossIsFrequency && cloud.depthIsFrequency, mode).toBe(false)
    }
  })

  it('transposes the orientation, swapping which axis is frequency', () => {
    const surface = ragged()
    const across = decayPoints(surface, 'Waterfall', 30)
    const periods = decayPoints(surface, 'WaterfallPeriods', 30)
    expect([across.rows, across.cols]).toEqual([4, 5])
    expect([periods.rows, periods.cols]).toEqual([5, 4])
    // The same cells, however they are indexed.
    expect(periods.points).toHaveLength(across.points.length)
    expect(periods.omitted).toBe(across.omitted)
  })

  /**
   * One coordinate per column and per curve, computed once. The library matches a row to a grid
   * position by exact equality, so a coordinate recomputed per point is a grid that may not line
   * up — and `Math.log10` is not required to return the same bits for the same input twice.
   */
  it('gives every curve the identical coordinate for a column', () => {
    const cloud = decayPoints(dense(), 'Waterfall', 30)
    const columns = new Set(cloud.points.map((p) => p.x))
    expect(columns.size).toBe(cloud.cols)
  })
})

describe('the axis coordinates round-trip', () => {
  it('undoes the logarithm, and leaves a linear axis alone', () => {
    for (const hz of [20, 632, 2000, 96000]) {
      expect(decayAxisValue(decayAxisCoordinate(hz, true), true)).toBeCloseTo(hz, 6)
    }
    for (const ms of [0, 0.5, 2.46, 120]) {
      expect(decayAxisValue(decayAxisCoordinate(ms, false), false)).toBe(ms)
    }
  })

  /** A log axis cannot carry a zero or a negative, which `freqHz[0]` could be on a bad surface. */
  it('keeps a non-positive frequency off a logarithmic axis', () => {
    expect(Number.isFinite(decayAxisCoordinate(0, true))).toBe(true)
    expect(Number.isFinite(decayAxisCoordinate(-5, true))).toBe(true)
  })

  it('labels a tick in the units the axis is read in', () => {
    expect(decayAcrossLabel(decayAxisCoordinate(2000, true), true)).toBe('2k')
    expect(decayAcrossLabel(2.46, false)).toBe('2.46')
    // The depth label undoes the sign as well as the coordinate.
    expect(decayDepthLabel(DECAY_DEPTH_SIGN * 2.46, false)).toBe('2.46')
    expect(decayDepthLabel(DECAY_DEPTH_SIGN * decayAxisCoordinate(1000, true), true)).toBe('1k')
  })

  it('says nothing rather than NaN for a tick the library has no value for', () => {
    expect(decayAcrossLabel(Number.NaN, true)).toBe('')
    expect(decayDepthLabel(Number.NaN, false)).toBe('')
  })
})

describe('reading a point back', () => {
  /**
   * The readout has to ask the cloud which axis carries the frequency. One that assumed the across
   * axis would print the by-period view's *depth* coordinate as hertz — a plausible wrong number,
   * which is the worst kind.
   */
  it('finds the frequency and the time whichever axis each is on', () => {
    for (const mode of MODES) {
      const cloud = decayPoints(dense(), mode, 30)
      for (const point of cloud.points) {
        const hz = decayPointFrequencyHz(point, cloud)
        expect([100, 1000, 10000].some((f) => Math.abs(hz - f) < 1e-6), `${mode} ${hz}`).toBe(true)
        const time = decayPointTime(point, cloud)
        expect([0, 0.5].some((t) => Math.abs(time - t) < 1e-9), `${mode} ${time}`).toBe(true)
      }
    }
  })
})

describe('decayColormap', () => {
  /**
   * The floor fraction is the thing this must not quietly drop: where a palette's own foot is too
   * dark to read, the level-to-colour mapping is squeezed into the top of its ramp, and a colormap
   * that skipped it would put the quiet half of every surface back into the near-black. It is also
   * what keeps a colour read off the surface matching the key beside it and the Canvas 2D chart's
   * own curves.
   */
  it('is paletteLut read through paletteIndex, floor fraction and all', () => {
    for (const palette of ['Jet', 'Hot', 'Grey'] as const) {
      for (const onDark of [false, true]) {
        const colormap = decayColormap(palette, onDark)
        const lut = paletteLut(palette, onDark)
        const floor = paletteFloorFraction(palette)
        for (const fraction of [0, 0.25, 0.5, 0.75, 1]) {
          const index = paletteIndex(fraction, floor)
          expect(colormap(fraction), `${palette} ${onDark} ${fraction}`).toEqual({
            r: lut[index * 3],
            g: lut[index * 3 + 1],
            b: lut[index * 3 + 2],
          })
        }
      }
    }
  })

  it('is a colour at the foot of the scale rather than a hole', () => {
    const foot = decayColormap('Jet', true)(0)
    expect(foot.r + foot.g + foot.b).toBeGreaterThan(0)
  })

  /** The library hands it a fraction it computed; a non-finite one must not come back as NaN ink. */
  it('answers the foot of the ramp for a fraction it cannot use', () => {
    const colormap = decayColormap('Jet', false)
    expect(colormap(Number.NaN)).toEqual(colormap(0))
  })
})

describe('decayVerticalRatio', () => {
  /**
   * The slider's whole travel maps onto the whole of the library's documented 0.1 to 1.0, so
   * neither end is a dead stretch — which is the fault the magnification control had on the engine
   * this replaced, where seven eighths of the slider rendered byte-identically.
   */
  it('covers the library’s whole range from the slider’s own ends', () => {
    expect(decayVerticalRatio(DECAY_EXPLODE_Z.min)).toBeCloseTo(0.1, 12)
    expect(decayVerticalRatio(DECAY_EXPLODE_Z.max)).toBeCloseTo(1.0, 12)
  })

  it('rises at every step of the slider', () => {
    let previous = -1
    for (let v = DECAY_EXPLODE_Z.min; v <= DECAY_EXPLODE_Z.max; v += DECAY_EXPLODE_Z.step) {
      const ratio = decayVerticalRatio(v)
      expect(ratio, String(v)).toBeGreaterThan(previous)
      previous = ratio
    }
  })

  /** A coincidence worth keeping: the box this chart opens on is the shape the library defaults to. */
  it('opens near the library’s own default box', () => {
    expect(decayVerticalRatio(DECAY_EXPLODE_Z.default)).toBeCloseTo(0.5, 1)
  })

  it('clamps rather than handing out a box with no height', () => {
    expect(decayVerticalRatio(0)).toBeCloseTo(0.1, 12)
    expect(decayVerticalRatio(1000)).toBeCloseTo(1.0, 12)
    expect(decayVerticalRatio(Number.NaN)).toBe(decayVerticalRatio(DECAY_EXPLODE_Z.default))
  })
})

describe('explodeZForAxisLengths', () => {
  /**
   * <b>The measurement that let three sliders become one.</b> The box Joost asked to keep on
   * 2026-10-27 was 30 / 11 / 30, and 11/30 = 0.3667 against √2 × 26/100 = 0.3677 — three parts in
   * a thousand, so the default reproduces it and a measurement that stored it opens unchanged.
   */
  it('carries a stored box over as the height against its floor', () => {
    expect(explodeZForAxisLengths(30, 11)).toBe(26)
    // Three parts in a thousand of the box it replaces, which is why it could replace it. Written
    // out rather than read off the chart, the aspect triple having gone with the old engine.
    expect(Math.abs((Math.SQRT2 * 26) / 100 / (11 / 30) - 1)).toBeLessThan(0.005)
  })

  it('reads the ratio and not the lengths, so a box scaled up is the same explode', () => {
    expect(explodeZForAxisLengths(60, 22)).toBe(explodeZForAxisLengths(30, 11))
  })

  it('stays inside the slider it feeds, and falls back rather than dividing by zero', () => {
    expect(explodeZForAxisLengths(30, 300)).toBe(DECAY_EXPLODE_Z.max)
    expect(explodeZForAxisLengths(30, 0.01)).toBe(DECAY_EXPLODE_Z.min)
    expect(explodeZForAxisLengths(0, 11)).toBe(DECAY_EXPLODE_Z.default)
    expect(explodeZForAxisLengths(30, 0)).toBe(DECAY_EXPLODE_Z.default)
  })
})

describe('decayCameraDistance', () => {
  /**
   * <p><b>The fault this mapping exists to make impossible.</b> Reported twice against the previous
   * engine — "a magnification of 90 % results in a larger graph than 110 %", "above 200 % does not
   * work" — and the cause both times was a control whose travel was wider than what it drove, so
   * most of the slider pushed against a limit. The library's arm is sevenfold where the slider is
   * fortyfold, so a proportional map would pin everything below about 58 % and above about 400 %.
   * A logarithmic one reaches both ends and moves at every step in between.</p>
   */
  it('moves at every step of the slider, with no two steps alike', () => {
    let previous = Number.POSITIVE_INFINITY
    for (let p = DECAY_VIEW_MAGNIFICATION.min; p <= DECAY_VIEW_MAGNIFICATION.max; p += DECAY_VIEW_MAGNIFICATION.step) {
      const distance = decayCameraDistance(p)
      expect(distance, String(p)).toBeLessThan(previous)
      previous = distance
    }
  })

  it('reaches both ends of the arm the library documents, and leaves neither', () => {
    expect(decayCameraDistance(DECAY_VIEW_MAGNIFICATION.min)).toBeCloseTo(5.0, 10)
    expect(decayCameraDistance(DECAY_VIEW_MAGNIFICATION.max)).toBeCloseTo(0.71, 10)
    for (let p = DECAY_VIEW_MAGNIFICATION.min; p <= DECAY_VIEW_MAGNIFICATION.max; p += DECAY_VIEW_MAGNIFICATION.step) {
      expect(decayCameraDistance(p), String(p)).toBeGreaterThanOrEqual(0.71)
      expect(decayCameraDistance(p), String(p)).toBeLessThanOrEqual(5.0)
    }
  })

  /**
   * More magnification is a shorter arm, because the drawn size is its reciprocal: the library's own
   * parallel-projection conversion is `dx / armLength`.
   */
  it('brings the camera in as the magnification rises', () => {
    expect(decayCameraDistance(400)).toBeLessThan(decayCameraDistance(100))
  })

  /** The view a measurement opens on is near the framing the library was tuned for. */
  it('opens near the library’s own default arm', () => {
    expect(decayCameraDistance(DECAY_VIEW_MAGNIFICATION.default)).toBeCloseTo(1.7, 0)
  })

  it('falls back to the default rather than to a camera inside the box', () => {
    for (const bad of [0, -10, Number.NaN]) {
      expect(decayCameraDistance(bad), String(bad)).toBe(decayCameraDistance(DECAY_VIEW_MAGNIFICATION.default))
    }
  })
})

describe('decayCameraAngles', () => {
  it('is the panel’s lean in radians, straight through', () => {
    expect(decayCameraAngles(0, 50).horizontal).toBeCloseTo(0, 12)
    expect(decayCameraAngles(60, 50).horizontal).toBeCloseTo(Math.PI / 3, 12)
    expect(decayCameraAngles(-60, 50).horizontal).toBeCloseTo(-Math.PI / 3, 12)
  })

  /**
   * Tilt is how much of the picture the depth axis takes: 0 is level with the floor, where the
   * stack has no depth at all, and a quarter turn looks straight down on it, where it has nothing
   * but. The library clamps `vertical` to that quarter turn itself, so this must not exceed it.
   */
  it('maps tilt onto the quarter turn the library allows, and no further', () => {
    for (let p = DECAY_DEPTH_PERCENT.min; p <= DECAY_DEPTH_PERCENT.max; p++) {
      const { vertical } = decayCameraAngles(0, p)
      expect(vertical, String(p)).toBeGreaterThan(0)
      expect(vertical, String(p)).toBeLessThanOrEqual(Math.PI / 2)
    }
    expect(decayCameraAngles(0, DECAY_DEPTH_PERCENT.max).vertical)
      .toBeGreaterThan(decayCameraAngles(0, DECAY_DEPTH_PERCENT.min).vertical)
  })

  it('clamps a value outside the slider, and falls back for one that is not a number', () => {
    expect(decayCameraAngles(900, 50).horizontal).toBeCloseTo((DECAY_ROTATION_DEG.max * Math.PI) / 180, 12)
    expect(decayCameraAngles(0, 900).vertical).toBeCloseTo((DECAY_DEPTH_PERCENT.max / 100) * (Math.PI / 2), 12)
    expect(decayCameraAngles(Number.NaN, Number.NaN))
      .toEqual(decayCameraAngles(DECAY_ROTATION_DEG.default, DECAY_DEPTH_PERCENT.default))
  })
})

describe('decayCenterPercent', () => {
  it('leaves the box where the library puts it when neither slider has moved', () => {
    expect(decayCenterPercent(0, 0)).toEqual({ xCenter: '55%', yCenter: '45%' })
  })

  /**
   * The library measures its vertical centre *down* the canvas, so a slider labelled "Move
   * vertically" that sent the surface down when pushed up would be its own bug.
   */
  it('moves the box right and up for positive values', () => {
    const right = decayCenterPercent(1, 0)
    const up = decayCenterPercent(0, 1)
    expect(Number.parseFloat(right.xCenter)).toBeGreaterThan(55)
    expect(Number.parseFloat(up.yCenter)).toBeLessThan(45)
  })

  it('keeps the box on the plot across each slider’s own range', () => {
    for (const x of [DECAY_VIEW_MOVE_X.min, 0, DECAY_VIEW_MOVE_X.max]) {
      for (const y of [DECAY_VIEW_MOVE_Y.min, 0, DECAY_VIEW_MOVE_Y.max]) {
        const { xCenter, yCenter } = decayCenterPercent(x, y)
        expect(xCenter).toMatch(/^-?[\d.]+%$/)
        expect(yCenter).toMatch(/^-?[\d.]+%$/)
      }
    }
  })

  it('falls back rather than writing NaN% into the options', () => {
    expect(decayCenterPercent(Number.NaN, Number.NaN)).toEqual(decayCenterPercent(0, 0))
  })
})

describe('decayViewDefaults', () => {
  /**
   * <b>The point of the function.</b> The Surface view panel's Reset view button writes these
   * values; a new measurement opens on them too, and it has to be the same set — a button that
   * restored a view no measurement ever starts at would be the worse of two wrong answers. Asserted
   * against `defaultImpulseDisplay` rather than against a copy of the figures, so the test fails if
   * either side gains a field the other has not.
   */
  it('is exactly the view a new measurement opens on', () => {
    const fresh = defaultImpulseDisplay()
    const defaults = decayViewDefaults()
    for (const [key, value] of Object.entries(defaults)) {
      expect(fresh[key as keyof typeof fresh], key).toBe(value)
    }
  })

  /**
   * Rotation and Tilt are in it although their sliders sit above the panel the button is in.
   *
   * <p>It was eight fields until the engine was replaced on 2026-10-30; the two that went were the
   * light's angles, <c>vis-graph3d</c> having no light to position.</p>
   */
  it('covers the camera as well as the box, and no longer a light', () => {
    expect(Object.keys(decayViewDefaults()).sort()).toEqual([
      'decayDepthPercent',
      'decayExplodeZ',
      'decayMagnification',
      'decayMoveX',
      'decayMoveY',
      'decayRotationDeg',
    ])
  })

  /**
   * A default outside its own slider's range makes a control that cannot be returned to where it
   * started — the slider clamps the value the moment it is dragged, and the figure it opened on is
   * then unreachable. It fails silently, which is why it is worth a test rather than a glance.
   */
  it('puts every slider inside its own range', () => {
    const d = decayViewDefaults()
    const within = (v: number, range: { min: number; max: number }, what: string) => {
      expect(v, what).toBeGreaterThanOrEqual(range.min)
      expect(v, what).toBeLessThanOrEqual(range.max)
    }
    within(d.decayRotationDeg, DECAY_ROTATION_DEG, 'rotation')
    within(d.decayDepthPercent, DECAY_DEPTH_PERCENT, 'tilt')
    within(d.decayMoveX, DECAY_VIEW_MOVE_X, 'move x')
    within(d.decayMoveY, DECAY_VIEW_MOVE_Y, 'move y')
    within(d.decayMagnification, DECAY_VIEW_MAGNIFICATION, 'magnification')
    within(d.decayExplodeZ, DECAY_EXPLODE_Z, 'explode z')
  })

  /**
   * Each slider's own step has to land on its default, or the first nudge jumps to a figure the
   * chart never opened on and there is no way back to it by dragging.
   */
  it('lands on each slider’s own step', () => {
    const d = decayViewDefaults()
    const onStep = (v: number, min: number, step: number, what: string) => {
      const steps = (v - min) / step
      expect(Math.abs(steps - Math.round(steps)), what).toBeLessThan(1e-9)
    }
    onStep(d.decayRotationDeg, DECAY_ROTATION_DEG.min, 1, 'rotation')
    onStep(d.decayDepthPercent, DECAY_DEPTH_PERCENT.min, 1, 'tilt')
    onStep(d.decayMoveX, DECAY_VIEW_MOVE_X.min, 0.1, 'move x')
    onStep(d.decayMoveY, DECAY_VIEW_MOVE_Y.min, 0.1, 'move y')
    onStep(d.decayMagnification, DECAY_VIEW_MAGNIFICATION.min, 5, 'magnification')
    onStep(d.decayExplodeZ, DECAY_EXPLODE_Z.min, DECAY_EXPLODE_Z.step, 'explode z')
  })

  /** Fresh objects: the panel writes into the models it hands back, and freezing one would throw. */
  it('hands back a new object each time', () => {
    expect(decayViewDefaults()).not.toBe(decayViewDefaults())
  })
})

describe('magnificationFromStored', () => {
  /**
   * The control was re-based on 2026-10-29 — it ran to 2000 and is a tenth of that now, asked for
   * in the same message ("when 2000%, show 200% instead"). Anything above the new ceiling has to
   * be the old unit.
   */
  it('divides a figure written on the old scale by ten', () => {
    expect(magnificationFromStored(1500)).toBe(150)
    expect(magnificationFromStored(2000)).toBe(200)
    expect(magnificationFromStored(900)).toBe(90)
  })

  it('leaves a figure already on this one alone', () => {
    expect(magnificationFromStored(150)).toBe(150)
    expect(magnificationFromStored(DECAY_VIEW_MAGNIFICATION.max)).toBe(DECAY_VIEW_MAGNIFICATION.max)
  })

  it('stays inside the slider, and falls back rather than handing out a nonsense box', () => {
    expect(magnificationFromStored(5)).toBe(DECAY_VIEW_MAGNIFICATION.min)
    expect(magnificationFromStored(0)).toBe(DECAY_VIEW_MAGNIFICATION.default)
    expect(magnificationFromStored(Number.NaN)).toBe(DECAY_VIEW_MAGNIFICATION.default)
  })
})

describe('decayExportTitle', () => {
  it('names the measurement and the view, for all three modes', () => {
    const burst = ragged()
    const csd = dense()
    expect(decayExportTitle(burst, 'Waterfall', 30)).toBe('Burst decay — waterfall, 30 dB range')
    expect(decayExportTitle(burst, 'WaterfallPeriods', 30))
      .toBe('Burst decay — waterfall, by period, 30 dB range')
    expect(decayExportTitle(csd, 'Sonogram', 40))
      .toBe('Cumulative spectral decay — sonogram, 40 dB range')
  })

  it('still names the view when there is no surface', () => {
    expect(decayExportTitle(null, 'Waterfall', 30)).toContain('waterfall')
  })
})

describe('decayOptionsKey', () => {
  /**
   * <b>The regression, written down as the case that caused it.</b> Switching the view from the
   * cumulative spectral decay to the burst decay hands the chart a different surface at the same
   * mode and the same range — and the second one's depth is counted in periods. A key that read
   * only the two settings called them equal, so the chart kept the labels it had and drew the
   * burst decay under "Time (ms)" over a scale of periods.
   */
  it('tells the two decay analyses apart, which differ only in their slice axis', () => {
    const csd = dense()      // Milliseconds
    const burst = ragged()   // Periods
    expect(csd.sliceAxis).not.toBe(burst.sliceAxis)
    expect(decayOptionsKey(csd, 'Waterfall', 30)).not.toBe(decayOptionsKey(burst, 'Waterfall', 30))
  })

  it('changes with the mode and with the range', () => {
    const s = dense()
    expect(decayOptionsKey(s, 'Waterfall', 30)).not.toBe(decayOptionsKey(s, 'WaterfallPeriods', 30))
    expect(decayOptionsKey(s, 'Waterfall', 30)).not.toBe(decayOptionsKey(s, 'Waterfall', 40))
  })

  /** The point of it: a fresh pass of the same measurement must not pay for a rebuild. */
  it('is unchanged by a new pass of the same measurement', () => {
    expect(decayOptionsKey(dense(), 'Waterfall', 30)).toBe(decayOptionsKey(dense(), 'Waterfall', 30))
  })

  it('answers for an empty chart without throwing', () => {
    expect(decayOptionsKey(null, 'Waterfall', 30)).toBeTruthy()
    expect(decayOptionsKey(null, 'Waterfall', 30)).not.toBe(decayOptionsKey(dense(), 'Waterfall', 30))
  })
})

describe('decayModeIs3d', () => {
  it('is every mode but the sonogram, which is a flat colour map', () => {
    expect(decayModeIs3d('Waterfall')).toBe(true)
    expect(decayModeIs3d('WaterfallPeriods')).toBe(true)
    expect(decayModeIs3d('Sonogram')).toBe(false)
  })
})

describe('the two charts agree', () => {
  /** A guard against the point builder growing its own idea of where a slice's data starts. */
  it('about where a ragged slice begins, for every slice', () => {
    const surface = ragged()
    const cloud = decayPoints(surface, 'Waterfall', 30)
    const acrossCoords = surface.freqHz.map((f) => decayAxisCoordinate(f, true))

    for (const [r, slice] of surface.slices.entries()) {
      const y = DECAY_DEPTH_SIGN * slice.value
      for (let c = 0; c < surface.freqHz.length; c++) {
        const present = cloud.points.some((p) => p.y === y && p.x === acrossCoords[c])
        expect(present, `${r},${c}`).toBe(levelAt(surface, r, c) !== null)
        if (c < slice.firstValidColumn) expect(present, `${r},${c} before its start`).toBe(false)
      }
    }
  })
})
