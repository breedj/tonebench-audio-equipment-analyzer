/**
 * The status bar's front-end gain boxes, in dB or as a plain ratio.
 *
 * A divider is the common case and it is naturally read as a ratio — a 10:1 divider is `0.1`, not
 * −20 dB — so the bar offers both and converts here. The **stored value is always dB**: that is what
 * `ExternalGainConfig` holds, what `params_json` records on every measurement, and what the server
 * applies at the pipeline's door. The unit is a display preference and nothing downstream sees it,
 * so a project measured with a divider reads the same whichever way the bench happened to spell it.
 *
 * Extracted from the status bar rather than written in the template because nothing in a `.vue` is
 * testable under the node environment vitest runs in, and because the ratio direction has two edge
 * cases that are easy to get backwards.
 */
export type GainUnit = 'Db' | 'Times'

/** The server's bound, mirroring `ExternalGainConfig.MaxAbsDb`. */
export const MAX_ABS_DB = 60

/** What the box shows for a stored dB value. */
export function toDisplay(db: number, unit: GainUnit): number {
  if (!Number.isFinite(db)) return unit === 'Times' ? 1 : 0
  return unit === 'Times' ? 10 ** (db / 20) : db
}

/**
 * The dB value a typed box means, clamped to the server's bound.
 *
 * A ratio of zero or less has no dB equivalent — `log10(0)` is −∞ and a negative ratio is an
 * inversion, which is a phase statement rather than a gain. Both are read as "no gain declared"
 * rather than being allowed through as ±Infinity, which would reach the server as an invalid
 * document and come back as a 400 the user cannot act on.
 */
export function fromDisplay(value: number, unit: GainUnit): number {
  if (!Number.isFinite(value)) return 0
  if (unit === 'Db') return clamp(value)
  if (value <= 0) return 0
  return clamp(20 * Math.log10(value))
}

/** How many decimals the box shows: dB reads naturally to one, a ratio needs more at the small end. */
export function displayPrecision(unit: GainUnit): number {
  return unit === 'Times' ? 4 : 2
}

/**
 * The chip on the bar, e.g. `−20 dB` or `×0.1`.
 *
 * Signed, so a divider and an amplifier are told apart at a glance — but **zero carries no sign**.
 * `+0 dB` reads as a quantity someone chose, where the badge is grey at 0 precisely to say the
 * opposite: nothing is declared and every level is as measured.
 */
export function formatGain(db: number, unit: GainUnit): string {
  const shown = toDisplay(db, unit)
  return unit === 'Times'
    ? `×${trim(shown, displayPrecision(unit))}`
    : `${shown > 0 ? '+' : ''}${trim(shown, displayPrecision(unit))} dB`
}

/**
 * One entry in the probe row: what the hardware is labelled, and the gain that means.
 *
 * <p><b>Labelled by its division ratio, the way the hardware is</b> — a 10:1 divider says 10:1 on
 * the body, and that is −20 dB of gain. The inversion is the whole hazard here: a divider typed as
 * +20 dB makes every level reported twenty decibels high, and nothing on screen contradicts it
 * because the app believes whatever the bench declares. So the chip carries the hardware's own
 * label and the tooltip carries the dB.</p>
 */
export interface ProbePreset {
  /** As the probe or divider is marked. */
  label: string
  /** The gain that ratio is, in dB. Negative for anything that divides. */
  db: number
}

/**
 * The presets the external-gain panel offers, commonest first.
 *
 * <p>Dividers only, and deliberately: an amplifier ahead of the input is a figure somebody measured
 * rather than a ratio printed on a body, so it is typed. 1:1 is here because it is the way <em>back</em>
 * — a bench that has moved its probes needs one press to say "nothing between" — and because a
 * stored gain nobody remembers setting is this setting's one real hazard.</p>
 */
export const PROBE_PRESETS: readonly ProbePreset[] = Object.freeze([
  { label: '1:1', db: 0 },
  { label: '10:1', db: -20 },
  { label: '100:1', db: -40 },
])

/**
 * Which preset a stored pair of gains *is*, or null where it is neither — a figure somebody typed,
 * or two inputs that do not match.
 *
 * <p>Both channels, because a preset writes both: the row says "both inputs" and would otherwise
 * light up for a bench with a divider on one side only, which is exactly the case where the label
 * would be a lie. Compared with a tolerance, since the stored value is a double and a user may have
 * typed −20.0 by hand.</p>
 */
export function matchingProbePreset(leftDb: number, rightDb: number): ProbePreset | null {
  if (!Number.isFinite(leftDb) || !Number.isFinite(rightDb)) return null
  if (Math.abs(leftDb - rightDb) > 0.001) return null
  return PROBE_PRESETS.find((preset) => Math.abs(preset.db - leftDb) <= 0.001) ?? null
}

/** The tooltip a chip carries: what it will write, in both the units the panel offers. */
export function probePresetHint(preset: ProbePreset): string {
  if (preset.db === 0) {
    return 'Nothing between the DUT and either input — levels are as measured. The way back after a probe set has come off.'
  }
  return `A ${preset.label} divider on both inputs: ${formatGain(preset.db, 'Db')}, or ${formatGain(preset.db, 'Times')}. Every level shown is then the DUT's.`
}

function clamp(db: number): number {
  return Math.max(-MAX_ABS_DB, Math.min(MAX_ABS_DB, db))
}

/** Fixed decimals without the trailing zeros, so 0.1 does not read as 0.1000. */
function trim(value: number, decimals: number): string {
  return String(Number(value.toFixed(decimals)))
}
