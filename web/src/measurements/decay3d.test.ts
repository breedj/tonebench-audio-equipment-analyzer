import { describe, expect, it } from 'vitest'
import type { DecaySurface } from '../api/client'
import { levelAt, orientSurface, paletteIndex, paletteLut } from './decay'
import {
  decayExportTitle,
  DECAY_LIGHT,
  DECAY_LIGHT_AZIMUTH,
  DECAY_LIGHT_ELEVATION,
  DECAY_LIGHT_RADIUS,
  DECAY_LIGHT_STRENGTH,
  decayLightVector,
  decayFieldOfView,
  decayPulledBackZoom,
  DECAY_VIEW_MAGNIFICATION,
  decayCameraZoom,
  DECAY_3D_ZOOM,
  DECAY_3D_ZOOM_NARROW,
  DECAY_AXIS_LENGTH_DEFAULT,
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
   * The floor fraction is the thing this must not quietly drop: the level-to-colour mapping is
   * squeezed into the top three quarters of the ramp, and an engine palette that skipped it would
   * put the quiet half of every surface back into Jet's near-black foot.
   */
  it('is the palette LUT read through paletteIndex, so the floor fraction carries over', () => {
    const colors = decaySubsetColors('Jet', true)
    const lut = paletteLut('Jet', true)
    expect(colors).toHaveLength(256)
    for (const i of [0, 1, 128, 254, 255]) {
      const index = paletteIndex(i / 255)
      expect(colors[i]).toBe(peColor(lut[index * 3], lut[index * 3 + 1], lut[index * 3 + 2]))
    }
  })

  it('does not reach the very foot of the ramp, which is the body fill', () => {
    const colors = decaySubsetColors('Jet', true)
    const lut = paletteLut('Jet', true)
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
   * <b>The constraint, and the whole reason the three sliders were dead.</b> The engine measures x
   * and y against z, and z against x — so X and Z state one ratio twice and their product is 1.
   * Raw lengths break that (10 and 14), and a box told two contradictory things about its shape
   * degenerates to a slab that no later value moves.
   */
  it('writes a triple that agrees with itself, so x over z and z over x multiply to one', () => {
    for (const lengths of [
      DECAY_AXIS_LENGTH_DEFAULT,
      { x: 3, y: 2, z: 30 },
      { x: 25, y: 12, z: 4 },
    ]) {
      const a = decayGridAspect(lengths)
      expect(a.x * a.z).toBeCloseTo(1, 10)
    }
  })

  /**
   * Each slider has to reach its own edge, which is the second report of 2026-10-26: writing only
   * X and Y satisfied the constraint but left the depth as the unit, so the Time slider shortened
   * the other two instead of lengthening time.
   */
  it('lengthens the axis whose slider moved, and leaves the other two alone', () => {
    const base = decayGridAspect(DECAY_AXIS_LENGTH_DEFAULT)
    const deeper = decayGridAspect({ ...DECAY_AXIS_LENGTH_DEFAULT, z: DECAY_AXIS_LENGTH_DEFAULT.z * 2 })
    // z against x doubles; x and y, which are measured against z, halve with it.
    expect(deeper.z).toBeCloseTo(base.z * 2, 10)
    expect(deeper.x).toBeCloseTo(base.x / 2, 10)
    expect(deeper.y / deeper.x).toBeCloseTo(base.y / base.x, 10)

    const wider = decayGridAspect({ ...DECAY_AXIS_LENGTH_DEFAULT, x: DECAY_AXIS_LENGTH_DEFAULT.x * 2 })
    expect(wider.x).toBeCloseTo(base.x * 2, 10)
    expect(wider.z).toBeCloseTo(base.z / 2, 10)

    const taller = decayGridAspect({ ...DECAY_AXIS_LENGTH_DEFAULT, y: DECAY_AXIS_LENGTH_DEFAULT.y * 2 })
    expect(taller.y).toBeCloseTo(base.y * 2, 10)
    expect(taller.x).toBeCloseTo(base.x, 10)
    expect(taller.z).toBeCloseTo(base.z, 10)
  })

  /** A length of zero would divide by it; the stored fields are guarded, and so is this. */
  it('falls back to the default rather than dividing by zero', () => {
    for (const lengths of [
      { ...DECAY_AXIS_LENGTH_DEFAULT, z: 0 },
      { ...DECAY_AXIS_LENGTH_DEFAULT, x: 0 },
      { ...DECAY_AXIS_LENGTH_DEFAULT, y: 0 },
    ]) {
      const a = decayGridAspect(lengths)
      expect(Number.isFinite(a.x) && Number.isFinite(a.y) && Number.isFinite(a.z)).toBe(true)
    }
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

describe('decayFieldOfView and decayPulledBackZoom', () => {
  const BASE = -3.3

  /**
   * The report, in three parts: "a magnification of 90 % results in a larger graph than 110 %",
   * "above 200 % does not work", "setting it lower, up to 50, does not change it". All three are
   * the engine quantising DxFOV to whole numbers and clamping it at 1 — measured, 100 / 120 / 150 %
   * rendered byte-identically at 19 122 pixels of surface, as did 50 and 100 %.
   */
  it('takes the whole number of the lens, never below one', () => {
    expect(decayFieldOfView(50)).toBe(1)
    expect(decayFieldOfView(100)).toBe(1)
    expect(decayFieldOfView(199)).toBe(1)
    expect(decayFieldOfView(200)).toBe(2)
    expect(decayFieldOfView(800)).toBe(8)
  })

  /** The camera covers what the lens cannot, which is what makes the slider monotonic throughout. */
  it('grows without a dead step anywhere along the slider', () => {
    const sizes = []
    for (let pct = DECAY_VIEW_MAGNIFICATION.min; pct <= DECAY_VIEW_MAGNIFICATION.max; pct += 5) {
      // Bigger means a narrower lens, or a camera nearer the surface — zoom is negative, so
      // "nearer" is a smaller magnitude.
      sizes.push(decayFieldOfView(pct) / Math.abs(decayPulledBackZoom(BASE, pct)))
    }
    for (let i = 1; i < sizes.length; i++) expect(sizes[i]).toBeGreaterThan(sizes[i - 1])
  })

  /** Below 100 % the lens is pinned, so the camera is the whole of the reduction. */
  it('pulls the camera back below 100 per cent, where the lens cannot open any wider', () => {
    expect(decayPulledBackZoom(BASE, 50)).toBeCloseTo(BASE * 2, 10)
    expect(decayPulledBackZoom(BASE, 100)).toBeCloseTo(BASE, 10)
  })

  /** On a whole number the camera sits at the base framing every figure here was measured at. */
  it('leaves the camera at its base wherever the lens lands exactly', () => {
    for (const pct of [100, 200, 300, 800]) {
      expect(decayPulledBackZoom(BASE, pct)).toBeCloseTo(BASE, 10)
    }
  })

  it('clamps to the slider’s own range, and survives a cleared box', () => {
    expect(decayFieldOfView(10_000)).toBe(decayFieldOfView(DECAY_VIEW_MAGNIFICATION.max))
    expect(decayFieldOfView(0)).toBe(decayFieldOfView(DECAY_VIEW_MAGNIFICATION.min))
    expect(Number.isFinite(decayPulledBackZoom(BASE, Number.NaN))).toBe(true)
  })

  /** The vendor documents 8 to 10 as near-isometric, which is where the picture stops growing. */
  it('reaches the near-isometric end the engine documents, and no further', () => {
    expect(decayFieldOfView(DECAY_VIEW_MAGNIFICATION.max)).toBeGreaterThanOrEqual(8)
    expect(decayFieldOfView(DECAY_VIEW_MAGNIFICATION.max)).toBeLessThanOrEqual(10)
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
   * <p>It is also above the box, which is the negative y half; see decayLightVector.</p>
   */
  it('is in front of the surface and above it, which is the side the camera is on', () => {
    expect(DECAY_LIGHT.x).toBeLessThan(0)
    expect(DECAY_LIGHT.y).toBeLessThan(0)
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
