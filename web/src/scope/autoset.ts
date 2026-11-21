/**
 * Autoset: one press that makes an unknown signal readable and standing still.
 *
 * <p><b>What it is, against Autofit, which is already on this screen.</b> Autofit frames whatever
 * was captured — the whole record across, the volts window around what is in it. That is the right
 * answer for looking at a capture and the wrong one for looking at a <em>waveform</em>: a 20 ms
 * record of a 1 kHz tone drawn whole is twenty cycles in a few hundred pixels, where nothing about
 * the shape of one can be read. Autoset answers the other question — show me a few cycles of this,
 * standing still — which needs the three things a bench scope's own Autoset sets: the vertical from
 * the measured extremes, the <b>timebase</b> from the measured frequency, and the trigger level at
 * the middle of the signal.</p>
 *
 * <p><b>It sets the timebase and not a window, which is the whole of what it does horizontally</b>
 * (asked for 2026-11-21). It used to do both: the record was left as it was and a window of three
 * periods was chosen inside it, which could only ever work where the record already held them —
 * three periods of 20 Hz is 150 ms, and a 20 ms capture does not contain them at any zoom. Setting
 * the record to three periods and then showing <em>all</em> of it says the same thing with one
 * control instead of two, and leaves the zoom bar covering the whole strip rather than a box the
 * press put somewhere. The window the caller writes afterwards is therefore the record's own span,
 * which is why none of it is computed here: the record's geometry needs the pre-trigger fraction,
 * which belongs to the panel rather than to this arithmetic.</p>
 *
 * <p><b>What it did is not reported anywhere, deliberately</b> (Joost, 2026-11-21). There was a
 * line under the button saying how many cycles it had landed on; it appeared after the first
 * press and stayed for the rest of the session, which cost two rows of the rail to describe a
 * press whose whole result is already on screen — in the timebase, the vertical and the trigger
 * level it wrote, and in the trace those three produce.</p>
 *
 * <p><b>Here rather than in a component</b> for this project's usual reason — nothing in a
 * <code>.vue</code> is testable under the node environment vitest runs in — and because what it
 * writes crosses two panels: the vertical and the timebase belong to the display and acquisition
 * rails, the trigger level to the settings rail, and the arithmetic belongs to none of them.</p>
 */

import { fitVoltWindow, roundVoltsForWindow } from './voltAxis'
import { fiftyPercentLevel } from './triggerSpec'
import { snapTimePerDivision, timePerDivisionFor } from './timeAxis'

/** How many periods of the measured signal Autoset asks the record to hold. */
export const AUTOSET_PERIODS = 3

/** What one press writes. The timebase is absent where the record cannot say. */
export interface ScopeAutoset {
  voltsPerDivision: number
  offsetV: number
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
  /** Null where no frequency was measured, which is what leaves the record alone. */
  periodS: number | null
  /**
   * The timebase to capture at — three periods over the ten divisions, snapped to a rung.
   *
   * <p><b>Null where there is no frequency to work from</b>, in which case the record is left as it
   * is and only the vertical and the trigger are set. This is what makes Autoset a horizontal
   * control at all: before it existed the press could only choose a window inside whatever record
   * happened to be set, so a signal slower than the capture could not be shown however it was
   * framed.</p>
   */
  timePerDivisionS: number | null
}

/** What Autoset reads: the measured extremes and frequency of the trace it is setting up for. */
export interface ScopeAutosetInput {
  maxV: number
  minV: number
  /** The measured frequency of the triggered channel, or null. */
  frequencyHz: number | null | undefined
}

/**
 * Works out the three settings.
 *
 * <p>Returns null only where the extremes are not numbers, which is a record that measured
 * nothing.</p>
 */
export function scopeAutoset(input: ScopeAutosetInput): ScopeAutoset | null {
  if (!Number.isFinite(input.maxV) || !Number.isFinite(input.minV)) return null

  const fit = fitVoltWindow(input.maxV, input.minV)

  const hz = input.frequencyHz
  const periodS = hz !== null && hz !== undefined && Number.isFinite(hz) && hz > 0 ? 1 / hz : null

  // Three periods across the whole graticule, on the nearest detent. Null with no frequency, where
  // there is nothing to size a timebase from and the record is left alone.
  const timePerDivisionS =
    periodS !== null ? snapTimePerDivision(timePerDivisionFor(periodS * AUTOSET_PERIODS)) : null

  return {
    voltsPerDivision: fit.voltsPerDivision,
    offsetV: fit.offsetV,
    timePerDivisionS,
    triggerLevelV: roundVoltsForWindow(
      fiftyPercentLevel(input.maxV, input.minV) as number,
      Math.abs(input.maxV - input.minV),
    ),
    periodS,
  }
}
