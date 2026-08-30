import { describe, expect, it } from 'vitest'
import type { ThdSweepPoint } from '../api/client'
import {
  decadeSplits,
  decimalLabel,
  formatSweepX,
  minorLogSplits,
  siPrefixed,
  sweepXLabel,
  sweepXUnit,
  sweepXValue,
} from './xAxis'

function point(partial: Partial<ThdSweepPoint>): ThdSweepPoint {
  return {
    freqHz: 1000,
    channel: 0,
    fundamentalDbv: -20,
    gainDb: 0,
    powerWatt: 0.00125,
    thdPercent: 0.01,
    thdNPercent: 0.02,
    snrDb: 90,
    harmonicPercent: [],
    d6PlusPercent: 0,
    noisePercent: 0,
    generatorDbv: -20,
    previewFft: null,
    previewScope: null,
    ...partial,
  }
}

describe('sweepXValue', () => {
  it('reads frequency straight off the point', () => {
    expect(sweepXValue(point({ freqHz: 1234.5 }), 'frequency')).toBe(1234.5)
  })

  /**
   * The two output-referred axes are the *measured* level, not the requested one — that is the whole
   * difference between them and inputVolt, and what lets a compressing DUT show a knee.
   */
  it('converts the measured fundamental to volts for the output-voltage axis', () => {
    expect(sweepXValue(point({ fundamentalDbv: 0 }), 'outputVolt')).toBeCloseTo(1, 9)
    expect(sweepXValue(point({ fundamentalDbv: -20 }), 'outputVolt')).toBeCloseTo(0.1, 9)
  })

  it('takes output power as already computed against the load', () => {
    expect(sweepXValue(point({ powerWatt: 1.25 }), 'outputWatt')).toBe(1.25)
  })

  it('converts the generator level to volts for the input-voltage axis', () => {
    expect(sweepXValue(point({ generatorDbv: -60 }), 'inputVolt')).toBeCloseTo(0.001, 9)
  })

  /**
   * A point whose fundamental was never located carries -Infinity dBV. It has to come back as 0 —
   * a value the log chart drops — rather than NaN or a clamped floor that would draw a phantom
   * point at the left edge.
   */
  it('reports a never-located fundamental as 0 rather than a number the chart would plot', () => {
    expect(sweepXValue(point({ fundamentalDbv: Number.NEGATIVE_INFINITY }), 'outputVolt')).toBe(0)
  })
})

describe('siPrefixed', () => {
  it('picks the prefix from the magnitude and trims to three significant figures', () => {
    expect(siPrefixed(0.001)).toBe('1m')
    expect(siPrefixed(0.0125)).toBe('12.5m')
    expect(siPrefixed(0.1)).toBe('100m')
    expect(siPrefixed(1)).toBe('1')
    expect(siPrefixed(7.9)).toBe('7.9')
    expect(siPrefixed(2500)).toBe('2.5k')
    expect(siPrefixed(0.0000005)).toBe('500n')
  })

  it('has a form for zero and none for a non-finite value', () => {
    expect(siPrefixed(0)).toBe('0')
    expect(siPrefixed(Number.NaN)).toBe('')
  })
})

describe('formatSweepX', () => {
  it('keeps the frequency axis in the hertz style the rest of the app uses', () => {
    expect(formatSweepX(1000, 'frequency')).toBe('1.00 kHz')
    expect(formatSweepX(250, 'frequency')).toBe('250 Hz')
  })

  it('carries the unit on the volt and watt axes', () => {
    expect(formatSweepX(0.25, 'outputVolt')).toBe('250 mV')
    expect(formatSweepX(0.001, 'outputWatt')).toBe('1 mW')
    expect(formatSweepX(2, 'inputVolt')).toBe('2 V')
  })

  it('has no reading for a value the log axis cannot place', () => {
    expect(formatSweepX(0, 'outputVolt')).toBe('—')
    expect(formatSweepX(Number.NaN, 'frequency')).toBe('—')
  })
})

describe('sweepXLabel / sweepXUnit', () => {
  it('names every axis and its unit', () => {
    expect(sweepXLabel('frequency')).toBe('Frequency')
    expect(sweepXLabel('outputVolt')).toBe('Output voltage')
    expect(sweepXLabel('outputWatt')).toBe('Output power')
    expect(sweepXLabel('inputVolt')).toBe('Input voltage')

    expect(sweepXUnit('frequency')).toBe('Hz')
    expect(sweepXUnit('outputVolt')).toBe('V')
    expect(sweepXUnit('outputWatt')).toBe('W')
    expect(sweepXUnit('inputVolt')).toBe('V')
  })
})

describe('decadeSplits', () => {
  it('returns the exact powers of ten inside the window', () => {
    expect(decadeSplits(0.001, 10)).toEqual([0.001, 0.01, 0.1, 1, 10])
  })

  /**
   * The display panel's own From/To are decades, so the scale sits exactly on one at each end.
   * Rounding the log of 0.001 the naive way loses that end label to float error.
   */
  it('keeps a bound that sits exactly on a decade', () => {
    expect(decadeSplits(0.0001, 1)[0]).toBe(0.0001)
    expect(decadeSplits(0.0001, 1).at(-1)).toBe(1)
  })

  it('writes each decade from its exponent, not by repeated multiplication', () => {
    // 1e-4 stepped up from 1e-9 by multiplying accumulates error; decimalLabel would then not
    // recognise it as a decade and would print an SI form instead of "0.0001".
    expect(decadeSplits(1e-9, 1).map(decimalLabel)).toContain('0.0001')
  })

  it('has nothing to offer a window with no decade in it', () => {
    expect(decadeSplits(1.1, 9.9)).toEqual([])
    expect(decadeSplits(0, 10)).toEqual([])
  })
})

describe('minorLogSplits', () => {
  it('fills 2..9 between the decades, and never lands on one', () => {
    const minor = minorLogSplits(0.01, 1)
    expect(minor).toContain(0.02)
    expect(minor).toContain(0.5)
    expect(minor).not.toContain(0.1)
    expect(minor.every((v) => v > 0.01 && v < 1)).toBe(true)
  })

  /**
   * Below two decades the major axis falls back to its own labelled 1/2/5 ticks, and a second set
   * of lines underneath them would be noise rather than structure.
   */
  it('draws nothing once the major axis has fallen back to labelled ticks', () => {
    expect(minorLogSplits(1.1, 9.9)).toEqual([])
  })
})

describe('decimalLabel', () => {
  it('writes decades as the plain decimals the axis is read by', () => {
    expect([0.0001, 0.001, 0.01, 0.1, 1, 10].map(decimalLabel)).toEqual([
      '0.0001',
      '0.001',
      '0.01',
      '0.1',
      '1',
      '10',
    ])
  })

  it('falls back to the short SI form off a decade, and for one too small to write out', () => {
    expect(decimalLabel(0.25)).toBe('250m')
    // Past nano there is no prefix left, and "0.000000000001" is not a label — siPrefixed's own
    // floor takes over.
    expect(decimalLabel(1e-12)).toBe('0.001n')
  })

  it('has no text for a value the log axis cannot place', () => {
    expect(decimalLabel(0)).toBe('')
    expect(decimalLabel(Number.NaN)).toBe('')
  })
})
