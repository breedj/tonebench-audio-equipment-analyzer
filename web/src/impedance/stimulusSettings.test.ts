import { describe, expect, it } from 'vitest'
import { MAX_AVERAGES, STIMULUS_DEFAULTS, defaultsByStimulus, readSettings, toStored } from './stimulusSettings'

describe('STIMULUS_DEFAULTS', () => {
  it('gives the two broadband stimuli the records they need and the stepped one its own', () => {
    expect(STIMULUS_DEFAULTS.Chirp).toEqual({ averageCount: 10, smoothingDenominator: 12 })
    expect(STIMULUS_DEFAULTS.PinkNoise).toEqual({ averageCount: 10, smoothingDenominator: 12 })
    // A stepped sweep pays its averages at every point of its grid; ten of them is ten sweeps.
    expect(STIMULUS_DEFAULTS.SteppedSine.averageCount).toBeLessThan(STIMULUS_DEFAULTS.Chirp.averageCount)
  })

  it('stays inside what the server will accept', () => {
    for (const slot of Object.values(STIMULUS_DEFAULTS)) {
      expect(slot.averageCount).toBeGreaterThanOrEqual(1)
      expect(slot.averageCount).toBeLessThanOrEqual(MAX_AVERAGES)
    }
  })
})

describe('readSettings', () => {
  it('is this build\u2019s defaults when nothing was stored', () => {
    expect(readSettings(undefined, 'PinkNoise', {})).toEqual(defaultsByStimulus())
    expect(readSettings(null, 'Chirp', {})).toEqual(defaultsByStimulus())
  })

  it('keeps a measurement saved before the map existed, and defaults the other two', () => {
    // The whole compatibility case: one pair, stored against whichever stimulus was selected.
    const map = readSettings(undefined, 'Chirp', { averageCount: 2, smoothingDenominator: 48 })

    expect(map.Chirp).toEqual({ averageCount: 2, smoothingDenominator: 48 })
    expect(map.PinkNoise).toEqual(STIMULUS_DEFAULTS.PinkNoise)
    expect(map.SteppedSine).toEqual(STIMULUS_DEFAULTS.SteppedSine)
  })

  it('lets the active pair win over the stored slot for the selected stimulus', () => {
    // They cannot be allowed to disagree about the stimulus that is showing: averageCount and
    // smoothingDenominator are what the run used, and the map is the memory behind them.
    const map = readSettings(
      { chirp: { averageCount: 4, smoothingDenominator: 24 } },
      'Chirp',
      { averageCount: 7, smoothingDenominator: 6 },
    )

    expect(map.Chirp).toEqual({ averageCount: 7, smoothingDenominator: 6 })
  })

  it('leaves the stimuli that are not selected on their stored values', () => {
    const map = readSettings(
      { pinkNoise: { averageCount: 16, smoothingDenominator: 24 }, steppedSine: { averageCount: 3 } },
      'Chirp',
      { averageCount: 10, smoothingDenominator: 12 },
    )

    expect(map.PinkNoise).toEqual({ averageCount: 16, smoothingDenominator: 24 })
    expect(map.SteppedSine.averageCount).toBe(3)
    // An absent half of a slot falls back rather than becoming zero.
    expect(map.SteppedSine.smoothingDenominator).toBe(STIMULUS_DEFAULTS.SteppedSine.smoothingDenominator)
  })

  it('ignores junk rather than carrying it onto the panel', () => {
    const map = readSettings(
      { chirp: { averageCount: 0 }, pinkNoise: { smoothingDenominator: -4 }, Nonsense: { averageCount: 5 } } as never,
      'SteppedSine',
      {},
    )

    expect(map.Chirp).toEqual(STIMULUS_DEFAULTS.Chirp)
    expect(map.PinkNoise).toEqual(STIMULUS_DEFAULTS.PinkNoise)
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
    map.SteppedSine = { averageCount: 5, smoothingDenominator: 0 }
    map.Chirp = { averageCount: 20, smoothingDenominator: 3 }

    expect(readSettings(toStored(map), 'PinkNoise', {})).toEqual(map)
  })
})
