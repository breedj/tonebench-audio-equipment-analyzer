import { describe, expect, it } from 'vitest'
import {
  MAX_AVERAGES,
  STIMULUS_DEFAULTS,
  defaultsByStimulus,
  readSettings,
  toStored,
  toStoredWithActive,
} from './stimulusSettings'

describe('STIMULUS_DEFAULTS', () => {
  it('gives the two broadband stimuli the records they need and the stepped one its own', () => {
    expect(STIMULUS_DEFAULTS.Chirp).toEqual({
      averageCount: 10,
      smoothingDenominator: 3,
      levelValue: 6,
      levelUnit: 'Dbv',
    })
    expect(STIMULUS_DEFAULTS.PinkNoise).toEqual({
      averageCount: 10,
      smoothingDenominator: 3,
      levelValue: 6,
      levelUnit: 'Dbv',
    })
    // A stepped sweep pays its averages at every point of its grid; ten of them is ten sweeps.
    expect(STIMULUS_DEFAULTS.SteppedSine.averageCount).toBeLessThan(STIMULUS_DEFAULTS.Chirp.averageCount)
  })

  it('smooths the broadband pair at the widest band on offer', () => {
    // 1/3 octave, asked for 2026-10-22, and the one default here that depends on a *fix* rather than
    // on a preference: that window reaches 12 % past the top analysed bin, which before
    // FractionalOctaveSmoother.Band clamped it put the open-circuit ceiling into the last columns.
    // The top-of-band spike reported on 2026-10-20 was at exactly this setting.
    expect(STIMULUS_DEFAULTS.Chirp.smoothingDenominator).toBe(3)
    expect(STIMULUS_DEFAULTS.PinkNoise.smoothingDenominator).toBe(STIMULUS_DEFAULTS.Chirp.smoothingDenominator)
    // Nothing smooths a stepped sweep, so its own figure is inert -- but it must stay a *narrower*
    // band than the broadband pair's, or the day it is read it would smooth a point grid flat.
    expect(STIMULUS_DEFAULTS.SteppedSine.smoothingDenominator).toBeGreaterThan(
      STIMULUS_DEFAULTS.Chirp.smoothingDenominator,
    )
  })

  it('drives the stepped sine far gentler than the broadband pair, which is the measured difference', () => {
    // 6 dBV against -20 dBV, asked for 2026-10-21 and measured on a QA403: the broadband stimuli
    // need some 30 dB more drive for the same coherence, because a stepped sine puts all of its
    // energy on one bin at a time. The two broadband ones are interchangeable at equal RMS.
    expect(STIMULUS_DEFAULTS.SteppedSine.levelValue).toBe(-20)
    expect(STIMULUS_DEFAULTS.Chirp.levelValue).toBe(6)
    expect(STIMULUS_DEFAULTS.PinkNoise.levelValue).toBe(STIMULUS_DEFAULTS.Chirp.levelValue)
    expect(STIMULUS_DEFAULTS.Chirp.levelValue - STIMULUS_DEFAULTS.SteppedSine.levelValue).toBeGreaterThan(20)
  })

  it('stays inside what the server will accept', () => {
    for (const slot of Object.values(STIMULUS_DEFAULTS)) {
      expect(slot.averageCount).toBeGreaterThanOrEqual(1)
      expect(slot.averageCount).toBeLessThanOrEqual(MAX_AVERAGES)
      // Every default is in dBV, which is the one unit a negative level is legal in: this panel
      // refuses a non-positive volt or millivolt amplitude, so -20 V would be a default that cannot
      // be run.
      expect(slot.levelUnit).toBe('Dbv')
    }
  })
})

describe('readSettings', () => {
  it('is this build’s defaults when nothing was stored', () => {
    expect(readSettings(undefined, 'PinkNoise', {})).toEqual(defaultsByStimulus())
    expect(readSettings(null, 'Chirp', {})).toEqual(defaultsByStimulus())
  })

  it('keeps a measurement saved before the map existed, and defaults the other two', () => {
    // The whole compatibility case: one set of values, stored against whichever stimulus was
    // selected. A measurement saved before the level joined the slot is the same case one field
    // along, so its stored level lands on the selected stimulus and the others keep the defaults.
    const map = readSettings(undefined, 'Chirp', {
      averageCount: 2,
      smoothingDenominator: 48,
      levelValue: -20,
      levelUnit: 'Dbv',
    })

    expect(map.Chirp).toEqual({
      averageCount: 2,
      smoothingDenominator: 48,
      levelValue: -20,
      levelUnit: 'Dbv',
    })
    expect(map.PinkNoise).toEqual(STIMULUS_DEFAULTS.PinkNoise)
    expect(map.SteppedSine).toEqual(STIMULUS_DEFAULTS.SteppedSine)
  })

  it('lets the active values win over the stored slot for the selected stimulus', () => {
    // They cannot be allowed to disagree about the stimulus that is showing: averageCount,
    // smoothingDenominator, levelValue and levelUnit are what the run used, and the map is the
    // memory behind them.
    const map = readSettings(
      { chirp: { averageCount: 4, smoothingDenominator: 24, levelValue: 0, levelUnit: 'Dbv' } },
      'Chirp',
      { averageCount: 7, smoothingDenominator: 6, levelValue: 12, levelUnit: 'Volt' },
    )

    expect(map.Chirp).toEqual({
      averageCount: 7,
      smoothingDenominator: 6,
      levelValue: 12,
      levelUnit: 'Volt',
    })
  })

  it('leaves the stimuli that are not selected on their stored values', () => {
    const map = readSettings(
      {
        pinkNoise: { averageCount: 16, smoothingDenominator: 24, levelValue: 2, levelUnit: 'Volt' },
        steppedSine: { averageCount: 3 },
      },
      'Chirp',
      { averageCount: 10, smoothingDenominator: 12 },
    )

    expect(map.PinkNoise).toEqual({
      averageCount: 16,
      smoothingDenominator: 24,
      levelValue: 2,
      levelUnit: 'Volt',
    })
    expect(map.SteppedSine.averageCount).toBe(3)
    // An absent part of a slot falls back rather than becoming zero.
    expect(map.SteppedSine.smoothingDenominator).toBe(STIMULUS_DEFAULTS.SteppedSine.smoothingDenominator)
    expect(map.SteppedSine.levelValue).toBe(STIMULUS_DEFAULTS.SteppedSine.levelValue)
    expect(map.SteppedSine.levelUnit).toBe(STIMULUS_DEFAULTS.SteppedSine.levelUnit)
  })

  it('ignores junk rather than carrying it onto the panel', () => {
    const map = readSettings(
      {
        chirp: { averageCount: 0 },
        pinkNoise: { smoothingDenominator: -4 },
        steppedSine: { levelValue: Number.NaN },
        Nonsense: { averageCount: 5 },
      } as never,
      'SteppedSine',
      {},
    )

    expect(map.Chirp).toEqual(STIMULUS_DEFAULTS.Chirp)
    expect(map.PinkNoise).toEqual(STIMULUS_DEFAULTS.PinkNoise)
    // NaN would reach the number box as a blank and the request as a level the server refuses.
    expect(map.SteppedSine.levelValue).toBe(STIMULUS_DEFAULTS.SteppedSine.levelValue)
  })

  it('keeps a level of exactly zero, which is a real drive level and not an absent one', () => {
    // 0 dBV is 1 V and a perfectly ordinary setting, so the guard has to be Number.isFinite rather
    // than a truthiness test -- the trap the smoothing field already documents below.
    const map = readSettings({ chirp: { levelValue: 0 } }, 'PinkNoise', {})
    expect(map.Chirp.levelValue).toBe(0)
  })

  it('refuses a power unit from a stored slot, as the panel refuses one from the pair', () => {
    // A one-port has no output power to aim at, so there is no level mode here and the server
    // rejects watts and milliwatts alike. Both halves fall back together: a value kept under a unit
    // that was dropped would mean a different level than it was typed as.
    const map = readSettings(
      { chirp: { levelValue: 4, levelUnit: 'Watt' }, pinkNoise: { levelUnit: 'MilliWatt' } } as never,
      'SteppedSine',
      {},
    )

    expect(map.Chirp.levelUnit).toBe('Dbv')
    expect(map.PinkNoise.levelUnit).toBe('Dbv')
  })

  it('keeps smoothing None, which is a real choice and not an absent one', () => {
    const map = readSettings({ chirp: { smoothingDenominator: 0 } }, 'PinkNoise', {})
    expect(map.Chirp.smoothingDenominator).toBe(0)
  })
})

describe('toStored', () => {
  it('writes the keys params_json is read back with', () => {
    // The server's serializer camelCases property names, so a map written by the client has to use
    // that spelling or a measurement saved here reads back as "no map at all" over there -- and the
    // two stimuli that are not selected would silently revert to the defaults on every load.
    const stored = toStored(defaultsByStimulus())

    expect(Object.keys(stored).sort()).toEqual(['chirp', 'pinkNoise', 'steppedSine'])
    expect(readSettings(stored, 'Chirp', {})).toEqual(defaultsByStimulus())
  })

  it('round-trips a customised map', () => {
    const map = defaultsByStimulus()
    map.SteppedSine = { averageCount: 5, smoothingDenominator: 0, levelValue: -34, levelUnit: 'Dbv' }
    map.Chirp = { averageCount: 20, smoothingDenominator: 3, levelValue: 1.5, levelUnit: 'Volt' }

    expect(readSettings(toStored(map), 'PinkNoise', {})).toEqual(map)
  })
})

describe('toStoredWithActive', () => {
  it('stores the values on screen under the stimulus that is selected', () => {
    // The report this exists for: a level typed and saved without touching the stimulus dropdown.
    // The form parks a slot only on a switch, so the map still held the previous figure.
    const stored = toStoredWithActive(defaultsByStimulus(), 'PinkNoise', {
      averageCount: 4,
      smoothingDenominator: 24,
      levelValue: 0,
      levelUnit: 'Dbv',
    })

    expect(stored.pinkNoise).toEqual({
      averageCount: 4,
      smoothingDenominator: 24,
      levelValue: 0,
      levelUnit: 'Dbv',
    })
    // And leaves the other two exactly as the form holds them.
    expect(stored.chirp).toEqual(STIMULUS_DEFAULTS.Chirp)
    expect(stored.steppedSine).toEqual(STIMULUS_DEFAULTS.SteppedSine)
  })

  it('agrees with readSettings about which slot the active values belong to', () => {
    // The two are a save/load pair, so a document written by one must read back unchanged by the
    // other — that is what makes the stored map self-consistent rather than merely repaired on load.
    const map = defaultsByStimulus()
    map.Chirp = { averageCount: 3, smoothingDenominator: 6, levelValue: 2, levelUnit: 'Volt' }
    const active = { averageCount: 9, smoothingDenominator: 0, levelValue: -5, levelUnit: 'Dbv' as const }

    const stored = toStoredWithActive(map, 'SteppedSine', active)

    expect(readSettings(stored, 'SteppedSine', active)).toEqual(readSettings(stored, 'SteppedSine', {}))
    expect(readSettings(stored, 'SteppedSine', {}).SteppedSine).toEqual(active)
    expect(readSettings(stored, 'SteppedSine', {}).Chirp).toEqual(map.Chirp)
  })
})
