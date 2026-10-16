/**
 * The Screenshot dialog's vocabulary and the picture it produces.
 *
 * A screenshot is the chart on screen, rendered offscreen at a chosen size in the light palette
 * (each chart's own `exportPng`), inside a *frame*: an optional title band above it and an optional
 * date line below it. The frame is composed here on top of the finished chart PNG rather than inside
 * the nine charts' `exportPng`s, the way black and white is (`monochromeChartPng`) — one place, and
 * the charts keep drawing exactly what the report asks them for.
 *
 * The size and aspect ratio describe the **whole picture**, bands included, so a 1000 × 500 request
 * is a 1000 × 500 file whatever is ticked; the chart gives up the bands' rows. The chart is rendered
 * at its final pixel size rather than at 1600 wide and scaled, because uPlot's fonts are fixed
 * pixel sizes: at 500 px wide a downscaled chart's labels would be unreadable, and rendered natively
 * they are simply large relative to a small plot, which is what a small picture wants.
 *
 * `screenshotFrameLayout` is pure so the arithmetic is unit-tested (screenshot.test.ts); the canvas
 * work is `frameChartPng`.
 */

import { EXPORT_BACKGROUND, EXPORT_LEGEND_COLOR, EXPORT_TITLE_COLOR } from './charts/exportChart'

export type ScreenshotAspectRatioId = '3:2' | '2:1'
export type ScreenshotDestinationId = 'clipboard' | 'file' | 'both'

export interface ScreenshotSizeOption {
  widthPx: number
  label: string
}

/** The Image size dropdown: seven steps from 500 to 2000 px wide. The *height* follows the aspect ratio. */
export const SCREENSHOT_SIZES: readonly ScreenshotSizeOption[] = [
  { widthPx: 500, label: 'Smallest' },
  { widthPx: 750, label: 'Small' },
  { widthPx: 1000, label: 'Medium' },
  { widthPx: 1250, label: 'Large' },
  { widthPx: 1500, label: 'X Large' },
  { widthPx: 1750, label: 'XX Large' },
  { widthPx: 2000, label: 'XXX Large' },
]

export const SCREENSHOT_ASPECT_RATIOS: readonly { id: ScreenshotAspectRatioId; label: string; ratio: number }[] = [
  { id: '3:2', label: '3 : 2', ratio: 3 / 2 },
  { id: '2:1', label: '2 : 1', ratio: 2 },
]

export const SCREENSHOT_DESTINATIONS: readonly { id: ScreenshotDestinationId; label: string }[] = [
  { id: 'clipboard', label: 'Copy to clipboard' },
  { id: 'file', label: 'Save to file' },
  { id: 'both', label: 'Save to file + copy to clipboard' },
]

/** Everything the dialog asks, in the shape the server stores (`screenshot.settings`) and App.vue consumes. */
export interface ScreenshotSettings {
  title: string
  /**
   * The measurement `title` was typed for, by name — so a kept title is offered again on that
   * measurement and nowhere else. Set by the dialog from the measurement it was opened on, never
   * by the user; empty when none was open. See `screenshotTitleFor`.
   */
  titleFor: string
  saveText: boolean
  addDate: boolean
  widthPx: number
  aspectRatio: ScreenshotAspectRatioId
  destination: ScreenshotDestinationId
  monochrome: boolean
}

export const DEFAULT_SCREENSHOT_SETTINGS: ScreenshotSettings = {
  title: '',
  titleFor: '',
  saveText: false,
  addDate: false,
  widthPx: 1500,
  aspectRatio: '3:2',
  destination: 'file',
  monochrome: false,
}

/** A stored width, or anything else, snapped to the nearest offered size. */
export function toScreenshotWidthPx(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return DEFAULT_SCREENSHOT_SETTINGS.widthPx
  let best = SCREENSHOT_SIZES[0].widthPx
  for (const size of SCREENSHOT_SIZES) {
    if (Math.abs(size.widthPx - value) < Math.abs(best - value)) best = size.widthPx
  }
  return best
}

export function toScreenshotAspectRatioId(value: unknown): ScreenshotAspectRatioId {
  return SCREENSHOT_ASPECT_RATIOS.some((r) => r.id === value) ? (value as ScreenshotAspectRatioId) : DEFAULT_SCREENSHOT_SETTINGS.aspectRatio
}

export function toScreenshotDestinationId(value: unknown): ScreenshotDestinationId {
  return SCREENSHOT_DESTINATIONS.some((d) => d.id === value) ? (value as ScreenshotDestinationId) : DEFAULT_SCREENSHOT_SETTINGS.destination
}

/**
 * The title the Screenshot dialog opens holding, for the measurement named by `measurement`.
 *
 * <p>The open measurement's name, which is the caption wanted nearly every time and the one the
 * app already knows — <b>unless</b> **Save text** is on and the kept title was typed for this same
 * measurement, in which case that one comes back, which is what the checkbox is for.</p>
 *
 * <p>Scoping it to the measurement is the whole of the fix. Before 2026-10-16 the box carried the
 * last title typed anywhere, for ever: Joost's bench had Save text on, so every screenshot of
 * every measurement opened holding "Elliptic - Nyquist" from some earlier session. Keying it on
 * the name rather than on an id is deliberate — the name is what the caption says, so renaming the
 * measurement rightly drops a caption that no longer describes it, and two measurements that share
 * a name may as well share one.</p>
 */
export function screenshotTitleFor(
  stored: Pick<ScreenshotSettings, 'title' | 'titleFor' | 'saveText'>,
  measurement: string,
): string {
  const name = measurement.trim()
  const kept = stored.title.trim()
  if (stored.saveText && kept.length > 0 && stored.titleFor.trim() === name) return stored.title
  return name
}

/** The title band above the chart, in pixels; zero without a title. */
export const SCREENSHOT_TITLE_BAND_PX = 48
/** The date line below the chart, in pixels; zero without a date. */
export const SCREENSHOT_DATE_BAND_PX = 28

export interface ScreenshotFrameLayout {
  /** The whole picture. */
  width: number
  height: number
  /** The title band's height, 0 when there is no title. */
  titleBand: number
  /** The date line's height, 0 when there is no date. */
  dateBand: number
  /** What the chart itself is rendered at — the picture less the bands. */
  chartWidth: number
  chartHeight: number
}

/**
 * The picture's geometry from the dialog's answers. The height is the width over the ratio,
 * rounded to whole pixels; the bands come out of the chart's share so the file is exactly the size
 * asked for. A blank (whitespace-only) title gets no band.
 */
export function screenshotFrameLayout(
  widthPx: number,
  aspectRatio: ScreenshotAspectRatioId,
  title: string,
  addDate: boolean,
): ScreenshotFrameLayout {
  const ratio = SCREENSHOT_ASPECT_RATIOS.find((r) => r.id === aspectRatio)?.ratio ?? 1.5
  const width = Math.round(widthPx)
  const height = Math.round(width / ratio)
  const titleBand = title.trim().length > 0 ? SCREENSHOT_TITLE_BAND_PX : 0
  const dateBand = addDate ? SCREENSHOT_DATE_BAND_PX : 0
  return { width, height, titleBand, dateBand, chartWidth: width, chartHeight: height - titleBand - dateBand }
}

const TITLE_FONT = '600 20px ui-sans-serif, system-ui, sans-serif'
const DATE_FONT = '13px ui-sans-serif, system-ui, sans-serif'

export interface FrameChartOptions {
  chart: Blob
  layout: ScreenshotFrameLayout
  title: string
  /** The date line's text, already formatted; empty for none. */
  date: string
}

/**
 * The chart PNG inside its frame: white page, the title centred in the band above, the chart
 * scaled onto its own area (a no-op when it was rendered at `layout.chartWidth × chartHeight`, which
 * it always is — the scale is only there so a chart that rounded differently cannot leave a seam),
 * and the date right-aligned on the line below. A layout with neither band returns the chart's
 * pixels unchanged, re-encoded.
 */
export async function frameChartPng({ chart, layout, title, date }: FrameChartOptions): Promise<Blob> {
  const bitmap = await createImageBitmap(chart)
  try {
    const canvas = document.createElement('canvas')
    canvas.width = layout.width
    canvas.height = layout.height
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('Could not get a 2D context for the screenshot frame.')

    ctx.fillStyle = EXPORT_BACKGROUND
    ctx.fillRect(0, 0, layout.width, layout.height)

    if (layout.titleBand > 0) {
      ctx.font = TITLE_FONT
      ctx.fillStyle = EXPORT_TITLE_COLOR
      ctx.textAlign = 'center'
      ctx.textBaseline = 'middle'
      ctx.fillText(title.trim(), layout.width / 2, layout.titleBand / 2, layout.width - 24)
    }

    ctx.drawImage(bitmap, 0, layout.titleBand, layout.chartWidth, layout.chartHeight)

    if (layout.dateBand > 0 && date) {
      ctx.font = DATE_FONT
      ctx.fillStyle = EXPORT_LEGEND_COLOR
      ctx.textAlign = 'right'
      ctx.textBaseline = 'middle'
      ctx.fillText(date, layout.width - 12, layout.height - layout.dateBand / 2)
    }

    return await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob((blob) => {
        if (blob) resolve(blob)
        else reject(new Error('The browser produced no PNG for the screenshot.'))
      }, 'image/png')
    })
  } finally {
    bitmap.close()
  }
}

/**
 * Puts a PNG on the system clipboard. Needs a secure context (localhost is one) and a focused
 * document; the browser says no in a sentence when it refuses, which the caller shows as is.
 */
export async function copyPngToClipboard(png: Blob): Promise<void> {
  if (typeof ClipboardItem === 'undefined' || !navigator.clipboard?.write) {
    throw new Error('This browser cannot copy images to the clipboard — save to a file instead.')
  }
  await navigator.clipboard.write([new ClipboardItem({ 'image/png': png })])
}
