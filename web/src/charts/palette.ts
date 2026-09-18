/**
 * The colours a uPlot chart is built with, chosen once per build and threaded through explicitly
 * rather than read from `isDark` at draw time.
 *
 * The export (see exportChart.ts) renders a second plot with the light palette while the screen may
 * be dark, and uPlot's plugins draw in a microtask after construction — a module-level override
 * flipped for the build would be read back too late, or leak into the visible plot's next redraw. An
 * argument cannot be either. Every chart keeps its own `[light, dark]` colour pairs for its curves
 * and asks the palette to pick; the axis and grid greys are the same neutrals on every chart.
 */
export interface ChartPalette {
  dark: boolean
  /** Picks from a `[light, dark]` pair. */
  color(pair: readonly string[]): string
  axis: string
  grid: string
  /** Behind a label drawn over the plot, so the line it sits on does not run through the text. */
  labelBackground: string
}

export function makeChartPalette(dark: boolean): ChartPalette {
  return {
    dark,
    color: (pair) => (dark ? pair[1] : pair[0]),
    axis: dark ? '#9ca3af' : '#4b5563',
    grid: dark ? '#374151' : '#e5e7eb',
    labelBackground: dark ? '#111827' : '#ffffff',
  }
}
