import { describe, expect, it } from 'vitest'
import type { DecaySurface } from '../api/client'
import { levelAt, orientSurface, paletteIndex, paletteLut } from './decay'
import {
  decayExportTitle,
  decayCameraZoom,
  DECAY_3D_ZOOM,
  DECAY_3D_ZOOM_NARROW,
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
    expect([...across.z]).toEqual([0, 0.5])
    expect(across.acrossIsFrequency).toBe(true)
    expect(across.depthIsFrequency).toBe(false)

    const byPeriod = decayMesh(dense(), 'WaterfallPeriods', 30)
    expect([...byPeriod.x]).toEqual([0, 0.5])
    expect([...byPeriod.z]).toEqual([100, 1000, 10000])
    expect(byPeriod.acrossIsFrequency).toBe(false)
    expect(byPeriod.depthIsFrequency).toBe(true)
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
    expect(decayGridAspect()).toEqual(decayGridAspect())
    const a = decayGridAspect()
    expect(a.x).toBeGreaterThan(0)
    expect(a.y).toBeGreaterThan(0)
    expect(a.z).toBeGreaterThan(0)
  })

  it('is wider and deeper than it is tall, so a waterfall reads as one', () => {
    const a = decayGridAspect()
    expect(a.y).toBeLessThan(a.x)
    expect(a.y).toBeLessThan(a.z)
  })

  it('is close to square in the floor plane, so neither axis is foreshortened away', () => {
    const a = decayGridAspect()
    expect(Math.max(a.x, a.z) / Math.min(a.x, a.z)).toBeLessThan(1.5)
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
