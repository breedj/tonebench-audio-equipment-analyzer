import type { ComplexPoint } from './complexPlane'

/** One place the curve crosses a threshold, interpolated between the two points that bracket it. */
export interface Crossing {
  freqHz: number
  magDb: number
  phaseDeg: number
}

export interface StabilityMargins {
  /** Every |G| = 1 crossing in the window, in ascending frequency. */
  gainCrossings: Crossing[]
  /** Every ∠G ≡ 180° (mod 360) crossing in the window, in ascending frequency. */
  phaseCrossings: Crossing[]
  /** The worst (smallest) phase margin over `gainCrossings`, wrapped into (−180°, 180°]. */
  phaseMarginDeg: number | null
  /** The worst (smallest) gain margin over `phaseCrossings`. Negative means the loop is over unity there. */
  gainMarginDb: number | null
  /** The frequency the reported phase margin was read at. */
  gainCrossoverHz: number | null
  /** The frequency the reported gain margin was read at. */
  phaseCrossoverHz: number | null
  /**
   * Neither kind of crossing exists inside the plotted window. Reported rather than left as a pair
   * of nulls for the reason `BandwidthResult.LowerClamped` exists: "the search ran off the end of
   * the measured band" is a different statement from "the answer is zero", and a UI that cannot
   * tell them apart prints a measurement of something that was never measured.
   */
  outOfBand: boolean
}

const EMPTY: StabilityMargins = {
  gainCrossings: [],
  phaseCrossings: [],
  phaseMarginDeg: null,
  gainMarginDb: null,
  gainCrossoverHz: null,
  phaseCrossoverHz: null,
  outOfBand: true,
}

/**
 * Wrap an angle into (−180°, 180°].
 *
 * Load-bearing, not cosmetic. `FrequencyResponseEstimator.Complete` starts its `PhaseUnwrapper` at
 * bin 0, so it accumulates whole turns through the sub-analysis-band junk before it ever reaches a
 * displayed bin: in-band φ carries an arbitrary k·360°. A Nyquist contour does not care
 * (e^{j360k} = 1), but `PM = 180 + φ` would come out a whole number of turns wrong, and on a
 * Nichols chart the critical point would sit in the wrong place.
 */
export function wrap180(deg: number): number {
  const wrapped = ((deg + 180) % 360 + 360) % 360 - 180
  // The modulo above maps exactly −180 onto −180; the convention here is the half-open interval
  // (−180, 180], so a curve sitting precisely on the critical phase reads as +180 rather than −180.
  return wrapped === -180 ? 180 : wrapped
}

/**
 * The frequency a fraction `t` of the way from `f0` to `f1`, **in log f** — matching
 * `BandwidthAnalyzer` and `FrequencyResponseChart.resampleOnto`.
 *
 * Exported so it can be asserted on its own. Going through a sampled fixture instead does not
 * discriminate: on any grid fine enough for the closed form to be recovered at all, log and linear
 * interpolation agree to a fraction of a percent, so an end-to-end tolerance loose enough to admit
 * the fixture's own curvature is also loose enough to admit the wrong interpolation. Measured: at
 * 6 points/octave the two differ by 0.17%, well inside the 0.5% the margin assertions need.
 */
export function crossingFreqHz(f0: number, f1: number, t: number): number {
  if (!(f0 > 0) || !(f1 > 0)) return f0 + t * (f1 - f0)
  return Math.pow(10, Math.log10(f0) + t * (Math.log10(f1) - Math.log10(f0)))
}

function crossingAt(a: ComplexPoint, b: ComplexPoint, valueA: number, valueB: number, target: number): Crossing {
  const span = valueB - valueA
  const t = span === 0 ? 0 : (target - valueA) / span

  const freqHz = crossingFreqHz(a.freqHz, b.freqHz, t)

  return {
    freqHz,
    magDb: a.magDb + t * (b.magDb - a.magDb),
    phaseDeg: a.phaseDeg + t * (b.phaseDeg - a.phaseDeg),
  }
}

/**
 * Gain and phase margins from a curve that has already been windowed, de-rotated and sign-corrected
 * by `toComplexCurve`.
 *
 * Every crossing is returned and the *worst* is reported, rather than the first found. Ripple around
 * crossover gives a real trace several 0 dB crossings, and a conditionally-stable loop crosses the
 * critical phase more than once; a first-crossing implementation reports a comfortable margin for a
 * loop whose real margin is somewhere else entirely.
 */
export function stabilityMargins(curve: ComplexPoint[]): StabilityMargins {
  if (curve.length < 2) return EMPTY

  const gainCrossings: Crossing[] = []
  const phaseCrossings: Crossing[] = []

  for (let i = 0; i < curve.length - 1; i++) {
    const a = curve[i]
    const b = curve[i + 1]

    // |G| = 1 is 0 dB. Working in dB rather than in the ratio keeps the interpolation linear in the
    // quantity the trace actually stores.
    if ((a.magDb <= 0 && b.magDb > 0) || (a.magDb >= 0 && b.magDb < 0)) {
      gainCrossings.push(crossingAt(a, b, a.magDb, b.magDb, 0))
    }

    // The critical phase is every value ≡ 180 (mod 360), so the test is on the turn count
    // m = (φ + 180)/360 passing an integer — not on φ passing the literal −180, which a DUT that
    // has accumulated a turn never does. A coarse grid can straddle more than one integer, hence
    // the loop rather than a single check.
    const mA = (a.phaseDeg + 180) / 360
    const mB = (b.phaseDeg + 180) / 360
    const lo = Math.min(mA, mB)
    const hi = Math.max(mA, mB)

    for (let n = Math.ceil(lo); n <= Math.floor(hi); n++) {
      // A segment that merely touches an integer at its upper end would otherwise be counted twice,
      // once here and once as the next segment's lower end.
      if (n === hi && hi !== lo && i < curve.length - 2) continue
      phaseCrossings.push(crossingAt(a, b, mA, mB, n))
    }
  }

  if (gainCrossings.length === 0 && phaseCrossings.length === 0) return EMPTY

  let phaseMarginDeg: number | null = null
  let gainCrossoverHz: number | null = null
  for (const crossing of gainCrossings) {
    const margin = wrap180(crossing.phaseDeg + 180)
    if (phaseMarginDeg === null || margin < phaseMarginDeg) {
      phaseMarginDeg = margin
      gainCrossoverHz = crossing.freqHz
    }
  }

  let gainMarginDb: number | null = null
  let phaseCrossoverHz: number | null = null
  for (const crossing of phaseCrossings) {
    const margin = -crossing.magDb
    if (gainMarginDb === null || margin < gainMarginDb) {
      gainMarginDb = margin
      phaseCrossoverHz = crossing.freqHz
    }
  }

  return {
    gainCrossings,
    phaseCrossings,
    phaseMarginDeg,
    gainMarginDb,
    gainCrossoverHz,
    phaseCrossoverHz,
    outOfBand: false,
  }
}
