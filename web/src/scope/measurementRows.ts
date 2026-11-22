/**
 * The chosen measurements as the table under the chart draws them: one row per measurement and
 * source, each with the number it read and the way to write it.
 *
 * <p><b>Here rather than in the component</b> for this project's usual reason — nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in — and because two of the
 * rules in it are invisible by eye and wrong by default. A statistic is written at its <em>own</em>
 * magnitude and not the value's, so a 306.864 mV peak-to-peak can sit above a 757 µV standard
 * deviation the way a bench instrument prints it; and the key a row's statistics accumulate under
 * has to survive a re-render while changing the moment the reading stops meaning the same thing.</p>
 */

import type { ScopeRecordResponse, ScopeXySlice } from '../api/client'
import {
  formatMathValue,
  formatSiValue,
  mathSliceAxisLabel,
  SCOPE_MATH_SLOTS,
  scopeMathSlice,
  type ScopeMathSlot,
  type ScopeMathUnit,
} from './math'
import {
  SCOPE_MEASUREMENT_ABSENT,
  scopeMeasurement,
  scopeMeasurementIsChannelsOnly,
  scopeSelectionId,
  scopeSelectionSources,
  type ScopeMeasurementDef,
  type ScopeMeasurementQuantity,
} from './measurementCatalogue'

/** Which trace a row reads. */
export type ScopeMeasurementSourceKind = 'channel' | 'math' | 'pair'

export interface ScopeMeasurementSource {
  kind: ScopeMeasurementSourceKind
  /** 0 or 1 for a captured input; absent otherwise. */
  channel?: number
  /** 1 or 2 for a math channel; absent otherwise. */
  slot?: ScopeMathSlot
  /** What the row calls it — "Left", "A − B", "L ↔ R". */
  label: string
  /** The unit its levels are in. Volts for a captured input, the operation's own for the math trace. */
  unit: ScopeMathUnit
  /** The part of a row's key that names this source. */
  key: string
}

export interface ScopeMeasurementRow {
  /** Stable across re-renders and across records; changes only when the reading changes meaning. */
  key: string
  id: string
  label: string
  sourceLabel: string
  title: string
  /** The reading itself, or null where this waveform has not got one. */
  value: number | null
  /** That value as the table prints it, or a dash. */
  text: string
  /**
   * How to write any figure of this quantity — used for the value above <b>and</b> for every
   * statistic beside it, so a mean and a standard deviation of a reading are in the same units as
   * the reading and are each written at their own magnitude.
   */
  format: (value: number) => string
}

/**
 * Four significant figures, which is what every reading on this screen is written to.
 *
 * <p>The same ladder <code>scope/math.ts</code> uses, repeated rather than exported from there
 * because what it is doing here is deciding decimals for a percentage and a bare ratio, which that
 * file has no business knowing about.</p>
 */
function sig(value: number): string {
  const magnitude = Math.abs(value)
  if (magnitude >= 100) return value.toFixed(1)
  if (magnitude >= 10) return value.toFixed(2)
  if (magnitude >= 1) return value.toFixed(3)
  return value.toFixed(4)
}

/**
 * How a figure of this quantity is written.
 *
 * <p><b>At its own magnitude, never at the axis's.</b> The trace readouts are written in the V/div
 * the graticule is in, so a column of them stays in one unit — right there and wrong here: a
 * standard deviation is routinely three orders below the value it belongs to, and printed on the
 * value's ladder it reads 0.001 V or simply 0.000. So levels go through the math channel's own
 * SI-prefix formatter, which is magnitude-driven, and the rest pick their own decimals.</p>
 *
 * <p><b>Four significant figures, the same as every other reading on this screen</b>, and one
 * formatter for the value and its statistics alike. A mean over N records does resolve about
 * <code>√N</code> below a single record's noise — an extra digit at a hundred records — so a bench
 * instrument prints its mean wider than its value. That is deliberately not done here: it would
 * need a second formatter per row, and the magnitude adaptation already carries the information
 * that matters, since a standard deviation three orders down is written in its own prefix rather
 * than as a row of zeros.</p>
 */
export function scopeMeasurementFormatter(
  quantity: ScopeMeasurementQuantity,
  unit: ScopeMathUnit,
): (value: number) => string {
  switch (quantity) {
    case 'level':
      return (v) => formatMathValue(v, unit)
    case 'area':
      return (v) => formatMathValue(v, 'VoltSecond')
    // A slew rate, which carries the rule formatMathValue exists for: the *denominator* is scaled,
    // so 2.4 MV/s reads 2.400 V/us -- the figure every amplifier datasheet quotes.
    case 'rate':
      return (v) => formatMathValue(v, 'VoltPerSecond')
    // Seconds and hertz through the same SI ladder rather than through the axis formatters, and
    // that is a correctness point rather than a tidiness one: those write in the unit the graticule
    // is in and bottom out at microseconds and at hundredths of a hertz, so a frequency that
    // wandered by 40 mHz would print 0.00 Hz and a skew that wandered by 800 ps would print
    // 0.00 µs. Those are precisely the figures a statistics column is read for.
    case 'time':
      return (v) => formatSiValue(v, 's')
    case 'frequency':
      return (v) => formatSiValue(v, 'Hz')
    case 'percent':
      return (v) => `${sig(v)} %`
    case 'angle':
      return (v) => `${sig(v)}°`
    case 'ratio':
    default:
      return (v) => sig(v)
  }
}

/**
 * Which traces a chosen measurement is shown for.
 *
 * <p>Every drawn input, plus the math trace when one is drawn — the FFT screen's rule, where a
 * metric ticked once appears in each channel's own panel rather than being chosen per channel.
 * What that buys is a picker that asks one question; what it costs is that a reader who wants one
 * channel's figure alone takes the other channel off the chart, which is the same gesture a bench
 * scope's per-channel display button is.</p>
 *
 * <p>The <b>Channel delay</b> group is the exception and has exactly one source, both inputs
 * together: a phase or a correlation is not a reading either channel has on its own.</p>
 *
 * <p>Area is offered for the captured inputs only. Its unit is a level times a time, and for A × B
 * or a derivative that is V²·s or plain volts — quantities nobody quotes and this app would have to
 * invent names for.</p>
 */
export function scopeMeasurementSources(
  record: ScopeRecordResponse | null | undefined,
  showLeft: boolean,
  showRight: boolean,
): ScopeMeasurementSource[] {
  const sources: ScopeMeasurementSource[] = []
  for (const slice of record?.channels ?? []) {
    if (slice.channel === 1 ? !showRight : !showLeft) continue
    sources.push({
      kind: 'channel',
      channel: slice.channel,
      label: slice.channel === 0 ? 'Left' : 'Right',
      unit: 'Volt',
      key: `ch${slice.channel}`,
    })
  }

  // One per math channel that drew something. Keyed by slot rather than by a single `math`, so a
  // reading chosen on Math 2 cannot quietly end up on Math 1's trace.
  for (const slot of SCOPE_MATH_SLOTS) {
    const math = scopeMathSlice(record, slot)
    if (!math) continue
    sources.push({
      kind: 'math',
      slot,
      // The sockets, not A and B: the picker and the display rail are looking at one trace.
      label: mathSliceAxisLabel(math),
      unit: math.unit as ScopeMathUnit,
      key: `math${slot}`,
    })
  }

  return sources
}

/** Which set of readings the rows are taken from — the window that is drawn, or the cursor gate. */
export type ScopeMeasurementReadingSource = 'window' | 'gate'

/**
 * The rows for one record.
 *
 * <p>Measurement-major and source-minor, so the two channels' figure for one reading sit next to
 * each other: comparing them is the commonest thing a reader does with a stereo capture, and that
 * comparison across a table's length is no comparison at all.</p>
 */
export function scopeMeasurementRows(
  record: ScopeRecordResponse | null | undefined,
  /** The stored selection: `id` for every source, or `id:source` for one — see the catalogue. */
  ids: readonly string[],
  options: {
    showLeft: boolean
    showRight: boolean
    source: ScopeMeasurementReadingSource
  },
): ScopeMeasurementRow[] {
  if (!record) return []

  const sources = scopeMeasurementSources(record, options.showLeft, options.showRight)
  const xy = record.xy ?? null
  // The left input's own measured frequency, which is what the skew divides the phase by. Taken
  // from the first captured channel rather than from the drawn one: the figure belongs to the pair,
  // and taking a channel off the screen must not change what a phase reads.
  const pairFrequencyHz = record.channels[0]?.measurements?.frequencyHz ?? null

  const rows: ScopeMeasurementRow[] = []
  for (const entry of ids) {
    const def = scopeMeasurement(scopeSelectionId(entry))
    if (!def) continue

    if (def.group === 'Delay') {
      rows.push(pairRow(def, xy, pairFrequencyHz))
      continue
    }

    // Which sources this entry stands for -- the one it names, or every one for a bare id, which
    // is what every selection stored before the picker had a Source control is. Intersected with
    // what this record actually carries and the rail is actually drawing, so asking for a source
    // the capture does not have is simply no row rather than an empty one.
    const wanted = scopeSelectionSources(entry)
    for (const source of sources) {
      if (!wanted.includes(source.key)) continue
      if (scopeMeasurementIsChannelsOnly(def.quantity) && source.kind !== 'channel') continue
      rows.push(channelRow(def, source, record, options.source))
    }
  }

  return rows
}

function channelRow(
  def: ScopeMeasurementDef,
  source: ScopeMeasurementSource,
  record: ScopeRecordResponse,
  reading: ScopeMeasurementReadingSource,
): ScopeMeasurementRow {
  const measurements =
    source.kind === 'math'
      ? (() => {
          const math = (record.maths ?? []).find((m) => m.slot === source.slot)
          if (!math) return null
          return reading === 'gate' ? (math.gated ?? null) : math.measurements
        })()
      : (() => {
          const slice = record.channels.find((c) => c.channel === source.channel)
          if (!slice) return null
          return reading === 'gate' ? slice.gated : slice.measurements
        })()

  const raw = measurements && def.read ? def.read(measurements) : null
  const value = raw === null || raw === undefined || !Number.isFinite(raw) ? null : raw
  const format = scopeMeasurementFormatter(def.quantity, source.unit)

  return {
    key: `${def.id}:${source.key}`,
    id: def.id,
    label: def.label,
    sourceLabel: source.label,
    title: def.title,
    value,
    text: value === null ? SCOPE_MEASUREMENT_ABSENT : format(value),
    format,
  }
}

function pairRow(
  def: ScopeMeasurementDef,
  xy: ScopeXySlice | null,
  frequencyHz: number | null,
): ScopeMeasurementRow {
  const raw = xy && def.readPair ? def.readPair(xy, frequencyHz) : null
  const value = raw === null || raw === undefined || !Number.isFinite(raw) ? null : raw
  const format = scopeMeasurementFormatter(def.quantity, 'Volt')

  return {
    key: `${def.id}:pair`,
    id: def.id,
    label: def.label,
    sourceLabel: 'L → R',
    title: def.title,
    value,
    text: value === null ? SCOPE_MEASUREMENT_ABSENT : format(value),
    format,
  }
}

/**
 * What the accumulated statistics are thrown away for.
 *
 * <p>The same rule the persistence picture follows, and for the same reason: a mean over records
 * taken under two configurations is a mean of two different measurements, and nothing in the figure
 * says so. So anything that changes what a reading <em>is</em> starts the count again — the capture
 * configuration, the stretch of the record being measured, whether the cursors are gating it, and
 * which math channel is drawn.</p>
 *
 * <p>The display path is in it through the capture signature's caller; the chosen measurements are
 * deliberately <b>not</b>, because adding a row must not reset the rows already counting.</p>
 */
export function scopeStatsSignature(
  captureSignature: string,
  record: ScopeRecordResponse | null | undefined,
  reading: ScopeMeasurementReadingSource,
  /**
   * The window that was <em>asked</em> for, or null for the whole record.
   *
   * <p><b>Asked for, never the record's own span</b>, and that is a measured correction rather than
   * a preference. A record's <code>fromS</code> is <code>TimeAt(firstFrame)</code>, which is
   * measured from the <i>interpolated</i> trigger position — so it moves by a picosecond or so from
   * capture to capture, every capture. Reading it here made the signature different on every record
   * and cleared the statistics each time: the count sat at 1 for ever, which looks exactly like a
   * feature that was never wired up. Rounding it would work until a value landed on the rounding
   * boundary; the requested window does not move at all.</p>
   */
  window: { fromS: number; toS: number } | null | undefined,
): string {
  if (!record) return captureSignature
  return [
    captureSignature,
    record.sampleRateHz,
    window ? `${window.fromS}:${window.toS}` : 'all',
    SCOPE_MATH_SLOTS.map((slot) => scopeMathSlice(record, slot)?.operation ?? 'Off').join('+'),
    reading,
  ].join('|')
}
