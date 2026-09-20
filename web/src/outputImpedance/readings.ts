import type { ImpedanceTrace } from '../composables/useImpedance'
import { dbOhmsToOhms, seriesEquivalent } from '../impedance/equivalent'

/**
 * Below this a Zout reading is no impedance at all: the wire floors a zero at 1e-9 Ω (−180 dB re 1 Ω,
 * `ImpedanceMath.ToDbOhms`), so a magnitude-only point that read nothing arrives as a nanohm, and a
 * damping factor of eight thousand million is not a figure to print. A microhm is below anything the
 * measurement can resolve.
 */
export const MIN_MEASURABLE_OHMS = 1e-6

/** One reading off a Zout curve: the impedance at (or nearest to) a frequency, and the damping factor it implies. */
export interface ZoutReading {
  freqHz: number
  ohms: number
  /** `nominal / ohms`; null where the impedance is zero (an ideal source). */
  dampingFactor: number | null
  /** The measured phase at that point, degrees — meaningful only when the run was referenced. */
  phaseDeg: number
}

/** The figures the readout panel and the report quote for one Zout trace. */
export interface ZoutReadings {
  /** The points nearest 20 Hz, 1 kHz and 20 kHz in log frequency — the three a specification quotes. */
  low: ZoutReading
  at1k: ZoutReading
  high: ZoutReading
  /** The highest impedance on the curve, i.e. the lowest damping factor, and where it is. */
  worst: ZoutReading
  /** Median |Zout| over the curve. */
  medianOhms: number
  /** The series R + L at the top point, when the run measured a phase (the output coil). */
  topEquivalent: { resistanceOhms: number; inductanceH: number | null } | null
}

/** The index of the point nearest `targetHz` in log frequency — the runner's rule, so both quote the same point. */
export function nearestInLogFrequency(freqHz: number[], targetHz: number): number {
  let best = 0
  let bestDistance = Infinity
  for (let i = 0; i < freqHz.length; i++) {
    const f = freqHz[i]
    if (!(f > 0)) continue
    const distance = Math.abs(Math.log(f) - Math.log(targetHz))
    if (distance < bestDistance) {
      bestDistance = distance
      best = i
    }
  }
  return best
}

/**
 * Reads the quoted figures off a Zout trace (an impedance trace: magnitudes in dB relative to 1 Ω).
 * Pure: the readout panel and the report call this on the same trace and print the same numbers.
 * Null for a trace with no points.
 */
export function zoutReadings(trace: ImpedanceTrace, nominalOhms: number, referenced: boolean): ZoutReadings | null {
  const n = trace.freqHz.length
  if (n === 0 || trace.magnitudeMaxDbOhms.length !== n) return null

  const ohms = trace.magnitudeMaxDbOhms.map(dbOhmsToOhms)
  const reading = (i: number): ZoutReading => ({
    freqHz: trace.freqHz[i],
    ohms: ohms[i],
    dampingFactor: ohms[i] > MIN_MEASURABLE_OHMS ? nominalOhms / ohms[i] : null,
    phaseDeg: trace.phaseDeg[i] ?? 0,
  })

  let worst = 0
  for (let i = 1; i < n; i++) if (ohms[i] > ohms[worst]) worst = i

  const sorted = [...ohms].sort((a, b) => a - b)
  const medianOhms = n % 2 === 1 ? sorted[(n - 1) / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2

  const top = n - 1
  const equivalent = referenced ? seriesEquivalent(ohms[top], trace.phaseDeg[top] ?? 0, trace.freqHz[top]) : null

  return {
    low: reading(nearestInLogFrequency(trace.freqHz, 20)),
    at1k: reading(nearestInLogFrequency(trace.freqHz, 1000)),
    high: reading(nearestInLogFrequency(trace.freqHz, 20000)),
    worst: reading(worst),
    medianOhms,
    topEquivalent: equivalent ? { resistanceOhms: equivalent.resistanceOhms, inductanceH: equivalent.inductanceH } : null,
  }
}

/** "286" / "12.4" / "3.95" — a damping factor with the precision its size deserves; a dash for none. */
export function formatDampingFactor(df: number | null): string {
  if (df === null || !Number.isFinite(df)) return '—'
  if (df >= 100) return df.toFixed(0)
  if (df >= 10) return df.toFixed(1)
  return df.toFixed(2)
}
