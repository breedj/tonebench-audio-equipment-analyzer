/**
 * Reading an impedance curve as a *named* component.
 *
 * <p>The same sweep means different things depending on what is on the terminals: a |Z| minimum with
 * the phase crossing zero upward is a capacitor's self-resonance and its ESR; the same shape upside
 * down is an inductor's. Nothing in the curve says which, so the user says — and this module then
 * reports only the numbers that are meaningful for that answer, rather than showing every reading
 * for every part and leaving the interpretation to whoever is looking.</p>
 *
 * <p>Pure, so it can be tested; nothing in a `.vue` is reachable under vitest's node environment.
 * Works from |Z| and the phase, which is everything a one-port measurement has.</p>
 */

/** What the user says is on the fixture. `None` shows no component block at all. */
export type DutType = 'None' | 'Capacitor' | 'Inductor' | 'Loudspeaker'

export const DUT_TYPES: { value: DutType; label: string }[] = [
  { value: 'None', label: 'None' },
  { value: 'Capacitor', label: 'Capacitor' },
  { value: 'Inductor', label: 'Inductor' },
  { value: 'Loudspeaker', label: 'Loudspeaker' },
]

export function isDutType(value: unknown): value is DutType {
  return DUT_TYPES.some((t) => t.value === value)
}

/**
 * Phase past which a sample counts as clearly reactive rather than ambiguous.
 *
 * <p>45 degrees is where the reactance overtakes the loss, so beyond it the value being fitted is the
 * dominant term rather than a small difference between two comparable ones. Samples nearer zero are
 * excluded from every fit: that is exactly the region where a fraction of a degree of residual
 * calibration phase moves the answer by tens of per cent.</p>
 */
const CLEARLY_REACTIVE_DEG = 45

/** And how far either side of a zero crossing the curve must actually go before it counts as one. */
const CROSSING_CLEARANCE_DEG = 20

/** A value fitted across a band, with the band it came from — so a reading can never be quoted without saying where. */
export interface BandFit {
  /** Farads or henries, depending on what was fitted. */
  value: number
  fromHz: number
  toHz: number
  points: number
}

export interface CapacitorReading {
  /** Capacitance, fitted below self-resonance. Null when the sweep never gets clearly capacitive. */
  capacitance: BandFit | null
  /** Self-resonant frequency: where the phase crosses from capacitive to inductive. */
  srfHz: number | null
  /**
   * |Z| at self-resonance, which is the only place ESR is honestly measurable — the reactance has
   * cancelled and what is left is the loss. Null when the sweep does not reach resonance.
   */
  esrOhms: number | null
  /** Equivalent series inductance, fitted above self-resonance. */
  esl: BandFit | null
  /** Dissipation factor (tan delta = R/|X|) in the capacitive band. Honest, but see `dissipationTrustworthy`. */
  dissipationFactor: number | null
  /**
   * False when the capacitive band's phase is so close to -90 degrees that D is below what the
   * fixture can resolve. A residual 0.01 degree of channel phase is 0.28 ohm of invented ESR on a
   * 1.6 kohm reactance, which is more than a good film capacitor's entire ESR — so the number is
   * calibration residue rather than a measurement, and saying so is the only useful thing to do
   * with it.
   */
  dissipationTrustworthy: boolean
}

export interface InductorReading {
  /** Inductance, fitted below self-resonance. */
  inductance: BandFit | null
  /** Self-resonant frequency: where the phase crosses from inductive to capacitive. */
  srfHz: number | null
  /** |Z| at self-resonance — a maximum for an inductor, where the winding's own capacitance parallels it. */
  zAtSrfOhms: number | null
  /** Winding resistance: the real part where |Z| is lowest, which is where the reactance is least in the way. */
  dcrOhms: number | null
  /** False when the curve is still clearly inductive at its lowest |Z|, meaning the sweep never got down to where R dominates. */
  dcrTrustworthy: boolean
  /** Q = |X|/R at the inductance fit's own band. */
  q: number | null
}

/** Reactance at a sample: |Z| sin(phase). Positive inductive, negative capacitive. */
function reactance(ohms: number, phaseDeg: number): number {
  return ohms * Math.sin((phaseDeg * Math.PI) / 180)
}

/** Resistance at a sample: |Z| cos(phase). */
function resistance(ohms: number, phaseDeg: number): number {
  return ohms * Math.cos((phaseDeg * Math.PI) / 180)
}

/**
 * The median of `values`. Median rather than mean throughout, the same choice
 * `ResponseCaptureSupport.MedianGainDb` makes and for the same reason: one bad sample at the edge of
 * a band drags a mean and leaves a median alone.
 */
function median(values: number[]): number {
  const sorted = [...values].sort((a, b) => a - b)
  return sorted[sorted.length >> 1]
}

/**
 * Where the phase crosses zero, interpolated in log frequency.
 *
 * <p>`rising` picks the direction: a capacitor goes from negative phase to positive as it passes
 * self-resonance, an inductor the other way. The curve must reach {@link CROSSING_CLEARANCE_DEG} on
 * both sides before a crossing is believed, so a curve hovering around zero because it is a resistor
 * does not report a resonance at every noisy sample.</p>
 */
function findPhaseCrossing(freqHz: number[], phaseDeg: number[], rising: boolean): number | null {
  const n = Math.min(freqHz.length, phaseDeg.length)

  for (let i = 0; i < n - 1; i++) {
    const a = phaseDeg[i]
    const b = phaseDeg[i + 1]
    const crosses = rising ? a <= 0 && b > 0 : a >= 0 && b < 0
    if (!crosses) continue

    const before = phaseDeg
      .slice(0, i + 1)
      .some((p) => (rising ? p < -CROSSING_CLEARANCE_DEG : p > CROSSING_CLEARANCE_DEG))
    const after = phaseDeg
      .slice(i + 1)
      .some((p) => (rising ? p > CROSSING_CLEARANCE_DEG : p < -CROSSING_CLEARANCE_DEG))

    if (!before || !after) continue

    const t = b === a ? 0 : (0 - a) / (b - a)
    return Math.exp(Math.log(freqHz[i]) + t * (Math.log(freqHz[i + 1]) - Math.log(freqHz[i])))
  }

  return null
}

/** |Z| at a frequency, interpolated in log frequency between the bracketing samples. */
function ohmsAt(freqHz: number[], ohms: number[], target: number): number | null {
  const n = Math.min(freqHz.length, ohms.length)
  for (let i = 0; i < n - 1; i++) {
    if (freqHz[i] <= target && target <= freqHz[i + 1]) {
      const span = Math.log(freqHz[i + 1]) - Math.log(freqHz[i])
      const t = span > 0 ? (Math.log(target) - Math.log(freqHz[i])) / span : 0
      return ohms[i] + t * (ohms[i + 1] - ohms[i])
    }
  }
  return null
}

/** Fits one reactive value across every sample that qualifies, and says which band it used. */
function fitBand(
  freqHz: number[],
  ohms: number[],
  phaseDeg: number[],
  qualifies: (freq: number, phase: number) => boolean,
  valueAt: (freq: number, x: number) => number,
): BandFit | null {
  const values: number[] = []
  let fromHz = Number.POSITIVE_INFINITY
  let toHz = 0

  const n = Math.min(freqHz.length, ohms.length, phaseDeg.length)
  for (let i = 0; i < n; i++) {
    const f = freqHz[i]
    if (!Number.isFinite(ohms[i]) || ohms[i] <= 0) continue
    if (!qualifies(f, phaseDeg[i])) continue

    const value = valueAt(f, reactance(ohms[i], phaseDeg[i]))
    if (!Number.isFinite(value) || value <= 0) continue

    values.push(value)
    fromHz = Math.min(fromHz, f)
    toHz = Math.max(toHz, f)
  }

  if (values.length < 3) return null
  return { value: median(values), fromHz, toHz, points: values.length }
}

/** A capacitor, read from its curve. */
export function analyseCapacitor(
  freqHz: number[],
  ohms: number[],
  phaseDeg: number[],
): CapacitorReading {
  const srfHz = findPhaseCrossing(freqHz, phaseDeg, true)
  const belowSrf = srfHz ?? Number.POSITIVE_INFINITY

  const capacitance = fitBand(
    freqHz,
    ohms,
    phaseDeg,
    (f, p) => f < belowSrf && p < -CLEARLY_REACTIVE_DEG,
    // X = -1/(wC), and X is negative here, so this comes out positive.
    (f, x) => -1 / (2 * Math.PI * f * x),
  )

  // The capacitive term is subtracted back out using the C just fitted, rather than taking X/w and
  // calling it L. Above resonance X is still `wL - 1/(wC)`, and a sweep that ends at less than a
  // couple of times SRF never gets far enough for the second term to vanish: measured on a 10 uF /
  // 1 uH part swept to 90 kHz, the naive reading is 0.50 uH for a 1.00 uH ESL -- half, and biased in
  // a direction that always flatters the part.
  const esl =
    srfHz === null || capacitance === null
      ? null
      : fitBand(
          freqHz,
          ohms,
          phaseDeg,
          (f, p) => f > srfHz && p > CLEARLY_REACTIVE_DEG,
          (f, x) => (x + 1 / (2 * Math.PI * f * capacitance.value)) / (2 * Math.PI * f),
        )

  let dissipationFactor: number | null = null
  let dissipationTrustworthy = false

  if (capacitance) {
    const ratios: number[] = []
    const n = Math.min(freqHz.length, ohms.length, phaseDeg.length)
    for (let i = 0; i < n; i++) {
      if (freqHz[i] < capacitance.fromHz || freqHz[i] > capacitance.toHz) continue
      const x = Math.abs(reactance(ohms[i], phaseDeg[i]))
      if (x > 0) ratios.push(resistance(ohms[i], phaseDeg[i]) / x)
    }

    if (ratios.length > 0) {
      dissipationFactor = median(ratios)

      // A tenth of a degree is about the best a two-channel fixture holds after an open calibration,
      // and tan(0.1 deg) is 0.0017. Anything under that is being read out of the calibration
      // residue rather than out of the capacitor.
      dissipationTrustworthy = dissipationFactor > Math.tan((0.1 * Math.PI) / 180)
    }
  }

  return {
    capacitance,
    srfHz,
    esrOhms: srfHz === null ? null : ohmsAt(freqHz, ohms, srfHz),
    esl,
    dissipationFactor,
    dissipationTrustworthy,
  }
}

/** An inductor, read from its curve. */
export function analyseInductor(
  freqHz: number[],
  ohms: number[],
  phaseDeg: number[],
): InductorReading {
  const srfHz = findPhaseCrossing(freqHz, phaseDeg, false)
  const belowSrf = srfHz ?? Number.POSITIVE_INFINITY

  const inductance = fitBand(
    freqHz,
    ohms,
    phaseDeg,
    (f, p) => f < belowSrf && p > CLEARLY_REACTIVE_DEG,
    (f, x) => x / (2 * Math.PI * f),
  )

  let dcrOhms: number | null = null
  let dcrTrustworthy = false
  let lowest = -1

  const n = Math.min(freqHz.length, ohms.length, phaseDeg.length)
  for (let i = 0; i < n; i++) {
    if (!Number.isFinite(ohms[i]) || ohms[i] <= 0) continue
    if (lowest < 0 || ohms[i] < ohms[lowest]) lowest = i
  }

  if (lowest >= 0) {
    // The *real part* at that point, not |Z|. A choke measured from 20 Hz still carries 1.3 ohms of
    // reactance there against 2 ohms of winding, so |Z| reads 2.36 for a 2.00 ohm winding -- an 18%
    // error that looks like a plausible DCR. |Z|cos(phase) removes it exactly.
    dcrOhms = resistance(ohms[lowest], phaseDeg[lowest])

    // Only the winding resistance if the curve had actually stopped being inductive by then. A sweep
    // that starts above that point reports its own lowest reactance as a resistance, which for a
    // choke is out by orders of magnitude.
    dcrTrustworthy = Math.abs(phaseDeg[lowest]) < CLEARLY_REACTIVE_DEG
  }

  let q: number | null = null
  if (inductance) {
    const ratios: number[] = []
    for (let i = 0; i < n; i++) {
      if (freqHz[i] < inductance.fromHz || freqHz[i] > inductance.toHz) continue
      const r = resistance(ohms[i], phaseDeg[i])
      if (r > 0) ratios.push(Math.abs(reactance(ohms[i], phaseDeg[i])) / r)
    }
    if (ratios.length > 0) q = median(ratios)
  }

  return {
    inductance,
    srfHz,
    zAtSrfOhms: srfHz === null ? null : ohmsAt(freqHz, ohms, srfHz),
    dcrOhms,
    dcrTrustworthy,
    q,
  }
}
