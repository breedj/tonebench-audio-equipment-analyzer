/**
 * The unit the generator panel's level fields are typed in.
 *
 * The wire — `GeneratorTone.ampDbv`, `WhiteNoiseSettings.levelDbv`, everything the device is told —
 * is dBV and stays dBV; this is a display choice and nothing else, converted at the input's edge the
 * way the sweep panels' own `levelUnit` is (there the server does the conversion, because the sweep
 * request carries the unit; the generator request does not, and adding one would give the device two
 * ways to be told the same amplitude).
 *
 * Deliberately not `SweepLevelUnit`, which also has `Watt`: a generator level has no load behind it.
 */
export type GeneratorLevelUnit = 'Dbv' | 'Volt' | 'MilliVolt'

/** The dropdown's entries, in menu order. dBV first — it is the default and the unit everything is stored in. */
export const GENERATOR_LEVEL_UNITS: { value: GeneratorLevelUnit; label: string }[] = [
  { value: 'Dbv', label: 'dBV' },
  { value: 'Volt', label: 'V' },
  { value: 'MilliVolt', label: 'mV' },
]

/** The unit's label on its own, for a summary line or a read-only caption. */
export function generatorLevelUnitLabel(unit: GeneratorLevelUnit): string {
  return GENERATOR_LEVEL_UNITS.find((u) => u.value === unit)?.label ?? 'dBV'
}

/**
 * A stored unit name back as a unit — anything unrecognised, including the null a type saved before
 * this existed carries, is dBV.
 */
export function generatorLevelUnitOf(name: string | null | undefined): GeneratorLevelUnit {
  return GENERATOR_LEVEL_UNITS.some((u) => u.value === name) ? (name as GeneratorLevelUnit) : 'Dbv'
}

/**
 * What the input box shows for a stored dBV level.
 *
 * Volts are rounded to four significant figures, which is what makes the box stable: typing 501.2 mV
 * stores −5.9998 dBV, and displaying that again has to come back to 501.2 rather than 501.19999.
 * dBV is handed back untouched — it *is* the stored number, so rounding it here would quietly edit
 * what the user typed.
 */
export function generatorLevelFromDbv(ampDbv: number, unit: GeneratorLevelUnit): number {
  if (!Number.isFinite(ampDbv)) return 0
  if (unit === 'Dbv') return ampDbv

  const volt = 10 ** (ampDbv / 20)
  return Number((unit === 'MilliVolt' ? volt * 1000 : volt).toPrecision(4))
}

/**
 * The dBV a typed level means. Null for anything that cannot be one — a blank box, or a
 * non-positive voltage, which has no dBV at all (0 V is −∞) and must leave the stored level alone
 * rather than writing an infinity into the generator config.
 *
 * A converted level is rounded to four decimals, because it is the number the dBV box will show:
 * 250 mV is −12.041199826559248 dBV exactly, and switching the unit back should not read like that.
 * Four decimals is 1.2e-5 in relative terms, finer than the four significant figures
 * {@link generatorLevelFromDbv} displays, so the volt figure still round-trips to the digit. A level
 * typed *in* dBV is handed straight through — nobody types five decimals of it, and rounding what
 * the user wrote is exactly what the other direction refuses to do.
 */
export function generatorLevelToDbv(value: number, unit: GeneratorLevelUnit): number | null {
  if (!Number.isFinite(value)) return null
  if (unit === 'Dbv') return value
  if (!(value > 0)) return null

  const volt = unit === 'MilliVolt' ? value / 1000 : value
  return Number((20 * Math.log10(volt)).toFixed(4))
}
