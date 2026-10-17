import type { ThdMethodName, ThdSweepPoint } from '../api/client'
import { sweepDistortionDb, sweepTraceLabel, sweepTraceValue, type SweepTraceFamily, type SweepTraceKey } from './points'
import { dbvToVolt, formatSweepX, sweepXLabel, sweepXValue, type SweepXAxis } from './xAxis'

/**
 * The sweep chart's metrics table as pure functions: the columns, one row per point, and the
 * formatting of every cell. ThdSweepChart reads these for the table under its cursor and the
 * report's sweep chapter reads them for every point, so a figure on paper is the figure on screen —
 * nothing in a .vue file is testable, which is why the arithmetic lives here and not there.
 */

/** Which unit a distortion figure is written in — the display panel's Unit choice. */
export type SweepUnit = 'percent' | 'db'

/**
 * A distortion/noise percentage as the metrics pane's active unit wants it — through the same
 * sweepDistortionDb the chart plots with, `gainDb` and all, so a row's number is the number the
 * corresponding trace actually sits at on screen. Pass the point's own `gainDb`; it is required
 * rather than defaulted, because a silently-omitted gain is a row that reads 20-odd dB away from
 * the trace it describes with nothing to say so. Unlike the chart's toY (which returns null for a
 * non-positive value to draw a gap), this reports a real reading: a point whose figure is 0 is
 * "0%", not a hole in the table.
 */
export function formatDistortion(percent: number, unit: SweepUnit, gainDb: number): string {
  if (!Number.isFinite(percent)) return '—'
  if (unit === 'db') {
    const db = sweepDistortionDb(percent, gainDb)
    return Number.isFinite(db) ? `${db.toFixed(2)} dB` : '—'
  }
  if (!(percent > 0)) return '0%'
  const decimals = percent >= 1 ? 2 : Math.min(6, Math.max(0, Math.ceil(-Math.log10(percent)) + 1))
  return `${percent.toFixed(decimals)}%`
}

/** Magnitude is a gain in dB on both unit modes — the chart's TRACES say why it has no percent form. */
export function formatMagnitude(gainDb: number): string {
  return Number.isFinite(gainDb) ? `${gainDb.toFixed(2)} dB` : '—'
}

/**
 * `100 mV (-20.00 dBV)` — the level the generator actually played this point at.
 *
 * Both forms, because neither alone answers the question: the Level field may have been typed in
 * either unit, and under an output-referred Level mode it was typed in neither — the runner solved
 * for this figure and nothing else on the chart states it. Volts first, since that is the quantity
 * a DUT's input is specified in; the dBV in brackets is what every other level readout in the app
 * is written in.
 */
export function formatGenerator(generatorDbv: number): string {
  if (!Number.isFinite(generatorDbv)) return '—'
  return `${formatSweepX(dbvToVolt(generatorDbv), 'inputVolt')} (${generatorDbv.toFixed(2)} dBV)`
}

/**
 * Whether the metrics table carries a Generator column at all. Omitted on an `inputVolt` sweep,
 * whose x axis already *is* the generator level — the column would restate the first one.
 */
export function showsGeneratorColumn(xAxis: SweepXAxis): boolean {
  return xAxis !== 'inputVolt'
}

/** "1 mW (8 Ω)" — the load a sweep's Power figure was computed against, named beside it so the number cannot be read against another. */
export function formatPower(watt: number, loadOhms: number): string {
  if (!Number.isFinite(watt)) return '—'
  const mw = watt * 1000
  const value = mw >= 100 ? mw.toFixed(0) : mw >= 10 ? mw.toFixed(1) : mw.toFixed(2)
  return `${value} mW (${loadOhms} Ω)`
}

/**
 * One column of the metrics table: its header, and which line of the *screen's* readout it is
 * written on. Paper has one line (see {@link sweepMetricColumns}); the chart column has not, which
 * is what `readoutLine` is for.
 */
interface MetricColumnSpec {
  label: string
  /** 0 = what was played and what came back; 1 = the distortion orders. See {@link sweepReadoutLines}. */
  readoutLine: 0 | 1
}

/**
 * The metrics table's columns, in order, for one run's method — with the line each is written on
 * when the screen breaks them in two.
 *
 * <p>One list rather than a label carried alongside every value: the header is written once per
 * block and the rows underneath it are bare values, so the two only line up if they come from the
 * same order — see sweepMetricRow, which returns exactly this many strings. Per run rather than one
 * global list because a frequency-normalized run reads nTHD/nD2… while a conventional one overlaid
 * on it reads THD/D2…, and the header is the only place that difference is stated; the IMD family
 * renames the total and the orders the same way.</p>
 */
function sweepMetricColumnSpecs(method: ThdMethodName, family: SweepTraceFamily, xAxis: SweepXAxis): MetricColumnSpec[] {
  const t = (key: SweepTraceKey) => sweepTraceLabel(key, method, family)
  const level = (label: string): MetricColumnSpec => ({ label, readoutLine: 0 })
  const order = (label: string): MetricColumnSpec => ({ label, readoutLine: 1 })
  return [
    level(sweepXLabel(xAxis)),
    ...(showsGeneratorColumn(xAxis) ? [level('Generator')] : []),
    order(t('thd')), level('Magnitude'),
    order(t('d2')), order(t('d3')), order(t('d4')), order(t('d5')), order(t('d6plus')),
    level('Power'), level('Noise floor'),
    // The two band-integrated figures, printed for every sweep that has them -- an IMD run carries
    // NaN for both and formatDistortion renders that as the table's own dash.
    ...(family === 'imd' ? [] : [level('THD+N'), level('SNR')]),
  ]
}

/**
 * The metrics table's columns, in order, for one run's method — the flat list, which is the order
 * {@link sweepMetricRow} returns its values in and the order the report's table prints.
 */
export function sweepMetricColumns(method: ThdMethodName, family: SweepTraceFamily, xAxis: SweepXAxis): string[] {
  return sweepMetricColumnSpecs(method, family, xAxis).map((c) => c.label)
}

/**
 * How the *screen's* readout breaks those columns into two lines, as indices into
 * {@link sweepMetricColumns} / {@link sweepMetricRow}.
 *
 * <p>Fourteen columns do not fit the chart column: even at 11 px with no forced nowrap the readout
 * wants 652 px, and the column is 533 px at a 1440 px window and 373 px at 1280 — so it scrolled
 * sideways, and the last readings sat behind a scrollbar nobody looks for (reported 2026-10-17,
 * after a first attempt that only narrowed it). Two lines is what Joost asked for, and this is
 * where the split is decided rather than in the template, so the header and the values can only
 * ever be split the same way.</p>
 *
 * <p>The split is by <b>meaning</b>, not by halving the list: line 0 is the level the point was
 * played and measured at and the noise around it, line 1 is the distortion orders, which belong
 * together and are the narrowest columns there are. Indices rather than labels because the values
 * have to follow their own column, and `sweepMetricRow` is positional.</p>
 *
 * <p>Nothing here reaches the report: paper has room for one line, and
 * {@link sweepMetricColumns} is unchanged for it.</p>
 */
export function sweepReadoutLines(method: ThdMethodName, family: SweepTraceFamily, xAxis: SweepXAxis): number[][] {
  const specs = sweepMetricColumnSpecs(method, family, xAxis)
  return [0, 1].map((line) =>
    specs.reduce<number[]>((out, spec, i) => (spec.readoutLine === line ? [...out, i] : out), []),
  )
}

/** One point's row of values for the metrics table, in sweepMetricColumns order, under its run's method. */
export function sweepMetricRow(
  point: ThdSweepPoint,
  method: ThdMethodName,
  xAxis: SweepXAxis,
  unit: SweepUnit,
  loadOhms: number,
  family: SweepTraceFamily = 'thd',
): string[] {
  const v = (key: SweepTraceKey) => sweepTraceValue(point, key, method)
  const d = (key: SweepTraceKey) => formatDistortion(v(key), unit, point.gainDb)
  return [
    formatSweepX(sweepXValue(point, xAxis), xAxis),
    ...(showsGeneratorColumn(xAxis) ? [formatGenerator(point.generatorDbv)] : []),
    d('thd'),
    formatMagnitude(point.gainDb),
    d('d2'),
    d('d3'),
    d('d4'),
    d('d5'),
    d('d6plus'),
    formatPower(point.powerWatt, loadOhms),
    d('noise'),
    // SNR prints as the plain figure it is stored as, not through formatDistortion: a table has
    // room to be unambiguous, where the chart has to share one axis with the distortion traces and
    // draws the noise instead (sweepTraceValue). THD+N follows the unit toggle like every other
    // distortion column.
    ...(family === 'imd' ? [] : [d('thdn'), formatSnr(point.snrDb)]),
  ]
}

/** An SNR as it is quoted: counting upward, in dB, whatever unit the distortion columns are in. */
function formatSnr(snrDb: number): string {
  return Number.isFinite(snrDb) ? `${snrDb.toFixed(2)} dB` : '—'
}

/** Where the total distortion (THD or IMD) of one channel's sweep is largest and smallest. */
export interface SweepSummary {
  /** Points on the channel, whatever their figures. */
  points: number
  /** `gainDb` rides along because the dB form of a figure is that point's own (see formatDistortion). */
  worst: { x: number; percent: number; gainDb: number }
  best: { x: number; percent: number; gainDb: number }
}

/**
 * The worst and best total distortion over one channel's points, and where on the x axis each
 * sits. Points whose figure is NaN — a normalized order without its reference, the top of an
 * extended grid — are skipped, and a channel with nothing readable is null rather than a summary of
 * nothing. `points` counts every point on the channel, including the skipped ones, since that is
 * what the sweep measured.
 */
export function sweepSummary(points: readonly ThdSweepPoint[], method: ThdMethodName, xAxis: SweepXAxis): SweepSummary | null {
  let worst: { x: number; percent: number; gainDb: number } | null = null
  let best: { x: number; percent: number; gainDb: number } | null = null
  for (const point of points) {
    const percent = sweepTraceValue(point, 'thd', method)
    if (!Number.isFinite(percent)) continue
    // Worst and best are decided on the percentage — the distortion ratio — under either unit.
    // The dB form is a presentation of that same point, so it carries the point's gain with it
    // rather than being ranked after the compensation, which would rank the DUT's response too.
    const x = sweepXValue(point, xAxis)
    if (!worst || percent > worst.percent) worst = { x, percent, gainDb: point.gainDb }
    if (!best || percent < best.percent) best = { x, percent, gainDb: point.gainDb }
  }
  return worst && best ? { points: points.length, worst, best } : null
}
