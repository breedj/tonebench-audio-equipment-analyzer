// The colour of every trace on the sweep chart, in one place, so the display rail's swatches and
// the chart's own lines cannot drift apart. Added with the 2026-10-12 redesign, which puts a swatch
// beside each Graph data checkbox — before that the colours lived only inside ThdSweepChart's series
// table and the rail said nothing about which line it was switching on.
//
// A pair per trace, `[light, dark]`, chosen the same way every other theme pair here is: the dark
// member is the light one lifted so it reads against a near-black plot. The right channel is drawn
// as its own hue blended toward the background (see sweepLineStyle), so one entry serves both
// channels and the swatch names the trace rather than a channel.

/** Every trace the sweep chart can draw, by the key `SweepTraceKey` uses. */
export type SweepTraceColorKey = 'magnitude' | 'thd' | 'thdn' | 'd2' | 'd3' | 'd4' | 'd5' | 'd6plus' | 'noise' | 'snr'

export const SWEEP_TRACE_COLORS: Record<SweepTraceColorKey, readonly [string, string]> = {
  magnitude: ['#be185d', '#f472b6'],
  thd: ['#111827', '#e5e7eb'],
  d2: ['#1d6fd0', '#60a5fa'],
  d3: ['#e07b16', '#fbbf24'],
  d4: ['#1a7a3c', '#4ade80'],
  d5: ['#c62828', '#f87171'],
  d6plus: ['#6b3fa0', '#c084fc'],
  // Not in the previous app's legend, so it gets a hue that stays clear of the six above. Teal
  // rather than the neutral grey it used to be: blending near-black THD toward the background for
  // the right channel lands on a mid grey that a neutral Noise trace is very hard to tell from, so
  // grey belongs to the THD family alone.
  noise: ['#0d7d84', '#2dd4bf'],
  // The two added 2026-10-14. Both have to stay clear of the eight above, which already spend
  // rose, neutral, blue, orange, green, red, purple and teal -- so fuchsia and brown, the two
  // remaining hues that survive being blended toward the background for the right channel.
  // THD+N is THD plus the noise, so it is drawn at THD's weight rather than a harmonic's.
  thdn: ['#a21caf', '#e879f9'],
  snr: ['#78350f', '#d8a05a'],
}

/** The one a given theme draws. `dark` is the app's `.dark` class being on, not the OS preference. */
export function sweepTraceColor(key: SweepTraceColorKey, dark: boolean): string {
  return SWEEP_TRACE_COLORS[key][dark ? 1 : 0]
}
