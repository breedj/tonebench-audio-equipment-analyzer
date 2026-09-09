import type { ImpedanceCalibrationJson } from '../api/client'

// Turning a stored calibration standard back into the impedance it represents.
//
// Extracted from ImpedanceChart.vue once the display panel's autofit needed the same numbers: two
// implementations of this would drift, and the axis would then be fitted to a curve subtly unlike
// the one drawn on it. Pure and unit-tested, per the project's rule that nothing inside a .vue file
// is testable under the node environment vitest runs in.

/**
 * The ceiling a recovered impedance is held at, matching `ImpedanceMath.MaxOhms` on the server.
 *
 * <p>An open standard has H -> 1, which drives the divider inversion's denominator to zero. The
 * impedance really is enormous there, so it is bounded rather than allowed to reach infinity and
 * wreck a logarithmic axis — or, worse, reach `Infinity` and travel into a chart as a value uPlot
 * cannot place.</p>
 */
export const MAX_STANDARD_OHMS = 1e9

/** A standard resolved onto a frequency grid: what it is worth as an impedance, and at what angle. */
export interface StandardCurve {
  /** |Z| in ohms. Null outside the band the standard was captured over. */
  ohms: (number | null)[]
  /**
   * arg Z in degrees, for the chart's phase axis.
   *
   * <p>Null outside the captured band, and null where the divider inversion is degenerate — an open
   * standard's impedance has a magnitude worth bounding but no meaningful angle, so a gap is drawn
   * rather than a number invented.</p>
   */
  phaseDeg: (number | null)[]
}

/**
 * A stored standard sampled onto `grid`, as impedance in ohms and degrees.
 *
 * <p>A standard is stored as the measured channel ratio H, which is dimensionless — so it cannot go
 * on an ohms axis as it stands. Inverting the divider it came from, `Z = R·H/(1 − H)`, puts it back
 * into ohms, where it can be read directly against the DUT curve: the <b>short</b> is the lead
 * impedance every measurement sits on top of, and a DUT curve approaching it is a DUT this fixture
 * cannot measure there.</p>
 *
 * <p>H is interpolated <b>in log frequency</b>, matching the log-spaced grid the standards are
 * captured on, and interpolated as a complex ratio rather than as an impedance — inverting the
 * divider is not linear, so interpolating |Z| instead would give a different (and wrong) curve
 * between the stored points.</p>
 *
 * <p>Returns `null` when there is no usable standard, and a `null` <i>entry</i> at any frequency
 * outside the band the standard was captured over — extrapolating a calibration is how a fixture
 * gets credited with accuracy it was never measured to have.</p>
 *
 * <p>Magnitude and phase come out of one pass because they come out of one complex number: a caller
 * that wanted only the magnitude would otherwise interpolate and invert the whole curve twice.</p>
 *
 * @param grid frequencies to sample at, ascending — the DUT's own grid where there is one, so a
 *   caller can index it alongside the measured curve.
 * @param referenceOhms the series resistor the standard was captured through. The standards are
 *   only valid at that resistance, so this is not a display preference.
 */
export function standardOnGrid(
  cal: ImpedanceCalibrationJson | null | undefined,
  grid: ArrayLike<number>,
  referenceOhms: number,
): StandardCurve | null {
  if (!cal || !cal.freqHz || cal.freqHz.length < 2) return null
  if (cal.real?.length !== cal.freqHz.length || cal.imag?.length !== cal.freqHz.length) return null
  if (!(referenceOhms > 0)) return null

  const lowHz = cal.freqHz[0]
  const highHz = cal.freqHz[cal.freqHz.length - 1]
  const ohms: (number | null)[] = new Array(grid.length)
  const phaseDeg: (number | null)[] = new Array(grid.length)
  // Walks forward with the grid rather than searching per point: both are ascending.
  let j = 0

  for (let i = 0; i < grid.length; i++) {
    const f = grid[i]
    if (!(f >= lowHz) || f > highHz) {
      ohms[i] = null
      phaseDeg[i] = null
      continue
    }

    while (j < cal.freqHz.length - 2 && cal.freqHz[j + 1] < f) j++

    const f0 = cal.freqHz[j]
    const f1 = cal.freqHz[j + 1]
    const t = f1 > f0 ? (Math.log10(f) - Math.log10(f0)) / (Math.log10(f1) - Math.log10(f0)) : 0
    const re = cal.real[j] + t * (cal.real[j + 1] - cal.real[j])
    const im = cal.imag[j] + t * (cal.imag[j + 1] - cal.imag[j])

    const z = dividerZ(re, im, referenceOhms)
    ohms[i] = z.ohms
    phaseDeg[i] = z.phaseDeg
  }

  return { ohms, phaseDeg }
}

/** Z from one complex ratio: `Z = R·H/(1 − H)`, bounded at both ends of its magnitude's range. */
function dividerZ(re: number, im: number, referenceOhms: number): { ohms: number; phaseDeg: number | null } {
  const dr = 1 - re
  const di = -im
  const d2 = dr * dr + di * di
  if (d2 < 1e-30) return { ohms: MAX_STANDARD_OHMS, phaseDeg: null }

  const zr = (referenceOhms * (re * dr + im * di)) / d2
  const zi = (referenceOhms * (im * dr - re * di)) / d2
  const mag = Math.hypot(zr, zi)
  if (!Number.isFinite(mag)) return { ohms: MAX_STANDARD_OHMS, phaseDeg: null }

  return { ohms: Math.min(mag, MAX_STANDARD_OHMS), phaseDeg: (Math.atan2(zi, zr) * 180) / Math.PI }
}
