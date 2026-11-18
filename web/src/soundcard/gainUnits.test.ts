import { describe, expect, it } from 'vitest'
import {
  MAX_ABS_DB,
  PROBE_PRESETS,
  formatGain,
  fromDisplay,
  matchingProbePreset,
  probePresetHint,
  toDisplay,
} from './gainUnits'

describe('gainUnits', () => {
  it('toDisplay_InDb_IsTheStoredValue', () => {
    expect(toDisplay(-20, 'Db')).toBe(-20)
    expect(toDisplay(0, 'Db')).toBe(0)
  })

  // The case the unit selector exists for: a 10:1 divider reads as 0.1, not as -20.
  it('toDisplay_AsARatio_ReadsADividerTheWayABenchDoes', () => {
    expect(toDisplay(-20, 'Times')).toBeCloseTo(0.1, 10)
    expect(toDisplay(0, 'Times')).toBeCloseTo(1, 10)
    expect(toDisplay(20, 'Times')).toBeCloseTo(10, 10)
  })

  it('fromDisplay_RoundTripsThroughBothUnits', () => {
    for (const db of [-40, -20, -6, 0, 6, 20]) {
      expect(fromDisplay(toDisplay(db, 'Db'), 'Db')).toBeCloseTo(db, 10)
      expect(fromDisplay(toDisplay(db, 'Times'), 'Times')).toBeCloseTo(db, 10)
    }
  })

  it('fromDisplay_ARatioOfOne_IsNoGain', () => {
    expect(fromDisplay(1, 'Times')).toBeCloseTo(0, 10)
  })

  // log10(0) is -Infinity, which would reach the server as an invalid document and come back as a
  // 400 the user cannot act on. Zero and negative ratios mean "nothing declared" instead.
  it('fromDisplay_ARatioOfZero_IsNoGainRatherThanMinusInfinity', () => {
    expect(fromDisplay(0, 'Times')).toBe(0)
    expect(Number.isFinite(fromDisplay(0, 'Times'))).toBe(true)
  })

  // A negative ratio is an inversion, which is a phase statement rather than a gain.
  it('fromDisplay_ANegativeRatio_IsNoGain', () => {
    expect(fromDisplay(-0.1, 'Times')).toBe(0)
  })

  it('fromDisplay_NotANumber_IsNoGain', () => {
    expect(fromDisplay(Number.NaN, 'Db')).toBe(0)
    expect(fromDisplay(Number.POSITIVE_INFINITY, 'Times')).toBe(0)
  })

  it('fromDisplay_BeyondTheServersBound_IsClamped', () => {
    expect(fromDisplay(500, 'Db')).toBe(MAX_ABS_DB)
    expect(fromDisplay(-500, 'Db')).toBe(-MAX_ABS_DB)
    // 1e6 is +120 dB, past the bound.
    expect(fromDisplay(1e6, 'Times')).toBe(MAX_ABS_DB)
  })

  it('formatGain_SignsTheDbValue_AndMarksTheRatio', () => {
    expect(formatGain(-20, 'Db')).toBe('-20 dB')
    expect(formatGain(6, 'Db')).toBe('+6 dB')
    expect(formatGain(-20, 'Times')).toBe('×0.1')
  })

  // 0.1 must not read as 0.1000 just because a ratio needs four decimals at the small end.
  it('formatGain_DropsTrailingZeros', () => {
    expect(formatGain(0, 'Times')).toBe('×1')
    // Zero carries no sign: the badge is grey at 0 to say nothing is declared, and "+0" reads as
    // a figure that was chosen.
    expect(formatGain(0, 'Db')).toBe('0 dB')
  })
})

describe('the probe presets', () => {
  it('are dividers, labelled the way the hardware is', () => {
    // The inversion is the hazard: a 10:1 divider typed as +20 dB makes every reported level
    // twenty decibels high, and nothing on screen contradicts it. So the chip carries the
    // hardware's own label and the gain it means is negative.
    expect(PROBE_PRESETS.map((p) => p.label)).toEqual(['1:1', '10:1', '100:1'])
    expect(PROBE_PRESETS.map((p) => p.db)).toEqual([0, -20, -40])
  })

  it('offers the way back as well as the way out', () => {
    // A stored gain nobody remembers setting is this setting's one real hazard, so 1:1 is a press.
    expect(PROBE_PRESETS[0]).toEqual({ label: '1:1', db: 0 })
  })

  it('reads each ratio as the dB it is, both ways round', () => {
    for (const preset of PROBE_PRESETS) {
      expect(toDisplay(preset.db, 'Db')).toBeCloseTo(preset.db, 12)
    }
    // 10:1 is a tenth, 100:1 a hundredth -- the ratio direction this module exists to pin.
    expect(toDisplay(-20, 'Times')).toBeCloseTo(0.1, 12)
    expect(toDisplay(-40, 'Times')).toBeCloseTo(0.01, 12)
  })

  it('stays inside the bound the server enforces', () => {
    for (const preset of PROBE_PRESETS) {
      expect(Math.abs(preset.db)).toBeLessThanOrEqual(MAX_ABS_DB)
      expect(fromDisplay(preset.db, 'Db')).toBeCloseTo(preset.db, 12)
    }
  })
})

describe('matchingProbePreset', () => {
  it('names the preset a stored pair is', () => {
    expect(matchingProbePreset(0, 0)?.label).toBe('1:1')
    expect(matchingProbePreset(-20, -20)?.label).toBe('10:1')
    expect(matchingProbePreset(-40, -40)?.label).toBe('100:1')
  })

  it('is null where the two inputs differ', () => {
    // A preset writes both, so lighting its chip for a bench with a divider on one side only would
    // be the one case where the label is a lie.
    expect(matchingProbePreset(-20, 0)).toBeNull()
  })

  it('is null for a figure somebody typed', () => {
    expect(matchingProbePreset(-6, -6)).toBeNull()
    expect(matchingProbePreset(-19.5, -19.5)).toBeNull()
  })

  it('allows a hand-typed figure that is the preset', () => {
    // The stored value is a double and a user may well have typed -20.0 by hand.
    expect(matchingProbePreset(-20.0005, -20.0005)?.label).toBe('10:1')
  })

  it('is null for a figure that is not one', () => {
    expect(matchingProbePreset(Number.NaN, Number.NaN)).toBeNull()
  })
})

describe('probePresetHint', () => {
  it('says what a divider will write, in both units', () => {
    const hint = probePresetHint(PROBE_PRESETS[1])
    expect(hint).toContain('10:1')
    expect(hint).toContain('dB')
    expect(hint).toContain('×0.1')
  })

  it('says what 1:1 is for rather than quoting zero at the reader', () => {
    expect(probePresetHint(PROBE_PRESETS[0])).toContain('Nothing between')
  })
})
