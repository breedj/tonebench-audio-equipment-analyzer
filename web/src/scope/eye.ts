import type { ScopeEyeSlice } from '../api/client'
import type { ScopeReading } from './readings'
import { formatSeconds } from './readings'
import { formatSiValue } from './math'
import { formatVolts } from './voltAxis'

/**
 * The oscilloscope's eye view — the window folded on the signal's own period, every cycle drawn on
 * top of every other.
 *
 * <p><b>What the picture is for, and what it is not.</b> A comms eye diagram folds a record on the
 * symbol clock and reads the opening a receiver has to decide in. There is no symbol clock on an
 * audio analyzer, so this folds on the frequency the window measured, and what it shows is
 * <em>repeatability</em>: a signal that repeats exactly folds into one line, and anything that
 * moves it from cycle to cycle spreads that line into a band. Amplitude modulation, a rail sagging
 * under a burst, a drifting generator, a supply ripple at a frequency unrelated to the tone, a
 * crossover that behaves differently on alternate cycles — each of those is a band of its own
 * shape, and each reads as "a slightly fuzzy trace" on an ordinary time axis.</p>
 *
 * <p>The folding and the two readings are computed on the analyzer, over the samples at full
 * resolution; this module holds the wording and the window arithmetic, which is the half nothing in
 * a <code>.vue</code> could be tested on.</p>
 */

/** The width the overlay's own traces are stroked at, and how transparent each one is. */
export const EYE_TRACE_ALPHA = 0.3

/**
 * How many divisions the eye's volts axis spans — the waveform view's own count, so a trace and an
 * eye of the same signal fill the screen the same way and the V/div knob means one thing.
 */
export const EYE_DIVISIONS = 8

export function eyeVoltWindow(voltsPerDivision: number, offsetV: number): { min: number; max: number } {
  const half = Math.abs(voltsPerDivision) * (EYE_DIVISIONS / 2) || 1
  return { min: -half - offsetV, max: half - offsetV }
}

/**
 * Whether the view draws an eye at all.
 *
 * <p>Its own function rather than <code>view === 'Eye'</code> at each call site, matching
 * <code>scopeViewIsXy</code>: half a dozen places ask, and a string compared by hand in one of them
 * is how a view comes to half work.</p>
 */
export function scopeViewIsEye(view: string): boolean {
  return view === 'Eye'
}

/**
 * Why there is no eye to draw, in the reader's own terms, or null when there is one.
 *
 * <p><b>A sentence rather than an empty chart</b>, because every one of these is a thing the reader
 * can do something about, and an eye that is simply absent reads as a broken view. The order is the
 * order they are worth checking in.</p>
 */
export function eyeAbsenceNote(
  eye: ScopeEyeSlice | null | undefined,
  hasRecord: boolean,
  measuredFrequencyHz: number | null | undefined,
): string | null {
  if (eye) return null
  if (!hasRecord) return 'Capture a record and this folds it on its own period.'
  if (measuredFrequencyHz == null)
    return 'No frequency was measured in this window, so there is no period to fold on. Widen the window until the Measurements panel reads one.'
  return 'The window holds fewer than two whole periods, so there is nothing to overlay. Widen it.'
}

/**
 * Whether the overlay is the first of the window's cycles rather than all of them.
 *
 * <p>Derived here rather than carried on the wire, as the X–Y figure's is: two counts that always
 * answer it, and a third field could disagree with them.</p>
 */
export function eyeTruncated(eye: ScopeEyeSlice): boolean {
  return eye.foldCount < eye.windowFoldCount
}

/** What the header says about the overlay on screen. */
/**
 * A jitter, which is the one time on this screen that routinely lives below a microsecond.
 *
 * <p><code>formatSeconds</code> is the chart's own axis formatter and bottoms out there: it writes
 * 110 ns as <code>0.110 us</code> and 800 ps as <code>0.001 us</code>, which is the right scale for
 * a window and a cursor and useless for this. The SI formatter carries the prefix down instead, so
 * the figure reads in the unit it is in — and it is the same one the measurement table's own
 * statistics go through, for the reason that file records about exactly this number.</p>
 *
 * <p>Its sibling readings keep <code>formatSeconds</code> on purpose: <b>Where</b> is a position in
 * the fold, read against the picture's own axis, so it belongs in that axis's unit.</p>
 */
function formatJitter(seconds: number): string {
  return formatSiValue(seconds, 's')
}

export function eyeNote(eye: ScopeEyeSlice): string {
  const parts = [
    `${eye.foldCount.toLocaleString()} ${eye.foldCount === 1 ? 'cycle' : 'cycles'}`,
    `${formatFrequency(eye.periodHz)} fold`,
  ]
  // Deliberately the picture's *geometry* and nothing measured: how many cycles are folded and on
  // what. The readings are a row of their own under the canvas, so each can carry the tooltip that
  // says what it means -- which is what `eyeReadings` was written for and what a header cannot do.
  if (eyeTruncated(eye)) parts.push(`of ${eye.windowFoldCount.toLocaleString()} in the window`)
  return parts.join(' · ')
}

/**
 * Said only when the window held more cycles than the overlay draws.
 *
 * <p>The cap is on the first cycles of the window rather than on every n-th, for the X–Y figure's
 * reason: an intermittent fault one cycle in three is exactly what someone turns this picture on to
 * find, and striding the folds is how it would be hidden.</p>
 */
export function eyeTruncationNote(eye: ScopeEyeSlice): string | null {
  if (!eyeTruncated(eye)) return null
  return `The first ${eye.foldCount.toLocaleString()} cycles of the ${eye.windowFoldCount.toLocaleString()} in this window. Narrow the window to move the overlay along the record.`
}

/**
 * The readings under the picture.
 *
 * <p>All three are of the <em>overlay</em> rather than of the record: the spread is how much one
 * phase of the waveform moved from cycle to cycle, not the waveform's own amplitude, and the jitter
 * is of the folds this picture drew. <code>Where</code> is worth as much as the spread itself — a
 * spread at the peaks is amplitude, one at a crossing is timing — which is why it is a row and not
 * a parenthesis.</p>
 */
export function eyeReadings(eye: ScopeEyeSlice | null | undefined): ScopeReading[] {
  if (!eye) return []

  const readings: ScopeReading[] = [
    {
      label: 'Spread',
      // Its own magnitude as the scale, so a millivolt spread reads in millivolts whatever the
      // V/div knob is set to -- this is a reading rather than an axis label.
      value: formatVolts(eye.maxSpreadV, Math.abs(eye.maxSpreadV) || 1),
      title:
        'The widest the band gets anywhere across the fold, peak to peak — the largest cycle-to-cycle difference in this window. Zero for a waveform that repeats exactly.',
    },
    {
      label: 'Where',
      value: formatSeconds(eye.spreadAtS),
      title:
        'Where in the fold that worst spread sits. At the waveform’s peaks it is amplitude varying; at a crossing it is timing; at one phase only, something is switching.',
    },
    {
      label: 'Mean spread',
      value: formatVolts(eye.meanSpreadV, Math.abs(eye.meanSpreadV) || 1),
      title: 'The band’s average width over the whole fold — the figure to watch, where the worst case is set by one bad cycle.',
    },
  ]

  readings.push({
    label: 'Jitter',
    value: eye.jitterS == null ? '—' : formatJitter(eye.jitterS),
    title:
      eye.jitterS == null
        ? 'Absent: too few of the folds cross their own mean for a spread of crossings to mean anything.'
        : 'How much the folds’ own crossings move, as a standard deviation. Measured between samples, so it reads below the sample interval.',
  })

  return readings
}

/** A frequency in the unit a reader of this panel expects, which is not the chart's own time unit. */
export function formatFrequency(hz: number): string {
  if (!Number.isFinite(hz)) return '—'
  if (Math.abs(hz) >= 1000) return `${(hz / 1000).toFixed(3)} kHz`
  return `${hz.toFixed(2)} Hz`
}
