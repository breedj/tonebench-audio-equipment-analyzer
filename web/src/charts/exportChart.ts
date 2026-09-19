/**
 * Chart export at a fixed size.
 *
 * Every chart that can be exported renders itself a second time, offscreen, at exactly
 * `CHART_EXPORT_WIDTH_PX` × `CHART_EXPORT_HEIGHT_PX` and with the light palette, whatever the window
 * or the theme is doing — so a screenshot is always the same size and a report always prints. The
 * helpers here are what those charts share: the size, a header line (title on the left, legend
 * swatches on the right) drawn above the plot, and the compositing onto one PNG.
 *
 * uPlot 1.6 sizes its canvas by the *global* devicePixelRatio and offers no per-instance override,
 * so the plot canvas may be larger than the requested size; `composeChartPng` draws it scaled onto a
 * canvas of the requested size, which is a no-op at DPR 1 and a clean downscale at DPR 2.
 */

import type uPlot from 'uplot'

export const CHART_EXPORT_WIDTH_PX = 1600
export const CHART_EXPORT_HEIGHT_PX = 900

/** The title/legend line above the plot, in export pixels. */
export const CHART_EXPORT_HEADER_PX = 40

/** The light palette's page colours, the same neutrals the app's light theme uses. */
export const EXPORT_BACKGROUND = '#ffffff'
export const EXPORT_TITLE_COLOR = '#171717'
export const EXPORT_LEGEND_COLOR = '#404040'

export interface ExportLegendEntry {
  label: string
  color: string
  /** A dash pattern, for a curve that is drawn dashed on the plot. */
  dash?: number[]
}

export interface ChartExportOptions {
  width: number
  height: number
  /**
   * Copy the visible plot's zoom onto the export — what the Screenshot button wants. The report's
   * headless render has no visible plot and leaves it off, so it gets the display panel's window.
   */
  captureZoom?: boolean
  /**
   * A name for the chart's own (primary) curves in the legend, for a report chart that draws several
   * measurements at once: the primary is one of them and the legend has to say which. Unset on
   * screen, where the primary is simply the live measurement.
   */
  primaryLabel?: string
  /**
   * Black and white instead of colour. Not read by the charts' own `exportPng` — the two callers
   * that hand a PNG on (renderChartPng for reports, the Screenshot dialog) apply
   * `monochromeChartPng` to the finished picture — but carried here so one options object describes
   * the whole export.
   */
  monochrome?: boolean
}

export interface ComposeChartOptions {
  width: number
  height: number
  /** The plot, already drawn; any size — it is scaled onto the area below the header. */
  plotCanvas: HTMLCanvasElement
  headerHeight: number
  title?: string
  legend?: ExportLegendEntry[]
}

const LEGEND_FONT = '13px ui-sans-serif, system-ui, sans-serif'
const TITLE_FONT = '600 15px ui-sans-serif, system-ui, sans-serif'
const SWATCH_WIDTH = 22
const SWATCH_GAP = 6
const ENTRY_GAP = 18

export interface LegendPlacement {
  x: number
  entry: ExportLegendEntry
}

/**
 * Lays the legend out right-aligned across `width`, given each label's measured width, and drops
 * entries from the *end* when they will not fit — the first entries are the live curves, the last
 * ones the overlays, and a legend that cuts the overlay names is better than one that cuts the
 * curve the plot is about. Pure, so it can be tested without a canvas.
 */
export function layoutLegend(
  entries: ExportLegendEntry[],
  labelWidths: number[],
  width: number,
  rightMargin: number,
): LegendPlacement[] {
  const entryWidth = (i: number) => SWATCH_WIDTH + SWATCH_GAP + labelWidths[i]
  const totalWidth = (count: number) => {
    let total = 0
    for (let i = 0; i < count; i++) total += entryWidth(i)
    return total + Math.max(0, count - 1) * ENTRY_GAP
  }

  let count = entries.length
  while (count > 0 && totalWidth(count) > width - 2 * rightMargin) count--

  const placements: LegendPlacement[] = []
  let x = width - rightMargin - totalWidth(count)
  for (let i = 0; i < count; i++) {
    placements.push({ x, entry: entries[i] })
    x += entryWidth(i) + ENTRY_GAP
  }
  return placements
}

/**
 * An axis title for the export build only, spread into a uPlot axis. On screen the display panel
 * names the quantities and every pixel belongs to the plot; on paper the picture has to say what its
 * axes are. uPlot lays a labelled axis out wider (its `labelSize`), so the on-screen build must not
 * get one — a chart whose furniture grew would be refitted by useResultChartHeight.
 */
export function exportAxisLabel(interactive: boolean, label: string): Pick<uPlot.Axis, 'label' | 'labelSize' | 'labelFont'> {
  if (interactive) return {}
  return { label, labelSize: 24, labelFont: '600 13px ui-sans-serif, system-ui, sans-serif' }
}

/** One macrotask later — uPlot commits its first draw in a microtask after construction, so a plain `await` is not enough. */
export function nextPaint(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0))
}

/** The export canvas: white page, header line, the plot scaled below it; resolves to the PNG. */
export function composeChartPng(options: ComposeChartOptions): Promise<Blob> {
  const { width, height, plotCanvas, headerHeight, title, legend } = options
  if (!(width > 0) || !(height > headerHeight)) {
    return Promise.reject(new Error(`A chart export needs a positive size (got ${width}×${height}).`))
  }

  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const ctx = canvas.getContext('2d')
  if (!ctx) return Promise.reject(new Error('Could not get a 2D context for the chart export.'))

  ctx.fillStyle = EXPORT_BACKGROUND
  ctx.fillRect(0, 0, width, height)

  const margin = 12
  const midY = headerHeight / 2

  if (legend && legend.length > 0) {
    ctx.font = LEGEND_FONT
    const labelWidths = legend.map((entry) => ctx.measureText(entry.label).width)
    for (const { x, entry } of layoutLegend(legend, labelWidths, width, margin)) {
      ctx.strokeStyle = entry.color
      ctx.lineWidth = 2
      ctx.setLineDash(entry.dash ?? [])
      ctx.beginPath()
      ctx.moveTo(x, midY)
      ctx.lineTo(x + SWATCH_WIDTH, midY)
      ctx.stroke()
      ctx.setLineDash([])

      ctx.fillStyle = EXPORT_LEGEND_COLOR
      ctx.textAlign = 'left'
      ctx.textBaseline = 'middle'
      ctx.fillText(entry.label, x + SWATCH_WIDTH + SWATCH_GAP, midY)
    }
  }

  if (title) {
    ctx.font = TITLE_FONT
    ctx.fillStyle = EXPORT_TITLE_COLOR
    ctx.textAlign = 'left'
    ctx.textBaseline = 'middle'
    ctx.fillText(title, margin, midY)
  }

  ctx.drawImage(plotCanvas, 0, headerHeight, width, height - headerHeight)

  return new Promise((resolve, reject) => {
    canvas.toBlob((blob) => {
      if (blob) resolve(blob)
      else reject(new Error('The browser produced no PNG for the chart export.'))
    }, 'image/png')
  })
}

/**
 * Turns an exported chart into greys, in place: each pixel becomes its Rec. 601 luma, the same
 * weighting a monochrome printer's driver applies, so the picture on paper is the picture the
 * dialog promised. Alpha is left alone. Pure, so the arithmetic is unit-tested without a canvas
 * (exportChart.test.ts) — the canvas plumbing is `monochromeChartPng`.
 */
export function desaturateInPlace(rgba: Uint8ClampedArray): void {
  for (let i = 0; i < rgba.length; i += 4) {
    const grey = Math.round(0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2])
    rgba[i] = grey
    rgba[i + 1] = grey
    rgba[i + 2] = grey
  }
}

/**
 * A chart PNG re-encoded in black and white — the Screenshot dialog's and the report settings'
 * "Black and white" option. Applied to the finished PNG rather than inside each chart's
 * `exportPng`, so nine charts need no second palette and the legend swatches turn grey together
 * with the curves they name. Curves keep their line styles (the dashes that tell an overlay from a
 * live curve survive), and only their hue is lost.
 */
export async function monochromeChartPng(png: Blob): Promise<Blob> {
  const bitmap = await createImageBitmap(png)
  try {
    const canvas = document.createElement('canvas')
    canvas.width = bitmap.width
    canvas.height = bitmap.height
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('Could not get a 2D context for the black-and-white conversion.')
    ctx.drawImage(bitmap, 0, 0)
    const image = ctx.getImageData(0, 0, canvas.width, canvas.height)
    desaturateInPlace(image.data)
    ctx.putImageData(image, 0, 0)
    return await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob((blob) => {
        if (blob) resolve(blob)
        else reject(new Error('The browser produced no PNG for the black-and-white conversion.'))
      }, 'image/png')
    })
  } finally {
    bitmap.close()
  }
}
