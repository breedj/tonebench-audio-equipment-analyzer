import type { ImpedanceSettingsByStimulus, ImpedanceStimulusName, SweepLevelUnit } from '../api/client'
import { MAX_AVERAGE_COUNT } from '../measurements/averaging'
import { isPowerUnit } from '../measurements/levelMode'

// Averaging, smoothing and the drive level, kept per stimulus.
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
// active values staying authoritative so nothing downstream has to learn about the map.
//
// **The drive level is in the slot for the same reason and a measured one** (Joost, 2026-10-21).
// A broadband stimulus and a stepped sine do not want the same level and are not off by a little:
// measured on a QA403 against a ~1 ohm fixture on a 100 ohm reference, the 1st-percentile in-band
// coherence of a chirp went 0.018 at -30 dBV, 0.555 at -10, 0.935 at 0 and 0.979 at 6, with pink
// noise within a few hundredths of it at every level -- while a stepped sine read **0.948 at
// -30 dBV**, i.e. about 30 dB less drive for the same reading, because it puts all of its energy on
// one bin at a time. So the broadband pair sit at 6 dBV, which is also as hard as pink noise may be
// driven (its 12.5 dB crest factor clips the DAC by 12 dBV, where a chirp's 3 dB crest is clean to
// the 18 dBV maximum), and the stepped sine at -20 dBV, gentle on a driver and already clean.
//
// The **unit** travels with the value, which is not decoration: the three stimuli share one unit
// list, so a slot holding a bare number would read 6 as 6 V on a bench that had typed volts for the
// chirp, and -20 as -20 V, which this panel refuses outright. That is levelMode.ts's own bug
// ("a voltage wearing a power's unit") one family along.

/**
 * How many records one impedance measurement may fold in. The shared ceiling again since
 * 2026-10-21 — it was this screen's own 20 against every other kind's 10 for a day — and re-exported
 * under the name this panel already used rather than changed at its call sites.
 */
export const MAX_AVERAGES = MAX_AVERAGE_COUNT

export interface StimulusSettings {
  averageCount: number
  smoothingDenominator: number
  levelValue: number
  levelUnit: SweepLevelUnit
}

/**
 * What each stimulus starts at.
 *
 * <p>Chirp and pink noise at <b>10 averages, 1/12 octave and 6 dBV</b> (Joost, 2026-10-20 and
 * 2026-10-21): both were reported as too noisy to read on a sub-ohm fixture at the old 2 and 1/48,
 * both can afford the records, and both need the drive — see the measurements at the top of this
 * file. The stepped sine keeps 2 and 1/48 and sits at <b>-20 dBV</b>: it measures one tone at a
 * time, so its points are already clean at a level the broadband stimuli cannot read at, its
 * averages are the expensive ones, and nothing is smoothed on that path at all (the grid is coarser
 * than the narrowest band on offer).</p>
 */
export const STIMULUS_DEFAULTS: Record<ImpedanceStimulusName, StimulusSettings> = {
  Chirp: { averageCount: 10, smoothingDenominator: 12, levelValue: 6, levelUnit: 'Dbv' },
  PinkNoise: { averageCount: 10, smoothingDenominator: 12, levelValue: 6, levelUnit: 'Dbv' },
  SteppedSine: { averageCount: 2, smoothingDenominator: 48, levelValue: -20, levelUnit: 'Dbv' },
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

/**
 * The map as `params_json` should store it: every slot, with the **active values overlaid onto the
 * selected stimulus's slot**.
 *
 * <p>`toStored` alone writes the map as the form holds it, and the form only writes a slot when the
 * stimulus dropdown is *turned* — so typing a level and saving without touching the dropdown stored
 * a document whose `byStimulus` slot for the selected stimulus disagreed with its own authoritative
 * `levelValue`. That is harmless on the way back in, since `readSettings` overlays the pair again,
 * but a stored document that contradicts itself is one a report, an import or a person reading the
 * JSON can be wrong about. This is the save-side counterpart of `readSettings`, and the two agree
 * about which slot the active values belong to.</p>
 */
export function toStoredWithActive(
  map: Record<ImpedanceStimulusName, StimulusSettings>,
  selected: ImpedanceStimulusName,
  active: Partial<StimulusSettings>,
): SettingsByStimulus {
  return toStored(readSettings(toStored(map), selected, active))
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
 * <p>That last step is what levelMode.ts does and for its reason: `averageCount`,
 * `smoothingDenominator`, `levelValue` and `levelUnit` stay the authoritative fields — they are what
 * the run used, what every validation and report reads, and what a measurement saved before this
 * existed carries. The map is an extra, so the two can never disagree about the stimulus that is
 * selected.</p>
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
    if (typeof slot.levelValue === 'number' && Number.isFinite(slot.levelValue)) {
      map[name].levelValue = slot.levelValue
    }
    // A power unit is never read back, exactly as the panel's own restore refuses one: a one-port has
    // no output power to aim at, so there is no level mode here and the server rejects watts either
    // way. Dropping the unit and keeping the value would be worse than dropping both, so the slot
    // falls back to this build's dBV default.
    if (slot.levelUnit && !isPowerUnit(slot.levelUnit)) {
      map[name].levelUnit = slot.levelUnit
    }
  }

  if (typeof active.averageCount === 'number' && active.averageCount > 0) {
    map[selected].averageCount = Math.round(active.averageCount)
  }
  if (typeof active.smoothingDenominator === 'number' && active.smoothingDenominator >= 0) {
    map[selected].smoothingDenominator = Math.round(active.smoothingDenominator)
  }
  if (typeof active.levelValue === 'number' && Number.isFinite(active.levelValue)) {
    map[selected].levelValue = active.levelValue
  }
  if (active.levelUnit && !isPowerUnit(active.levelUnit)) {
    map[selected].levelUnit = active.levelUnit
  }

  return map
}
