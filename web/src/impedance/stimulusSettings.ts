import type { ImpedanceSettingsByStimulus, ImpedanceStimulusName } from '../api/client'

// Averaging and smoothing, kept per stimulus.
//
// The three stimuli want different numbers and always did. A **chirp** and **pink noise** fold whole
// records into one estimate, so ten of them costs ten records -- seconds -- and on a divider
// measurement, where the DUT channel can sit 40 dB below the reference, that is what makes the curve
// readable. A **stepped sine** pays that count at *every point* of its grid, so ten averages over
// fifty points is ten times a sweep that already takes minutes.
//
// One shared pair therefore carried a stepped sweep's settings onto a chirp and back again, which is
// the fault measurements/levelMode.ts documents in its own words: "One staged value served all three
// modes, so switching Level mode from Generator voltage to DUT output power carried the generator's
// figure over and the panel read -20 W." Same shape, same answer -- a slot per stimulus, with the
// active pair staying authoritative so nothing downstream has to learn about the map.

/**
 * How many records one impedance measurement may fold in. Mirrors
 * `ImpedanceRequest.MaxAverageCount`, which is 20 where every other kind stops at 10: a divider puts
 * the DUT channel tens of dB below the reference, and pink noise's records are independent draws
 * that keep improving the estimate.
 */
export const MAX_AVERAGES = 20

export interface StimulusSettings {
  averageCount: number
  smoothingDenominator: number
}

/**
 * What each stimulus starts at.
 *
 * <p>Chirp and pink noise at <b>10 averages and 1/12 octave</b> (Joost, 2026-10-20): both were
 * reported as too noisy to read on a sub-ohm fixture at the old 2 and 1/48, and both can afford the
 * records. The stepped sine keeps 2 and 1/48 — it measures one tone at a time, so its points are
 * already clean and its averages are the expensive ones, and nothing is smoothed on that path at all
 * (the grid is coarser than the narrowest band on offer).</p>
 */
export const STIMULUS_DEFAULTS: Record<ImpedanceStimulusName, StimulusSettings> = {
  Chirp: { averageCount: 10, smoothingDenominator: 12 },
  PinkNoise: { averageCount: 10, smoothingDenominator: 12 },
  SteppedSine: { averageCount: 2, smoothingDenominator: 48 },
}

/** The per-stimulus map as it is stored in `params_json`; absent slots fall back to the defaults. */
export type SettingsByStimulus = ImpedanceSettingsByStimulus

/**
 * The key each stimulus is stored under. `params_json` is written by the server's serializer, which
 * camelCases property names, so the stored spelling is not the `ImpedanceStimulusName` the rest of
 * the client indexes by — the same split `levelByMode`'s slot names have.
 */
const STORED_KEY: Record<ImpedanceStimulusName, keyof SettingsByStimulus> = {
  Chirp: 'chirp',
  PinkNoise: 'pinkNoise',
  SteppedSine: 'steppedSine',
}

const STIMULI: ImpedanceStimulusName[] = ['Chirp', 'PinkNoise', 'SteppedSine']

/** The map in the shape `params_json` stores it. */
export function toStored(map: Record<ImpedanceStimulusName, StimulusSettings>): SettingsByStimulus {
  return {
    chirp: { ...map.Chirp },
    pinkNoise: { ...map.PinkNoise },
    steppedSine: { ...map.SteppedSine },
  }
}

export function defaultsByStimulus(): Record<ImpedanceStimulusName, StimulusSettings> {
  return {
    Chirp: { ...STIMULUS_DEFAULTS.Chirp },
    PinkNoise: { ...STIMULUS_DEFAULTS.PinkNoise },
    SteppedSine: { ...STIMULUS_DEFAULTS.SteppedSine },
  }
}

/**
 * The map a stored measurement should be read back with: this build's defaults, then whatever the
 * stored map holds, then the **active pair overlaid onto the selected stimulus's slot**.
 *
 * <p>That last step is what levelMode.ts does and for its reason: `averageCount` and
 * `smoothingDenominator` stay the authoritative fields — they are what the run used, what every
 * validation and report reads, and what a measurement saved before this existed carries. The map is
 * an extra, so the two can never disagree about the stimulus that is selected.</p>
 */
export function readSettings(
  stored: SettingsByStimulus | null | undefined,
  selected: ImpedanceStimulusName,
  active: Partial<StimulusSettings>,
): Record<ImpedanceStimulusName, StimulusSettings> {
  const map = defaultsByStimulus()

  for (const name of STIMULI) {
    const slot = stored?.[STORED_KEY[name]]
    if (!slot) continue
    if (typeof slot.averageCount === 'number' && slot.averageCount > 0) {
      map[name].averageCount = Math.round(slot.averageCount)
    }
    if (typeof slot.smoothingDenominator === 'number' && slot.smoothingDenominator >= 0) {
      map[name].smoothingDenominator = Math.round(slot.smoothingDenominator)
    }
  }

  if (typeof active.averageCount === 'number' && active.averageCount > 0) {
    map[selected].averageCount = Math.round(active.averageCount)
  }
  if (typeof active.smoothingDenominator === 'number' && active.smoothingDenominator >= 0) {
    map[selected].smoothingDenominator = Math.round(active.smoothingDenominator)
  }

  return map
}
