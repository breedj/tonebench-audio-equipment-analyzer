import type { ScopeChannelSlice, ScopeSliceMeasurements } from '../api/client'
import { formatHz } from '../response/format'
import { formatScopeTime, toChartMs } from './timeAxis'
import { formatVolts } from './voltAxis'

/**
 * One channel's readings as the Measurements panel and the report both print them — one function,
 * so the paper and the screen cannot quote different numbers or call them different things.
 *
 * <p><b>Every figure here was measured on the analyzer</b>, on the samples of the window on screen
 * at full resolution (<code>ScopeSliceMeasurements</code>). None of it is derived from what is
 * drawn: a mean of column maxima is the mean of an envelope, and a period read off a column pair is
 * a reading of the column width.</p>
 *
 * <p><b>A reading that is absent is left out rather than dashed.</b> Most of these are undefined on
 * some waveform — a period on noise, a duty cycle on DC, a rise time with no edge — and a column of
 * dashes beside the four that always read is worse than a short list. The exception is the
 * <em>pair</em> of settled levels: where there is only one level, Top and Base are both absent and
 * everything measured between them is too, which is the honest consequence rather than an
 * omission.</p>
 */
export interface ScopeReading {
  label: string
  value: string
  /** What the row means, for the panel's tooltip. Paper has none, so the report prints the figures alone. */
  title: string
}

/** A time in seconds as this screen writes it — milliseconds or microseconds, by its own size. */
export function formatSeconds(seconds: number): string {
  const ms = toChartMs(seconds)
  return formatScopeTime(ms, Math.abs(ms) || 1)
}

function percent(value: number): string {
  return `${value.toFixed(1)} %`
}

export function scopeReadings(
  measurements: ScopeSliceMeasurements | null | undefined,
  voltsPerDivision: number,
): ScopeReading[] {
  if (!measurements) return []
  const m = measurements
  const volts = (v: number) => formatVolts(v, voltsPerDivision)
  const rows: ScopeReading[] = []
  const add = (label: string, value: string | null, title: string) => {
    if (value !== null) rows.push({ label, value, title })
  }
  const maybe = (value: number | null | undefined, format: (v: number) => string) =>
    value === null || value === undefined ? null : format(value)

  add('Maximum', volts(m.maxV), 'The highest sample in the window — exact even on a decimated slice, because both extremes of every column are kept.')
  add('Minimum', volts(m.minV), 'The lowest sample in the window.')
  add('Peak to peak', volts(m.peakToPeakV), 'The maximum less the minimum.')
  add(
    'Mean',
    volts(m.meanV),
    "Vmean — the average of the window's samples. Both this analyzer and a sound card are AC-coupled, so on a quiet input this is the input's own residual offset and not the DUT's DC.",
  )
  add('RMS', volts(m.rmsV), 'True RMS of the window, DC included.')
  add('AC RMS', volts(m.acRmsV), 'RMS about the mean — the figure to quote for a signal riding on an offset.')
  add('Crest factor', maybe(m.crestFactor, (v) => v.toFixed(2)), 'Peak over true RMS. 1.41 for a sine, 1.00 for a square.')

  add('Top', maybe(m.topV, volts), 'The settled high level, from a histogram of the samples rather than from the maximum — so an overshoot does not move it. A sine has no flat top, and this comes out at its peak.')
  add('Base', maybe(m.baseV, volts), 'The settled low level.')
  add('Amplitude', maybe(m.amplitudeV, volts), 'Top less base — what the edge thresholds and the overshoot are measured against.')

  add('Frequency', maybe(m.frequencyHz, formatHz), 'From one period of the trace, measured between successive rising crossings of the middle.')
  add('Period', maybe(m.periodS, formatSeconds), 'One cycle, between successive rising crossings of the middle.')
  add('Duty', maybe(m.dutyPercent, percent), 'How much of the period the waveform spends above the middle.')
  add('+ Width', maybe(m.positiveWidthS, formatSeconds), 'How long it spends above the middle in one cycle.')
  add('− Width', maybe(m.negativeWidthS, formatSeconds), 'And below it.')
  add('Rise', maybe(m.riseTimeS, formatSeconds), '10 % to 90 % of the first clean rising edge. Nothing faster than about 4 µs is the DUT rather than this analyzer.')
  add('Fall', maybe(m.fallTimeS, formatSeconds), '90 % to 10 % of the first clean falling edge.')
  add('Overshoot', maybe(m.overshootPercent, percent), 'How far past the top the trace goes, as a percentage of the amplitude.')
  add('Preshoot', maybe(m.preshootPercent, percent), 'The same below the base.')

  return rows
}

/**
 * Which set of readings to take off a slice: the window that is drawn, or the cursor gate.
 *
 * <p>Both arrive on the same slice, from the same fetch, so the choice is a presentation one and
 * cannot put the two out of step with each other or with the picture.</p>
 */
export type ScopeReadingSource = 'window' | 'gate'

/** The readings of each drawn channel, labelled — what both the panel and the report iterate. */
export function scopeChannelReadings(
  channels: readonly ScopeChannelSlice[] | null | undefined,
  voltsPerDivision: number,
  showLeft: boolean,
  showRight: boolean,
  source: ScopeReadingSource = 'window',
): { channel: number; label: string; readings: ScopeReading[] }[] {
  return (channels ?? [])
    .filter((slice) => (slice.channel === 1 ? showRight : showLeft))
    .map((slice) => ({
      channel: slice.channel,
      label: slice.channel === 0 ? 'Left' : 'Right',
      // A channel with no gated set is left out rather than falling back to the window's: the
      // server answers null when the cursor window holds none of this record, and the whole
      // window's figures under a "between the cursors" heading is the one answer worse than none.
      readings: scopeReadings(source === 'gate' ? slice.gated : slice.measurements, voltsPerDivision),
    }))
    .filter((block) => block.readings.length > 0)
}
