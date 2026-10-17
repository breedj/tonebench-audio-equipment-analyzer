/**
 * What an annotation's two numbers are *in* — hertz, milliseconds, ohms, watts, degrees — and the
 * arithmetic space each of those lives in.
 *
 * <p>An annotation on the spectrum chart could get away without this: its x was always a frequency
 * and its y always dBV, so the field names said the unit. Nothing else in the app is like that. The
 * distortion sweep's x is one of four unrelated quantities <i>of the same point</i> (hertz, output
 * volts, output watts, input volts) and its y is percent or dB; the frequency-response chart's y is
 * dBV in its level view and gain dB in its gain view; the impulse chart's y is volts or dB re peak.
 * So a bare number stored against "x" cannot be drawn safely: a label reading 1.4 cannot say whether
 * that is 1.4 W or 1.4 V, and an annotation placed on one axis would be silently redrawn — and
 * printed in the report's table — against another.</p>
 *
 * <p>Hence a quantity id travelling with every annotation, and one rule in {@link layoutAnnotations}:
 * <b>draw it where this axis plots that quantity, skip it where it does not</b> — never move it,
 * never delete it, exactly as an annotation outside the plotted window is already treated. Switch a
 * sweep from hertz to watts and the hertz notes stop being drawn; switch back and they return.</p>
 *
 * <p>The <b>space</b> is the other half, and it belongs to the quantity rather than to the chart.
 * It decides how a label's offset, a region's extent and every clamp are expressed: as a ratio where
 * the quantity is strictly positive by construction (a frequency, an impedance, a percentage of
 * distortion — all plotted on log axes, and all meaningless at or below zero), and as a plain
 * difference otherwise (a level in dB, a waveform's volts, a time that crosses zero, the real part
 * of a complex gain). Keeping it here rather than on each chart's axis is what stops the impedance
 * chart's runtime log↔linear flip from moving a label: |Z| is positive in both modes, so its offsets
 * are ratios in both, and a note keeps its relative place across the switch.</p>
 */

/**
 * How a quantity's offsets and extents are measured.
 *
 * <p>`toSpace` maps a value into the space an offset is added in and `fromSpace` maps it back, so an
 * offset is always `toSpace(there) - toSpace(here)` and a displaced value always
 * `fromSpace(toSpace(here) + offset)`. For a ratio quantity that is log2, i.e. the offset is in
 * octaves — which is what the original annotation feature stored, and why every annotation written
 * before this module existed reads back unchanged.</p>
 */
export interface AnnotationSpace {
  toSpace(value: number): number
  fromSpace(spaceValue: number): number
}

/** Offsets in octaves (log2 ratios): a frequency, an impedance, a voltage, a percentage. */
export const RATIO_SPACE: AnnotationSpace = {
  toSpace: (value) => Math.log2(value),
  fromSpace: (spaceValue) => Math.pow(2, spaceValue),
}

/** Offsets as plain differences: a level in dB, a time, a signed real part. */
export const LINEAR_SPACE: AnnotationSpace = {
  toSpace: (value) => value,
  fromSpace: (spaceValue) => spaceValue,
}

export interface AnnotationQuantity {
  /** For a table column head and the panel's field label — "Frequency", "Output power". */
  label: string
  /** As it follows a number — "Hz", "W", "°". Empty where the quantity has none. */
  unit: string
  space: AnnotationSpace
  /** True where zero and below have no meaning, which is every ratio quantity. */
  positiveOnly: boolean
  /** How many decimals a retypeable number box shows. Display only — a retype stores what was typed. */
  decimals: number
  /** One value, as this quantity is read — including its unit, and scaled where that is the custom. */
  format(value: number): string
  /** A range of this quantity, in one unit chosen from its high end. */
  formatRange(lo: number, hi: number): string
}

/**
 * A frequency as the charts' own labels print it: whole hertz low down, where the bins are fractions
 * of one, and kilohertz above 10 kHz, where four digits of hertz say nothing.
 */
export function formatAnnotationHz(freqHz: number): string {
  if (!Number.isFinite(freqHz)) return '—'
  if (freqHz >= 10000) return `${(freqHz / 1000).toFixed(2)} kHz`
  if (freqHz >= 1000) return `${(freqHz / 1000).toFixed(3)} kHz`
  if (freqHz >= 100) return `${freqHz.toFixed(1)} Hz`
  return `${freqHz.toFixed(2)} Hz`
}

/**
 * A frequency range in <b>one</b> unit chosen from its high end — "0.90 – 1.20 kHz" rather than
 * "900.0 Hz – 1.200 kHz". A range is read as one quantity, and two units inside it make the reader
 * convert before they can compare the ends.
 */
export function formatAnnotationHzRange(loHz: number, hiHz: number): string {
  if (!Number.isFinite(loHz) || !Number.isFinite(hiHz)) return '—'
  if (hiHz >= 1000) return `${(loHz / 1000).toFixed(2)} – ${(hiHz / 1000).toFixed(2)} kHz`
  return `${loHz.toFixed(1)} – ${hiHz.toFixed(1)} Hz`
}

/** A fixed-decimals quantity: "−95.0 dBV", and a range as "−95.0 – −40.0 dBV". */
function fixed(label: string, unit: string, decimals: number, space: AnnotationSpace): AnnotationQuantity {
  const one = (value: number) => (Number.isFinite(value) ? value.toFixed(decimals) : '—')
  return {
    label,
    unit,
    space,
    positiveOnly: space === RATIO_SPACE,
    decimals,
    format: (value) =>
      Number.isFinite(value) ? (unit ? `${one(value)} ${unit}` : one(value)) : '—',
    formatRange: (lo, hi) =>
      Number.isFinite(lo) && Number.isFinite(hi)
        ? unit
          ? `${one(lo)} – ${one(hi)} ${unit}`
          : `${one(lo)} – ${one(hi)}`
        : '—',
  }
}

/**
 * A quantity printed with three significant figures and no unit scaling — for the ones that span
 * decades on a log axis, where a fixed number of decimals reads as 0.00 at one end and 12345.678 at
 * the other.
 */
function decades(label: string, unit: string, space: AnnotationSpace): AnnotationQuantity {
  const one = (value: number) => {
    if (!Number.isFinite(value)) return '—'
    const magnitude = Math.abs(value)
    if (magnitude === 0) return '0'
    if (magnitude >= 100) return value.toFixed(0)
    if (magnitude >= 10) return value.toFixed(1)
    if (magnitude >= 1) return value.toFixed(2)
    if (magnitude >= 0.001) return value.toFixed(4)
    return value.toExponential(2)
  }
  return {
    label,
    unit,
    space,
    positiveOnly: space === RATIO_SPACE,
    // Four, because a quantity that spans decades needs them at the bottom end -- 0.0012 % is a
    // real distortion reading -- and a box showing 2.4000 W is merely wide, not wrong.
    decimals: 4,
    format: (value) => (Number.isFinite(value) ? (unit ? `${one(value)} ${unit}` : one(value)) : '—'),
    formatRange: (lo, hi) =>
      Number.isFinite(lo) && Number.isFinite(hi)
        ? unit
          ? `${one(lo)} – ${one(hi)} ${unit}`
          : `${one(lo)} – ${one(hi)}`
        : '—',
  }
}

const HZ: AnnotationQuantity = {
  label: 'Frequency',
  unit: 'Hz',
  space: RATIO_SPACE,
  positiveOnly: true,
  decimals: 2,
  format: formatAnnotationHz,
  formatRange: formatAnnotationHzRange,
}

/**
 * Every quantity an annotation can be stored in. Frozen, and one table rather than a per-chart
 * answer, so the chart's badge and the report's table cannot print a number in two different units.
 */
const QUANTITIES: Readonly<Record<string, AnnotationQuantity>> = Object.freeze({
  // --- the frequency axes: spectrum, spectrogram, response, impedance, harmonics, sweep
  hz: HZ,
  // --- levels. All linear: a dB difference is what an offset means, and dB can be negative.
  dbv: fixed('Level', 'dBV', 1, LINEAR_SPACE),
  db: fixed('Gain', 'dB', 1, LINEAR_SPACE),
  dbc: fixed('Level', 'dBc', 1, LINEAR_SPACE),
  deg: fixed('Phase', '°', 1, LINEAR_SPACE),
  // --- the distortion sweeps. Percent is strictly positive and log-plotted, so its offsets are
  //     ratios -- which is also what keeps a label in place across the chart's %/dB switch, a ratio
  //     in percent being a constant difference in dB.
  pct: decades('Distortion', '%', RATIO_SPACE),
  outputVolt: decades('Output level', 'V', RATIO_SPACE),
  outputWatt: decades('Output power', 'W', RATIO_SPACE),
  inputVolt: decades('Input level', 'V', RATIO_SPACE),
  // --- time. Linear, and deliberately so: the impulse response's window crosses t = 0, and the
  //     spectrogram's y is *seconds ago*, i.e. negative throughout.
  ms: fixed('Time', 'ms', 3, LINEAR_SPACE),
  secondsAgo: fixed('Age', 's', 2, LINEAR_SPACE),
  volt: decades('Level', 'V', LINEAR_SPACE),
  // --- impedance. Strictly positive, hence ratio offsets in both the log and the linear axis mode.
  ohm: decades('Impedance', 'Ω', RATIO_SPACE),
  // --- the complex planes. Signed throughout.
  re: fixed('Real part', '', 3, LINEAR_SPACE),
  im: fixed('Imaginary part', '', 3, LINEAR_SPACE),
  sigmaHz: decades('σ', 'Hz', LINEAR_SPACE),
  jwHz: decades('jω', 'Hz', LINEAR_SPACE),
})

/**
 * A fallback for an id this build does not know — a later version's quantity, or a hand-edited
 * `params_json`. It prints the bare number rather than refusing, which is {@link parseAnnotations}'
 * own tolerance rule applied to labelling: an annotation is the user's words, and a note whose unit
 * we cannot name is still worth showing.
 */
const UNKNOWN: AnnotationQuantity = decades('Value', '', LINEAR_SPACE)

export function annotationQuantity(id: string): AnnotationQuantity {
  return QUANTITIES[id] ?? UNKNOWN
}

/** Whether this build knows the quantity — for a test, and for the Edit dialog's field labels. */
export function isKnownQuantity(id: string): boolean {
  return Object.prototype.hasOwnProperty.call(QUANTITIES, id)
}

/** Every id in the table, for a test that walks them all. */
export function annotationQuantityIds(): string[] {
  return Object.keys(QUANTITIES)
}
