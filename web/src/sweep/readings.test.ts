import { describe, expect, it } from 'vitest'
import type { ThdSweepPoint } from '../api/client'
import {
  formatDistortion,
  formatGenerator,
  formatMagnitude,
  formatPower,
  sweepMetricColumns,
  sweepMetricRow,
  sweepSummary,
} from './readings'

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
    expect(formatDistortion(1.5, 'percent', 20)).toBe('1.50%')
    expect(formatDistortion(0.4, 'percent', 20)).toBe('0.40%')
    expect(formatDistortion(0.0123, 'percent', 20)).toBe('0.012%')
    expect(formatDistortion(0.00025, 'percent', 20)).toBe('0.00025%')
    expect(formatDistortion(0.0000001, 'percent', 20)).toBe('0.000000%')
    expect(formatDistortion(0, 'percent', 20)).toBe('0%')
    expect(formatDistortion(Number.NaN, 'percent', 20)).toBe('—')
  })

  it('leaves the percent form alone whatever the gain is', () => {
    expect(formatDistortion(0.4, 'percent', 0)).toBe('0.40%')
    expect(formatDistortion(0.4, 'percent', 26)).toBe('0.40%')
    expect(formatDistortion(0.4, 'percent', Number.NaN)).toBe('0.40%')
  })

  it('writes dB gain-compensated, through the same conversion the chart plots with', () => {
    expect(formatDistortion(1, 'db', 0)).toBe('-40.00 dB')
    expect(formatDistortion(0.1, 'db', 0)).toBe('-60.00 dB')
    expect(formatDistortion(1, 'db', 14.53)).toBe('-25.47 dB')
    expect(formatDistortion(0.1, 'db', -6)).toBe('-66.00 dB')
    expect(formatDistortion(Number.NaN, 'db', 0)).toBe('—')
  })

  it('writes a dash in dB for a point with no gain, rather than an uncompensated figure', () => {
    expect(formatDistortion(0.1, 'db', Number.NaN)).toBe('—')
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

describe('formatGenerator', () => {
  it('writes the level the point was played at in volts and in dBV', () => {
    expect(formatGenerator(-20)).toBe('100 mV (-20.00 dBV)')
    expect(formatGenerator(0)).toBe('1 V (0.00 dBV)')
    expect(formatGenerator(Number.NaN)).toBe('—')
  })
})

describe('sweepMetricColumns and sweepMetricRow', () => {
  it('lay out the same thirteen columns for a conventional THD sweep against frequency', () => {
    expect(sweepMetricColumns('Conventional', 'thd', 'frequency')).toEqual([
      'Frequency', 'Generator', 'THD', 'Magnitude', 'D2', 'D3', 'D4', 'D5', 'D6+', 'Power', 'Noise floor',
      'THD+N', 'SNR',
    ])
    expect(sweepMetricRow(point(), 'Conventional', 'frequency', 'percent', 8)).toEqual([
      '1.00 kHz', '100 mV (-20.00 dBV)', '0.40%', '-0.30 dB', '0.30%', '0.10%', '0.020%', '0.0010%', '0.0050%',
      '1.25 mW (8 Ω)', '0.0010%', '0.50%', '90.00 dB',
    ])
  })

  // The IMD sweep measures neither, so its table does not carry two columns of dashes.
  it('leave the two band figures off an IMD table', () => {
    const columns = sweepMetricColumns('Conventional', 'imd', 'outputVolt')
    expect(columns).not.toContain('THD+N')
    expect(columns).not.toContain('SNR')
    expect(sweepMetricRow(point(), 'Conventional', 'outputVolt', 'percent', 8, 'imd')).toHaveLength(columns.length)
  })

  // SNR is the one cell that does not follow the unit toggle: the chart has to draw its noise term
  // to share the distortion axis, and a table does not, so it prints the figure as it is quoted.
  it('print SNR counting upward in dB whichever unit the distortion cells are in', () => {
    expect(sweepMetricRow(point(), 'Conventional', 'frequency', 'percent', 8).at(-1)).toBe('90.00 dB')
    expect(sweepMetricRow(point(), 'Conventional', 'frequency', 'db', 8).at(-1)).toBe('90.00 dB')
  })

  it('rename the total and the orders for a normalized run and for the IMD family', () => {
    expect(sweepMetricColumns('FrequencyNormalized', 'thd', 'frequency').slice(2, 9)).toEqual([
      'nTHD', 'Magnitude', 'nD2', 'nD3', 'nD4', 'nD5', 'nD6+',
    ])
    expect(sweepMetricColumns('Conventional', 'imd', 'outputVolt')).toEqual([
      'Output voltage', 'Generator', 'IMD', 'Magnitude', 'IM2', 'IM3', 'IM4', 'IM5', 'IM6+', 'Power', 'Noise floor',
    ])
    // A normalized run reads its normalized figures, gaps where it has none.
    expect(sweepMetricRow(point(), 'FrequencyNormalized', 'frequency', 'percent', 8).slice(2, 9)).toEqual([
      '0.52%', '-0.30 dB', '0.50%', '0.12%', '—', '—', '—',
    ])
  })

  // The table sits under the chart and is read against it, so every distortion cell carries the
  // same gain term the trace does — here the fixture's -0.3 dB, on THD, every order and the noise
  // floor, with Magnitude (already a gain) untouched.
  it('write the dB cells gain-compensated, like the traces above them', () => {
    expect(sweepMetricRow(point(), 'Conventional', 'frequency', 'db', 8).slice(2)).toEqual([
      '-48.26 dB', '-0.30 dB', '-50.76 dB', '-60.30 dB', '-74.28 dB', '-100.30 dB', '-86.32 dB',
      '1.25 mW (8 Ω)', '-100.30 dB', '-46.32 dB', '90.00 dB',
    ])
  })

  it('put the point on the amplitude axes from its own fields', () => {
    const p = point({ fundamentalDbv: 0, generatorDbv: -20, powerWatt: 0.125 })
    expect(sweepMetricRow(p, 'Conventional', 'outputVolt', 'db', 8)[0]).toBe('1 V')
    expect(sweepMetricRow(p, 'Conventional', 'inputVolt', 'db', 8)[0]).toBe('100 mV')
    expect(sweepMetricRow(p, 'Conventional', 'outputWatt', 'db', 8)[0]).toBe('125 mW')
  })

  // An inputVolt sweep's x axis already IS the generator level, so a Generator column there would
  // restate the first one; every other axis leaves the level unstated without it.
  it('omit the generator column only on the input-voltage axis', () => {
    expect(sweepMetricColumns('Conventional', 'thd', 'inputVolt')).not.toContain('Generator')
    expect(sweepMetricColumns('Conventional', 'thd', 'outputWatt')).toContain('Generator')
    const row = sweepMetricRow(point(), 'Conventional', 'inputVolt', 'percent', 8)
    expect(row).toHaveLength(sweepMetricColumns('Conventional', 'thd', 'inputVolt').length)
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
      worst: { x: 10000, percent: 0.9, gainDb: -0.3 },
      best: { x: 1000, percent: 0.05, gainDb: -0.3 },
    })
  })

  it('reads the normalized figure under the normalized method', () => {
    const points = [point({ freqHz: 100, normalizedThdPercent: 0.7 }), point({ freqHz: 200, normalizedThdPercent: null })]
    expect(sweepSummary(points, 'FrequencyNormalized', 'frequency')).toEqual({
      points: 2,
      worst: { x: 100, percent: 0.7, gainDb: -0.3 },
      best: { x: 100, percent: 0.7, gainDb: -0.3 },
    })
  })

  it('is null when no point has a readable figure', () => {
    expect(sweepSummary([], 'Conventional', 'frequency')).toBeNull()
    expect(sweepSummary([point({ thdPercent: Number.NaN })], 'Conventional', 'frequency')).toBeNull()
  })
})
