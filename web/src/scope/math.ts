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

// ---------------------------------------------------------------------------
// Two channels, each with its own chosen sources
// ---------------------------------------------------------------------------

/** Which captured input an operand reads. */
export type ScopeMathSource = 'ch0' | 'ch1'

export const SCOPE_MATH_SOURCES: readonly ScopeMathSource[] = Object.freeze(['ch0', 'ch1'])

/** What a source is called in an expression. The socket, which is what the reader chose. */
export const SCOPE_MATH_SOURCE_LABELS: Readonly<Record<ScopeMathSource, string>> = Object.freeze({
  ch0: 'L',
  ch1: 'R',
})

/** Which record channel a source is. */
export function mathSourceChannel(source: ScopeMathSource): number {
  return source === 'ch1' ? 1 : 0
}

/**
 * And back: which source a record channel is.
 *
 * <p>The wire carries the two operands as channel indices, since that is what the analyzer reads
 * them as, and everything the reader sees is written in sockets — so a slice has to be read back
 * into a spec before it can be labelled. Anything that is not the right input is the left, which
 * is the same way round as <code>mathSourceChannel</code>.</p>
 */
export function mathSourceOfChannel(channel: number): ScopeMathSource {
  return channel === 1 ? 'ch1' : 'ch0'
}

export function isScopeMathSource(value: unknown): value is ScopeMathSource {
  return value === 'ch0' || value === 'ch1'
}

/** One math channel: what it does, and to which two inputs. */
export interface ScopeMathSpec {
  operation: ScopeMathOperation
  sourceA: ScopeMathSource
  sourceB: ScopeMathSource
}

/** Off, on the left and right inputs — which is what a stored measurement with no sources means. */
export const DEFAULT_SCOPE_MATH_SPEC: Readonly<ScopeMathSpec> = Object.freeze({
  operation: 'Off' as ScopeMathOperation,
  sourceA: 'ch0' as ScopeMathSource,
  sourceB: 'ch1' as ScopeMathSource,
})

/** Which of the two math channels. Numbered rather than named, as a scope numbers them. */
export type ScopeMathSlot = 1 | 2

export const SCOPE_MATH_SLOTS: readonly ScopeMathSlot[] = Object.freeze([1, 2])

export interface ScopeMathTile {
  /** The operation it writes, with A and B in the order the sources are given. */
  operation: ScopeMathOperation
  label: string
  /** A figure from scope/displayIcons.ts. */
  icon: string
  /** Whether the second operand means anything — the three one-operand tiles say no. */
  usesB: boolean
  title: string
}

/**
 * The operations the dialog offers.
 *
 * <p><b>Seven, where the table above has eleven</b> — and that is what choosing the sources buys.
 * A − B and B − A are one operation with the operands swapped; so are −A and −B, dA/dt and dB/dt,
 * ∫A and ∫B. Offering both of each made sense while A was always the left input and B always the
 * right, because there was no other way to say it; with a Source A and a Source B control there
 * is, and two tiles differing only in which socket they name are two chances to pick the wrong
 * one.</p>
 *
 * <p><b>The server's own enum is untouched.</b> All eleven names still parse — a stored
 * <code>BMinusA</code> reopens as A − B with its sources the other way round, through
 * <code>mathTileFor</code> — so nothing saved loses its meaning and no C# changed with this.</p>
 */
export const SCOPE_MATH_TILES: readonly ScopeMathTile[] = Object.freeze([
  {
    operation: 'AMinusB' as ScopeMathOperation,
    label: 'A − B',
    icon: 'mathSubtract',
    usesB: true,
    title:
      'A software differential probe. Measured on the samples rather than on the drawn columns, so a difference a thousandth of either input comes out exactly. Swap the sources for the other polarity.',
  },
  {
    operation: 'APlusB' as ScopeMathOperation,
    label: 'A + B',
    icon: 'mathAdd',
    usesB: true,
    title:
      'The two added. Twice the common-mode part of a differential pair, and the sum of a push-pull stage’s two halves.',
  },
  {
    operation: 'ATimesB' as ScopeMathOperation,
    label: 'A × B',
    icon: 'mathMultiply',
    usesB: true,
    title:
      'Instantaneous power where one input measures a current through a shunt — divide by the shunt’s ohms to read watts. The trace itself is in V².',
  },
  {
    operation: 'ADivB' as ScopeMathOperation,
    label: 'A ÷ B',
    icon: 'mathDivide',
    usesB: true,
    title:
      'The ratio, railed at ±1000. Only meaningful where B is a settled level: against two AC waveforms it rails every time B crosses zero, and for a transfer function the frequency-response measurement is the right tool.',
  },
  {
    operation: 'NegateA' as ScopeMathOperation,
    label: '−A',
    icon: 'mathNegate',
    usesB: false,
    title: 'A inverted, for comparing an inverting stage against its own input.',
  },
  {
    operation: 'DerivativeA' as ScopeMathOperation,
    label: 'dA/dt',
    icon: 'mathDerivative',
    usesB: false,
    title:
      'The slope of A — a slew-rate trace. Differentiation lifts the top of the band some 73 dB over 20 Hz, so on a quiet signal this is a picture of the noise; and nothing faster than about one sample is the DUT rather than this analyzer.',
  },
  {
    operation: 'IntegralA' as ScopeMathOperation,
    label: '∫A dt',
    icon: 'mathIntegral',
    usesB: false,
    title:
      'The running integral of A, with the window’s mean removed first — so it starts at zero and shows the AC part’s integral, which is a flux or a charge and not an absolute one.',
  },
])

/** The four operations whose operands are the other way round from the tile that writes them. */
const SWAPPED_OPERATIONS: Readonly<Partial<Record<ScopeMathOperation, ScopeMathOperation>>> =
  Object.freeze({
    BMinusA: 'AMinusB' as ScopeMathOperation,
    NegateB: 'NegateA' as ScopeMathOperation,
    DerivativeB: 'DerivativeA' as ScopeMathOperation,
    IntegralB: 'IntegralA' as ScopeMathOperation,
  })

/**
 * Which tile a stored operation reads as, and whether its operands are swapped.
 *
 * <p>This is the whole of the compatibility story: a measurement saved as <code>dB/dt</code>
 * reopens on the <b>dA/dt</b> tile with A pointing at the right input, which is the same trace by
 * another name. Null for <code>Off</code> and for a name this build does not know.</p>
 */
export function mathTileFor(
  operation: ScopeMathOperation,
): { tile: ScopeMathTile; swapped: boolean } | null {
  const direct = SCOPE_MATH_TILES.find((t) => t.operation === operation)
  if (direct) return { tile: direct, swapped: false }
  const mirrored = SWAPPED_OPERATIONS[operation]
  if (!mirrored) return null
  const tile = SCOPE_MATH_TILES.find((t) => t.operation === mirrored)
  return tile ? { tile, swapped: true } : null
}

/**
 * A stored operation read back as a spec on the sources it was really using.
 *
 * <p>With no sources stored — every measurement saved before 2026-11-20 — A is the left input and
 * B the right, which is what the operation names meant then. A mirrored operation comes back as
 * its tile with the pair exchanged, so the trace is unchanged.</p>
 */
export function mathSpecFromStored(
  operation: ScopeMathOperation,
  sourceA: unknown,
  sourceB: unknown,
): ScopeMathSpec {
  const stored = mathTileFor(operation)
  if (!stored) return { ...DEFAULT_SCOPE_MATH_SPEC }

  let a: ScopeMathSource = isScopeMathSource(sourceA) ? sourceA : 'ch0'
  let b: ScopeMathSource = isScopeMathSource(sourceB) ? sourceB : 'ch1'
  if (stored.swapped) [a, b] = [b, a]
  return { operation: stored.tile.operation, sourceA: a, sourceB: b }
}

/** Whether this channel draws anything. */
export function mathSpecDraws(spec: ScopeMathSpec): boolean {
  return mathDraws(spec.operation)
}

/**
 * What the expression reads as with its sources filled in — "L − R", "dR/dt".
 *
 * <p>The sockets rather than A and B, because with the operands chosen the letters no longer say
 * which is which. The bare <code>mathLabel</code> stays for the tile, where A and B are exactly
 * what is meant.</p>
 */
export function mathSpecLabel(spec: ScopeMathSpec): string {
  if (!mathSpecDraws(spec)) return 'Off'
  const resolved = mathTileFor(spec.operation)
  if (!resolved) return 'Off'
  // A stored mirrored operation keeps its own operand order, so its label reads the same way round
  // as the trace it draws.
  const a = SCOPE_MATH_SOURCE_LABELS[resolved.swapped ? spec.sourceB : spec.sourceA]
  const b = SCOPE_MATH_SOURCE_LABELS[resolved.swapped ? spec.sourceA : spec.sourceB]
  // Substituted into the tile's own label rather than matched out of it with a word-boundary
  // regex, which cannot see the A in "dA/dt" — the letter sits against a word character there.
  return resolved.tile.label.replace('A', a).replace('B', b)
}

/**
 * The same label for a slice off the wire, whose operands are channel indices.
 *
 * <p>One function rather than each reader building a spec of its own, because there are three of
 * them — the chart's axis, the measurement picker's source list and the report's table heading —
 * and a reader that forgot would write <em>A + B</em> where the rail beside it writes
 * <em>L + R</em>.</p>
 */
export function mathSliceAxisLabel(slice: {
  operation: string
  sourceA: number
  sourceB: number
}): string {
  return mathSpecAxisLabel({
    operation: slice.operation as ScopeMathOperation,
    sourceA: mathSourceOfChannel(slice.sourceA),
    sourceB: mathSourceOfChannel(slice.sourceB),
  })
}

/** The axis a math trace is read against — the expression with its sources, and its unit. */
export function mathSpecAxisLabel(spec: ScopeMathSpec): string {
  const unit = mathUnitSuffix(mathUnitOf(spec.operation))
  const label = mathSpecLabel(spec)
  return unit ? `${label} (${unit})` : label
}

/**
 * Which sources this channel needs and the record has not, by their own labels.
 *
 * <p>By <em>source</em> rather than by socket, which is the change choosing them forces: the
 * refusal has to name the input the reader picked, not the one the operand used to be fixed to.</p>
 */
export function mathSpecMissing(
  spec: ScopeMathSpec,
  capturedLeft: boolean,
  capturedRight: boolean,
): string[] {
  const has = (source: ScopeMathSource) => (source === 'ch1' ? capturedRight : capturedLeft)
  const missing = new Set<string>()
  if (mathNeedsA(spec.operation) && !has(spec.sourceA)) missing.add(SCOPE_MATH_SOURCE_LABELS[spec.sourceA])
  if (mathNeedsB(spec.operation) && !has(spec.sourceB)) missing.add(SCOPE_MATH_SOURCE_LABELS[spec.sourceB])
  return [...missing]
}

/**
 * One channel's query token, or null to ask for no trace at all.
 *
 * <p><code>operation:sourceA:sourceB</code> in one parameter rather than three, so the commonest
 * state — off — still puts nothing on the query and the fetch is byte-identical to one with no
 * math at all. Absent is off; the literal "Off" is tolerated; an unknown name is a 400, because a
 * trace quietly absent is how a display setting comes to look broken.</p>
 */
export function mathSpecQuery(spec: ScopeMathSpec): string | null {
  if (!mathSpecDraws(spec)) return null
  return `${spec.operation}:${spec.sourceA}:${spec.sourceB}`
}

/**
 * One slot's slice out of a record, or null where that channel drew nothing.
 *
 * <p><b>By slot rather than by position</b>: the record carries only the channels that built, so
 * Math 2 alone arrives as a list of one — and reading <code>maths[1]</code> would then find
 * nothing while <code>maths[0]</code> was quietly the wrong channel's trace.</p>
 *
 * <p>Everything that reads a math slice goes through this, which is the point: when the record
 * carried a single <code>math</code> field there were eight sites spelling it out, and two
 * channels would have meant sixteen.</p>
 */
export function scopeMathSlice<T extends { slot: number }>(
  record: { maths?: readonly T[] | null } | null | undefined,
  slot: ScopeMathSlot,
): T | null {
  return record?.maths?.find((m) => m.slot === slot) ?? null
}
