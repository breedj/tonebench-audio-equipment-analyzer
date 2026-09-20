import { describe, expect, it } from 'vitest'
import { parseSpectrumUnit, unitAxisLabel, unitOffsetDb, unitSuffix } from './spectrumUnits'
import {
  bandwidthPreset,
  bandwidthPresetValues,
  isWeighted,
  NO_WEIGHTING,
  weightingFromStored,
  weightingLabel,
  weightingQualifier,
} from './weighting'

describe('weightingLabel / weightingQualifier', () => {
  it('is null when nothing is weighted', () => {
    expect(weightingLabel(NO_WEIGHTING)).toBeNull()
    expect(weightingLabel(null)).toBeNull()
    expect(weightingQualifier(NO_WEIGHTING)).toBeNull()
    expect(isWeighted(NO_WEIGHTING)).toBe(false)
  })

  it('names the standard curve', () => {
    expect(weightingLabel({ standard: 'A', user1: false, user2: false })).toBe('A-weighted')
    expect(weightingLabel({ standard: 'ItuR468', user1: false, user2: false })).toBe('ITU-R 468')
    expect(weightingQualifier({ standard: 'A', user1: false, user2: false })).toBe('A')
    expect(weightingQualifier({ standard: 'ItuR468', user1: false, user2: false })).toBe('468')
  })

  it('combines the standard with the user tables and names them', () => {
    const spec = { standard: 'A' as const, user1: true, user2: true }
    expect(weightingLabel(spec, 'itu468', null)).toBe('A-weighted + User 1 (itu468) + User 2 (no table)')
    expect(weightingQualifier(spec)).toBe('A+U1+U2')
    expect(weightingLabel({ standard: 'None', user1: false, user2: true }, null, 'mine')).toBe('User 2 (mine)')
    expect(weightingQualifier({ standard: 'None', user1: false, user2: true })).toBe('U2')
  })
})

describe('weightingFromStored', () => {
  it('reads a stored type, case-insensitively, and treats an old row as unweighted', () => {
    expect(weightingFromStored('ccirarm', true, null)).toEqual({ standard: 'CcirArm', user1: true, user2: false })
    expect(weightingFromStored(undefined, undefined, undefined)).toEqual(NO_WEIGHTING)
    expect(weightingFromStored('B', false, false)).toEqual(NO_WEIGHTING)
  })
})

describe('bandwidth presets', () => {
  it('recognises the two presets and calls anything else custom', () => {
    expect(bandwidthPreset(20, 20000)).toBe('audio')
    expect(bandwidthPreset(20, 80000)).toBe('wide')
    expect(bandwidthPreset(20, 22000)).toBe('custom')
    expect(bandwidthPreset(10, 20000)).toBe('custom')
  })

  it('maps a preset back to its edges and custom to nothing', () => {
    expect(bandwidthPresetValues('audio')).toEqual({ loHz: 20, hiHz: 20000 })
    expect(bandwidthPresetValues('wide')).toEqual({ loHz: 20, hiHz: 80000 })
    expect(bandwidthPresetValues('custom')).toBeNull()
  })
})

describe('spectrum units', () => {
  it('needs no ENBW for dBV and offsets a density by -10·log10(ENBW)', () => {
    expect(unitOffsetDb('dBV', undefined, 20)).toBe(0)
    // Hann at 65536 points and 192 kHz: bin 2.93 Hz, ENBW 1.5 bins = 4.39 Hz -> -6.43 dB.
    const enbwHz = 1.5 * (192000 / 65536)
    expect(unitOffsetDb('dBVPerRootHz', enbwHz, 20)).toBeCloseTo(-6.43, 2)
    expect(unitOffsetDb('dBVPerRootHzInput', enbwHz, 20)).toBeCloseTo(-26.43, 2)
    expect(unitOffsetDb('dBVPerRootHzInput', enbwHz, Number.NaN)).toBeCloseTo(-6.43, 2)
  })

  it('is null for a density without an ENBW', () => {
    expect(unitOffsetDb('dBVPerRootHz', undefined, 0)).toBeNull()
    expect(unitOffsetDb('dBVPerRootHz', 0, 0)).toBeNull()
    expect(unitOffsetDb('dBVPerRootHzInput', undefined, 0)).toBeNull()
  })

  it('parses a stored unit and labels the axis', () => {
    expect(parseSpectrumUnit('dBVPerRootHz')).toBe('dBVPerRootHz')
    expect(parseSpectrumUnit(null)).toBe('dBV')
    expect(parseSpectrumUnit('volts')).toBe('dBV')
    expect(unitAxisLabel('dBV')).toBe('Level (dBV)')
    expect(unitAxisLabel('dBVPerRootHzInput')).toContain('Input-referred')
    expect(unitSuffix('dBVPerRootHz')).toBe('dBV/√Hz')
  })
})
