import { describe, expect, it } from 'vitest'
import {
  BROADBAND_STIMULI,
  defaultsByStimulus,
  isBroadband,
  MAX_AVERAGES,
  readSettings,
  STIMULI,
  STIMULUS_DEFAULTS,
  toStored,
  toStoredWithActive,
} from './stimulusSettings'
import { MAX_AVERAGE_COUNT } from '../measurements/averaging'

describe('output-impedance stimulus settings', () => {
  it('starts the broadband pair on enough records and a wide enough band to read a milliohm', () => {
    // Zout is a difference of two nearly equal gains — about a millidecibel per milliohm with
    // 8 ohm then 4 ohm — and a broadband stimulus puts a tiny fraction of its energy in any one
    // bin. The record count and the smoothing band are the two levers that buy that back, so a
    // default that quietly narrows either is a default that reads noise as output impedance.
    for (const stimulus of BROADBAND_STIMULI) {
      expect(STIMULUS_DEFAULTS[stimulus].averageCount).toBeGreaterThanOrEqual(8)
      expect(STIMULUS_DEFAULTS[stimulus].smoothingDenominator).toBeLessThanOrEqual(6)
    }
  })

  it('leaves the stepped sine at one average per point', () => {
    // What this measurement has always defaulted to: a stepped sweep pays its count at every point
    // of the grid, so the broadband pair's ten would be ten times a run that already takes minutes.
    expect(STIMULUS_DEFAULTS.SteppedSine.averageCount).toBe(1)
  })

  it('calls exactly the chirp and the pink sequence broadband', () => {
    expect(STIMULI.filter(isBroadband)).toEqual(['Chirp', 'PinkNoise'])
    expect(isBroadband('SteppedSine')).toBe(false)
  })

  it('shares the one averaging ceiling', () => {
    expect(MAX_AVERAGES).toBe(MAX_AVERAGE_COUNT)
  })

  it('round-trips every slot through the camelCase keys params_json is written with', () => {
    // The server's serializer writes params_json, so a map stored PascalCase reads back as no map
    // at all — which costs the two unselected stimuli their values on every load.
    const map = defaultsByStimulus()
    map.Chirp.averageCount = 7
    map.SteppedSine.smoothingDenominator = 24

    const stored = toStored(map)
    expect(stored.chirp?.averageCount).toBe(7)
    expect(stored.steppedSine?.smoothingDenominator).toBe(24)

    expect(readSettings(stored, 'PinkNoise', map.PinkNoise)).toEqual(map)
  })

  it('overlays the active pair onto the selected slot, so the two cannot disagree', () => {
    const stored = toStored(defaultsByStimulus())
    const read = readSettings(stored, 'Chirp', { averageCount: 3, smoothingDenominator: 12 })

    expect(read.Chirp).toEqual({ averageCount: 3, smoothingDenominator: 12 })
    // And the stimuli that are not selected keep what was stored.
    expect(read.PinkNoise).toEqual(STIMULUS_DEFAULTS.PinkNoise)
    expect(read.SteppedSine).toEqual(STIMULUS_DEFAULTS.SteppedSine)
  })

  it('reads a measurement stored before this existed as this build‘s defaults', () => {
    expect(readSettings(null, 'SteppedSine', STIMULUS_DEFAULTS.SteppedSine)).toEqual(defaultsByStimulus())
    expect(readSettings(undefined, 'Chirp', STIMULUS_DEFAULTS.Chirp)).toEqual(defaultsByStimulus())
  })

  it('keeps a slot that is only half stored', () => {
    // A slot written before one of the two fields existed means "this build's default" for it,
    // which is what lets a field be added here without a migration.
    const read = readSettings({ chirp: { averageCount: 6 } }, 'SteppedSine', STIMULUS_DEFAULTS.SteppedSine)
    expect(read.Chirp.averageCount).toBe(6)
    expect(read.Chirp.smoothingDenominator).toBe(STIMULUS_DEFAULTS.Chirp.smoothingDenominator)
  })

  it('clamps a stored count into the range the panel can show', () => {
    const tooMany = readSettings({ chirp: { averageCount: 9999 } }, 'SteppedSine', STIMULUS_DEFAULTS.SteppedSine)
    expect(tooMany.Chirp.averageCount).toBe(MAX_AVERAGES)

    const tooFew = readSettings({ chirp: { averageCount: 0 } }, 'SteppedSine', STIMULUS_DEFAULTS.SteppedSine)
    expect(tooFew.Chirp.averageCount).toBe(1)
  })

  it('ignores a stored value that is not a finite number', () => {
    const read = readSettings(
      { chirp: { averageCount: Number.NaN, smoothingDenominator: null } },
      'SteppedSine',
      STIMULUS_DEFAULTS.SteppedSine,
    )
    expect(read.Chirp).toEqual(STIMULUS_DEFAULTS.Chirp)
  })

  it('folds the active pair into the slot it belongs to when saving', () => {
    // The form only parks a slot when the dropdown is turned, so a figure typed and saved without
    // touching it would otherwise store a map that contradicts the document's own averageCount.
    const stored = toStoredWithActive(defaultsByStimulus(), 'PinkNoise', { averageCount: 4, smoothingDenominator: 6 })

    expect(stored.pinkNoise).toEqual({ averageCount: 4, smoothingDenominator: 6 })
    expect(stored.chirp).toEqual(STIMULUS_DEFAULTS.Chirp)
  })

  it('agrees with what readSettings would make of it', () => {
    const active = { averageCount: 5, smoothingDenominator: 24 }
    const stored = toStoredWithActive(defaultsByStimulus(), 'Chirp', active)
    expect(readSettings(stored, 'Chirp', active).Chirp).toEqual(active)
  })
})
