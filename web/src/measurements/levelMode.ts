import type { SweepLevelMode, SweepLevelUnit } from '../api/client'

/**
 * The drive-level controls every settings panel with a **Level mode** shares: which modes there
 * are, which units each one is typed in, and the per-mode memory that keeps the three apart.
 *
 * Six kinds carry a level mode — THD vs frequency, stepped response, frequency response, impulse
 * response, crosstalk and output impedance — and each panel had its own copy of the mode list, the
 * unit list, the `isPowerMode` predicate and the `Watt` special cases. That is what let the same
 * bug exist six times over, so the rule lives here and the panels hold the fields and the markup.
 * It is a plain module rather than a composable because the state belongs to each panel's own
 * `form` (one per measurement type, staged and saved with it), and because nothing in a `.vue` is
 * testable here.
 *
 * **The bug this exists for** (reported 2026-10-17): one staged value served all three modes, so
 * switching Level mode from Generator voltage to DUT output power carried the generator's figure
 * over and the panel read `-20 W`. Power cannot be negative — the run was refused, and the number
 * on screen was a voltage wearing a power's unit. Each mode now keeps its own value *and* its own
 * unit, so switching mode shows what that mode was last configured with and nothing else.
 */

export interface LevelUnitOption {
  value: SweepLevelUnit
  label: string
}

/** The three ways to specify drive level, ported from the previous app's `E_GeneratorType`. */
export const LEVEL_MODES: readonly { value: SweepLevelMode; label: string }[] = [
  { value: 'GeneratorVoltage', label: 'Generator voltage' },
  { value: 'OutputVoltage', label: 'DUT output voltage' },
  { value: 'OutputPower', label: 'DUT output power' },
]

/** The units a voltage level is typed in. dBV first: it is the default and what everything is stored in. */
export const VOLTAGE_LEVEL_UNITS: readonly LevelUnitOption[] = [
  { value: 'Dbv', label: 'dBV' },
  { value: 'Volt', label: 'V' },
  { value: 'MilliVolt', label: 'mV' },
]

/**
 * The units a power level is typed in. Milliwatts are not decoration: a headphone amplifier or a
 * line stage is quoted in mW, and typing `0.001` for it is how a decimal point gets lost. The
 * figure is stored in the unit it was typed in, so the panel reloads showing the same number —
 * the same reason the impulse screen's delay window is held in µs.
 */
export const POWER_LEVEL_UNITS: readonly LevelUnitOption[] = [
  { value: 'Watt', label: 'W' },
  { value: 'MilliWatt', label: 'mW' },
]

/** Both lists, for validating a stored unit against no particular mode. */
const ALL_LEVEL_UNITS: readonly LevelUnitOption[] = [...VOLTAGE_LEVEL_UNITS, ...POWER_LEVEL_UNITS]

/** A level naming power rather than voltage — the one that needs a load to mean anything. */
export function isPowerUnit(unit: SweepLevelUnit): boolean {
  return unit === 'Watt' || unit === 'MilliWatt'
}

/** Whether the mode's level is a power. Mirrors the server's `LevelMode == OutputPower`. */
export function isPowerMode(mode: SweepLevelMode): boolean {
  return mode === 'OutputPower'
}

/** Whether the generator amplitude has to be solved by probing — both output-referred modes. */
export function solvesGeneratorLevel(mode: SweepLevelMode): boolean {
  return mode !== 'GeneratorVoltage'
}

/** The unit dropdown's entries for a mode: watts and milliwatts for the power mode, volts for the other two. */
export function levelUnitsFor(mode: SweepLevelMode): readonly LevelUnitOption[] {
  return isPowerMode(mode) ? POWER_LEVEL_UNITS : VOLTAGE_LEVEL_UNITS
}

/** The unit as it is written in a message or a caption — "mW", not "MilliWatt". */
export function levelUnitLabel(unit: SweepLevelUnit): string {
  return ALL_LEVEL_UNITS.find((u) => u.value === unit)?.label ?? unit
}

/** The level each mode was last configured with. Persisted as `params_json`'s `levelByMode`. */
export interface LevelByMode {
  generatorValue: number
  generatorUnit: SweepLevelUnit
  outputVoltageValue: number
  outputVoltageUnit: SweepLevelUnit
  outputPowerValue: number
  outputPowerUnit: SweepLevelUnit
}

/** A panel's level state: the three remembered levels plus which of them is selected. */
export interface LevelForm extends LevelByMode {
  mode: SweepLevelMode
}

/**
 * Which pair of fields a mode reads and writes. A table rather than three `if`s, because
 * {@link activeLevel}, both setters and {@link applyStoredLevel} are all the same question and a
 * mode added to the enum has to land in all four of them at once.
 */
const SLOTS: Record<SweepLevelMode, { value: keyof LevelByMode; unit: keyof LevelByMode }> = {
  GeneratorVoltage: { value: 'generatorValue', unit: 'generatorUnit' },
  OutputVoltage: { value: 'outputVoltageValue', unit: 'outputVoltageUnit' },
  OutputPower: { value: 'outputPowerValue', unit: 'outputPowerUnit' },
}

/**
 * The defaults a panel starts from. Every panel's own default happens to be one of these — a
 * generator sweep at −20 dBV, an output-referred run at 0 dBV, a load-step at 1 W — so a panel
 * only overrides `mode` and, where it differs, the figure for that mode.
 */
export const DEFAULT_LEVEL_BY_MODE: LevelByMode = {
  generatorValue: -20,
  generatorUnit: 'Dbv',
  outputVoltageValue: 0,
  outputVoltageUnit: 'Dbv',
  outputPowerValue: 1,
  outputPowerUnit: 'Watt',
}

/** A panel's level state, defaults with the panel's own overrides applied. */
export function createLevelForm(overrides: Partial<LevelForm> = {}): LevelForm {
  return { mode: 'GeneratorVoltage', ...DEFAULT_LEVEL_BY_MODE, ...overrides }
}

/** The value and unit a run is sent, and that `levelValue`/`levelUnit` record: the selected mode's pair. */
export function activeLevel(form: LevelForm): { value: number; unit: SweepLevelUnit } {
  const slot = SLOTS[form.mode]
  return { value: form[slot.value] as number, unit: form[slot.unit] as SweepLevelUnit }
}

/** Writes the level box back to the selected mode's own slot, leaving the other two alone. */
export function setLevelValue(form: LevelForm, value: number): void {
  Object.assign(form, { [SLOTS[form.mode].value]: value })
}

/**
 * Writes the unit dropdown back to the selected mode's own slot. A unit the mode has no business
 * in is ignored rather than stored — the dropdown cannot offer one, but a stored file can carry
 * one, and the invariant that each slot holds a unit of its own kind is what makes the whole thing
 * safe to send.
 */
export function setLevelUnit(form: LevelForm, unit: SweepLevelUnit): void {
  if (!levelUnitsFor(form.mode).some((u) => u.value === unit)) return
  Object.assign(form, { [SLOTS[form.mode].unit]: unit })
}

/** The subset of a stored `params_json` this module reads. */
export interface StoredLevel {
  levelMode?: SweepLevelMode | null
  levelValue?: number | null
  levelUnit?: SweepLevelUnit | null
  levelByMode?: Partial<LevelByMode> | null
}

/**
 * Restores a stored measurement's level state into `form`, in place.
 *
 * Two sources, in this order, and the order is the point. `levelByMode` is the per-mode memory, so
 * it is read first and field by field — guarded, because an older `params_json` (or another kind's)
 * parses perfectly well and would otherwise write `undefined` over a default. Then the stored
 * `levelValue`/`levelUnit` — the *active* pair, which is what the run actually used and the only
 * record a type saved before this existed has — is overlaid onto the selected mode's slot. So the
 * two can never disagree about the mode that is selected, and a pre-existing measurement reloads
 * exactly as it did before with the other two modes at their defaults.
 *
 * A unit is only accepted into a slot whose kind it matches, which is what stops a `Watt` landing
 * in a voltage slot: the panel's dropdown would then be showing a unit it does not offer, and the
 * server rejects that pairing outright.
 */
export function applyStoredLevel(form: LevelForm, stored: StoredLevel): void {
  if (stored.levelMode && LEVEL_MODES.some((m) => m.value === stored.levelMode)) {
    form.mode = stored.levelMode
  }

  const by = stored.levelByMode
  if (by) {
    for (const mode of Object.keys(SLOTS) as SweepLevelMode[]) {
      const slot = SLOTS[mode]
      const value = by[slot.value]
      const unit = by[slot.unit]
      if (typeof value === 'number' && Number.isFinite(value)) {
        Object.assign(form, { [slot.value]: value })
      }
      if (typeof unit === 'string' && levelUnitsFor(mode).some((u) => u.value === unit)) {
        Object.assign(form, { [slot.unit]: unit })
      }
    }
  }

  if (typeof stored.levelValue === 'number' && Number.isFinite(stored.levelValue)) {
    setLevelValue(form, stored.levelValue)
  }
  if (stored.levelUnit) setLevelUnit(form, stored.levelUnit)
}

/** The three remembered levels, for `params_json`. The mode and the active pair are written separately. */
export function levelByModeOf(form: LevelForm): LevelByMode {
  return {
    generatorValue: form.generatorValue,
    generatorUnit: form.generatorUnit,
    outputVoltageValue: form.outputVoltageValue,
    outputVoltageUnit: form.outputVoltageUnit,
    outputPowerValue: form.outputPowerValue,
    outputPowerUnit: form.outputPowerUnit,
  }
}

/** A power level in watts, whichever power unit it was typed in. 0 for anything that is not a power. */
export function levelWatts(form: LevelForm): number {
  const { value, unit } = activeLevel(form)
  if (!isPowerUnit(unit) || !Number.isFinite(value) || value <= 0) return 0
  return unit === 'MilliWatt' ? value / 1000 : value
}

/**
 * The voltage a power target actually asks for, shown back to the user: V = sqrt(P·R). Watts are
 * the unit an amplifier is specified in, volts are what the analyzer drives, and the conversion is
 * the one thing a reader cannot do in their head.
 */
export function levelTargetVolt(form: LevelForm, loadOhms: number): number {
  return Math.sqrt(levelWatts(form) * Math.max(loadOhms, 0))
}

/**
 * Why the staged level cannot be run, or null. The shared half only — a panel keeps its own checks
 * for the load (two panels have more than one) and for everything that is not the level.
 *
 * The messages name the unit that is actually selected, because "must be above 0 W" in front of a
 * box reading mW is the kind of mismatch that makes a reader distrust the number rather than the
 * message.
 */
export function levelInvalid(form: LevelForm, loadOhms: number): string | null {
  const { value, unit } = activeLevel(form)
  if (!Number.isFinite(value)) return 'The level must be a number.'
  if (isPowerMode(form.mode)) {
    if (!(value > 0)) return `Output power must be above 0 ${levelUnitLabel(unit)}.`
    if (!(loadOhms > 0)) return 'A target power needs a load above 0 Ω.'
    return null
  }
  if (unit !== 'Dbv' && !(value > 0)) return `A level in ${levelUnitLabel(unit)} must be above 0.`
  return null
}
