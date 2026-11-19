/**
 * Autoset: one press that makes an unknown signal readable and standing still.
 *
 * <p><b>What it is, against Autofit, which is already on this screen.</b> Autofit frames the record
 * — the whole of it across, and the volts window around what was captured. That is the right answer
 * for looking at a capture and the wrong one for looking at a <em>waveform</em>: a 20 ms record of
 * a 1 kHz tone drawn whole is twenty cycles in a few hundred pixels, where nothing about the shape
 * of one cycle can be read. Autoset answers the other question — show me a few cycles of this,
 * standing still — which needs the three things a bench scope's own Autoset sets: the vertical from
 * the measured extremes, the timebase from the measured <em>frequency</em>, and the trigger level
 * at the middle of the signal so the trace stops moving.</p>
 *
 * <p><b>Here rather than in a component</b> for this project's usual reason — nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in — and because what it
 * writes crosses two panels: the vertical and the window belong to the display rail, the trigger
 * level to the settings rail, and the arithmetic belongs to neither.</p>
 */

import { fitVoltWindow, roundVoltsForWindow } from './voltAxis'
import { fiftyPercentLevel } from './triggerSpec'

/** How many periods of the measured signal Autoset puts on screen. */
export const AUTOSET_PERIODS = 3

/**
 * Where the trigger sits in the window Autoset chooses.
 *
 * <p>A quarter in, matching this app's own default pre-trigger: what is wanted is to see the signal
 * arrive, and a trigger at the left edge leaves nothing before it to compare the arrival against.
 * It is a <em>fraction of the window</em> and not of the record, so moving the window does not move
 * what the pre-trigger fraction means.</p>
 */
export const AUTOSET_TRIGGER_FRACTION = 0.25

/** What one press writes. Any of the three may be absent where the record cannot say. */
export interface ScopeAutoset {
  voltsPerDivision: number
  offsetV: number
  fromS: number
  toS: number
  /**
   * The midpoint of the measured extremes — the 50 % button's own rule, through the same function,
   * so the two cannot come to disagree about what the middle of a signal is.
   *
   * <p>Never null: the extremes are checked before anything here runs, and a record with one level
   * has a midpoint like any other. A trigger set on a flat record's own level crosses nothing,
   * which is the honest answer rather than a reason to leave the control alone.</p>
   *
   * <p><b>Rounded against the span it was taken from</b>, as the 50 % button's own write is: the
   * midpoint of two measured extremes carries every digit the arithmetic produced, and
   * <code>0.0052837500000000315</code> in the box is a reading of the noise floor rather than a
   * level anybody set.</p>
   */
  triggerLevelV: number
  /** Null where no frequency was measured: the window is then the record, and this says so. */
  periodS: number | null
}

/** What Autoset reads: the measured extremes and frequency of the trace it is setting up for. */
export interface ScopeAutosetInput {
  maxV: number
  minV: number
  /** The measured frequency of the triggered channel, or null. */
  frequencyHz: number | null | undefined
  /** The record's own span, which the window is clamped into. */
  recordFromS: number
  recordToS: number
}

/**
 * Works out the three settings.
 *
 * <p><b>The window is clamped into the record rather than allowed past it</b>, and that is the one
 * rule here that is quietly wrong by default: three periods of 20 Hz is 150 ms, which a 20 ms
 * record does not contain, and a window reaching past the capture draws a trace that stops in the
 * middle of the plot with empty graticule either side. Clamped, a record too short for three
 * periods simply shows what it has — which is the honest answer, and the capture length is the
 * control that fixes it.</p>
 *
 * <p>Returns null only where the extremes are not numbers, which is a record that measured
 * nothing.</p>
 */
export function scopeAutoset(input: ScopeAutosetInput): ScopeAutoset | null {
  if (!Number.isFinite(input.maxV) || !Number.isFinite(input.minV)) return null

  const fit = fitVoltWindow(input.maxV, input.minV)

  const hz = input.frequencyHz
  const periodS = hz !== null && hz !== undefined && Number.isFinite(hz) && hz > 0 ? 1 / hz : null

  const recordSpan = input.recordToS - input.recordFromS
  let fromS = input.recordFromS
  let toS = input.recordToS

  if (periodS !== null && recordSpan > 0) {
    const span = Math.min(periodS * AUTOSET_PERIODS, recordSpan)
    // Hung off zero — the trigger — rather than off the record's own start: that is where the
    // waveform is aligned, and a window placed anywhere else would move whenever the pre-trigger
    // fraction did.
    fromS = -span * AUTOSET_TRIGGER_FRACTION
    toS = fromS + span

    // Slid rather than squeezed where that falls outside the record: the number of periods on
    // screen is the whole point of the figure, so keeping the span and moving it is right where
    // shortening it would quietly show two cycles when three were asked for.
    if (fromS < input.recordFromS) {
      fromS = input.recordFromS
      toS = fromS + span
    }
    if (toS > input.recordToS) {
      toS = input.recordToS
      fromS = Math.max(input.recordFromS, toS - span)
    }
  }

  return {
    voltsPerDivision: fit.voltsPerDivision,
    offsetV: fit.offsetV,
    fromS,
    toS,
    triggerLevelV: roundVoltsForWindow(
      fiftyPercentLevel(input.maxV, input.minV) as number,
      Math.abs(input.maxV - input.minV),
    ),
    periodS,
  }
}

/** What the button says it did, for the line under it. */
export function autosetNote(result: ScopeAutoset | null): string | null {
  if (!result) return null
  if (result.periodS === null)
    return 'No frequency measured, so the window is the whole record — the vertical and the trigger level are set.'

  const cycles = (result.toS - result.fromS) / result.periodS
  return `${cycles.toFixed(1)} cycles on screen, with the trigger level at the middle of the signal.`
}
