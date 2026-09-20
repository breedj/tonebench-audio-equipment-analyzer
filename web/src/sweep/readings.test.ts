import { describe, expect, it } from 'vitest'
import type { ThdSweepPoint } from '../api/client'
import { formatDistortion, formatMagnitude, formatPower, sweepMetricColumns, sweepMetricRow, sweepSummary } from './readings'

function point(overrides: Partial<ThdSweepPoint> = {}): ThdSweepPoint {
  return {
    freqHz: 1000,
    channel: 0,
    fundamentalDbv: -20,
    gainDb: -0.3,
    powerWatt: 0.00125,
    thdPercent: 0.4,
    thdNPercent: 0.5,
    snrDb: 90,
    harmonicPercent: [0.3, 0.1, 0.02, 0.001],
    d6PlusPercent: 0.005,
    noisePercent: 0.001,
    generatorDbv: -20,
    previewFft: null,
    previewScope: null,
    normalizedThdPercent: 0.52,
    normalizedHarmonicPercent: [0.5, 0.12, null, null],
    normalizedD6PlusPercent: null,
    ...overrides,
  }
}

describe('formatDistortion', () => {
  it('writes percent with as many decimals as the figure needs, and never a gap for zero', () => {
    expect(formatDistortion(1.5, 'percent')).toBe('1.50%')
    expect(formatDistortion(0.4, 'percent')).toBe('0.40%')
    expect(formatDistortion(0.0123, 'percent')).toBe('0.012%')
    expect(formatDistortion(0.00025, 'percent')).toBe('0.00025%')
    expect(formatDistortion(0.0000001, 'percent')).toBe('0.000000%')
    expect(formatDistortion(0, 'percent')).toBe('0%')
    expect(formatDistortion(Number.NaN, 'percent')).toBe('—')
  })

  it('writes dB through the same conversion the chart plots with', () => {
    expect(formatDistortion(1, 'db')).toBe('-40.00 dB')
    expect(formatDistortion(0.1, 'db')).toBe('-60.00 dB')
    expect(formatDistortion(Number.NaN, 'db')).toBe('—')
  })
})

describe('formatMagnitude and formatPower', () => {
  it('quote a gain in dB and a power in milliwatts against its load', () => {
    expect(formatMagnitude(-0.3)).toBe('-0.30 dB')
    expect(formatMagnitude(Number.NaN)).toBe('—')
    expect(formatPower(0.00125, 8)).toBe('1.25 mW (8 Ω)')
    expect(formatPower(0.0125, 4)).toBe('12.5 mW (4 Ω)')
    expect(formatPower(0.125, 8)).toBe('125 mW (8 Ω)')
    expect(formatPower(Number.NaN, 8)).toBe('—')
  })
})

describe('sweepMetricColumns and sweepMetricRow', () => {
  it('lay out the same ten columns for a conventional THD sweep against frequency', () => {
    expect(sweepMetricColumns('Conventional', 'thd', 'frequency')).toEqual([
      'Frequency', 'THD', 'Magnitude', 'D2', 'D3', 'D4', 'D5', 'D6+', 'Power', 'Noise floor',
    ])
    expect(sweepMetricRow(point(), 'Conventional', 'frequency', 'percent', 8)).toEqual([
      '1.00 kHz', '0.40%', '-0.30 dB', '0.30%', '0.10%', '0.020%', '0.0010%', '0.0050%', '1.25 mW (8 Ω)', '0.0010%',
    ])
  })

  it('rename the total and the orders for a normalized run and for the IMD family', () => {
    expect(sweepMetricColumns('FrequencyNormalized', 'thd', 'frequency').slice(1, 8)).toEqual([
      'nTHD', 'Magnitude', 'nD2', 'nD3', 'nD4', 'nD5', 'nD6+',
    ])
    expect(sweepMetricColumns('Conventional', 'imd', 'outputVolt')).toEqual([
      'Output voltage', 'IMD', 'Magnitude', 'IM2', 'IM3', 'IM4', 'IM5', 'IM6+', 'Power', 'Noise floor',
    ])
    // A normalized run reads its normalized figures, gaps where it has none.
    expect(sweepMetricRow(point(), 'FrequencyNormalized', 'frequency', 'percent', 8).slice(1, 8)).toEqual([
      '0.52%', '-0.30 dB', '0.50%', '0.12%', '—', '—', '—',
    ])
  })

  it('put the point on the amplitude axes from its own fields', () => {
    const p = point({ fundamentalDbv: 0, generatorDbv: -20, powerWatt: 0.125 })
    expect(sweepMetricRow(p, 'Conventional', 'outputVolt', 'db', 8)[0]).toBe('1 V')
    expect(sweepMetricRow(p, 'Conventional', 'inputVolt', 'db', 8)[0]).toBe('100 mV')
    expect(sweepMetricRow(p, 'Conventional', 'outputWatt', 'db', 8)[0]).toBe('125 mW')
  })
})

describe('sweepSummary', () => {
  it('finds the worst and best total distortion and where they sit, skipping NaN gaps', () => {
    const points = [
      point({ freqHz: 100, thdPercent: 0.2 }),
      point({ freqHz: 1000, thdPercent: 0.05 }),
      point({ freqHz: 10000, thdPercent: 0.9 }),
      point({ freqHz: 20000, thdPercent: Number.NaN }),
    ]
    expect(sweepSummary(points, 'Conventional', 'frequency')).toEqual({
      points: 4,
      worst: { x: 10000, percent: 0.9 },
      best: { x: 1000, percent: 0.05 },
    })
  })

  it('reads the normalized figure under the normalized method', () => {
    const points = [point({ freqHz: 100, normalizedThdPercent: 0.7 }), point({ freqHz: 200, normalizedThdPercent: null })]
    expect(sweepSummary(points, 'FrequencyNormalized', 'frequency')).toEqual({
      points: 2,
      worst: { x: 100, percent: 0.7 },
      best: { x: 100, percent: 0.7 },
    })
  })

  it('is null when no point has a readable figure', () => {
    expect(sweepSummary([], 'Conventional', 'frequency')).toBeNull()
    expect(sweepSummary([point({ thdPercent: Number.NaN })], 'Conventional', 'frequency')).toBeNull()
  })
})
