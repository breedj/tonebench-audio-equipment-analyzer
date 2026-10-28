import type { ImpedanceStimulusName, OutputImpedanceSettingsByStimulus } from '../api/client'
import { MAX_AVERAGE_COUNT } from '../measurements/averaging'

// Averaging and smoothing, kept per stimulus on the output-impedance measurement.
//
// The same shape as impedance/stimulusSettings.ts and for the same reason: a **stepped sine** pays
// its average count at *every point* of a grid, so ten averages over fifty points is ten times a
// sweep that already takes minutes, while a **chirp** and **pink noise** fold whole records into one
// estimate and can afford ten of them in seconds. One shared pair carries a stepped sweep's figure
// onto a broadband run and back again, which is the fault measurements/levelMode.ts documents in its
// own words.
//
// **Two fields rather than that module's four, and the drive level is deliberately not among them.**
// An impedance measurement's level is a bare dBV drive into a fixture, so it belongs to the
// stimulus. An output-impedance target is *output-referred* — "1 W into 8 Ω" is a property of the
// amplifier being driven, not of what it is driven with — and this kind already keeps a value per
// level *mode* in `levelByMode`. A slot per stimulus on top of that would be a matrix, and the two
// memories would disagree about the figure the panel is showing.
//
// The one place that costs anything is **Generator voltage** mode, where the level really is a bare
// drive and a broadband run wants perhaps 30 dB more than a stepped sine for the same reading. That
// number carries over when the dropdown is turned, and the panel's own estimate line is where it
// shows. Worth revisiting only if it bites in use.

/** How many records or per-point averages one output-impedance measurement may fold in. */
export const MAX_AVERAGES = MAX_AVERAGE_COUNT

export interface StimulusSettings {
  averageCount: number
  /**
   * The fractional-octave band the broadband curve is smoothed over, as its denominator. Inert on a
   * stepped run — every sample there is a measurement, on a grid already coarser than the narrowest
   * band on offer — and kept in that slot anyway so the three rows of this table have one shape.
   */
  smoothingDenominator: number
}

/**
 * What each stimulus starts at.
 *
 * **1/3 octave and ten records for the broadband pair, and both figures are about signal-to-noise
 * rather than looks.** Zout is a *difference* of two nearly equal gains: with 8 Ω then 4 Ω the ratio
 * is q ≈ 1 − Zout/8, i.e. about a millidecibel per milliohm, and a broadband stimulus puts a tiny
 * fraction of its energy in any one bin. The record count and the smoothing band are the two levers
 * that buy that back — an RMS over the ~600 bins a 1/3-octave band spans at 1 kHz is worth some
 * 28 dB, which is the difference between a readable milliohm figure and noise.
 *
 * The stepped sine keeps **one** average per point, which is what this measurement has always
 * defaulted to, and a smoothing figure nothing reads.
 */
export const STIMULUS_DEFAULTS: Record<ImpedanceStimulusName, StimulusSettings> = {
  Chirp: { averageCount: 10, smoothingDenominator: 3 },
  PinkNoise: { averageCount: 10, smoothingDenominator: 3 },
  SteppedSine: { averageCount: 1, smoothingDenominator: 48 },
}

export type SettingsByStimulus = OutputImpedanceSettingsByStimulus

/**
 * The camelCase key each stimulus is stored under. `params_json` is written by the server's own
 * serializer, so a map written PascalCase reads back as no map at all — which costs the two
 * unselected stimuli their values on every load.
 */
const STORED_KEY: Record<ImpedanceStimulusName, keyof SettingsByStimulus> = {
  Chirp: 'chirp',
  PinkNoise: 'pinkNoise',
  SteppedSine: 'steppedSine',
}

export const STIMULI: ImpedanceStimulusName[] = ['Chirp', 'PinkNoise', 'SteppedSine']

/** Which stimuli are one broadband record per pass rather than a tone grid. */
export const BROADBAND_STIMULI: ImpedanceStimulusName[] = ['Chirp', 'PinkNoise']

export function isBroadband(stimulus: ImpedanceStimulusName): boolean {
  return BROADBAND_STIMULI.includes(stimulus)
}

export function defaultsByStimulus(): Record<ImpedanceStimulusName, StimulusSettings> {
  return {
    Chirp: { ...STIMULUS_DEFAULTS.Chirp },
    PinkNoise: { ...STIMULUS_DEFAULTS.PinkNoise },
    SteppedSine: { ...STIMULUS_DEFAULTS.SteppedSine },
  }
}

export function toStored(map: Record<ImpedanceStimulusName, StimulusSettings>): SettingsByStimulus {
  return {
    chirp: { ...map.Chirp },
    pinkNoise: { ...map.PinkNoise },
    steppedSine: { ...map.SteppedSine },
  }
}

/**
 * The save-side counterpart of {@link readSettings}. The form only parks a slot when the dropdown is
 * *turned*, so a figure typed and saved without touching it would store a slot that contradicts the
 * document's own `averageCount` — harmless on the way back in, since readSettings overlays the
 * active pair again, and worth fixing anyway: a stored document that contradicts itself is one a
 * report, an import or a person reading the JSON can be wrong about.
 */
export function toStoredWithActive(
  map: Record<ImpedanceStimulusName, StimulusSettings>,
  selected: ImpedanceStimulusName,
  active: StimulusSettings,
): SettingsByStimulus {
  return toStored({ ...map, [selected]: { ...active } })
}

function readCount(value: number | null | undefined, fallback: number): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return fallback
  return Math.min(MAX_AVERAGES, Math.max(1, Math.round(value)))
}

function readDenominator(value: number | null | undefined, fallback: number): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return fallback
  return Math.max(1, Math.round(value))
}

/**
 * Defaults, then whatever was stored, then the active pair overlaid onto the selected stimulus's
 * slot — so the authoritative fields and the map can never disagree about the stimulus that is
 * selected, and a type stored before this existed loads with the other two at this build's figures.
 */
export function readSettings(
  stored: SettingsByStimulus | null | undefined,
  selected: ImpedanceStimulusName,
  active: StimulusSettings,
): Record<ImpedanceStimulusName, StimulusSettings> {
  const map = defaultsByStimulus()

  for (const stimulus of STIMULI) {
    const slot = stored?.[STORED_KEY[stimulus]]
    if (!slot) continue
    map[stimulus] = {
      averageCount: readCount(slot.averageCount, map[stimulus].averageCount),
      smoothingDenominator: readDenominator(slot.smoothingDenominator, map[stimulus].smoothingDenominator),
    }
  }

  map[selected] = {
    averageCount: readCount(active.averageCount, map[selected].averageCount),
    smoothingDenominator: readDenominator(active.smoothingDenominator, map[selected].smoothingDenominator),
  }

  return map
}
