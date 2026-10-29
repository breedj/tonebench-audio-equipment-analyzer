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
  DECAY_EXPLODE_Z,
  DECAY_VIEW_MOVE_X,
  DECAY_VIEW_MOVE_Y,
  decayViewDefaults,
  decayExportTitle,
  DECAY_LIGHT,
  DECAY_LIGHT_AZIMUTH,
  DECAY_LIGHT_ELEVATION,
  DECAY_LIGHT_RADIUS,
  DECAY_LIGHT_STRENGTH,
  decayLightVector,
  magnificationFromStored,
  DECAY_VIEW_MAGNIFICATION,
  decayCameraZoom,
  DECAY_3D_ZOOM,
  DECAY_3D_ZOOM_NARROW,
  explodeZForAxisLengths,
  decayGridAspect,
  decayMesh,
  decayModeIs3d,
  decaySubsetColors,
  peColor,
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

describe('decayMesh', () => {
  /**
   * The one test that matters: a cell of the matrix the engine is handed must be the level the
   * Canvas 2D chart reads at the same point. Compared cell for cell rather than by a summary —
   * a transposed or offset matrix agrees on every summary and is wrong about every point.
   */
  it('agrees with levelAt cell for cell, on both orientations', () => {
    const surface = ragged()
    for (const mode of ['Waterfall', 'WaterfallPeriods'] as const) {
      const mesh = decayMesh(surface, mode, 30)
      const view = orientSurface(surface, mode === 'WaterfallPeriods')
      expect(mesh.rows).toBe(view.depth.length)
      expect(mesh.cols).toBe(view.across.length)
      for (let r = 0; r < mesh.rows; r++) {
        for (let c = 0; c < mesh.cols; c++) {
          const expected = view.level(r, c)
          const actual = mesh.y[r * mesh.cols + c]
          if (expected === null) expect(Number.isNaN(actual), `${mode} ${r},${c}`).toBe(true)
          else expect(actual, `${mode} ${r},${c}`).toBeCloseTo(expected, 12)
        }
      }
    }
  })

  /**
   * NaN rather than a sentinel, and this is measured behaviour rather than taste: the engine's
   * own NullDataValue is ignored on the 3D path, so a sentinel is drawn as data and takes the
   * height axis with it. See the remarks on DecayMesh.
   */
  it('writes NaN for a slice that has no data at a column, and counts them', () => {
    const mesh = decayMesh(ragged(), 'Waterfall', 30)
    // Slice 2 starts at column 2, slice 3 at column 4.
    expect(Number.isNaN(mesh.y[2 * mesh.cols + 0])).toBe(true)
    expect(Number.isNaN(mesh.y[2 * mesh.cols + 1])).toBe(true)
    expect(mesh.y[2 * mesh.cols + 2]).toBeCloseTo(-10, 12)
    expect(mesh.missing).toBe(2 + 4)
  })

  it('leaves a surface with nothing missing entirely free of NaN', () => {
    const mesh = decayMesh(dense(), 'Waterfall', 30)
    expect(mesh.missing).toBe(0)
    expect([...mesh.y].every(Number.isFinite)).toBe(true)
  })

  /** Clamped like levelY, not dropped — the two charts must agree about the foot of the scale. */
  it('clamps a level below the range to the foot of it rather than discarding it', () => {
    const surface: DecaySurface = {
      sliceAxis: 'Milliseconds',
      freqHz: [100, 1000],
      resolutionLimitHz: 0,
      slices: [{ value: 0, firstValidColumn: 0, levelDb: [-80, 5] }],
    }
    const mesh = decayMesh(surface, 'Waterfall', 30)
    expect(mesh.y[0]).toBe(-30)
    expect(mesh.y[1]).toBe(0)
    expect(mesh.missing).toBe(0)
  })

  /**
   * Real values on both axes, and a flag saying which one is a frequency — the chart scales that
   * one logarithmically, which is what puts true hertz on its ticks.
   */
  it('hands over the real axis values and says which axis is the frequency', () => {
    const across = decayMesh(dense(), 'Waterfall', 30)
    expect([...across.x]).toEqual([100, 1000, 10000])
    expect(across.acrossIsFrequency).toBe(true)
    expect(across.depthIsFrequency).toBe(false)

    const byPeriod = decayMesh(dense(), 'WaterfallPeriods', 30)
    expect([...byPeriod.x]).toEqual([-0, -0.5])
    expect([...byPeriod.z]).toEqual([100, 1000, 10000])
    expect(byPeriod.acrossIsFrequency).toBe(false)
    expect(byPeriod.depthIsFrequency).toBe(true)
  })

  /**
   * <p>The depth coordinates are the measurement's own and are never negated — which end of the
   * axis faces the viewer is the camera's business. Negating them to pair with the engine's
   * <c>InvertedZAxis</c> was tried and reverted: that flag is refused by the 3D control, so the
   * negation showed through as a time axis reading 0,0 / -0,5 / -1,0.</p>
   */
  it('leaves the depth coordinates exactly as the measurement gives them', () => {
    expect([...decayMesh(dense(), 'Waterfall', 30).z]).toEqual([0, 0.5])
    expect([...decayMesh(dense(), 'WaterfallPeriods', 30).z]).toEqual([100, 1000, 10000])
  })

  /**
   * <p>The across axis is negated exactly when it is the linear one, which is the half of
   * un-mirroring that <c>InvertedXAxis</c> cannot do on its own: on a linear axis that flag
   * reverses the labels and not the drawing. On the logarithmic frequency axis it reverses both,
   * and a log scale could not take a negative coordinate anyway.</p>
   */
  it('negates the across axis exactly when it is the linear one', () => {
    for (const mode of ['Waterfall', 'WaterfallPeriods'] as const) {
      const mesh = decayMesh(ragged(), mode, 30)
      expect(mesh.acrossNegated, mode).toBe(!mesh.acrossIsFrequency)
      const negative = [...mesh.x].some((v) => v < 0)
      expect(negative, `${mode} has a negative across coordinate`).toBe(mesh.acrossNegated)
    }
  })

  /** A log axis cannot carry a zero or a negative, whichever axis is carrying the frequency. */
  it('keeps the depth axis positive, and the frequency axis whichever one it is', () => {
    for (const mode of ['Waterfall', 'WaterfallPeriods'] as const) {
      const mesh = decayMesh(ragged(), mode, 30)
      expect([...mesh.z].every((v) => v >= 0), `${mode} z`).toBe(true)
      const freqs = mesh.acrossIsFrequency ? mesh.x : mesh.z
      expect([...freqs].every((f) => f > 0), `${mode} frequency`).toBe(true)
    }
  })

  it('never calls both axes a frequency, since one of them is always the time', () => {
    for (const mode of ['Waterfall', 'WaterfallPeriods'] as const) {
      const mesh = decayMesh(ragged(), mode, 30)
      expect(mesh.acrossIsFrequency && mesh.depthIsFrequency).toBe(false)
    }
  })

  /** A log axis cannot carry a zero or a negative, so the frequency values must all be positive. */
  it('keeps every frequency coordinate positive, as a log axis requires', () => {
    for (const mode of ['Waterfall', 'WaterfallPeriods'] as const) {
      const mesh = decayMesh(ragged(), mode, 30)
      const freqs = mesh.acrossIsFrequency ? mesh.x : mesh.z
      expect([...freqs].every((f) => f > 0)).toBe(true)
    }
  })

  it('transposes the orientation, swapping which axis is frequency', () => {
    const surface = ragged()
    const across = decayMesh(surface, 'Waterfall', 30)
    const periods = decayMesh(surface, 'WaterfallPeriods', 30)
    expect(across.rows).toBe(4)
    expect(across.cols).toBe(5)
    expect(periods.rows).toBe(5)
    expect(periods.cols).toBe(4)
    // The same cell, reached the other way round.
    expect(periods.y[2 * periods.cols + 0]).toBeCloseTo(across.y[0 * across.cols + 2], 12)
  })
})

describe('decaySubsetColors', () => {
  /**
   * The floor fraction is the thing this must not quietly drop: where a palette's own foot is too
   * dark to read, the level-to-colour mapping is squeezed into the top of its ramp, and an engine
   * palette that skipped that would put the quiet half of every surface back into the near-black.
   * It is a property of the palette — Jet is the vendor's ramp and reaches its own floor, Hot does
   * not — so this checks one of each.
   */
  it('is the palette LUT read through paletteIndex, so the floor fraction carries over', () => {
    for (const palette of ['Jet', 'Hot'] as const) {
      const colors = decaySubsetColors(palette, true)
      const lut = paletteLut(palette, true)
      expect(colors).toHaveLength(256)
      for (const i of [0, 1, 128, 254, 255]) {
        const index = paletteIndex(i / 255, paletteFloorFraction(palette))
        expect(colors[i], `${palette}/${i}`).toBe(peColor(lut[index * 3], lut[index * 3 + 1], lut[index * 3 + 2]))
      }
    }
  })

  /** A lifted palette's own foot stays out of reach, which is what the body fill is painted with. */
  it('does not reach the very foot of a ramp that is lifted off it', () => {
    const colors = decaySubsetColors('Hot', true)
    const lut = paletteLut('Hot', true)
    expect(colors[0]).not.toBe(peColor(lut[0], lut[1], lut[2]))
  })

  it('follows the theme for the one palette that is theme-dependent', () => {
    expect(decaySubsetColors('Grey', true)[200]).not.toBe(decaySubsetColors('Grey', false)[200])
    expect(decaySubsetColors('Jet', true)[200]).toBe(decaySubsetColors('Jet', false)[200])
  })
})

describe('peColor', () => {
  it('puts red in the low byte and leaves it opaque', () => {
    expect(peColor(255, 0, 0)).toBe(0xff0000ff >>> 0)
    expect(peColor(0, 0, 255)).toBe(0xffff0000 >>> 0)
    expect(peColor(0, 0, 0)).toBe(0xff000000 >>> 0)
  })
})

describe('decayGridAspect', () => {
  /**
   * The regression this exists for: a box in data units is seven times wider than deep on a
   * 400-column, 60-slice CSD, and the surface is then drawn edge-on and clipped.
   */
  it('is the same box whatever the resolution of the surface', () => {
    const a = decayGridAspect()
    expect(a.x).toBeGreaterThan(0)
    expect(a.y).toBeGreaterThan(0)
    expect(a.z).toBeGreaterThan(0)
  })

  it('is wider than it is tall, so a waterfall reads as one', () => {
    const a = decayGridAspect()
    expect(a.y).toBeLessThan(a.x)
  })

  /**
   * <b>The constraint, and the whole reason the three sliders this replaced were dead.</b> The
   * engine measures x and y against z, and z against x — so X and Z state one ratio twice and their
   * product is 1. Raw lengths break that, and a box told two contradictory things about its own
   * shape degenerates to a slab that no later value moves. A square floor satisfies it by
   * construction, which is the simplest way to be sure of it.
   */
  it('writes a triple that agrees with itself, so x over z and z over x multiply to one', () => {
    for (const explode of [DECAY_EXPLODE_Z.min, 10, DECAY_EXPLODE_Z.default, DECAY_EXPLODE_Z.max]) {
      const a = decayGridAspect(explode)
      expect(a.x * a.z, String(explode)).toBeCloseTo(1, 10)
      expect(a.x, String(explode)).toBe(a.z)
    }
  })

  /** The slider's whole job: more explode is a taller box, and the floor does not move. */
  it('raises the height against a floor that stays where it is', () => {
    const low = decayGridAspect(5)
    const high = decayGridAspect(40)
    expect(high.y).toBeGreaterThan(low.y)
    expect(high.x).toBe(low.x)
    expect(high.z).toBe(low.z)
  })

  /** The vendor's own formula, divided through by the floor edge. */
  it('is the demo’s height-to-diagonal ratio', () => {
    for (const explode of [1, 10, 26, 50]) {
      expect(decayGridAspect(explode).y, String(explode)).toBeCloseTo(Math.SQRT2 * (explode / 100), 12)
    }
  })

  /** A cleared or nonsense box falls back rather than drawing one with no extent. */
  it('falls back to the default rather than to a box of no height', () => {
    for (const bad of [0, -5, Number.NaN]) {
      const a = decayGridAspect(bad)
      expect(a.y, String(bad)).toBeCloseTo(decayGridAspect(DECAY_EXPLODE_Z.default).y, 12)
    }
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
    // Three parts in a thousand of the box it replaces, which is why it could replace it.
    expect(Math.abs(decayGridAspect(26).y / (11 / 30) - 1)).toBeLessThan(0.005)
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

describe('decayViewDefaults', () => {
  /**
   * <b>The point of the function.</b> The Surface view panel's Reset view button writes these ten
   * values; a new measurement opens on them too, and it has to be the same ten — a button that
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

  /** Rotation and Tilt are in it although their sliders sit above the panel the button is in. */
  it('covers the camera as well as the box and the light', () => {
    expect(Object.keys(decayViewDefaults()).sort()).toEqual([
      'decayDepthPercent',
      'decayExplodeZ',
      'decayLightAzimuthDeg',
      'decayLightElevationDeg',
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
    within(d.decayLightAzimuthDeg, DECAY_LIGHT_AZIMUTH, 'light azimuth')
    within(d.decayLightElevationDeg, DECAY_LIGHT_ELEVATION, 'light elevation')
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

describe('DECAY_LIGHT_STRENGTH', () => {
  /**
   * The vendor documents 0.1 to 0.5 for this, and the figure is at the top of it deliberately —
   * asked for 2026-10-25 because the foot of the palette read almost black. A value outside the
   * range is not refused by the engine, it is silently clamped or ignored, so the bound is worth
   * holding here.
   */
  it('is inside the range the engine documents, at the bright end of it', () => {
    expect(DECAY_LIGHT_STRENGTH).toBeGreaterThanOrEqual(0.1)
    expect(DECAY_LIGHT_STRENGTH).toBeLessThanOrEqual(0.5)
    expect(DECAY_LIGHT_STRENGTH).toBeGreaterThan(0.3)
  })
})

describe('magnification', () => {
  /**
   * <b>The report, and what was wrong with the old control.</b> "Our stops magnifying when the full
   * chart is still in view" (2026-10-29). It narrowed the camera's lens and left the camera where
   * it was, and a long lens converges on an orthographic view that no further narrowing enlarges:
   * measured at a fixed camera, the drawn width went 96 px at 100 %, 209 at 400, 243 at 800, 270 at
   * 2000 and 278 at 4000. Scaling the box instead is linear — 132 / 264 / 393 px at floor edges of
   * 0.5 / 1 / 1.5 — and has no ceiling but the plot's own edge.
   */
  it('scales the box in proportion, all the way up the slider', () => {
    const at = (percent: number) => decayGridAspect(DECAY_EXPLODE_Z.default, percent).x
    const unit = at(DECAY_VIEW_MAGNIFICATION.default)

    expect(at(DECAY_VIEW_MAGNIFICATION.default)).toBeCloseTo(1, 12)
    expect(at(2 * DECAY_VIEW_MAGNIFICATION.default) / unit).toBeCloseTo(2, 12)
    expect(at(DECAY_VIEW_MAGNIFICATION.default / 2) / unit).toBeCloseTo(0.5, 12)
    // Proportional at the top of the slider as well, which is where the old control had nothing
    // left to give: the ceiling is this many times the view the chart opens on.
    const ceiling = DECAY_VIEW_MAGNIFICATION.max / DECAY_VIEW_MAGNIFICATION.default
    expect(at(DECAY_VIEW_MAGNIFICATION.max) / unit).toBeCloseTo(ceiling, 12)
    expect(ceiling).toBeGreaterThan(4)
  })

  /** Every step moves the picture, which is the whole of the complaint. */
  it('grows at every step of its own slider', () => {
    let previous = 0
    for (let p = DECAY_VIEW_MAGNIFICATION.min; p <= DECAY_VIEW_MAGNIFICATION.max; p += DECAY_VIEW_MAGNIFICATION.step) {
      const edge = decayGridAspect(DECAY_EXPLODE_Z.default, p).x
      expect(edge, String(p)).toBeGreaterThan(previous)
      previous = edge
    }
  })

  /** It changes the size and not the shape: the height against the floor is the explode's alone. */
  it('leaves the box’s shape to Explode Z', () => {
    const small = decayGridAspect(26, 50)
    const large = decayGridAspect(26, 400)
    expect(large.y / large.x).toBeCloseTo(small.y / small.x, 12)
    expect(large.x).toBe(large.z)
  })

  it('falls back to the default rather than to a box of no size', () => {
    for (const bad of [0, -10, Number.NaN]) {
      const a = decayGridAspect(DECAY_EXPLODE_Z.default, bad)
      expect(a.x, String(bad)).toBeCloseTo(1, 12)
    }
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

describe('decayModeIs3d', () => {
  it('is every mode but the sonogram, which is a flat colour map', () => {
    expect(decayModeIs3d('Waterfall')).toBe(true)
    expect(decayModeIs3d('WaterfallPeriods')).toBe(true)
    expect(decayModeIs3d('Sonogram')).toBe(false)
  })
})

describe('the two charts agree', () => {
  /** A guard against the mesh builder growing its own idea of where a slice's data starts. */
  it('about where a ragged slice begins, for every slice', () => {
    const surface = ragged()
    const mesh = decayMesh(surface, 'Waterfall', 30)
    for (let r = 0; r < mesh.rows; r++) {
      const first = surface.slices[r].firstValidColumn
      for (let c = 0; c < mesh.cols; c++) {
        expect(Number.isNaN(mesh.y[r * mesh.cols + c])).toBe(levelAt(surface, r, c) === null)
        if (c < first) expect(Number.isNaN(mesh.y[r * mesh.cols + c])).toBe(true)
      }
    }
  })
})

describe('decayCameraZoom', () => {
  it('uses the ordinary framing for every plot shape the layout actually produces', () => {
    for (const [w, h] of [[1200, 300], [873, 458], [600, 458], [450, 458]]) {
      expect(decayCameraZoom(w, h), `${w}x${h}`).toBe(DECAY_3D_ZOOM)
    }
  })

  it('pulls further back where the plot is taller than it is wide, which clips otherwise', () => {
    expect(decayCameraZoom(345, 458)).toBe(DECAY_3D_ZOOM_NARROW)
    expect(DECAY_3D_ZOOM_NARROW).toBeLessThan(DECAY_3D_ZOOM)
  })

  it('falls back to the ordinary framing rather than NaN before the plot has a size', () => {
    expect(decayCameraZoom(0, 0)).toBe(DECAY_3D_ZOOM)
    expect(decayCameraZoom(Number.NaN, 400)).toBe(DECAY_3D_ZOOM)
  })
})

describe('DECAY_LIGHT', () => {
  /**
   * The light was turned about the height axis, not moved nearer or further: its distance from the
   * origin is the vendor demo's to the last bit. Brightness is a property of that distance, so an
   * edit that changed it would be changing two things while appearing to change one.
   */
  it("is the demo's light turned, not moved", () => {
    const demo = { x: -2, y: -7, z: -7 }
    const length = (v: { x: number; y: number; z: number }) => Math.hypot(v.x, v.y, v.z)
    expect(length(DECAY_LIGHT)).toBeCloseTo(length(demo), 12)
  })

  /**
   * <p>The invariant that survived the default being retuned by eye (2026-10-26, to 180/40 from
   * the measured 164/44): the light has to be on the <em>camera's</em> side of the box, which is
   * the negative x half. That is the fault this family has actually had — moving the camera to
   * `360 - lean` left the light behind the surface and the chart went dark, 70.4 against 86.6 mean
   * luminance — and it is the one an angle typed into the panel can bring back.</p>
   *
   * <p><b>Only the x half is held.</b> How high the light sits has been turned three times — above
   * the box, level with it, and below it since 2026-10-29 — and all three are legitimate views of
   * a surface; which side of the box the light is on is not, because the far side leaves the chart
   * dark whatever the elevation.</p>
   */
  it('is in front of the surface, which is the side the camera is on', () => {
    expect(DECAY_LIGHT.x).toBeLessThan(0)
    expect(DECAY_LIGHT).toEqual(decayLightVector(DECAY_LIGHT_AZIMUTH.default, DECAY_LIGHT_ELEVATION.default))
  })

  it('turns rather than dims: every angle is the same distance from the box', () => {
    for (const az of [0, 45, 164, 270, 359]) {
      for (const el of [-90, -20, 0, 44, 90]) {
        const v = decayLightVector(az, el)
        expect(Math.hypot(v.x, v.y, v.z), `${az}/${el}`).toBeCloseTo(DECAY_LIGHT_RADIUS, 10)
      }
    }
  })

  it('puts a higher elevation further above the box, which is the negative side', () => {
    expect(decayLightVector(164, 80).y).toBeLessThan(decayLightVector(164, 10).y)
    expect(decayLightVector(164, 0).y).toBeCloseTo(0, 10)
  })

  it("is not the demo's own position, which went dark as the camera moved", () => {
    expect([DECAY_LIGHT.x, DECAY_LIGHT.z]).not.toEqual([-2, -7])
  })
})
