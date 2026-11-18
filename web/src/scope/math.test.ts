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
