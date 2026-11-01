import { describe, expect, it } from 'vitest'
import { DECAY_BAND_EDGE_FRACTION, decayTopHz } from './decay'
import { DEFAULT_IMPULSE_DISPLAY, defaultImpulseDisplay, impulseDisplayFromParams } from './impulseDisplay'

/**
 * The decay views' own top of band. The whole of why it exists is in `DECAY_BAND_EDGE_FRACTION`:
 * the last stretch below a measurement's Nyquist rings rather than decaying, and on a surface
 * counted in periods of each frequency that draws as a wall standing the whole depth.
 */
describe('decayTopHz', () => {
  it('is four fifths of the measurement’s own Nyquist', () => {
    expect(decayTopHz(96000)).toBe(76800)
    expect(decayTopHz(24000)).toBe(19200)
    expect(DECAY_BAND_EDGE_FRACTION).toBe(0.8)
  })

  /** A measurement with no span to derive from must not produce a zero-width axis or a NaN. */
  it('answers zero rather than a number for a rate it was not given', () => {
    expect(decayTopHz(0)).toBe(0)
    expect(decayTopHz(Number.NaN)).toBe(0)
    expect(decayTopHz(-1)).toBe(0)
  })
})

describe('the decay views’ top of band', () => {
  it('is below the window the Bode and harmonics views open on', () => {
    const d = defaultImpulseDisplay()
    expect(d.decayFreqToHz).toBeLessThan(d.freqToHz)
    expect(d.decayFreqToHz).toBe(decayTopHz(d.freqToHz))
  })

  /**
   * The point of deriving it rather than defaulting it: the figure is only right for the span the
   * measurement was actually taken at, and a bench measuring at 24 kHz would otherwise open on a
   * window four times its own Nyquist.
   */
  it('follows the span the measurement was taken at', () => {
    const at = (spanHz: number) =>
      impulseDisplayFromParams(JSON.stringify({ frequencySpanHz: spanHz })).decayFreqToHz

    expect(at(96000)).toBe(76800)
    expect(at(24000)).toBe(19200)
    expect(at(48000)).toBe(38400)
  })

  /**
   * A measurement saved before this field existed has nothing stored for it, so it takes the
   * derived figure rather than the old full-span window — which is the point, since the wall that
   * window drew is not something anybody chose. Its *other* fields are untouched.
   */
  it('is derived for a measurement that stored a window before the field existed', () => {
    const display = impulseDisplayFromParams(
      JSON.stringify({ frequencySpanHz: 24000, chartFreqFromHz: 50, chartFreqToHz: 96000 }),
    )

    expect(display.decayFreqToHz).toBe(19200)
    expect(display.freqToHz).toBe(96000)
    expect(display.freqFromHz).toBe(50)
  })

  it('keeps a figure that was stored for it', () => {
    const display = impulseDisplayFromParams(
      JSON.stringify({ frequencySpanHz: 96000, chartDecayFreqToHz: 20000 }),
    )

    expect(display.decayFreqToHz).toBe(20000)
  })

  /** Guarded like every other window field here: a nonsense value leaves the base alone. */
  it('ignores a stored figure that is not a positive number', () => {
    for (const bad of [0, -5, 'wide', null]) {
      const display = impulseDisplayFromParams(JSON.stringify({ chartDecayFreqToHz: bad }))
      expect(display.decayFreqToHz, String(bad)).toBe(DEFAULT_IMPULSE_DISPLAY.decayFreqToHz)
    }
  })
})

describe('the two mesh-line settings', () => {
  const read = (stored: Record<string, unknown>) => impulseDisplayFromParams(JSON.stringify(stored))

  it('are both off on a new measurement', () => {
    const d = defaultImpulseDisplay()
    expect(d.decayMeshFrequency).toBe(false)
    expect(d.decayMeshTime).toBe(false)
  })

  /**
   * They were one checkbox until 2026-11-01. A measurement saved with it on asked for every edge,
   * so it opens with both — read on the way in, with nothing rewritten, the way the CSD's block
   * shift is. The one that was *off* must stay off, or every older measurement gains a mesh.
   */
  it('read the one checkbox they replaced, when they have nothing of their own', () => {
    expect(read({ chartDecayMeshLines: true })).toMatchObject({
      decayMeshFrequency: true,
      decayMeshTime: true,
    })
    expect(read({ chartDecayMeshLines: false })).toMatchObject({
      decayMeshFrequency: false,
      decayMeshTime: false,
    })
  })

  it('each win over it, so a measurement saved since can rule one direction', () => {
    expect(read({ chartDecayMeshLines: true, chartDecayMeshFrequency: false })).toMatchObject({
      decayMeshFrequency: false,
      // Nothing of its own, so this half still reads the old field.
      decayMeshTime: true,
    })
    expect(read({ chartDecayMeshFrequency: true, chartDecayMeshTime: false })).toMatchObject({
      decayMeshFrequency: true,
      decayMeshTime: false,
    })
  })

  it('are off for a measurement that says nothing about a mesh at all', () => {
    expect(read({ frequencySpanHz: 96000 })).toMatchObject({
      decayMeshFrequency: false,
      decayMeshTime: false,
    })
  })
})
