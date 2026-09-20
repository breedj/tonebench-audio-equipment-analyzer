import type { ThdMethodName, ThdSweepPoint } from '../api/client'
import { percentToDb, sweepTraceLabel, sweepTraceValue, type SweepTraceFamily, type SweepTraceKey } from './points'
import { formatSweepX, sweepXLabel, sweepXValue, type SweepXAxis } from './xAxis'

/**
 * The sweep chart's metrics table as pure functions: the columns, one row per point, and the
 * formatting of every cell. ThdSweepChart reads these for the table under its cursor and the
 * report's sweep chapter reads them for every point, so a figure on paper is the figure on screen —
 * nothing in a .vue file is testable, which is why the arithmetic lives here and not there.
 */

/** Which unit a distortion figure is written in — the display panel's Unit choice. */
export type SweepUnit = 'percent' | 'db'

/**
 * A distortion/noise percentage as the metrics pane's active unit wants it — the same
 * percentToDb conversion the chart itself plots with, so a row's numbers always match wherever
 * the corresponding trace actually sits on screen. Unlike the chart's toY (which returns null for
 * a non-positive value to draw a gap), this reports a real reading: a point whose figure is 0 is
 * "0%", not a hole in the table.
 */
export function formatDistortion(percent: number, unit: SweepUnit): string {
  if (!Number.isFinite(percent)) return '—'
  if (unit === 'db') return `${percentToDb(percent).toFixed(2)} dB`
  if (!(percent > 0)) return '0%'
  const decimals = percent >= 1 ? 2 : Math.min(6, Math.max(0, Math.ceil(-Math.log10(percent)) + 1))
  return `${percent.toFixed(decimals)}%`
}

/** Magnitude is a gain in dB on both unit modes — the chart's TRACES say why it has no percent form. */
export function formatMagnitude(gainDb: number): string {
  return Number.isFinite(gainDb) ? `${gainDb.toFixed(2)} dB` : '—'
}

/** "1 mW (8 Ω)" — the load a sweep's Power figure was computed against, named beside it so the number cannot be read against another. */
export function formatPower(watt: number, loadOhms: number): string {
  if (!Number.isFinite(watt)) return '—'
  const mw = watt * 1000
  const value = mw >= 100 ? mw.toFixed(0) : mw >= 10 ? mw.toFixed(1) : mw.toFixed(2)
  return `${value} mW (${loadOhms} Ω)`
}

/**
 * The metrics table's columns, in order, for one run's method. One list rather than a label
 * carried alongside every value: the header is written once per block and the rows underneath it
 * are bare values, so the two only line up if they come from the same order — see sweepMetricRow,
 * which returns exactly this many strings. Per run rather than one global list because a
 * frequency-normalized run reads nTHD/nD2… while a conventional one overlaid on it reads THD/D2…,
 * and the header is the only place that difference is stated; the IMD family renames the total and
 * the orders the same way.
 */
export function sweepMetricColumns(method: ThdMethodName, family: SweepTraceFamily, xAxis: SweepXAxis): string[] {
  const t = (key: SweepTraceKey) => sweepTraceLabel(key, method, family)
  return [sweepXLabel(xAxis), t('thd'), 'Magnitude', t('d2'), t('d3'), t('d4'), t('d5'), t('d6plus'), 'Power', 'Noise floor']
}

/** One point's row of values for the metrics table, in sweepMetricColumns order, under its run's method. */
export function sweepMetricRow(point: ThdSweepPoint, method: ThdMethodName, xAxis: SweepXAxis, unit: SweepUnit, loadOhms: number): string[] {
  const v = (key: SweepTraceKey) => sweepTraceValue(point, key, method)
  return [
    formatSweepX(sweepXValue(point, xAxis), xAxis),
    formatDistortion(v('thd'), unit),
    formatMagnitude(point.gainDb),
    formatDistortion(v('d2'), unit),
    formatDistortion(v('d3'), unit),
    formatDistortion(v('d4'), unit),
    formatDistortion(v('d5'), unit),
    formatDistortion(v('d6plus'), unit),
    formatPower(point.powerWatt, loadOhms),
    formatDistortion(v('noise'), unit),
  ]
}

/** Where the total distortion (THD or IMD) of one channel's sweep is largest and smallest. */
export interface SweepSummary {
  /** Points on the channel, whatever their figures. */
  points: number
  worst: { x: number; percent: number }
  best: { x: number; percent: number }
}

/**
 * The worst and best total distortion over one channel's points, and where on the x axis each
 * sits. Points whose figure is NaN — a normalized order without its reference, the top of an
 * extended grid — are skipped, and a channel with nothing readable is null rather than a summary of
 * nothing. `points` counts every point on the channel, including the skipped ones, since that is
 * what the sweep measured.
 */
export function sweepSummary(points: readonly ThdSweepPoint[], method: ThdMethodName, xAxis: SweepXAxis): SweepSummary | null {
  let worst: { x: number; percent: number } | null = null
  let best: { x: number; percent: number } | null = null
  for (const point of points) {
    const percent = sweepTraceValue(point, 'thd', method)
    if (!Number.isFinite(percent)) continue
    const x = sweepXValue(point, xAxis)
    if (!worst || percent > worst.percent) worst = { x, percent }
    if (!best || percent < best.percent) best = { x, percent }
  }
  return worst && best ? { points: points.length, worst, best } : null
}
