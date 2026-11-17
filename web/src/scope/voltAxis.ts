/**
 * The oscilloscope's vertical axis — volts per division and a centre offset, which is how a bench
 * instrument's vertical control works and is not the same thing as this app's other charts.
 *
 * <p><b>Why V/div and an offset rather than a top and a bottom.</b> Every other chart here stores
 * the two ends of its window, because every other chart is read by looking up a value against an
 * axis. A scope is read by <em>counting squares</em>: "two divisions of overshoot" and "half a
 * division of ripple" are the readings, and they only mean anything if a division is a round
 * number. Storing the ends lets a drag-zoom leave the graticule at 0.137 V a square, which is a
 * graticule nobody can count on. So the window is derived — centre ± half the graticule — and the
 * stored pair is the one a user sets.</p>
 */

/** A scope's vertical graticule. Eight divisions is the near-universal convention. */
export const VERTICAL_DIVISIONS = 8

/**
 * The V/div ladder, 1-2-5 per decade.
 *
 * <p>The top rung is set by the hardware rather than by taste: the QA403's widest input range is
 * 42 dBV, i.e. about 126 V rms and 178 V peak, which is 22 V a division over eight of them. 50 V/div
 * is therefore the first rung that can hold anything the analyzer is able to measure, and nothing
 * above it is reachable. The bottom rung is 10 µV/div — 80 µV across the whole graticule, which is
 * under the analyzer's own noise floor on its most sensitive range, so what is drawn down there is
 * the floor itself. That is the point of having it: a scope is how a noise floor is <em>looked
 * at</em>, and a ladder that stopped where the signal stopped could not show one.</p>
 */
export const VOLTS_PER_DIVISION_LADDER: readonly number[] = Object.freeze([
  0.00001, 0.00002, 0.00005,
  0.0001, 0.0002, 0.0005,
  0.001, 0.002, 0.005,
  0.01, 0.02, 0.05,
  0.1, 0.2, 0.5,
  1, 2, 5,
  10, 20, 50,
])

/**
 * How many divisions a fitted trace is allowed to fill.
 *
 * <p>Six of eight, which is what a scope's own autoset aims for: filling all eight leaves a
 * waveform touching both edges, where any overshoot on the next capture is clipped off the top of
 * the plot with nothing saying so.</p>
 */
const FIT_DIVISIONS = 6

/**
 * How near the middle a trace has to sit before the fit centres it on zero exactly.
 *
 * <p>A fraction of one division. Without it an AC-coupled signal, whose midpoint is zero plus
 * whatever noise the record happened to carry, fits with an offset of 0.3 mV and the panel's box
 * reads a figure nobody typed — and a reader then cannot tell a real DC offset from arithmetic
 * showing through.</p>
 */
const ZERO_SNAP_DIVISIONS = 0.02

/** The two ends of the plot, which is what uPlot is given. */
export interface VoltWindow {
  topV: number
  bottomV: number
}

/** Where the graticule puts the two ends, given the pair the user sets. */
export function voltWindow(voltsPerDivision: number, offsetV: number): VoltWindow {
  const vpd = Number.isFinite(voltsPerDivision) && voltsPerDivision > 0 ? voltsPerDivision : 1
  const centre = Number.isFinite(offsetV) ? offsetV : 0
  const half = (vpd * VERTICAL_DIVISIONS) / 2
  return { topV: centre + half, bottomV: centre - half }
}

/** The rung at or above <paramref name="wanted"/>, or the top of the ladder when nothing reaches. */
export function snapVoltsPerDivision(wanted: number): number {
  if (!Number.isFinite(wanted) || wanted <= 0) return VOLTS_PER_DIVISION_LADDER[0]
  return VOLTS_PER_DIVISION_LADDER.find((v) => v >= wanted - v * 1e-9) ?? VOLTS_PER_DIVISION_LADDER.at(-1)!
}

/** One rung up or down the ladder, stopping at either end. */
export function stepVoltsPerDivision(current: number, direction: 1 | -1): number {
  const index = VOLTS_PER_DIVISION_LADDER.findIndex((v) => v >= current - v * 1e-9)
  const at = index < 0 ? VOLTS_PER_DIVISION_LADDER.length - 1 : index
  const next = Math.min(Math.max(at + direction, 0), VOLTS_PER_DIVISION_LADDER.length - 1)
  return VOLTS_PER_DIVISION_LADDER[next]
}

/** The vertical pair a fit produced. */
export interface VoltFit {
  voltsPerDivision: number
  offsetV: number
}

/**
 * The vertical setting that frames a trace running from <paramref name="minV"/> to
 * <paramref name="maxV"/>.
 *
 * <p>The centre is the midpoint of the extremes rather than the mean, for the reason
 * <code>TriggerAutoLevel.FiftyPercent</code> gives about the trigger's own auto-level: a square
 * wave with an unequal duty cycle has a mean nowhere near the middle of the picture, and it is the
 * picture being framed. A flat trace — a DC level, a dead input — is given the bottom rung, which
 * is the most magnified view of whatever small thing is there.</p>
 */
export function fitVoltWindow(maxV: number, minV: number): VoltFit {
  if (!Number.isFinite(maxV) || !Number.isFinite(minV)) {
    return { voltsPerDivision: VOLTS_PER_DIVISION_LADDER.at(-1)!, offsetV: 0 }
  }

  const hi = Math.max(maxV, minV)
  const lo = Math.min(maxV, minV)
  const centre = (hi + lo) / 2
  const half = (hi - lo) / 2

  const voltsPerDivision = snapVoltsPerDivision(half / (FIT_DIVISIONS / 2))
  const offsetV = Math.abs(centre) <= voltsPerDivision * ZERO_SNAP_DIVISIONS ? 0 : centre
  return { voltsPerDivision, offsetV }
}

/**
 * A voltage written for a window of <paramref name="voltsPerDivision"/> — the one formatter the
 * axis ticks, the measurement rows and the cursor readout go through, so a figure means the same
 * thing wherever it is read.
 *
 * <p>The unit follows the <em>division</em> and not the value, which is what keeps a column of
 * readings in one unit instead of switching from mV to V part way down it.</p>
 */
export function formatVolts(volts: number, voltsPerDivision: number, withUnit = true): string {
  if (!Number.isFinite(volts)) return '—'
  const vpd = Number.isFinite(voltsPerDivision) && voltsPerDivision > 0 ? voltsPerDivision : 1
  const { scale, unit } = voltUnitFor(vpd)
  const decimals = vpd * scale >= 1 ? 2 : 3
  const text = (volts * scale).toFixed(decimals)
  const cleaned = /^-0(\.0*)?$/.test(text) ? text.slice(1) : text
  return withUnit ? `${cleaned} ${unit}` : cleaned
}

/**
 * The unit a graticule of this many volts a division is read in.
 *
 * <p>One table rather than a branch in each formatter, so a tick label, a cursor readout and the
 * V/div label itself cannot end up in different units on the same picture — which is the whole
 * point of taking the unit from the division rather than from the value.</p>
 */
function voltUnitFor(voltsPerDivision: number): { scale: number; unit: string } {
  if (voltsPerDivision < 0.001) return { scale: 1e6, unit: 'µV' }
  if (voltsPerDivision < 0.1) return { scale: 1000, unit: 'mV' }
  return { scale: 1, unit: 'V' }
}

/**
 * A dragged voltage, rounded to about a thousandth of the window it was read from.
 *
 * <p>Dragging a line reads a pixel back through the plot's own scale, so what comes out is the
 * arithmetic of that division: <code>0.6017391304347828</code> for a line the user put at roughly
 * six tenths of a volt. A thousandth of the window is far below one pixel of the plot, so the line
 * does not visibly step — but it is the difference between a trigger box reading "0.602" and one
 * reading all seventeen digits.</p>
 *
 * <p><b>As a decimal place, not as a multiplier</b>, and that distinction is the whole of why this
 * is a function rather than one line at the call site: rounding to the nearest multiple of
 * <code>span / 1000</code> puts the digits straight back, because that step is itself not
 * representable — 144 × 0.004 is 0.5760000000000001, which was the box's reading after the first
 * attempt at this.</p>
 */
export function roundVoltsForWindow(volts: number, spanV: number): number {
  if (!Number.isFinite(volts)) return volts
  if (!Number.isFinite(spanV) || spanV <= 0) return volts

  // Clamped at both ends: a window of several hundred volts would otherwise ask for no decimals at
  // all, and one of microvolts for more than a double can carry.
  const decimals = Math.min(12, Math.max(0, Math.ceil(-Math.log10(spanV / 1000))))
  return Number(volts.toFixed(decimals))
}

/** The per-division figure the status line quotes — "500 mV/div". */
export function formatVoltsPerDivision(voltsPerDivision: number): string {
  if (!Number.isFinite(voltsPerDivision) || voltsPerDivision <= 0) return '—'
  const { scale, unit } = voltUnitFor(voltsPerDivision)
  const value = voltsPerDivision * scale
  return `${value >= 1 ? value.toFixed(0) : value.toFixed(1)} ${unit}/div`
}
