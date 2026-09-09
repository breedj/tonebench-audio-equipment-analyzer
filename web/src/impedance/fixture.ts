import type { ImpedanceCalibrationJson, ImpedanceFixtureConfig } from '../api/client'

// Deciding whether a measurement's own calibration is already what the machine has stored globally.
//
// Pure and unit-tested, per the project's rule that nothing inside a .vue file is testable under the
// node environment vitest runs in — and this is the kind of comparison where an "obvious" shortcut
// (same length, same first point) silently answers yes for two genuinely different calibrations.

/** The parts of a measurement that a stored fixture would have to match to make storing it a no-op. */
export interface LocalFixture {
  referenceOhms: number
  openCal: ImpedanceCalibrationJson | null
  shortCal: ImpedanceCalibrationJson | null
}

/**
 * Whether two calibration curves are the same measurement.
 *
 * <p>Compared by value across every point rather than by reference: a curve arrives either freshly
 * parsed out of `params_json` or freshly captured from a run, so it is a different object each time
 * even when it holds identical numbers. 256 points is 768 comparisons, run inside a computed whose
 * dependencies change only when a calibration does — cheap enough that sampling a few points to save
 * time would be trading correctness for nothing.</p>
 */
export function sameCalibration(
  a: ImpedanceCalibrationJson | null | undefined,
  b: ImpedanceCalibrationJson | null | undefined,
): boolean {
  if (a === b) return true
  if (!a || !b) return false
  if (!a.freqHz || !b.freqHz || a.freqHz.length !== b.freqHz.length) return false
  if (a.real?.length !== a.freqHz.length || b.real?.length !== b.freqHz.length) return false
  if (a.imag?.length !== a.freqHz.length || b.imag?.length !== b.freqHz.length) return false

  for (let i = 0; i < a.freqHz.length; i++) {
    if (a.freqHz[i] !== b.freqHz[i] || a.real[i] !== b.real[i] || a.imag[i] !== b.imag[i]) return false
  }

  return true
}

/**
 * Whether the global fixture already holds exactly this measurement's calibration.
 *
 * <p>What it drives: the "Store as global calibration" button is hidden while this is true, because
 * pressing it would write back what is already there. The reference resistor is part of the
 * comparison and not an afterthought — the standards are only valid at the resistance they were
 * captured through, so the same curves stored against a different resistor is a genuinely different
 * fixture and does need storing.</p>
 */
export function matchesStoredFixture(
  local: LocalFixture,
  fixture: ImpedanceFixtureConfig | null | undefined,
): boolean {
  if (!fixture || !fixture.hasCalibration) return false
  if (local.referenceOhms !== fixture.referenceOhms) return false

  return sameCalibration(local.openCal, fixture.openCal) && sameCalibration(local.shortCal, fixture.shortCal)
}
