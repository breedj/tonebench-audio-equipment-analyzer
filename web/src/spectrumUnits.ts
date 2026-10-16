/**
 * The spectrum chart's level unit. The wire, the retained snapshot and the database keep one
 * normalisation — dBV per bin, the amplitude convention of CLAUDE.md — and a density unit is a
 * per-frame constant added at draw time:
 *
 *   dBV/√Hz = dBV − 10·log10(ENBW_Hz)
 *
 * where ENBW is the window's equivalent noise bandwidth at the frame's sample rate and FFT size
 * (PSD_k = A_k²·S1²/(2·fs·S2), i.e. the bin's power spread over the window's noise bandwidth). The
 * input-referred unit subtracts the DUT gain on top, so a preamp's output floor reads as the noise
 * at its input. Client-side rather than a second stored normalisation because every overlay then
 * converts with its own window's ENBW, and the arithmetic is one add per point.
 *
 * Note what a density unit does to a tone: a −20 dBV sine's bin does not have a density, and on
 * this axis it reads −20 − 10·log10(ENBW) — lower on a wider bin — which is the same convention
 * every analyzer with a dBV/√Hz axis uses. The noise floor, on the other hand, sits still when the
 * FFT size changes, which is the point of the unit.
 */

export const SPECTRUM_UNITS = ['dBV', 'dBVPerRootHz', 'dBVPerRootHzInput'] as const
export type SpectrumUnit = (typeof SPECTRUM_UNITS)[number]

export const DEFAULT_SPECTRUM_UNIT: SpectrumUnit = 'dBV'

export const SPECTRUM_UNIT_OPTIONS: { value: SpectrumUnit; label: string }[] = [
  { value: 'dBV', label: 'dBV' },
  { value: 'dBVPerRootHz', label: 'dBV/√Hz' },
  { value: 'dBVPerRootHzInput', label: 'dBV/√Hz (input-referred)' },
]

/** A stored `chartUnit` string, or anything else, as a unit — unknown values are dBV. */
export function parseSpectrumUnit(value: string | null | undefined): SpectrumUnit {
  return (SPECTRUM_UNITS as readonly string[]).includes(value ?? '') ? (value as SpectrumUnit) : DEFAULT_SPECTRUM_UNIT
}

/**
 * The offset to add to a dBV value for `unit`, or null when the conversion needs an ENBW the frame
 * does not carry (a spectrum frame that arrived before any noise frame, a stored channel without
 * one). dBV needs nothing and is never null.
 */
export function unitOffsetDb(unit: SpectrumUnit, enbwHz: number | undefined, dutGainDb: number): number | null {
  if (unit === 'dBV') return 0
  if (enbwHz === undefined || !(enbwHz > 0)) return null
  const density = -10 * Math.log10(enbwHz)
  return unit === 'dBVPerRootHzInput' ? density - (Number.isFinite(dutGainDb) ? dutGainDb : 0) : density
}

/** The y-axis label for `unit`. */
export function unitAxisLabel(unit: SpectrumUnit): string {
  switch (unit) {
    case 'dBV':
      return 'Level (dBV)'
    case 'dBVPerRootHz':
      return 'Density (dBV/√Hz)'
    case 'dBVPerRootHzInput':
      return 'Input-referred density (dBV/√Hz)'
  }
}

/** The suffix a cursor readout or export title uses. */
export function unitSuffix(unit: SpectrumUnit): string {
  return unit === 'dBV' ? 'dBV' : 'dBV/√Hz'
}

/**
 * What the harmonic markers over the spectrum are labelled with: each harmonic's level relative to
 * the fundamental, or its own absolute level. Only the markers -- the trace's own axis is
 * {@link SpectrumUnit} above, and the two are deliberately independent: dBc is a *ratio*, so it has
 * no place on a level axis, and a marker that silently changed meaning with the axis would make the
 * two unreadable together.
 *
 * <p>dBc by default, which is what the markers always showed and what a distortion figure is quoted
 * in; dBV answers the other question the same lines are used for -- how loud is H3, in absolute
 * terms, against the noise floor beside it. Both are the figures the harmonics table prints in its
 * two columns (HarmonicsTable.vue), from the same arithmetic: dBV = fundamental dBV + dBc.</p>
 *
 * <p>Note the label stays in dBV under the chart's density units, exactly as that table does: a
 * harmonic is a coherent tone and has a level, not a density (see this file's own header).</p>
 */
export const HARMONIC_MARKER_UNITS = ['dBc', 'dBV'] as const
export type HarmonicMarkerUnit = (typeof HARMONIC_MARKER_UNITS)[number]

export const DEFAULT_HARMONIC_MARKER_UNIT: HarmonicMarkerUnit = 'dBc'

/** A stored `chartHarmonicMarkerUnit` string, or anything else, as a unit -- unknown values are dBc. */
export function parseHarmonicMarkerUnit(value: string | null | undefined): HarmonicMarkerUnit {
  return (HARMONIC_MARKER_UNITS as readonly string[]).includes(value ?? '')
    ? (value as HarmonicMarkerUnit)
    : DEFAULT_HARMONIC_MARKER_UNIT
}

/**
 * The text beside a harmonic marker: the harmonic's dBc, or its absolute dBV derived from the
 * fundamental the same tick measured. One decimal in dBV (a level is read against a 10 dB grid) and
 * none in dBc, which is how the markers have always printed and keeps the labels narrow enough to
 * sit between two adjacent orders at the low end of a log axis.
 *
 * <p>Null when dBV was asked for and there is no fundamental level to add -- a marker labelled
 * with a bare dBc figure under a dBV heading would be wrong by whatever the fundamental is.</p>
 */
export function harmonicMarkerLabel(
  unit: HarmonicMarkerUnit,
  dbc: number,
  fundamentalDbv: number | undefined,
): string | null {
  if (unit === 'dBc') return dbc.toFixed(0)
  if (fundamentalDbv === undefined || !Number.isFinite(fundamentalDbv)) return null
  return (fundamentalDbv + dbc).toFixed(1)
}
