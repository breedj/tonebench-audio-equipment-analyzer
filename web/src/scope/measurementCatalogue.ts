/**
 * The measurements a reader can put under the chart, and what each one reads.
 *
 * <p><b>Why a catalogue rather than the list the Measurements panel already prints.</b> That panel
 * is the "everything" view: it shows every reading the window has, in one order, for every drawn
 * channel. What a bench scope offers beside it is a <em>chosen few</em>, large enough to read at a
 * glance, with statistics accumulated across records — and those two want different shapes. A
 * chosen set needs a stable id to store, a group to be found in, an icon to be picked by and, above
 * all, a way to get at the figure as a <b>number</b>: a mean, a standard deviation or a
 * peak-to-peak of a column of formatted strings is not a measurement.</p>
 *
 * <p>So every entry here carries <code>read</code>, which answers a number or null, and
 * <code>quantity</code>, which says what that number is in. The formatting is derived from the
 * quantity at the point of display, which is also what lets one entry serve a channel in volts and
 * the math trace in V², V/s or a bare ratio.</p>
 *
 * <p><b>Absent is null, never a zero.</b> Most of these are undefined on most waveforms — a period
 * on noise, a duty cycle on DC, a rise time with no edge — which is the oscilloscope screen's rule
 * everywhere and the one this file has the most opportunities to break.</p>
 */

import type { ScopeSliceMeasurements, ScopeXySlice } from '../api/client'

/** The tabs of the picker, in the order it offers them. */
export type ScopeMeasurementGroup = 'Vertical' | 'Horizontal' | 'Miscellaneous' | 'Delay'

export const SCOPE_MEASUREMENT_GROUPS: readonly { value: ScopeMeasurementGroup; label: string }[] =
  Object.freeze([
    { value: 'Vertical', label: 'Vertical' },
    { value: 'Horizontal', label: 'Horizontal' },
    { value: 'Miscellaneous', label: 'Miscellaneous' },
    { value: 'Delay', label: 'Channel delay' },
  ])

/**
 * What a reading is in — which decides both how it is written and whether the math trace's own unit
 * applies to it.
 *
 * <p><code>level</code> is the one that moves: for a captured channel it is volts, and for A × B,
 * A ÷ B, a derivative or an integral it is that operation's own unit. Everything else means the
 * same thing whatever trace it was taken from, which is why a time is a time on the math channel
 * too.</p>
 */
export type ScopeMeasurementQuantity =
  | 'level'
  | 'time'
  | 'frequency'
  | 'percent'
  | 'ratio'
  | 'angle'
  | 'area'

export interface ScopeMeasurementDef {
  /** Stored in <code>params_json</code>, so it never changes once shipped. */
  id: string
  /** What the row and the picker call it — a scope's own short form. */
  label: string
  group: ScopeMeasurementGroup
  /** Which glyph <code>ScopeMeasurementIcon</code> draws for it. */
  icon: string
  /** What it means, for the picker's and the row's tooltip. */
  title: string
  quantity: ScopeMeasurementQuantity
  /** One channel's (or the math trace's) own reading set. Absent on the pair group. */
  read?: (m: ScopeSliceMeasurements) => number | null | undefined
  /**
   * The two channels taken together, from the X–Y figure.
   *
   * <p>Given the left channel's measured frequency as well, because the one reading that is not
   * already on the figure — the skew — is the phase expressed as a time and needs it.</p>
   */
  readPair?: (xy: ScopeXySlice, frequencyHz: number | null | undefined) => number | null
}

/**
 * Every measurement, in the order each tab offers them.
 *
 * <p><b>What is deliberately not here.</b> A bench scope's own list carries several this app would
 * have to guess at or could not honestly answer, and each omission is a decision rather than a gap.
 * <em>Median</em> and <em>Cycle median</em> want a sort of up to a quarter of a million samples per
 * channel per fetch for a figure nobody in audio quotes. <em>FOV/FPRE/ROV/RPRE</em> split the
 * overshoot and preshoot by edge, where the pair here is measured over the whole record — the same
 * question answered once rather than twice, for very little more. <em>Tsu/Th</em> and the
 * <em>FRFR</em> family are logic-timing readings between two digital edges, which is not what this
 * instrument is pointed at. And <em>L@T</em> and <em>Delay</em> take a time or a level as a
 * parameter, which this screen already has a better answer for: the cursors.</p>
 */
export const SCOPE_MEASUREMENTS: readonly ScopeMeasurementDef[] = Object.freeze([
  // ---- Vertical ----
  {
    id: 'max', label: 'Max', group: 'Vertical', icon: 'max', quantity: 'level',
    title: 'The highest sample in the window — exact even on a decimated slice, because both extremes of every column are kept.',
    read: (m) => m.maxV,
  },
  {
    id: 'min', label: 'Min', group: 'Vertical', icon: 'min', quantity: 'level',
    title: 'The lowest sample in the window.',
    read: (m) => m.minV,
  },
  {
    id: 'pkpk', label: 'Pk-Pk', group: 'Vertical', icon: 'pkpk', quantity: 'level',
    title: 'The maximum less the minimum.',
    read: (m) => m.peakToPeakV,
  },
  {
    id: 'top', label: 'Top', group: 'Vertical', icon: 'top', quantity: 'level',
    title: 'The settled high level, from a histogram of the samples rather than from the maximum — so an overshoot does not move it. A sine has no flat top and this comes out at its peak.',
    read: (m) => m.topV,
  },
  {
    id: 'base', label: 'Base', group: 'Vertical', icon: 'base', quantity: 'level',
    title: 'The settled low level.',
    read: (m) => m.baseV,
  },
  {
    id: 'amplitude', label: 'Amplitude', group: 'Vertical', icon: 'amplitude', quantity: 'level',
    title: 'Top less base — what the edge thresholds and the overshoot are measured against.',
    read: (m) => m.amplitudeV,
  },
  {
    id: 'mean', label: 'Mean', group: 'Vertical', icon: 'mean', quantity: 'level',
    title: "The average of the window's samples. Both this analyzer and a sound card are AC-coupled, so on a quiet input this is the input's own residual offset and not the DUT's DC.",
    read: (m) => m.meanV,
  },
  {
    id: 'cycleMean', label: 'Cycle mean', group: 'Vertical', icon: 'cycleMean', quantity: 'level',
    title: 'The mean over one period rather than over the window. The mean of a sine over a whole cycle is zero; over the 10.3 cycles a timebase happens to show, it is whatever the 0.3 contributes.',
    read: (m) => m.cycleMeanV,
  },
  {
    id: 'rms', label: 'RMS', group: 'Vertical', icon: 'rms', quantity: 'level',
    title: 'True RMS of the window, DC included.',
    read: (m) => m.rmsV,
  },
  {
    id: 'cycleRms', label: 'Cycle RMS', group: 'Vertical', icon: 'cycleRms', quantity: 'level',
    title: 'True RMS over one period. The figure to quote for a periodic signal: a window holding a part-cycle biases the whole-window RMS by up to 0.4 % at ten cycles in view and 3.6 % at one, and nothing on screen would say so.',
    read: (m) => m.cycleRmsV,
  },
  {
    id: 'acRms', label: 'AC RMS', group: 'Vertical', icon: 'acRms', quantity: 'level',
    title: 'RMS about the mean — the figure to quote for a signal riding on an offset. It is also the standard deviation of the samples.',
    read: (m) => m.acRmsV,
  },
  {
    id: 'cycleStdev', label: 'Cycle stdev', group: 'Vertical', icon: 'cycleStdev', quantity: 'level',
    title: "RMS about one cycle's own mean — the AC RMS of a single period.",
    read: (m) => m.cycleStdevV,
  },
  {
    id: 'crest', label: 'Crest', group: 'Vertical', icon: 'crest', quantity: 'ratio',
    title: 'Peak over true RMS. 1.41 for a sine, 1.00 for a square. Absent on digital silence, where it is infinite.',
    read: (m) => m.crestFactor,
  },
  {
    id: 'overshoot', label: 'Overshoot', group: 'Vertical', icon: 'overshoot', quantity: 'percent',
    title: 'How far past the top the trace goes, as a percentage of the amplitude.',
    read: (m) => m.overshootPercent,
  },
  {
    id: 'preshoot', label: 'Preshoot', group: 'Vertical', icon: 'preshoot', quantity: 'percent',
    title: 'The same below the base.',
    read: (m) => m.preshootPercent,
  },

  // ---- Horizontal ----
  {
    id: 'freq', label: 'Freq', group: 'Horizontal', icon: 'freq', quantity: 'frequency',
    title: 'From one period of the trace, measured between successive rising crossings of the middle.',
    read: (m) => m.frequencyHz,
  },
  {
    id: 'period', label: 'Period', group: 'Horizontal', icon: 'period', quantity: 'time',
    title: 'One cycle, between successive rising crossings of the middle.',
    read: (m) => m.periodS,
  },
  {
    id: 'posWidth', label: '+Width', group: 'Horizontal', icon: 'posWidth', quantity: 'time',
    title: 'How long the waveform spends above the middle in one cycle.',
    read: (m) => m.positiveWidthS,
  },
  {
    id: 'negWidth', label: '−Width', group: 'Horizontal', icon: 'negWidth', quantity: 'time',
    title: 'How long it spends below the middle in one cycle — the other half of the period.',
    read: (m) => m.negativeWidthS,
  },
  {
    id: 'posDuty', label: '+Duty', group: 'Horizontal', icon: 'posDuty', quantity: 'percent',
    title: 'How much of the period the waveform spends above the middle.',
    read: (m) => m.dutyPercent,
  },
  {
    id: 'negDuty', label: '−Duty', group: 'Horizontal', icon: 'negDuty', quantity: 'percent',
    title: 'And below it. Measured from the negative width rather than written as 100 − duty, so it is a second reading of the cycle instead of a restatement of the first.',
    read: (m) => m.negativeDutyPercent,
  },
  {
    id: 'rise', label: 'Rise', group: 'Horizontal', icon: 'rise', quantity: 'time',
    title: '10 % to 90 % of the first clean rising edge. Nothing faster than about 4 µs is the DUT rather than this analyzer.',
    read: (m) => m.riseTimeS,
  },
  {
    id: 'fall', label: 'Fall', group: 'Horizontal', icon: 'fall', quantity: 'time',
    title: '90 % to 10 % of the first clean falling edge.',
    read: (m) => m.fallTimeS,
  },
  {
    id: 'timeAtMax', label: 'Time@max', group: 'Horizontal', icon: 'timeAtMax', quantity: 'time',
    title: 'When the maximum fell, on the chart’s own axis — negative anywhere in the pre-trigger. The first such sample, so a clipped flat top says where the clipping began.',
    read: (m) => m.timeAtMaxS,
  },
  {
    id: 'timeAtMin', label: 'Time@min', group: 'Horizontal', icon: 'timeAtMin', quantity: 'time',
    title: 'The same for the minimum.',
    read: (m) => m.timeAtMinS,
  },

  // ---- Miscellaneous ----
  {
    id: 'area', label: 'Area', group: 'Miscellaneous', icon: 'area', quantity: 'area',
    title: 'The integral of the window, in volt-seconds — the mean times how long the window is.',
    read: (m) => m.areaVs,
  },
  {
    id: 'cycleArea', label: 'Cycle area', group: 'Miscellaneous', icon: 'cycleArea', quantity: 'area',
    title: 'The integral over one period. Zero for anything symmetric about its own mean, so what it reads is the net offset a cycle carries.',
    read: (m) => m.cycleAreaVs,
  },

  // ---- Channel delay: the two inputs taken together ----
  {
    id: 'phase', label: 'Phase', group: 'Delay', icon: 'phase', quantity: 'angle',
    title: 'How far the right input lags the left, from the argument of each channel’s own single-bin transform. Absent where the two disagree about their frequency, where either is silent, or where one number is not this figure’s phase.',
    readPair: (xy) => xy.phaseDeg,
  },
  {
    id: 'skew', label: 'Skew', group: 'Delay', icon: 'skew', quantity: 'time',
    title: 'That same lag written as a time. It is the phase divided by the frequency rather than a second measurement, so it is defined exactly where the phase is and carries the same ambiguity beyond half a period.',
    readPair: (xy, frequencyHz) =>
      xy.phaseDeg !== null && frequencyHz != null && Number.isFinite(frequencyHz) && frequencyHz > 0
        ? xy.phaseDeg / 360 / frequencyHz
        : null,
  },
  {
    id: 'correlation', label: 'Correlation', group: 'Delay', icon: 'correlation', quantity: 'ratio',
    title: "Pearson's r over the pair, about each channel's own mean. +1 is the same signal, 0 unrelated, −1 inverted. The one figure here that is always meaningful.",
    readPair: (xy) => xy.correlation,
  },
  {
    id: 'amplitudeRatio', label: 'R/L ratio', group: 'Delay', icon: 'ratio', quantity: 'ratio',
    title: "The right input's AC RMS over the left's.",
    readPair: (xy) => xy.amplitudeRatio,
  },
])

const BY_ID = new Map(SCOPE_MEASUREMENTS.map((d) => [d.id, d]))

export function scopeMeasurement(id: string): ScopeMeasurementDef | null {
  return BY_ID.get(id) ?? null
}

/** The entries of one tab, in catalogue order. */
export function scopeMeasurementsIn(group: ScopeMeasurementGroup): ScopeMeasurementDef[] {
  return SCOPE_MEASUREMENTS.filter((d) => d.group === group)
}

/**
 * A stored selection, kept to the ids this build knows and to one of each.
 *
 * <p>An unknown id is dropped rather than carried: a measurement from a newer build has nothing
 * here that can read it, and a row that is always blank reads as a fault. The <b>catalogue's</b>
 * order is imposed rather than the stored one, so the table reads the same way whichever order they
 * were ticked in — and so a selection saved by a build with a different order opens looking
 * right.</p>
 */
/**
 * The sources a measurement can be taken off, in the order the picker offers them.
 *
 * <p>Fixed rather than read from the record, because a <em>selection</em> outlives any one
 * capture: a measurement chosen for the right input must still be chosen when the next run has
 * only the left one, or taking a channel off the screen would silently forget what was asked for.
 * Which of these actually produce a row is decided per record, by what it carries — see
 * <code>scopeMeasurementSources</code>.</p>
 */
export const SCOPE_SELECTION_SOURCES: readonly string[] = Object.freeze([
  'ch0',
  'ch1',
  'math1',
  'math2',
])

/**
 * What a retired source name reads as now.
 *
 * <p>There was one math channel until 2026-11-20 and its source was spelled <code>math</code>;
 * there are two, spelled <code>math1</code> and <code>math2</code>. Without this,
 * <code>parseScopeMeasurementIds</code> would do what it does with any unknown source — drop it —
 * and every stored selection would silently lose its math rows, which is a setting disappearing
 * rather than changing.</p>
 */
const RETIRED_SOURCES: Readonly<Record<string, string>> = Object.freeze({ math: 'math1' })

/** The catalogue id of a stored selection entry, which may carry a source after a colon. */
export function scopeSelectionId(entry: string): string {
  const colon = entry.indexOf(':')
  return colon < 0 ? entry : entry.slice(0, colon)
}

/**
 * Which source a stored selection entry names, or null where it names none.
 *
 * <p><b>Null is "every source", and that is what makes this change free of a migration.</b> Every
 * selection stored before the picker had a Source control is a bare id, and a bare id meant "shown
 * for every input the chart is drawing" — so reading it as every source reproduces exactly what it
 * did. New writes are explicit; the stored shape converges on its own.</p>
 */
export function scopeSelectionSource(entry: string): string | null {
  const colon = entry.indexOf(':')
  return colon < 0 ? null : entry.slice(colon + 1)
}

/** Builds a selection entry. A null source is a bare id, i.e. every source. */
export function scopeSelectionEntry(id: string, source: string | null): string {
  return source === null ? id : `${id}:${source}`
}

/**
 * The sources one stored entry stands for: the one it names, or all of them for a bare id.
 *
 * <p>A <b>Channel delay</b> entry stands for none — it reads the two inputs together and has one
 * row of its own, so a source on it would mean nothing.</p>
 */
export function scopeSelectionSources(entry: string): readonly string[] {
  const def = BY_ID.get(scopeSelectionId(entry))
  if (!def || def.group === 'Delay') return []
  const named = scopeSelectionSource(entry)
  return named === null ? SCOPE_SELECTION_SOURCES : [named]
}

/**
 * Reads a stored selection, keeping only what this build knows and imposing the catalogue's order.
 *
 * <p>Entries are <code>id</code> or <code>id:source</code>, the same <code>id:source</code>
 * spelling a row key and <code>chartTrendRow</code> already use. Ordered by the catalogue and then
 * by source, so the table reads the same way whichever order the tiles were pressed in; deduped by
 * the whole entry, so one measurement may legitimately appear once per source.</p>
 */
export function parseScopeMeasurementIds(value: unknown): string[] | null {
  if (!Array.isArray(value)) return null

  const wanted = new Set(
    value
      .filter((v): v is string => typeof v === 'string')
      // A source this build has renamed is read as its new name rather than dropped; see
      // RETIRED_SOURCES. The id and the source are split and rejoined so a measurement id that
      // happened to contain a colon could not be mangled by a bare string replace.
      .map((entry) => {
        const source = scopeSelectionSource(entry)
        const renamed = source === null ? null : RETIRED_SOURCES[source]
        return renamed ? scopeSelectionEntry(scopeSelectionId(entry), renamed) : entry
      }),
  )
  const out: string[] = []
  for (const def of SCOPE_MEASUREMENTS) {
    // The bare id first: it is the broader claim, and a selection holding both is a reader who
    // asked for everything and then for one source again.
    if (wanted.has(def.id)) out.push(def.id)
    if (def.group === 'Delay') continue
    for (const source of SCOPE_SELECTION_SOURCES) {
      const entry = scopeSelectionEntry(def.id, source)
      if (wanted.has(entry)) out.push(entry)
    }
  }

  return out
}

/**
 * Takes one <em>row</em> off a stored selection, by its row key.
 *
 * <p><b>A row, not a measurement</b>, which is the whole difference the Source control makes: the
 * table's cross used to drop a measurement and every source of it at once, which was right while
 * the sources were implied and wrong the moment they are chosen.</p>
 *
 * <p>Removing one source of a <b>bare</b> entry expands it into the others, because a bare id is
 * shorthand for all of them and there is no shorter way to say "all but this one". A
 * <b>Channel delay</b> row has no source, so it takes its whole entry with it.</p>
 */
export function removeScopeSelectionRow(entries: readonly string[], rowKey: string): string[] {
  const id = scopeSelectionId(rowKey)
  const source = scopeSelectionSource(rowKey)
  const out: string[] = []

  for (const entry of entries) {
    if (scopeSelectionId(entry) !== id) {
      out.push(entry)
      continue
    }

    const def = BY_ID.get(id)
    // No source to take off: the pair row is the measurement.
    if (!def || def.group === 'Delay' || source === null) continue

    const named = scopeSelectionSource(entry)
    if (named === null) {
      for (const other of SCOPE_SELECTION_SOURCES) {
        if (other !== source) out.push(scopeSelectionEntry(id, other))
      }
      continue
    }
    if (named !== source) out.push(entry)
  }

  return out
}

/**
 * What a chosen measurement with no reading shows.
 *
 * <p>A dash and not an empty cell, which is the opposite of the rail's rule — and the difference is
 * exactly that here somebody asked for it. In a list nobody chose, an absent reading is left out;
 * in a fixed set of rows a reader picked, a blank reads as something that failed to arrive where a
 * dash reads as a waveform that does not have this reading.</p>
 */
export const SCOPE_MEASUREMENT_ABSENT = '—'

/** What a fresh oscilloscope measurement watches: the three a scope is read for at a glance. */
export const DEFAULT_SCOPE_MEASUREMENTS: readonly string[] = Object.freeze(['pkpk', 'rms', 'freq'])

/** Whether any chosen measurement needs the two channels taken together — i.e. the X–Y figure. */
export function scopeMeasurementsNeedPair(entries: readonly string[]): boolean {
  return entries.some((entry) => BY_ID.get(scopeSelectionId(entry))?.group === 'Delay')
}
