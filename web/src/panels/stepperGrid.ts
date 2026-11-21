/**
 * The 1-2-5 grid behind a stepper tile — Joost's design of 2026-11-20
 * (`docs/design/B + E · Trigger-style stepper with 1-2-5 grid`).
 *
 * <p><b>Why a grid rather than a dropdown.</b> A per-division ladder is two numbers at once: a
 * decade and a mantissa, and that is how a bench instrument's knob is read — "one step coarser",
 * "the same but times five". A `select` flattens both into one column of twenty-one entries, so
 * picking 2 ms from 500 µs means counting rows rather than crossing a grid. Laid out as decades
 * down and ×1 ×2 ×5 across, every rung is one glance away and the *shape* of the ladder is on
 * screen: the reader can see that there is nothing below 2 µs and nothing above 100 ms.</p>
 *
 * <p>Everything here is derived from the ladder and the control's own formatter, so the grid can
 * never name a value differently from the tile above it — which is the one way a picker like this
 * goes quietly wrong.</p>
 */
import { splitUnit } from '../response/format'
import type { MeasurementKindTone } from '../measurements/kinds'

/** The three columns of a 1-2-5 ladder, as the header writes them. */
export const STEPPER_COLUMNS: readonly string[] = Object.freeze(['×1', '×2', '×5'])

/** Their mantissas, in the same order. */
export const STEPPER_MANTISSAS: readonly number[] = Object.freeze([1, 2, 5])

/**
 * The hues the decade dots step back through, smallest unit first.
 *
 * <p>The group the control's own value is written in takes the <em>control's</em> tone, and the
 * groups below it come from here — so the volts grid reads green (µV), blue (mV), violet (V) and
 * the time grid green (µs), orange (ms), which is what the design draws. The point of the dot is
 * to find where the <em>unit</em> changes, since within a unit the numbers simply run 1 … 500 and
 * a reader skimming for "2 ms" would otherwise have to check three rows that all offer a 2.</p>
 */
const PRECEDING_TONES: readonly MeasurementKindTone[] = Object.freeze([
  'green',
  'blue',
  'teal',
  'cyan',
  'indigo',
])

export interface StepperCell {
  /** The ladder value, or null for a rung the ladder does not reach. */
  value: number | null
  /** What the cell prints: the number in its row's own unit, or an em dash. */
  text: string
}

export interface StepperRow {
  /** The decade as the control writes it — `1 µs`, `100 µV`, `0.1 V`. */
  label: string
  /** The dot's hue: where this row's unit sits against the one the control is written in. */
  tone: MeasurementKindTone
  cells: StepperCell[]
}

export interface StepperGrid {
  columns: readonly string[]
  rows: StepperRow[]
  /** The whole ladder end to end, for the popover's header — `2 µs – 100 ms`. */
  range: string
}

/**
 * The power of ten a 1-2-5 rung sits on.
 *
 * <p>The nudge is not decoration: `Math.log10(0.001)` is −2.9999999999999996 on some engines, and
 * a bare floor would put 1 ms on the 100 µs row with a mantissa of 10 — a grid that is right for
 * most of its rungs and silently wrong for the round ones.</p>
 */
export function decadeExponent(value: number): number {
  return Math.floor(Math.log10(value) + 1e-9)
}

/** A number written the way a cell wants it: `0.1`, `2`, `500` — never `0.1000000001` or `2.000`. */
export function tidyNumber(value: number): string {
  return Number(value.toPrecision(12)).toString()
}

/** What the control's formatter says, split into its number and its unit. */
function parts(value: number, format: (value: number) => string): { n: number; unit: string } {
  const { value: text, unit } = splitUnit(format(value))
  return { n: Number(text), unit }
}

/**
 * One value as the grid writes it: the formatter's own unit, with the number tidied.
 *
 * <p>Tidied because a per-division formatter pads for a readout — `1.000 ms` is right above a
 * trace and wrong in a cell three characters wide — while the <em>unit</em> must stay the
 * formatter's, or the grid and the tile would name one rung two ways.</p>
 */
export function tidyFormat(value: number, format: (value: number) => string): string {
  const { n, unit } = parts(value, format)
  if (!Number.isFinite(n) || unit === '') return format(value)
  return `${tidyNumber(n)} ${unit}`
}

/** How much one of the row's own units is worth — 1e-6 for a µs row, 1 for a V row. */
function unitScale(decade: number, format: (value: number) => string): number {
  const { n } = parts(decade, format)
  return Number.isFinite(n) && n > 0 ? decade / n : decade
}

/**
 * The grid for one ladder.
 *
 * <p>Rows ascend, smallest decade at the top, which is how both designs draw it and how a ladder
 * is read: coarser is further down, the way the values themselves grow.</p>
 */
export function buildStepperGrid(
  ladder: readonly number[],
  format: (value: number) => string,
  tone: MeasurementKindTone,
): StepperGrid {
  const usable = ladder.filter((v) => Number.isFinite(v) && v > 0).slice().sort((a, b) => a - b)
  if (usable.length === 0) return { columns: STEPPER_COLUMNS, rows: [], range: '' }

  const exponents: number[] = []
  for (const value of usable) {
    const exp = decadeExponent(value)
    if (!exponents.includes(exp)) exponents.push(exp)
  }
  exponents.sort((a, b) => a - b)

  // One hue per unit, assigned by where the unit sits in the ladder rather than by its name: the
  // last group is the control's own, and the ones below it step back through the cooler list.
  const units = exponents.map((exp) => parts(10 ** exp, format).unit)
  const distinct: string[] = []
  for (const unit of units) if (!distinct.includes(unit)) distinct.push(unit)
  const toneOfUnit = new Map<string, MeasurementKindTone>()
  distinct.forEach((unit, index) => {
    if (index === distinct.length - 1) toneOfUnit.set(unit, tone)
    else toneOfUnit.set(unit, PRECEDING_TONES[Math.min(index, PRECEDING_TONES.length - 1)])
  })

  const rows = exponents.map((exp) => {
    const decade = 10 ** exp
    const scale = unitScale(decade, format)
    const { unit } = parts(decade, format)
    return {
      label: tidyFormat(decade, format),
      tone: toneOfUnit.get(unit) ?? tone,
      cells: STEPPER_MANTISSAS.map((mantissa) => {
        const wanted = decade * mantissa
        // Matched by ratio rather than by equality: a ladder writes 5e-5 and 10 ** -5 * 5 is
        // 5.000000000000001 of it, which an === would call a rung the ladder does not reach.
        const found = usable.find((v) => Math.abs(v / wanted - 1) < 1e-9)
        return found === undefined
          ? { value: null, text: '—' }
          : { value: found, text: tidyNumber(found / scale) }
      }),
    }
  })

  return {
    columns: STEPPER_COLUMNS,
    rows,
    range: `${tidyFormat(usable[0], format)} – ${tidyFormat(usable[usable.length - 1], format)}`,
  }
}

/**
 * One rung up or down from wherever the control stands.
 *
 * <p>Expressed as "the next rung past the current value" rather than as an index step, because the
 * current value need not be <em>on</em> the ladder: a measurement stored at an odd record length
 * reopens reading what it captured (the `withCurrentValue` rule), and from there a press has to
 * mean the nearest rung in that direction rather than nothing at all.</p>
 *
 * <p>At either end it answers the value it was given, so a press is a no-op and never a wrap.</p>
 */
export function stepLadder(ladder: readonly number[], value: number, direction: 1 | -1): number {
  const usable = ladder.filter((v) => Number.isFinite(v) && v > 0).slice().sort((a, b) => a - b)
  if (usable.length === 0 || !Number.isFinite(value) || value <= 0) return value
  const tolerance = 1e-9
  if (direction > 0) {
    const next = usable.find((v) => v / value - 1 > tolerance)
    return next ?? value
  }
  const below = usable.filter((v) => 1 - v / value > tolerance)
  return below.length > 0 ? below[below.length - 1] : value
}

/** The two rungs either side, as the tile's hint prints them. Null at an end of the ladder. */
export function ladderNeighbours(
  ladder: readonly number[],
  value: number,
  format: (value: number) => string,
): { down: string | null; up: string | null } {
  const down = stepLadder(ladder, value, -1)
  const up = stepLadder(ladder, value, 1)
  return {
    down: down === value ? null : tidyFormat(down, format),
    up: up === value ? null : tidyFormat(up, format),
  }
}
