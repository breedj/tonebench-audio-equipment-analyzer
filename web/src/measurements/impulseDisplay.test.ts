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
