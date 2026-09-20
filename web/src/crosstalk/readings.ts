import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { formatHz } from '../response/format'

export { formatHz }

/** One reading off a crosstalk curve: the level at (or nearest to) a frequency. */
export interface CrosstalkReading {
  freqHz: number
  db: number
}

/** The figures the Measurements panel and the report quote for one crosstalk trace. */
export interface CrosstalkReadings {
  /** The point nearest `atHz` in log frequency — 1 kHz by default, the figure a spec sheet quotes. */
  at: CrosstalkReading
  /** The worst (highest, least negative) point on the curve. */
  worst: CrosstalkReading
  /** The first and last measured points — the band's ends. */
  low: CrosstalkReading
  high: CrosstalkReading
}

/**
 * Reads the quoted figures off a crosstalk trace. Pure: the panel and the report call this on the
 * same trace and print the same numbers. Null for a trace with no points.
 */
export function crosstalkReadings(trace: ResponseTrace, opts: { atHz?: number } = {}): CrosstalkReadings | null {
  const n = trace.freqHz.length
  if (n === 0 || trace.magnitudeMaxDb.length !== n) return null

  const atHz = opts.atHz ?? 1000
  let nearest = 0
  let nearestDistance = Infinity
  let worst = 0
  for (let i = 0; i < n; i++) {
    const f = trace.freqHz[i]
    if (f > 0) {
      const distance = Math.abs(Math.log(f) - Math.log(atHz))
      if (distance < nearestDistance) {
        nearestDistance = distance
        nearest = i
      }
    }
    if (trace.magnitudeMaxDb[i] > trace.magnitudeMaxDb[worst]) worst = i
  }

  const reading = (i: number): CrosstalkReading => ({ freqHz: trace.freqHz[i], db: trace.magnitudeMaxDb[i] })
  return { at: reading(nearest), worst: reading(worst), low: reading(0), high: reading(n - 1) }
}

/** "L→R" / "R→L" for a crosstalk trace kind; the kind itself for anything else. */
export function crosstalkDirectionLabel(kind: ResponseTrace['kind']): string {
  if (kind === 'CrosstalkLeftToRight') return 'L→R'
  if (kind === 'CrosstalkRightToLeft') return 'R→L'
  return kind
}

/** "−64.2 dB at 1.00 kHz" — one reading as the panel prints it. */
export function formatCrosstalkReading(reading: CrosstalkReading): string {
  return `${reading.db.toFixed(1)} dB at ${formatHz(reading.freqHz)}`
}

