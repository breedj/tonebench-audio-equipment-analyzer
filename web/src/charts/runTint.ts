// A subtle colour shift per drawn run, for the charts where hue has room for one.
//
// On the **sweep** chart hue is bound to the quantity -- THD, D2..D6+, Magn, Noise, eight of them --
// so a run cannot have one, and measurements/sweepOverlayStyles.ts says why painting a whole stored
// run in one colour was tried and dropped there. The **impedance** chart plots two quantities, |Z|
// and phase, and tells runs apart by dash alone; four curves of one near-black and four of one
// violet are hard to follow where they cross, which is exactly where a comparison is read.
//
// So the dash stays the primary signal -- it is what the legend draws and what survives printing in
// black and white -- and this adds a *second*, weaker one on top of it. Each run's colour is the
// quantity's own base pulled a little way toward that run's tint, so every |Z| curve is still
// obviously a |Z| curve and every phase curve a phase curve.

/**
 * The hue each run after the first is pulled toward. Run 0 -- the curve you are meant to read -- is
 * never tinted, exactly as it is never dashed.
 *
 * <p>One entry per overlay slot, and they are chosen to stay apart under both themes and under the
 * two bases they are mixed into (a near-black/near-white magnitude and a violet phase): blue, green
 * and amber are roughly equally spaced in hue and none of them is violet.</p>
 */
export const RUN_TINTS = ['#2563eb', '#16a34a', '#d97706'] as const

/** How far a tinted run is pulled toward its hue. Enough to tell apart, not enough to re-label the curve. */
export const RUN_TINT_AMOUNT = 0.42

/** Parses `#rgb` or `#rrggbb`; null for anything else, so a caller falls back to the base. */
function parseHex(hex: string): [number, number, number] | null {
  const value = hex.trim()
  if (!value.startsWith('#')) return null

  const digits = value.slice(1)
  if (digits.length === 3) {
    const [r, g, b] = [...digits].map((d) => parseInt(d + d, 16))
    return [r, g, b].some(Number.isNaN) ? null : [r, g, b]
  }

  if (digits.length !== 6) return null
  const rgb: [number, number, number] = [
    parseInt(digits.slice(0, 2), 16),
    parseInt(digits.slice(2, 4), 16),
    parseInt(digits.slice(4, 6), 16),
  ]
  return rgb.some(Number.isNaN) ? null : rgb
}

function toHex(channel: number): string {
  return Math.round(Math.min(255, Math.max(0, channel))).toString(16).padStart(2, '0')
}

/** `base` mixed `amount` of the way toward `tint`, both as `#rrggbb`. */
export function mixHex(base: string, tint: string, amount: number): string {
  const from = parseHex(base)
  const to = parseHex(tint)
  if (!from || !to) return base

  const t = Math.min(1, Math.max(0, amount))
  const mixed = from.map((channel, i) => channel + ((to[i] - channel) * t))
  return `#${mixed.map(toHex).join('')}`
}

/**
 * The colour the n-th drawn run uses for a curve whose base colour is `base`.
 *
 * <p>Run 0 is the base, untouched. Past the tints on offer it wraps, which matches `runDash` wrapping
 * its patterns: with more runs than either has entries the pair still differs even where one repeats.
 * </p>
 */
export function runTint(base: string, runIndex: number, amount: number = RUN_TINT_AMOUNT): string {
  if (runIndex <= 0) return base
  const tint = RUN_TINTS[(runIndex - 1) % RUN_TINTS.length]
  return mixHex(base, tint, amount)
}
