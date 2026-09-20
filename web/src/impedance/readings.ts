import type { ImpedanceTrace } from '../composables/useImpedance'
import { formatHz } from '../response/format'
import { DUT_TYPES, analyseCapacitor, analyseInductor, type DutType } from './component'
import { dbOhmsToOhms, formatCapacitance, formatInductance, formatOhms } from './equivalent'
import { findExtremes, findResonance } from './resonance'

/**
 * The numbers an impedance curve is read for, as pure functions: the peak, the minimum and the
 * median |Z|, the drive current, and the component fit for whatever the user said is on the
 * fixture. ImpedanceReadoutPanel shows them and the report's impedance chapter prints them, and
 * both read this module so the two cannot disagree — nothing in a .vue file is testable, which is
 * why the arithmetic and the formatting live here and not there.
 */

export interface ImpedanceReadings {
  /** The highest |Z| on the curve — for a driver, its resonance. */
  peak: { ohms: number; freqHz: number }
  /** The lowest |Z|: the worst-case load, where the DUT draws the most current from whatever drives it. */
  min: { ohms: number; freqHz: number }
  /** Median |Z| over the curve's columns. */
  medianOhms: number
  /**
   * Roughly what the DUT was driven with, by the runner's own formula: the generator's RMS volts
   * over the reference resistor plus the median |Z|. Approximate on purpose — a sweep's current
   * varies with Z across the band — and null when the generator level is not known.
   */
  driveCurrentAmps: number | null
}

/**
 * The curve as ohms, taken from the per-column **maximum** — the array the chart draws as its line
 * and the one the trace's own `peakDbOhms` came from, so nothing derived here can disagree with the
 * peak. (A stepped sweep puts one measured point in every column, so its min and max are the same
 * number and the question does not arise.)
 */
export function curveOhms(trace: ImpedanceTrace): number[] {
  return trace.magnitudeMaxDbOhms.map(dbOhmsToOhms)
}

/**
 * The readings for one impedance trace, or null for an empty one.
 *
 * The median is the median of the curve's columns. The live readout's median comes from the server,
 * which takes it over the full-resolution spectrum inside the analysis band; a stored result keeps
 * only the decimated curve, so the report takes the curve's median for the live and the stored
 * chapter alike, and the two chapters of one result cannot differ. The drive current follows from
 * it by the runner's formula, so it is approximate twice over — which is still the figure that says
 * whether a driver was measured gently enough to trust.
 */
export function impedanceReadings(trace: ImpedanceTrace, referenceOhms: number, generatorAmpDbv: number | null): ImpedanceReadings | null {
  const n = trace.freqHz.length
  if (n === 0 || trace.magnitudeMaxDbOhms.length !== n) return null

  const ohms = curveOhms(trace)
  const extremes = findExtremes(trace.freqHz, ohms)
  if (!extremes) return null

  const sorted = ohms.filter((v) => Number.isFinite(v)).sort((a, b) => a - b)
  const m = sorted.length
  const medianOhms = m % 2 === 1 ? sorted[(m - 1) / 2] : (sorted[m / 2 - 1] + sorted[m / 2]) / 2

  const driveCurrentAmps =
    generatorAmpDbv !== null && Number.isFinite(generatorAmpDbv) && referenceOhms + medianOhms > 0
      ? 10 ** (generatorAmpDbv / 20) / (referenceOhms + medianOhms)
      : null

  return {
    peak: { ohms: dbOhmsToOhms(trace.peakDbOhms), freqHz: trace.peakFreqHz },
    min: { ohms: extremes.minOhms, freqHz: extremes.minFreqHz },
    medianOhms,
    driveCurrentAmps,
  }
}

// ---------------------------------------------------------------------------
// The component fit — what the Component section of the readout shows
// ---------------------------------------------------------------------------

/** "1.2 kHz–48 kHz", so a fitted value is never quoted without the band it came from. */
export function formatBand(fit: { fromHz: number; toHz: number } | null): string {
  return fit ? `${formatHz(fit.fromHz)}–${formatHz(fit.toHz)}` : '—'
}

/** A dissipation factor or a Q: three decimals, or exponent form once it is below what three decimals can show. */
export function formatRatio(value: number | null): string {
  if (value === null) return '—'
  return value < 0.01 ? value.toExponential(1) : value.toFixed(3)
}

/** A Thiele-Small Q, two decimals — the precision a datasheet quotes them to. */
export function formatQ(q: number | null): string {
  return q === null ? '—' : q.toFixed(2)
}

/** The Component dropdown's label for a DUT type. */
export function componentLabel(dutType: DutType): string {
  return DUT_TYPES.find((t) => t.value === dutType)?.label ?? dutType
}

export const CAPACITOR_DISSIPATION_NOTE =
  'D is below what this fixture can resolve, so treat it as calibration residue rather than a ' +
  'measurement: a tenth of a degree of leftover channel phase is more invented loss than a good ' +
  'capacitor has. ESR at self-resonance is the number to trust.'

export const CAPACITOR_NOT_CAPACITIVE_NOTE =
  'Nothing in this sweep is clearly capacitive — the phase never goes far enough below zero for ' +
  'the reactance to dominate the loss — so there is no capacitance to report. Either this is not a ' +
  'capacitor, or the sweep is above its self-resonance.'

export const CAPACITOR_NO_SRF_NOTE =
  'The sweep never reaches self-resonance, so there is no ESR or ESL to report — only C, which is ' +
  'the dominant term and is measured well. Raise the top of the sweep to reach them.'

export const INDUCTOR_DCR_NOTE =
  'The sweep never gets down to where the resistance dominates — the curve is still clearly ' +
  'inductive at its lowest point — so DCR is a small real part read out of a large reactance and ' +
  'any residual phase lands straight on it. Start the sweep lower.'

export const LOUDSPEAKER_NO_Q_NOTE =
  'The sweep does not reach far enough past resonance on one side to find where the curve falls to ' +
  'its half-power point, so there is no width to read a Q from. Widen the sweep.'

export const LOUDSPEAKER_NOTE =
  'Vas, Bl and Mms need a second run with a known added mass or a sealed box, so they are not ' +
  'derivable from this one and are deliberately not shown. Smoothing blunts Zmax and widens the ' +
  'skirts, so measure with none if these numbers matter.'

export const LOUDSPEAKER_NO_RESONANCE_NOTE =
  'No motional resonance found: the curve is flat, still rising at the top of the sweep, or its ' +
  'peak is under half again its baseline. Nothing here is reported from a shape that is not there.'

/** One row of a component fit: the readout's label and value, `flagged` where the panel shows its amber "?". */
export interface ComponentRow {
  label: string
  value: string
  flagged?: boolean
}

/** The component fit for a DUT type, as the readout panel lays it out: its rows, then the notes that apply. */
export interface ComponentReading {
  /** The Component dropdown's label — the section's title. */
  label: string
  rows: ComponentRow[]
  notes: string[]
}

/**
 * The component fit for the DUT the user named, or null for `None`. The rows are the readout
 * panel's, in its order and with its labels; a fit the sweep cannot support yields no rows and the
 * note that says why, never a half-filled table.
 */
export function componentReading(trace: ImpedanceTrace, dutType: DutType): ComponentReading | null {
  if (dutType === 'None' || trace.freqHz.length === 0) return null
  const ohms = curveOhms(trace)
  const label = componentLabel(dutType)

  if (dutType === 'Capacitor') {
    const c = analyseCapacitor(trace.freqHz, ohms, trace.phaseDeg)
    const flagged = c.dissipationFactor !== null && !c.dissipationTrustworthy
    const notes: string[] = []
    if (flagged) notes.push(CAPACITOR_DISSIPATION_NOTE)
    else if (!c.capacitance) notes.push(CAPACITOR_NOT_CAPACITIVE_NOTE)
    else if (c.srfHz === null) notes.push(CAPACITOR_NO_SRF_NOTE)
    return {
      label,
      rows: [
        { label: 'C', value: formatCapacitance(c.capacitance?.value ?? null) },
        { label: 'over', value: formatBand(c.capacitance) },
        { label: 'SRF', value: c.srfHz === null ? '—' : formatHz(c.srfHz) },
        { label: 'ESR', value: c.esrOhms === null ? '—' : formatOhms(c.esrOhms) },
        { label: 'ESL', value: formatInductance(c.esl?.value ?? null) },
        { label: 'D', value: flagged ? `${formatRatio(c.dissipationFactor)} ?` : formatRatio(c.dissipationFactor), flagged },
      ],
      notes,
    }
  }

  if (dutType === 'Inductor') {
    const l = analyseInductor(trace.freqHz, ohms, trace.phaseDeg)
    const flagged = l.dcrOhms !== null && !l.dcrTrustworthy
    return {
      label,
      rows: [
        { label: 'L', value: formatInductance(l.inductance?.value ?? null) },
        { label: 'over', value: formatBand(l.inductance) },
        { label: 'DCR', value: l.dcrOhms === null ? '—' : flagged ? `${formatOhms(l.dcrOhms)} ?` : formatOhms(l.dcrOhms), flagged },
        { label: 'SRF', value: l.srfHz === null ? '—' : formatHz(l.srfHz) },
        { label: 'Z at SRF', value: l.zAtSrfOhms === null ? '—' : formatOhms(l.zAtSrfOhms) },
        { label: 'Q', value: formatRatio(l.q) },
      ],
      notes: flagged ? [INDUCTOR_DCR_NOTE] : [],
    }
  }

  const r = findResonance(trace.freqHz, ohms)
  if (!r) return { label, rows: [], notes: [LOUDSPEAKER_NO_RESONANCE_NOTE] }
  return {
    label,
    rows: [
      { label: 'fs', value: formatHz(r.fsHz) },
      { label: 'Zmax', value: formatOhms(r.zMaxOhms) },
      { label: 'Re', value: formatOhms(r.reOhms) },
      { label: 'f1 / f2', value: r.f1Hz === null || r.f2Hz === null ? '—' : `${formatHz(r.f1Hz)} / ${formatHz(r.f2Hz)}` },
      { label: 'Qms', value: formatQ(r.qms) },
      { label: 'Qes', value: formatQ(r.qes) },
      { label: 'Qts', value: formatQ(r.qts) },
    ],
    notes: [r.qms === null ? LOUDSPEAKER_NO_Q_NOTE : LOUDSPEAKER_NOTE],
  }
}
