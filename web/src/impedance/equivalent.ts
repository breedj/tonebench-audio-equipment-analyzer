// Turning one point of an impedance curve into the numbers it is actually used as: resistance,
// reactance, and the inductance or capacitance that reactance corresponds to at that frequency.
//
// Pure and unit-tested, because nothing inside a .vue file is testable under the node environment
// vitest runs in — and this is exactly the arithmetic where a degrees/radians slip or a sign
// convention produces a plausible wrong answer rather than an obvious one.

/** dB relative to 1 ohm, as the wire and the `response_trace` rows carry impedance magnitudes. */
export function dbOhmsToOhms(dbOhms: number): number {
  return Math.pow(10, dbOhms / 20)
}

export function ohmsToDbOhms(ohms: number): number {
  return 20 * Math.log10(Math.max(ohms, 1e-9))
}

export interface SeriesEquivalent {
  /** Real part, in ohms. */
  resistanceOhms: number
  /** Imaginary part, in ohms: positive is inductive, negative capacitive. */
  reactanceOhms: number
  /** Henries, when the reactance is inductive. Null when it is capacitive or zero. */
  inductanceH: number | null
  /** Farads, when the reactance is capacitive. Null when it is inductive or zero. */
  capacitanceF: number | null
  /**
   * |X| / R — how reactive this point is relative to its own loss. Infinite for a purely reactive
   * point, which is a real answer rather than an error: a lossless component has unbounded Q.
   */
  q: number
}

/**
 * The series R + jX equivalent at one frequency.
 *
 * <p>A one-port's impedance at a single frequency is one complex number, and there are two circuits
 * that produce it — a resistance in series with a reactance, or the two in parallel. The series form
 * is the one reported here because it is what a measurement through a series reference resistor is
 * naturally expressed in, and what a speaker builder or an output-transformer measurement wants:
 * "6.4 ohms and 0.4 mH" describes a voice coil directly.</p>
 *
 * <p>Only one of `inductanceH` and `capacitanceF` is ever non-null, decided by the sign of the
 * reactance. Reporting both would mean reporting one of them as negative, which is not a component.</p>
 */
export function seriesEquivalent(ohms: number, phaseDeg: number, freqHz: number): SeriesEquivalent {
  const radians = (phaseDeg * Math.PI) / 180
  const resistanceOhms = ohms * Math.cos(radians)
  const reactanceOhms = ohms * Math.sin(radians)

  const omega = 2 * Math.PI * freqHz
  const usable = Number.isFinite(omega) && omega > 0 && Number.isFinite(reactanceOhms)

  return {
    resistanceOhms,
    reactanceOhms,
    inductanceH: usable && reactanceOhms > 0 ? reactanceOhms / omega : null,
    capacitanceF: usable && reactanceOhms < 0 ? -1 / (omega * reactanceOhms) : null,
    // Compared against the magnitude rather than against zero. cos(90 degrees) is 6.1e-17 in
    // floating point, not 0, so an exact test never fires and a purely reactive point reports a Q
    // of 1.6e16 — which is Infinity wearing a disguise, and reads as one in a table.
    q: Math.abs(resistanceOhms) < 1e-12 * Math.abs(ohms)
      ? Infinity
      : Math.abs(reactanceOhms / resistanceOhms),
  }
}

/** Ohms with a sensible number of digits and a milliohm range, since fixture impedances live there. */
export function formatOhms(ohms: number): string {
  if (!Number.isFinite(ohms)) return '—'
  if (Math.abs(ohms) < 1) return `${(ohms * 1000).toFixed(1)} mΩ`
  if (Math.abs(ohms) < 100) return `${ohms.toFixed(3)} Ω`
  if (Math.abs(ohms) < 10000) return `${ohms.toFixed(1)} Ω`
  return `${(ohms / 1000).toFixed(2)} kΩ`
}

/** Henries across the range an audio measurement produces, from a fraction of a microhenry to tens of henries. */
export function formatInductance(henries: number | null): string {
  if (henries === null || !Number.isFinite(henries)) return '—'

  const abs = Math.abs(henries)
  if (abs < 1e-6) return `${(henries * 1e9).toFixed(1)} nH`
  if (abs < 1e-3) return `${(henries * 1e6).toFixed(2)} µH`
  if (abs < 1) return `${(henries * 1e3).toFixed(3)} mH`
  return `${henries.toFixed(3)} H`
}

/** Farads, likewise — a crossover capacitor is microfarads, a coupling one nanofarads. */
export function formatCapacitance(farads: number | null): string {
  if (farads === null || !Number.isFinite(farads)) return '—'

  const abs = Math.abs(farads)
  if (abs < 1e-9) return `${(farads * 1e12).toFixed(1)} pF`
  if (abs < 1e-6) return `${(farads * 1e9).toFixed(2)} nF`
  if (abs < 1e-3) return `${(farads * 1e6).toFixed(3)} µF`
  return `${(farads * 1e3).toFixed(3)} mF`
}

/** The reactive component as one string, whichever kind it is. */
export function formatReactiveComponent(equivalent: SeriesEquivalent): string {
  if (equivalent.inductanceH !== null) return formatInductance(equivalent.inductanceH)
  if (equivalent.capacitanceF !== null) return formatCapacitance(equivalent.capacitanceF)
  return '—'
}

/** Amps, in the range an impedance measurement actually drives: microamps to a few hundred milliamps. */
export function formatCurrent(amps: number | null | undefined): string {
  if (amps === null || amps === undefined || !Number.isFinite(amps)) return '—'

  const abs = Math.abs(amps)
  if (abs < 1e-3) return `${(amps * 1e6).toFixed(1)} µA`
  if (abs < 1) return `${(amps * 1e3).toFixed(3)} mA`
  return `${amps.toFixed(3)} A`
}
