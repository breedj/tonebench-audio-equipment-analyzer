import type { ResponseBandwidthView } from '../composables/useFrequencyResponse'

/** A frequency for the readout table and the chart's bandwidth labels: kHz above 1000, Hz below. */
export function formatHz(hz: number): string {
  if (!Number.isFinite(hz)) return '—'
  if (hz >= 1000) return `${(hz / 1000).toFixed(hz >= 10000 ? 1 : 2)} kHz`
  return `${hz.toFixed(hz >= 100 ? 1 : 2)} Hz`
}

/**
 * One end of a bandwidth reading.
 *
 * A clamped end is rendered with a leading `>` (for the upper) or `<` (for the lower) rather than as
 * a plain number, because the curve never reached the threshold there — the frequency is where the
 * search stopped, not a measured corner. The previous app printed a bare number in that case, which
 * reads as a measurement of something that was not measured.
 */
export function formatCorner(hz: number, clamped: boolean, side: 'lower' | 'upper'): string {
  if (!clamped) return formatHz(hz)
  return `${side === 'upper' ? '>' : '<'} ${formatHz(hz)}`
}

/** The bandwidth itself, marked approximate when either corner ran off the end of the measured band. */
export function formatBandwidth(reading: ResponseBandwidthView): string {
  const value = formatHz(reading.bandwidthHz)
  return reading.lowerClamped || reading.upperClamped ? `> ${value}` : value
}

/** Gain as a linear ratio, the "53.03 x" the previous app showed beside the dB figure. */
export function formatRatio(db: number): string {
  if (!Number.isFinite(db)) return '—'
  const ratio = Math.pow(10, db / 20)
  if (ratio >= 100) return `${ratio.toFixed(1)} x`
  if (ratio >= 10) return `${ratio.toFixed(2)} x`
  return `${ratio.toFixed(3)} x`
}

export function formatDb(db: number): string {
  if (!Number.isFinite(db)) return '—'
  // `(-0.0001).toFixed(2)` is "-0.00", which reads as a signed quantity where there is none — a
  // flat loopback's gain lands there every time. Adding zero normalises it away.
  return `${(db + 0).toFixed(2).replace(/^-0\.00$/, '0.00')} dB`
}

/**
 * A group or phase delay in the unit its size calls for: milliseconds from 1 ms up, microseconds
 * below that, nanoseconds under a microsecond. Sign kept — an excess delay against the generated
 * sweep can legitimately read negative. A dash for null (a stored result from before the delay
 * curves) or anything non-finite.
 */
export function formatDelay(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return '—'
  const magnitude = Math.abs(seconds)
  if (magnitude >= 1e-3) return `${(seconds * 1e3).toFixed(3)} ms`
  if (magnitude >= 1e-6) return `${(seconds * 1e6).toFixed(1)} µs`
  return `${(seconds * 1e9 + 0).toFixed(0).replace(/^-0$/, '0')} ns`
}

/**
 * A formatted reading split into its number and its unit, so the readout can set the unit in grey
 * beside the figure — the 2026-10-12 mockup's treatment, and the thing that lets a column of
 * readings be scanned for its numbers rather than read.
 *
 * The split is the *last* space, which is where every formatter above puts the unit, and it keeps a
 * clamped corner's marker with the number ("< 52.55" / "Hz") rather than treating "<" as the value.
 * A dash, or anything with no unit at all, comes back as the whole string and no unit — never as an
 * empty value with the text as its unit.
 */
export function splitUnit(text: string): { value: string; unit: string } {
  const at = text.lastIndexOf(' ')
  if (at <= 0) return { value: text, unit: '' }
  const unit = text.slice(at + 1)
  // "< 52.55 Hz" splits at the last space; "< 52.55" must not split at the first.
  if (/[0-9]/.test(unit)) return { value: text, unit: '' }
  return { value: text.slice(0, at), unit }
}
