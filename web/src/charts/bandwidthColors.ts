/**
 * The two hues the ±1 dB and −3 dB bandwidth markers are drawn in — the dashed verticals and the
 * double-headed arrow on the Bode chart, and (since the 2026-10-12 redesign) the dashed swatch
 * beside each one's checkbox in the display rail and each one's row in the Measurements panel.
 *
 * In its own module for the reason sweep/traceColors.ts is: three places now draw these, and a
 * swatch that does not match the line it switches on is worse than no swatch at all.
 *
 * `[light, dark]`, like every other theme pair here.
 */
export const BANDWIDTH_COLORS = {
  oneDb: ['#d97706', '#fbbf24'],
  threeDb: ['#2563eb', '#60a5fa'],
} as const

export type BandwidthMarker = keyof typeof BANDWIDTH_COLORS

export function bandwidthColor(marker: BandwidthMarker, dark: boolean): string {
  return BANDWIDTH_COLORS[marker][dark ? 1 : 0]
}
