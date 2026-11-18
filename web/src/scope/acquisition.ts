/**
 * What the oscilloscope does with the records after the first one.
 *
 * <p>Three answers to three different questions. <b>Normal</b> draws each record over the last, and
 * is what a scope does. <b>Average</b> asks what this signal looks like with the noise taken out,
 * and divides the uncorrelated part by the square root of the count. <b>Envelope</b> asks the
 * opposite — how much does it move — and draws the band every sample position has swept.</p>
 *
 * <p>The arithmetic is <code>Tonebench.Dsp.RecordAccumulator</code>'s and runs on the analyzer, over
 * whole records at full resolution. What lives here is the wording and the two bounds, which is the
 * half nothing in a <code>.vue</code> could be tested on.</p>
 */

export type ScopeAccumulation = 'Normal' | 'Average' | 'Envelope'

export const SCOPE_ACCUMULATIONS: readonly { value: ScopeAccumulation; label: string }[] = Object.freeze([
  { value: 'Normal', label: 'Normal' },
  { value: 'Average', label: 'Average' },
  { value: 'Envelope', label: 'Envelope' },
])

/** The tooltip each one gets. The difference between Average and Envelope is the one worth stating. */
export const SCOPE_ACCUMULATION_HINTS: Readonly<Record<ScopeAccumulation, string>> = Object.freeze({
  Normal: 'Each record replaces the one before it.',
  Average:
    'The mean of the last N records, sample by sample — the noise falls as √N while the signal stays. It needs a trigger that is standing still: averaging records that are not aligned low-passes the signal itself.',
  Envelope:
    'The band every sample position has swept since the run started — how much the waveform moves rather than what it looks like. A signal that repeats exactly draws as a line.',
})

/** `RecordAccumulator.MinCount`, mirrored so the box cannot offer a figure the analyzer refuses. */
export const MIN_SCOPE_AVERAGE_COUNT = 2

/** `RecordAccumulator.MaxCount`. 256 is 24 dB of noise floor under averaging, and four seconds of 20 ms records. */
export const MAX_SCOPE_AVERAGE_COUNT = 256

/** The default, which is 12 dB of noise floor and about a third of a second of 20 ms records. */
export const DEFAULT_SCOPE_AVERAGE_COUNT = 16

export function accumulationCombines(mode: ScopeAccumulation): boolean {
  return mode !== 'Normal'
}

/**
 * Whether this run mode produces the repeated records an accumulation needs.
 *
 * <p>Single takes one record and holds it, so there is never a second one to combine with — a
 * screen that sat at "1 of 16" with no way to advance it is worse than the refusal. And Roll has no
 * record boundary at all: its windows overlap by all but one roll step, so averaging them would
 * low-pass the signal and an envelope of them would be the same band drawn N times. Mirrors
 * <code>OscilloscopeRequest.Validate</code>, so the control is disabled rather than the run
 * refused.</p>
 */
export function modeCanAccumulate(mode: string): boolean {
  return mode === 'Auto' || mode === 'Normal'
}

/** Why the control is disabled, in the reader's own terms, or null when it is not. */
export function accumulationUnavailableNote(mode: string): string | null {
  if (modeCanAccumulate(mode)) return null
  if (mode === 'Single') return 'Single holds one record, so there is nothing to combine it with. Use Auto or Normal.'
  return 'Roll has no record boundary — its windows overlap — so there is nothing to combine. Use Auto or Normal.'
}

/**
 * How far the accumulation has got, as a sentence, or null where there is nothing to say.
 *
 * <p><b>Counted up rather than shown as a fraction of a bar</b>, because the figure a reader wants
 * is how many records are in the trace: that is what says whether the noise floor on screen is the
 * one the count promises, and it is the only way to tell "still filling" from "settled".</p>
 */
export function accumulationProgress(
  mode: ScopeAccumulation,
  taken: number,
  count: number,
): string | null {
  if (!accumulationCombines(mode) || count <= 1) return null
  if (taken >= count) return mode === 'Average' ? `Averaged over ${count} records` : `Envelope of ${taken} records`
  return mode === 'Average' ? `Averaging ${taken} of ${count}` : `Envelope of ${taken} records`
}

/**
 * The badge the chart carries while an accumulation is on, or null.
 *
 * <p>A badge rather than a line, for the spliced flag's reason: a note that comes and goes above
 * the plot takes its height off the canvas every time it appears, and an envelope that has not
 * settled yet changes on every record.</p>
 */
export function accumulationBadge(
  mode: ScopeAccumulation,
  taken: number,
  count: number,
): { label: string; detail: string } | null {
  if (!accumulationCombines(mode)) return null

  if (mode === 'Envelope') {
    return {
      label: `Envelope ×${taken}`,
      detail:
        'This is the band every sample position has swept over the records so far, not a waveform — so the readings are its own extremes and nothing else, and the math channel, the X–Y figure and the eye are all absent. Clear to start the band again.',
    }
  }

  return {
    label: taken >= count ? `Averaged ×${count}` : `Averaging ${taken}/${count}`,
    detail:
      'The mean of the records so far, sample by sample. The noise falls as the square root of the count; the signal only stays put if the trigger does, so a jittery trigger buys a smaller waveform rather than a quieter one.',
  }
}

/** Clamps a typed count to what the analyzer will accept. */
export function clampAverageCount(count: number): number {
  if (!Number.isFinite(count)) return DEFAULT_SCOPE_AVERAGE_COUNT
  return Math.min(MAX_SCOPE_AVERAGE_COUNT, Math.max(MIN_SCOPE_AVERAGE_COUNT, Math.round(count)))
}

/**
 * How long an accumulation takes to settle, in seconds, or null where it does not apply.
 *
 * <p>Worth saying before the run rather than after: 256 records of the one-second capture this app
 * allows is over four minutes, and a display nobody waits for is a display nobody believes. The
 * figure is the record plus whatever the arming costs, times the count — the same arithmetic the
 * panel's own capture estimate uses.</p>
 */
export function accumulationSettleSeconds(
  mode: ScopeAccumulation,
  count: number,
  captureSeconds: number,
): number | null {
  if (!accumulationCombines(mode) || !(captureSeconds > 0)) return null
  return captureSeconds * clampAverageCount(count)
}
