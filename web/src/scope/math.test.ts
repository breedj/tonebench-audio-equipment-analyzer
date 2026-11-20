import { describe, expect, it } from 'vitest'
import {
  SCOPE_MATH_OPERATIONS,
  formatMathValue,
  mathAxisLabel,
  mathDraws,
  mathLabel,
  mathMissingChannels,
  mathNeedsA,
  mathNeedsB,
  mathQueryValue,
  mathUnitOf,
  mathUnitSuffix,
  type ScopeMathOperation,
  DEFAULT_SCOPE_MATH_SPEC,
  SCOPE_MATH_TILES,
  mathSliceAxisLabel,
  mathSourceOfChannel,
  mathSpecAxisLabel,
  mathSpecFromStored,
  mathSpecLabel,
  mathSpecMissing,
  mathSpecQuery,
  mathTileFor,
  scopeMathSlice,
  type ScopeMathSpec,
} from './math'

describe('the operations table', () => {
  it('offers every operation exactly once, Off first', () => {
    const values = SCOPE_MATH_OPERATIONS.map((o) => o.value)
    expect(values[0]).toBe('Off')
    expect(new Set(values).size).toBe(values.length)
    expect(values.length).toBe(12)
  })

  it('gives every operation a label, a unit and something to say about it', () => {
    for (const option of SCOPE_MATH_OPERATIONS) {
      expect(option.label.length).toBeGreaterThan(0)
      expect(option.title.length).toBeGreaterThan(20)
      expect(['Volt', 'VoltSquared', 'Ratio', 'VoltPerSecond', 'VoltSecond']).toContain(option.unit)
    }
  })

  // The honesty requirement: the readings come back in a record whose fields are all named
  // "...V", so anything but a volt trace has to be relabelled from this table or the panel prints
  // a figure in volts on a scale that is not volts.
  it('carries the unit each expression is really in', () => {
    expect(mathUnitOf('AMinusB')).toBe('Volt')
    expect(mathUnitOf('BMinusA')).toBe('Volt')
    expect(mathUnitOf('APlusB')).toBe('Volt')
    expect(mathUnitOf('NegateA')).toBe('Volt')
    expect(mathUnitOf('NegateB')).toBe('Volt')
    expect(mathUnitOf('ATimesB')).toBe('VoltSquared')
    expect(mathUnitOf('ADivB')).toBe('Ratio')
    expect(mathUnitOf('DerivativeA')).toBe('VoltPerSecond')
    expect(mathUnitOf('DerivativeB')).toBe('VoltPerSecond')
    expect(mathUnitOf('IntegralA')).toBe('VoltSecond')
    expect(mathUnitOf('IntegralB')).toBe('VoltSecond')
  })

  it('offers both polarities of the difference, because a differential reading has a sign', () => {
    expect(mathLabel('AMinusB')).toBe('A − B')
    expect(mathLabel('BMinusA')).toBe('B − A')
  })

  // A name from a newer build, or a hand-edited params_json, must not reach the chart as an
  // operation nothing knows: it falls back to Off, which draws nothing.
  it('reads an operation it does not know as Off', () => {
    const unknown = 'FourierTransformOfA' as ScopeMathOperation
    expect(mathDraws(unknown)).toBe(false)
    expect(mathLabel(unknown)).toBe('Off')
    expect(mathQueryValue(unknown)).toBeNull()
  })

  it('asks for no trace at all when it is Off, rather than for one called Off', () => {
    expect(mathQueryValue('Off')).toBeNull()
    expect(mathQueryValue('AMinusB')).toBe('AMinusB')
    expect(mathDraws('Off')).toBe(false)
    expect(mathDraws('AMinusB')).toBe(true)
  })
})

describe('which inputs an operation needs', () => {
  it('names only the channels it reads', () => {
    expect(mathNeedsA('AMinusB')).toBe(true)
    expect(mathNeedsB('AMinusB')).toBe(true)

    expect(mathNeedsA('NegateA')).toBe(true)
    expect(mathNeedsB('NegateA')).toBe(false)
    expect(mathNeedsA('DerivativeA')).toBe(true)
    expect(mathNeedsB('DerivativeA')).toBe(false)
    expect(mathNeedsA('IntegralA')).toBe(true)
    expect(mathNeedsB('IntegralA')).toBe(false)

    expect(mathNeedsA('NegateB')).toBe(false)
    expect(mathNeedsB('NegateB')).toBe(true)
    expect(mathNeedsA('DerivativeB')).toBe(false)
    expect(mathNeedsB('DerivativeB')).toBe(true)
    expect(mathNeedsA('IntegralB')).toBe(false)
    expect(mathNeedsB('IntegralB')).toBe(true)
  })

  it('needs nothing at all when it is Off', () => {
    expect(mathNeedsA('Off')).toBe(false)
    expect(mathNeedsB('Off')).toBe(false)
    expect(mathMissingChannels('Off', false, false)).toEqual([])
  })

  // The server answers null where a needed channel was not captured, so the panel has to be able
  // to say which one — an absent trace with nothing beside it reads as a broken setting.
  it('says which input is missing', () => {
    expect(mathMissingChannels('AMinusB', true, false)).toEqual(['right'])
    expect(mathMissingChannels('AMinusB', false, true)).toEqual(['left'])
    expect(mathMissingChannels('AMinusB', false, false)).toEqual(['left', 'right'])
    expect(mathMissingChannels('AMinusB', true, true)).toEqual([])
    expect(mathMissingChannels('DerivativeA', true, false)).toEqual([])
    expect(mathMissingChannels('DerivativeB', true, false)).toEqual(['right'])
  })
})

describe('units and axis labels', () => {
  it('writes each unit the way it is read', () => {
    expect(mathUnitSuffix('Volt')).toBe('V')
    expect(mathUnitSuffix('VoltSquared')).toBe('V²')
    expect(mathUnitSuffix('VoltPerSecond')).toBe('V/s')
    expect(mathUnitSuffix('VoltSecond')).toBe('V·s')
    // A ratio has no unit, and a parenthesis holding nothing is worse than none.
    expect(mathUnitSuffix('Ratio')).toBe('')
  })

  it('labels the axis with the expression and its unit', () => {
    expect(mathAxisLabel('AMinusB')).toBe('A − B (V)')
    expect(mathAxisLabel('ATimesB')).toBe('A × B (V²)')
    expect(mathAxisLabel('DerivativeA')).toBe('dA/dt (V/s)')
    expect(mathAxisLabel('ADivB')).toBe('A ÷ B')
  })
})

describe('formatting a reading', () => {
  it('scales a voltage by SI prefix', () => {
    expect(formatMathValue(1.5, 'Volt')).toBe('1.500 V')
    expect(formatMathValue(0.0123, 'Volt')).toBe('12.30 mV')
    expect(formatMathValue(4.5e-6, 'Volt')).toBe('4.500 µV')
    expect(formatMathValue(-0.25, 'Volt')).toBe('-250.0 mV')
    expect(formatMathValue(0, 'Volt')).toBe('0.000 V')
  })

  // The denominator is scaled, not the numerator: 2.400 V/µs is the figure an amplifier datasheet
  // quotes, and 2.400 MV/s is the same number nobody writes.
  it('writes a slew rate in volts per microsecond', () => {
    expect(formatMathValue(2.4e6, 'VoltPerSecond')).toBe('2.400 V/µs')
    expect(formatMathValue(1.2345e9, 'VoltPerSecond')).toBe('1234.5 V/µs')
    expect(formatMathValue(4_500, 'VoltPerSecond')).toBe('4.500 V/ms')
    expect(formatMathValue(12.5, 'VoltPerSecond')).toBe('12.50 V/s')
  })

  it('writes V² and V·s with their own unit', () => {
    expect(formatMathValue(0.5, 'VoltSquared')).toBe('500.0 mV²')
    expect(formatMathValue(2.5e-5, 'VoltSecond')).toBe('25.00 µV·s')
  })

  it('writes a ratio as a bare number', () => {
    expect(formatMathValue(0.5, 'Ratio')).toBe('0.500')
    expect(formatMathValue(1000, 'Ratio')).toBe('1000.0')
  })

  // Nothing on the wire can be a NaN -- the server will not write one -- but a reading derived on
  // the client can be, and a dash is better than "NaN V".
  it('writes a dash for anything that is not a number', () => {
    expect(formatMathValue(Number.NaN, 'Volt')).toBe('—')
    expect(formatMathValue(Number.POSITIVE_INFINITY, 'Ratio')).toBe('—')
  })
})

describe('the seven tiles', () => {
  it('cover every operation once, with the mirrored four reading back onto them', () => {
    // Eleven operations, seven tiles: A - B and B - A are one with the operands swapped, and so
    // are the three one-operand pairs. Every one of the eleven still has to resolve, because a
    // stored measurement can carry any of them.
    for (const option of SCOPE_MATH_OPERATIONS) {
      if (option.value === 'Off') {
        expect(mathTileFor(option.value)).toBeNull()
        continue
      }
      const resolved = mathTileFor(option.value)
      expect(resolved, option.value).not.toBeNull()
      expect(SCOPE_MATH_TILES).toContain(resolved!.tile)
    }
  })

  it('swaps exactly the four whose operands are the other way round', () => {
    const swapped = SCOPE_MATH_OPERATIONS.filter(
      (o) => o.value !== 'Off' && mathTileFor(o.value)?.swapped === true,
    ).map((o) => o.value)
    expect(swapped.sort()).toEqual(['BMinusA', 'DerivativeB', 'IntegralB', 'NegateB'])
  })

  it('gives every tile a figure, a sentence and an operation the server knows', () => {
    const known = new Set(SCOPE_MATH_OPERATIONS.map((o) => o.value))
    for (const tile of SCOPE_MATH_TILES) {
      expect(known.has(tile.operation), tile.label).toBe(true)
      expect(tile.icon.length).toBeGreaterThan(0)
      expect(tile.title.length, tile.label).toBeGreaterThan(30)
    }
  })

  it('says which tiles read a second operand', () => {
    const oneOperand = SCOPE_MATH_TILES.filter((t) => !t.usesB).map((t) => t.operation)
    expect(oneOperand.sort()).toEqual(['DerivativeA', 'IntegralA', 'NegateA'])
    for (const tile of SCOPE_MATH_TILES) {
      expect(mathNeedsB(tile.operation), tile.label).toBe(tile.usesB)
    }
  })
})

describe('mathSpecFromStored', () => {
  it('reads an operation with no sources as the left and right inputs', () => {
    // Every measurement saved before the sources could be chosen. A was the left input and B the
    // right, which is what the names meant then.
    expect(mathSpecFromStored('AMinusB', undefined, undefined)).toEqual({
      operation: 'AMinusB',
      sourceA: 'ch0',
      sourceB: 'ch1',
    })
  })

  it('reads a mirrored operation as its own tile with the pair exchanged', () => {
    // B - A on (left, right) is A - B on (right, left): the same trace, by another name.
    expect(mathSpecFromStored('BMinusA', undefined, undefined)).toEqual({
      operation: 'AMinusB',
      sourceA: 'ch1',
      sourceB: 'ch0',
    })
    expect(mathSpecFromStored('DerivativeB', undefined, undefined)).toEqual({
      operation: 'DerivativeA',
      sourceA: 'ch1',
      sourceB: 'ch0',
    })
  })

  it('takes stored sources where there are some', () => {
    expect(mathSpecFromStored('ATimesB', 'ch1', 'ch1')).toEqual({
      operation: 'ATimesB',
      sourceA: 'ch1',
      sourceB: 'ch1',
    })
  })

  it('answers Off for a name this build does not know, rather than drawing something else', () => {
    expect(mathSpecFromStored('Nonesuch' as never, 'ch0', 'ch1')).toEqual(DEFAULT_SCOPE_MATH_SPEC)
    expect(mathSpecFromStored('Off', 'ch0', 'ch1')).toEqual(DEFAULT_SCOPE_MATH_SPEC)
  })

  it('ignores a source it has never heard of', () => {
    expect(mathSpecFromStored('AMinusB', 'stim', 42)).toEqual({
      operation: 'AMinusB',
      sourceA: 'ch0',
      sourceB: 'ch1',
    })
  })
})

describe('mathSpecLabel and mathSpecQuery', () => {
  const spec = (over: Partial<ScopeMathSpec> = {}): ScopeMathSpec => ({
    ...DEFAULT_SCOPE_MATH_SPEC,
    operation: 'AMinusB',
    ...over,
  })

  it('writes the sockets rather than A and B, which no longer say which is which', () => {
    expect(mathSpecLabel(spec())).toBe('L − R')
    expect(mathSpecLabel(spec({ sourceA: 'ch1', sourceB: 'ch0' }))).toBe('R − L')
    expect(mathSpecLabel(spec({ operation: 'DerivativeA', sourceA: 'ch1' }))).toBe('dR/dt')
  })

  it('says Off rather than an expression for a channel that draws nothing', () => {
    expect(mathSpecLabel(DEFAULT_SCOPE_MATH_SPEC)).toBe('Off')
  })

  it('carries the unit onto the axis', () => {
    expect(mathSpecAxisLabel(spec())).toBe('L − R (V)')
    expect(mathSpecAxisLabel(spec({ operation: 'ATimesB' }))).toBe('L × R (V²)')
    // A ratio has no unit, so the axis is the expression alone.
    expect(mathSpecAxisLabel(spec({ operation: 'ADivB' }))).toBe('L ÷ R')
  })

  it('asks for nothing at all when it is off, so the fetch is unchanged', () => {
    expect(mathSpecQuery(DEFAULT_SCOPE_MATH_SPEC)).toBeNull()
  })

  it('names the operation and both sources in one token', () => {
    expect(mathSpecQuery(spec())).toBe('AMinusB:ch0:ch1')
    expect(mathSpecQuery(spec({ operation: 'IntegralA', sourceA: 'ch1' }))).toBe('IntegralA:ch1:ch1')
  })
})

describe('mathSpecMissing', () => {
  const spec = (over: Partial<ScopeMathSpec> = {}): ScopeMathSpec => ({
    ...DEFAULT_SCOPE_MATH_SPEC,
    operation: 'AMinusB',
    ...over,
  })

  it('names the input the reader chose, not the operand it used to be fixed to', () => {
    expect(mathSpecMissing(spec({ sourceA: 'ch1', sourceB: 'ch0' }), true, false)).toEqual(['R'])
  })

  it('is silent about an operand the operation does not read', () => {
    // A derivative reads one input; a B pointing at an uncaptured channel is not its problem.
    expect(mathSpecMissing(spec({ operation: 'DerivativeA', sourceB: 'ch1' }), true, false)).toEqual([])
  })

  it('says each missing input once, however many operands name it', () => {
    expect(mathSpecMissing(spec({ sourceA: 'ch1', sourceB: 'ch1' }), true, false)).toEqual(['R'])
  })

  it('has nothing to say about a channel that is off', () => {
    expect(mathSpecMissing(DEFAULT_SCOPE_MATH_SPEC, false, false)).toEqual([])
  })
})

describe('scopeMathSlice', () => {
  const slice = (slot: number) => ({ slot, operation: 'AMinusB', unit: 'Volt' })

  it('finds a channel by its slot rather than by its place in the list', () => {
    // Math 2 alone arrives as a list of one: reading maths[1] would find nothing, and maths[0]
    // would quietly be the wrong channel's trace.
    const record = { maths: [slice(2)] }
    expect(scopeMathSlice(record, 2)?.slot).toBe(2)
    expect(scopeMathSlice(record, 1)).toBeNull()
  })

  it('answers null for a record that has none, or none at all', () => {
    expect(scopeMathSlice({ maths: [] }, 1)).toBeNull()
    expect(scopeMathSlice({}, 1)).toBeNull()
    expect(scopeMathSlice(null, 1)).toBeNull()
  })
})

describe('mathSliceAxisLabel', () => {
  const slice = (operation: string, sourceA: number, sourceB: number) => ({
    operation,
    sourceA,
    sourceB,
  })

  it('writes the sockets for a slice off the wire, whose operands are channel indices', () => {
    // The chart's axis, the measurement picker's source list and the report's table heading all
    // read this. Any of them writing "A + B" beside a rail that writes "L + R" is one trace
    // described two ways.
    expect(mathSliceAxisLabel(slice('APlusB', 0, 1))).toBe('L + R (V)')
    expect(mathSliceAxisLabel(slice('APlusB', 1, 1))).toBe('R + R (V)')
    expect(mathSliceAxisLabel(slice('DerivativeA', 1, 1))).toBe('dR/dt (V/s)')
  })

  it('reads a mirrored operation the way round it is drawn', () => {
    expect(mathSliceAxisLabel(slice('BMinusA', 0, 1))).toBe('R \u2212 L (V)')
  })

  it('round-trips a channel through a source', () => {
    expect(mathSourceOfChannel(0)).toBe('ch0')
    expect(mathSourceOfChannel(1)).toBe('ch1')
    // Anything else is the left input, the same way round as mathSourceChannel.
    expect(mathSourceOfChannel(7)).toBe('ch0')
  })
})
