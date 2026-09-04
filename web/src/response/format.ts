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
