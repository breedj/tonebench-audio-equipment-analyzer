import type { HarmonicCurveJson, ImpulseTraceJson, ReportRow } from '../api/client'
import { nearestInLogFrequency } from '../outputImpedance/readings'
import { formatHz } from '../response/format'
import { formatImpulsePeak } from './impulseUnit'

/**
 * The numbers the impulse-response screen is read for, as pure functions: the Display panel's
 * **Measurement** block, the impulse chart's own header figures, and each harmonic order's level at
 * the frequencies a report quotes. The panel shows them and the report's impulse chapter prints
 * them, and both read this module so the two cannot disagree — nothing in a .vue file is testable.
 */

/** What the run itself says, as against what its curves say: the status' fields, or a stored result's. */
export interface ImpulseRunFacts {
  referenceMode: string
  /** The stored loopback calibration's name, when that is the reference. */
  referenceName: string | null
  /** The delay the estimator removed; null when the reference was the generated sweep (see CLAUDE.md). */
  latencySamples: number | null
  /** How many records went into what is on screen; null for a stored result, which did not keep it. */
  averagesTaken: number | null
  averageCount: number
  generatorAmpDbv: number | null
}

/** "right input channel" / the calibration's name / "the generated sweep" — the Display panel's own wording. */
export function impulseReferenceLabel(mode: string, calibrationName: string | null): string {
  if (mode === 'RightChannel') return 'right input channel'
  if (mode === 'StoredLoopback') return calibrationName ?? 'stored loopback'
  return 'the generated sweep'
}

/**
 * The Measurement block as report rows: the impulse's own figures (peak, gate, resolution limit and
 * record length — the chart header's line) followed by the run's (reference, records, generator and
 * the removed delay). `—` for anything the source does not carry, and the delay row is omitted
 * altogether when there is none, exactly as the panel omits it.
 */
export function impulseReadingRows(impulse: ImpulseTraceJson | null, run: ImpulseRunFacts): ReportRow[] {
  const rows: ReportRow[] = []
  if (impulse) {
    rows.push(
      { label: 'Peak', value: formatImpulsePeak(impulse.peakVolts) },
      { label: 'Gate', value: `−${impulse.gatePreMs} / +${impulse.gatePostMs} ms` },
      { label: 'Resolved above', value: `${impulse.resolutionLimitHz.toFixed(0)} Hz` },
      { label: 'Record', value: `${impulse.fullSampleCount} samples at ${formatHz(impulse.sampleRateHz)}` },
    )
  }
  rows.push({ label: 'Reference', value: impulseReferenceLabel(run.referenceMode, run.referenceName) })
  rows.push({
    label: 'Records',
    value: run.averagesTaken === null ? String(run.averageCount) : `${run.averagesTaken} of ${run.averageCount}`,
  })
  if (run.generatorAmpDbv !== null && Number.isFinite(run.generatorAmpDbv)) {
    rows.push({ label: 'Generator', value: `${run.generatorAmpDbv.toFixed(2)} dBV` })
  }
  if (run.latencySamples !== null && Number.isFinite(run.latencySamples) && impulse) {
    const ms = (run.latencySamples / impulse.sampleRateHz) * 1000
    rows.push({ label: 'DUT delay', value: `${run.latencySamples.toFixed(1)} samples (${ms.toFixed(3)} ms)` })
  } else if (run.latencySamples !== null && Number.isFinite(run.latencySamples)) {
    rows.push({ label: 'DUT delay', value: `${run.latencySamples.toFixed(1)} samples` })
  }
  return rows
}

/** The frequencies the harmonics table quotes — a decade apart, the three any distortion figure is given at. */
export const HARMONIC_REPORT_FREQUENCIES_HZ = [100, 1000, 10000]

/**
 * One order's level at a frequency, in dBc, or null where the curve does not reach it: past
 * `upperValidHz` (the k-th harmonic is only observable up to the sweep's end over k) or outside the
 * fundamentals the curve actually holds. Nearest in **log** f, like every other lookup here, and
 * refused when the nearest point is more than a third of an octave away — a curve that stops at
 * 9 kHz must not answer for 10 kHz.
 */
export function harmonicLevelAt(curve: HarmonicCurveJson, targetHz: number): number | null {
  if (targetHz > curve.upperValidHz || curve.fundamentalHz.length === 0) return null
  const i = nearestInLogFrequency(curve.fundamentalHz, targetHz)
  const f = curve.fundamentalHz[i]
  if (!(f > 0) || Math.abs(Math.log2(f / targetHz)) > 1 / 3) return null
  const level = curve.levelDbc[i]
  return Number.isFinite(level) ? level : null
}

/** The harmonics table: a row per order, its valid ceiling and its level at each quoted frequency. */
export function harmonicRows(curves: readonly HarmonicCurveJson[]): { headers: string[]; cells: string[][] } {
  const headers = ['Order', 'Valid up to', ...HARMONIC_REPORT_FREQUENCIES_HZ.map((hz) => formatHz(hz))]
  const cells = [...curves]
    .sort((a, b) => a.order - b.order)
    .map((curve) => [
      `H${curve.order}`,
      formatHz(curve.upperValidHz),
      ...HARMONIC_REPORT_FREQUENCIES_HZ.map((hz) => {
        const level = harmonicLevelAt(curve, hz)
        return level === null ? '—' : `${level.toFixed(1)} dBc`
      }),
    ])
  return { headers, cells }
}
