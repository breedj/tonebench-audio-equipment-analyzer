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
