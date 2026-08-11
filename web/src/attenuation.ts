/**
 * Determines the input-range attenuation needed for a signal to sit within the hardware's range.
 * Ported from the previous WinForms app's QaLibrary.DetermineAttenuation — same 5 dBV THD-measurement
 * headroom, same step ladder.
 * @param dBV The maximum measured level, in dBV.
 * @returns The attenuation in dB — one of the QA403's input-range steps (0/6/12/18/24/30/36/42).
 */
export function determineAttenuation(dBV: number): number {
  const testDbv = dBV + 5 // Extra headroom for a better THD measurement.
  if (testDbv <= 0) return 0
  if (testDbv <= 6) return 6
  if (testDbv <= 12) return 12
  if (testDbv <= 18) return 18
  if (testDbv <= 24) return 24
  if (testDbv <= 30) return 30
  if (testDbv <= 36) return 36
  return 42
}

/** Snaps to the closest value actually offered by this device's input-range list, in case it ever differs from the standard QA403 ladder this function's steps assume. */
export function nearestInputRange(target: number, options: readonly number[]): number {
  if (options.length === 0) return target
  return options.reduce((best, o) => (Math.abs(o - target) < Math.abs(best - target) ? o : best))
}
