/**
 * Which unit an impulse response's volts axis is drawn in, chosen from the trace's own peak.
 *
 * Pure and in its own module rather than inside `ImpulseResponseChart.vue`, for the reason
 * `reorder.ts` is: nothing in a `.vue` file is testable under the node environment vitest runs in,
 * and this is arithmetic whose failure is silent.
 *
 * **Why it exists at all.** uPlot's default axis formatter is `Intl.NumberFormat` with its default
 * options — three fraction digits. Every other chart in this app plots decibels, where values are
 * tens and that is fine. The impulse response is the only linear-magnitude axis, and it spans six
 * decades between a power amplifier and a phono cartridge, so a quiet DUT labelled *every* tick
 * "0" and the trace looked like a plotting glitch rather than a small signal. Scaling into V / mV /
 * µV keeps the numbers in the 1–1000 range where the default formatter is right.
 */
export interface ImpulseUnit {
  /** Multiply volts by this to get the plotted value. */
  factor: number
  /** The y-axis label. */
  label: string
  /** The short form, for the peak readout. */
  suffix: string
}

const VOLTS: ImpulseUnit = { factor: 1, label: 'Volts', suffix: 'V' }
const MILLIVOLTS: ImpulseUnit = { factor: 1e3, label: 'Millivolts (mV)', suffix: 'mV' }
const MICROVOLTS: ImpulseUnit = { factor: 1e6, label: 'Microvolts (µV)', suffix: 'µV' }

/**
 * @param peakVolts The impulse's largest excursion. Zero or negative — no measurement yet — yields
 * volts, so an empty chart carries a sensible axis rather than an arbitrary one.
 */
export function impulseUnit(peakVolts: number): ImpulseUnit {
  if (!(peakVolts > 0) || peakVolts >= 1) return VOLTS
  if (peakVolts >= 1e-3) return MILLIVOLTS
  return MICROVOLTS
}

/**
 * The peak as the chart header states it: three significant figures in whichever unit the axis is
 * using, e.g. `954 mV`, `12.3 µV`, `1.07 V`.
 *
 * Fixed *significant* figures rather than fixed decimals, because a fixed-decimal readout is what
 * printed "0.0000 V" for a 95 µV impulse — the same defect as the axis, in the one line that was
 * supposed to tell you the number.
 */
export function formatImpulsePeak(peakVolts: number, unit: ImpulseUnit = impulseUnit(peakVolts)): string {
  const scaled = peakVolts * unit.factor
  const digits = scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2
  return `${scaled.toFixed(digits)} ${unit.suffix}`
}
