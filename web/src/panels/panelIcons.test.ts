import { describe, expect, it } from 'vitest'
import { PANEL_GLYPHS, PANEL_ICON_FALLBACK, panelIconFor } from './panelIcons'

describe('panelIconFor', () => {
  it('matches a title whatever its case or surrounding space', () => {
    expect(panelIconFor('Display')).toEqual(panelIconFor('  display '))
  })

  it('reads a frequency range as the horizontal axis and every other range as the vertical one', () => {
    expect(panelIconFor('Frequency range (Hz)').icon).toBe('rangeX')
    expect(panelIconFor('Frequency axis').icon).toBe('rangeX')
    expect(panelIconFor('Gain range (dB)').icon).toBe('rangeY')
    expect(panelIconFor('Distortion range (%)').icon).toBe('rangeY')
    expect(panelIconFor('Phase range (°)').icon).toBe('rangeY')
  })

  it('reads a delay range as a clock rather than as an axis', () => {
    // It carries "range", so the generic axis rule would take it — the delay rule has to be first.
    expect(panelIconFor('Delay range (ms)').icon).toBe('stopwatch')
    expect(panelIconFor('Delay range (µs)').icon).toBe('stopwatch')
  })

  it('reads the per-channel spectrum tables, whose titles carry the channel', () => {
    expect(panelIconFor('Left: Harmonics').icon).toBe('bars')
    expect(panelIconFor('Right: Intermodulation').icon).toBe('bars')
    expect(panelIconFor('Left: Other peaks').icon).toBe('bars')
    expect(panelIconFor('Right: Multitone').icon).toBe('bars')
  })

  it('falls back rather than returning nothing', () => {
    expect(panelIconFor('Something nobody has written yet')).toEqual(PANEL_ICON_FALLBACK)
  })

  /**
   * The whole point of deriving the icon from the title is that no panel has to be edited, so a
   * title the app really renders landing on the fallback is a panel nobody noticed. This is the
   * list of titles CollapsibleSection is given across the app (grep `title=` on it); a new panel
   * belongs here as well as in the table.
   */
  it('gives every panel title in the app a deliberate icon', () => {
    const titles = [
      'Display',
      'Measurements',
      'Measurement',
      'Sweep',
      'Measurement settings',
      'Frequency range (Hz)',
      'Phase axis',
      'Impedance axis',
      'dBV range',
      'Gain range',
      'dB range',
      'Distortion range (%)',
      'Delay range (ms)',
      'Frequency range (kHz)',
      'Left: Other peaks',
      'Left: Multitone',
      'Left: Intermodulation',
      'Left: Harmonics',
      'Transfer function',
      'Stepped sweep',
      'Stability',
      'Reference',
      'Poles and zeros',
      'Pole/zero range (Hz)',
      'Phase range (°)',
      'Output impedance sweep',
      'Notes',
      'Level range',
      'History',
      'History details',
      'Harmonics',
      'Generator',
      'Gate',
      'Gain range (dB)',
      'Frequency range',
      'Frequency axis',
      'Fixture',
      'FFT settings',
      'Equivalent circuit',
      'Crosstalk sweep',
      'Crosstalk range (dB)',
      'Copy as…',
      'Component',
      'Calibration',
      'Bandwidth & weighting',
      'Advanced',
    ]
    const missed = titles.filter((t) => panelIconFor(t) === PANEL_ICON_FALLBACK)
    expect(missed).toEqual([])
  })

  it('names a glyph that exists', () => {
    for (const title of ['Display', 'Sweep', 'Delay range (ms)', 'Unknown']) {
      expect(PANEL_GLYPHS[panelIconFor(title).icon]).toBeDefined()
    }
  })
})
