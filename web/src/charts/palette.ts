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
  /** Device pixels a grid line is drawn at — wider on an export, which is shown scaled down. */
  gridWidth: number
  /** Behind a label drawn over the plot, so the line it sits on does not run through the text. */
  labelBackground: string
}

export function makeChartPalette(dark: boolean, forExport = false): ChartPalette {
  const grid = dark ? '#374151' : '#e5e7eb'
  return {
    dark,
    color: (pair) => (dark ? pair[1] : pair[0]),
    axis: dark ? '#9ca3af' : '#4b5563',
    grid: forExport ? exportGridStroke(grid) : grid,
    gridWidth: forExport ? EXPORT_GRID_WIDTH_SCALE : 1,
    labelBackground: dark ? '#111827' : '#ffffff',
  }
}

/**
 * How much wider a grid line is drawn when a chart is exported rather than displayed.
 *
 * An export is 1600 px wide and a report shows it about 480 pt across, i.e. at roughly 240 DPI, so a
 * PDF viewer at 100 % draws it about 2.5x smaller than it was rendered. A 1 px grid line then lands
 * on well under a whole pixel and is blended down to a fraction of its opacity: measured on the
 * light grid, `#e5e5e5` arrives at something near `#f4f4f4`, which on white is nothing at all. That
 * is why the report grids read as *thin* rather than merely pale, and why widening matters at least
 * as much as darkening — a 1 px line cannot be rescued by colour alone once it is partly covering a
 * pixel. Two device pixels survive the same downscale at about 80 % opacity.
 */
export const EXPORT_GRID_WIDTH_SCALE = 2

/**
 * How far a grid grey is taken toward black for an export, as a multiplier on each channel.
 *
 * A multiply rather than a fixed colour, because a chart with more than one grid tier (the sweep
 * chart's decade, major and minor greys) has to keep them in the same order and roughly the same
 * spacing; replacing them all with one grey would flatten the decade lines into the minor ones.
 * With the width scale above, 0.8 puts the ordinary light grid at about `#c8c8c8` once the viewer
 * has scaled it down — plainly visible, still clearly behind the data.
 */
const EXPORT_GRID_DARKEN = 0.8

/**
 * A grid grey as an export should draw it. Anything that is not a `#rrggbb` literal — an `rgba()`
 * marker tint, say — is handed back untouched rather than guessed at.
 */
export function exportGridStroke(stroke: string): string {
  const match = /^#([0-9a-f]{6})$/i.exec(stroke.trim())
  if (match === null) return stroke

  const value = Number.parseInt(match[1]!, 16)
  const darken = (channel: number) => Math.round(Math.min(255, Math.max(0, channel * EXPORT_GRID_DARKEN)))
  const r = darken((value >> 16) & 0xff)
  const g = darken((value >> 8) & 0xff)
  const b = darken(value & 0xff)
  return `#${((r << 16) | (g << 8) | b).toString(16).padStart(6, '0')}`
}

/**
 * A chart's own light theme, as its export should draw it: same colours, darker and wider grid.
 *
 * Every chart already hands an explicit light theme into its export path (`buildOptions(...,
 * THEME.light, ...)`), so this wraps that one argument rather than threading an "exporting" flag
 * through each chart's build. A theme with more than one grid grey passes each through
 * `exportGridStroke` itself.
 */
export function exportGridTheme<T extends { grid: string; gridWidth: number }>(light: T): T {
  return {
    ...light,
    grid: exportGridStroke(light.grid),
    gridWidth: light.gridWidth * EXPORT_GRID_WIDTH_SCALE,
  }
}
