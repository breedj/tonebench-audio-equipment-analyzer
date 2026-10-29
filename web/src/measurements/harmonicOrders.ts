/**
 * The harmonic orders the impulse screen's Harmonics view can draw, the colour each is drawn in,
 * and which of them are currently on the chart.
 *
 * <p>In a module for the two reasons this codebase keeps giving. Nothing in a `.vue` is testable
 * under the node environment vitest runs in, and the colour table is read in two places — the
 * chart's own series and the display rail's swatch beside each checkbox — so one table is what
 * stops the rail naming a colour the chart does not draw. `sweep/traceColors.ts` is the same thing
 * one screen along, and exists for the same reason.</p>
 */

/**
 * The orders a sweep's deconvolution residue can be read for: `ImpulseResponseRequest`'s own
 * `MinHarmonicOrder`..`MaxSupportedHarmonicOrder`. A measurement may have extracted fewer of them
 * (its **Max harmonic order** setting), in which case the missing ones simply have no curve — the
 * checkbox is still offered, because a control that comes and goes with the last run is harder to
 * find than one that is always in the same place.
 */
export const HARMONIC_ORDERS = [2, 3, 4, 5] as const

export type HarmonicOrder = (typeof HARMONIC_ORDERS)[number]

/**
 * The hue per order. This is the impulse chart's own table, moved here unchanged.
 *
 * <p>Note it is <em>not</em> the sweep chart's, although a comment in that chart claimed so for a
 * long time: H3 is green and H4 amber here, where the sweep draws D3 orange and D4 green. The two
 * screens therefore disagree about those two orders. Left as it is rather than quietly repainted —
 * it is a decision about both charts, not something to change on the way past.</p>
 */
const ORDER_COLORS: Record<number, { dark: string; light: string }> = {
  2: { dark: '#60a5fa', light: '#1d6fd0' },
  3: { dark: '#4ade80', light: '#15803d' },
  4: { dark: '#fbbf24', light: '#b45309' },
  5: { dark: '#f87171', light: '#c62828' },
}

/** The colour order `order` is drawn in. An order off the end of the table takes H5's, as the chart always did. */
export function harmonicOrderColor(order: number, dark: boolean): string {
  const pair = ORDER_COLORS[order] ?? ORDER_COLORS[5]
  return dark ? pair.dark : pair.light
}

/** Every order shown: what a measurement with nothing stored draws, and the chart's own fallback. */
export function allHarmonicOrders(): number[] {
  return [...HARMONIC_ORDERS]
}

/**
 * The stored `chartHarmonicOrders`, or null where the document does not say — in which case the
 * caller keeps its own default, the way every other field of `impulseDisplayFromParams` is guarded.
 *
 * <p>An <b>empty list is a real answer</b> — every curve switched off — and survives as one. What
 * does not is a list that says something this build cannot read: a non-empty array with no
 * recognised order in it reads as null rather than as "nothing shown", because the alternative is a
 * hand-edited or newer document blanking the chart with no way of telling why.</p>
 */
export function parseHarmonicOrders(value: unknown): number[] | null {
  if (!Array.isArray(value)) return null
  const kept = [...new Set(value.filter((v): v is number => typeof v === 'number' && (HARMONIC_ORDERS as readonly number[]).includes(v)))]
  if (kept.length === 0 && value.length > 0) return null
  return kept.sort((a, b) => a - b)
}

/**
 * `shown` with `order` added or removed — a new array, never the one passed in, because the list is
 * a model the dirty watcher compares by identity (the "replaced, never mutated" rule).
 */
export function harmonicOrdersWith(shown: readonly number[], order: number, visible: boolean): number[] {
  const next = shown.filter((o) => o !== order)
  if (visible) next.push(order)
  return next.sort((a, b) => a - b)
}
