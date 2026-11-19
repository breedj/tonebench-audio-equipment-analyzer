/**
 * The oscilloscope's math channel — one waveform derived from the two captured ones.
 *
 * <p>Channels are <b>A and B</b> here rather than left and right, which is the convention every
 * bench instrument's math menu uses and the one that keeps an expression readable: "A − B" says
 * what it does where "left minus right" says which sockets it came from. A is the left input and
 * B the right, and the panel says so beside the dropdown.</p>
 *
 * <p><b>The derived waveform itself is computed on the server, on the samples.</b> It is not
 * optional where that happens: a drawn column carries the min and the max of what it covers, and
 * the difference of two envelopes is not the envelope of a difference — the two extremes need not
 * even fall at the same sample. On a differential pair, which is what A − B is for, the two
 * channels are nearly equal and the small difference between them is the entire reading, so an
 * envelope subtraction would draw zero where the signal is.</p>
 *
 * <p>What is here is the table of operations, what each one's numbers are in, and how to write
 * them — the half nothing in a <code>.vue</code> could be tested on.</p>
 */

export type ScopeMathOperation =
  | 'Off'
  | 'AMinusB'
  | 'BMinusA'
  | 'APlusB'
  | 'ATimesB'
  | 'ADivB'
  | 'NegateA'
  | 'NegateB'
  | 'DerivativeA'
  | 'DerivativeB'
  | 'IntegralA'
  | 'IntegralB'

/**
 * What a math trace's numbers are in.
 *
 * <p><b>This is the honesty requirement of the whole feature.</b> The readings come back in the
 * same record every channel's do, whose fields are named <code>maxV</code>, <code>rmsV</code> and
 * so on — right for A − B and wrong for everything else, and a figure labelled volts on a scale
 * that is not volts is exactly the plausible wrong answer this app's rules exist to prevent.</p>
 */
export type ScopeMathUnit = 'Volt' | 'VoltSquared' | 'Ratio' | 'VoltPerSecond' | 'VoltSecond'

export interface ScopeMathOption {
  value: ScopeMathOperation
  /** What the dropdown says. The expression, because that is what a reader is looking for. */
  label: string
  /** Which group it sits under, so twelve entries read as four short lists. */
  group: string
  unit: ScopeMathUnit
  /** What the tooltip and the panel's note say about it. */
  title: string
}

/**
 * The operations, in the order the dropdown offers them.
 *
 * <p><b>Both polarities of the difference are entries.</b> A differential reading cares which way
 * round it is — a transformer winding, a bridged amplifier, a current shunt — and there is no
 * second operation to chain, so <code>BMinusA</code> is offered rather than left to an invert
 * applied afterwards.</p>
 */
export const SCOPE_MATH_OPERATIONS: readonly ScopeMathOption[] = Object.freeze([
  {
    value: 'Off',
    label: 'Off',
    group: '',
    unit: 'Volt',
    title: 'No math trace. The default, and what costs nothing: a math channel is a pass over the window on every fetch.',
  },
  {
    value: 'AMinusB',
    label: 'A − B',
    group: 'Difference',
    unit: 'Volt',
    title:
      'The left input less the right — a software differential probe. Measured on the samples rather than on the drawn columns, so a difference a thousandth of either channel comes out exactly.',
  },
  {
    value: 'BMinusA',
    label: 'B − A',
    group: 'Difference',
    unit: 'Volt',
    title: 'The same reading with the probes the other way round, which is a different sign and sometimes the one wanted.',
  },
  {
    value: 'APlusB',
    label: 'A + B',
    group: 'Sum',
    unit: 'Volt',
    title:
      'The two inputs added. Twice the common-mode part of a differential pair, and the sum of a push-pull stage’s two halves.',
  },
  {
    value: 'ATimesB',
    label: 'A × B',
    group: 'Product',
    unit: 'VoltSquared',
    title:
      'The two inputs multiplied — instantaneous power where one of them measures a current through a shunt, in which case divide by the shunt’s ohms to read watts. The trace itself is in V².',
  },
  {
    value: 'ADivB',
    label: 'A ÷ B',
    group: 'Product',
    unit: 'Ratio',
    title:
      'The ratio, railed at ±1000. Only meaningful where B is a settled level: against two AC waveforms it rails every time B crosses zero, and for a transfer function the frequency-response measurement is the right tool.',
  },
  {
    value: 'NegateA',
    label: '−A',
    group: 'Invert',
    unit: 'Volt',
    title: 'The left input inverted, for comparing an inverting stage against its own input.',
  },
  {
    value: 'NegateB',
    label: '−B',
    group: 'Invert',
    unit: 'Volt',
    title: 'The right input inverted.',
  },
  {
    value: 'DerivativeA',
    label: 'dA/dt',
    group: 'Calculus',
    unit: 'VoltPerSecond',
    title:
      'The slope of the left input — a slew-rate trace. Differentiation lifts the top of the band some 73 dB over 20 Hz, so on a quiet signal this is a picture of the noise; and nothing faster than about one sample is the DUT rather than this analyzer.',
  },
  {
    value: 'DerivativeB',
    label: 'dB/dt',
    group: 'Calculus',
    unit: 'VoltPerSecond',
    title: 'The slope of the right input.',
  },
  {
    value: 'IntegralA',
    label: '∫A dt',
    group: 'Calculus',
    unit: 'VoltSecond',
    title:
      'The running integral of the left input, with the window’s mean removed first — so it starts at zero and shows the AC part’s integral, which is a flux or a charge and not an absolute one.',
  },
  {
    value: 'IntegralB',
    label: '∫B dt',
    group: 'Calculus',
    unit: 'VoltSecond',
    title: 'The running integral of the right input.',
  },
])

const BY_VALUE = new Map(SCOPE_MATH_OPERATIONS.map((option) => [option.value, option]))

/** Whether a math trace is drawn at all. */
export function mathDraws(operation: ScopeMathOperation): boolean {
  return operation !== 'Off' && BY_VALUE.has(operation)
}

/** The entry for an operation, or Off's for anything this build does not know. */
export function mathOption(operation: ScopeMathOperation): ScopeMathOption {
  return BY_VALUE.get(operation) ?? SCOPE_MATH_OPERATIONS[0]
}

export function mathLabel(operation: ScopeMathOperation): string {
  return mathOption(operation).label
}

export function mathUnitOf(operation: ScopeMathOperation): ScopeMathUnit {
  return mathOption(operation).unit
}

/**
 * Whether the operation reads the right input.
 *
 * <p>Asked by the panel, not by the chart: a measurement that captures one input has no second
 * waveform to combine, and the honest answer is no trace with a line saying why — the server
 * answers null for exactly that case, and an absent trace with nothing beside it reads as a broken
 * setting.</p>
 */
export function mathNeedsB(operation: ScopeMathOperation): boolean {
  return (
    operation !== 'Off' &&
    operation !== 'NegateA' &&
    operation !== 'DerivativeA' &&
    operation !== 'IntegralA'
  )
}

/** Whether it reads the left input. */
export function mathNeedsA(operation: ScopeMathOperation): boolean {
  return (
    operation !== 'Off' &&
    operation !== 'NegateB' &&
    operation !== 'DerivativeB' &&
    operation !== 'IntegralB'
  )
}

/** Which inputs an operation needs, in the words the panel's refusal uses. */
export function mathMissingChannels(
  operation: ScopeMathOperation,
  capturedLeft: boolean,
  capturedRight: boolean,
): string[] {
  const missing: string[] = []
  if (mathNeedsA(operation) && !capturedLeft) missing.push('left')
  if (mathNeedsB(operation) && !capturedRight) missing.push('right')
  return missing
}

/** The axis label a math trace is read against — the expression and its unit. */
export function mathAxisLabel(operation: ScopeMathOperation): string {
  const unit = mathUnitSuffix(mathUnitOf(operation))
  return unit ? `${mathLabel(operation)} (${unit})` : mathLabel(operation)
}

/** The bare unit as it is written, or an empty string for a ratio, which has none. */
export function mathUnitSuffix(unit: ScopeMathUnit): string {
  switch (unit) {
    case 'Volt':
      return 'V'
    case 'VoltSquared':
      return 'V²'
    case 'VoltPerSecond':
      return 'V/s'
    case 'VoltSecond':
      return 'V·s'
    case 'Ratio':
    default:
      return ''
  }
}

/** The SI prefixes a magnitude is written with, from the smallest up. */
const PREFIXES: readonly { scale: number; prefix: string }[] = Object.freeze([
  { scale: 1e-12, prefix: 'p' },
  { scale: 1e-9, prefix: 'n' },
  { scale: 1e-6, prefix: 'µ' },
  { scale: 1e-3, prefix: 'm' },
  { scale: 1, prefix: '' },
  { scale: 1e3, prefix: 'k' },
  { scale: 1e6, prefix: 'M' },
])

/**
 * A figure at its own magnitude: four significant figures under the largest SI prefix it still
 * reads as at least one of.
 *
 * <p>Exported because the measurement readout under the chart needs the same ladder for seconds and
 * hertz, and for the reason the readout exists at all: a <em>statistic</em> of a reading is
 * routinely orders below the reading, so the axis formatters — which write in the unit the
 * graticule is in, deliberately — would print a spread of 40 mHz as <code>0.00 Hz</code> and a
 * jitter of 800 ps as <code>0.00 µs</code>. Those are exactly the figures a statistics column is
 * read for.</p>
 */
export function formatSiValue(value: number, suffix: string): string {
  if (!Number.isFinite(value)) return '—'
  const magnitude = Math.abs(value)
  if (magnitude === 0) return suffix ? `0.000 ${suffix}` : '0.000'

  let chosen = PREFIXES[0]
  for (const candidate of PREFIXES) {
    if (magnitude >= candidate.scale) chosen = candidate
  }
  const written = sig(value / chosen.scale)
  const unit = `${chosen.prefix}${suffix}`
  return unit ? `${written} ${unit}` : written
}

/** Four significant figures, which is what every reading on this screen is written to. */
function sig(value: number): string {
  const magnitude = Math.abs(value)
  if (magnitude >= 100) return value.toFixed(1)
  if (magnitude >= 10) return value.toFixed(2)
  return value.toFixed(3)
}

/**
 * A math reading as the panel and the report both write it.
 *
 * <p><b>The rate's denominator is scaled rather than its numerator</b>, so a slew rate reads
 * 2.400 V/µs and not 2.400 MV/s. The two are the same figure, and the first is the one every
 * amplifier datasheet quotes — which is the whole reason anyone turns on a derivative trace.</p>
 */
export function formatMathValue(value: number, unit: ScopeMathUnit): string {
  if (!Number.isFinite(value)) return '—'

  if (unit === 'Ratio') return sig(value)

  if (unit === 'VoltPerSecond') {
    const magnitude = Math.abs(value)
    if (magnitude >= 1e6) return `${sig(value / 1e6)} V/µs`
    if (magnitude >= 1e3) return `${sig(value / 1e3)} V/ms`
    return `${sig(value)} V/s`
  }

  return formatSiValue(value, mathUnitSuffix(unit))
}

/**
 * What to send as the <code>math</code> query parameter, or null to ask for no trace at all.
 *
 * <p>Off is <em>absent</em> rather than the string "Off", so the commonest state puts nothing on
 * the query at all and the fetch is byte-identical to the one before this existed.</p>
 */
export function mathQueryValue(operation: ScopeMathOperation): string | null {
  return mathDraws(operation) ? operation : null
}
