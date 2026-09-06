import { describe, expect, it } from 'vitest'
import { formatImpulsePeak, impulseUnit } from './impulseUnit'

describe('impulseUnit', () => {
  it('picks a unit that keeps the axis numbers readable', () => {
    expect(impulseUnit(2.5).suffix).toBe('V')
    expect(impulseUnit(1).suffix).toBe('V')
    expect(impulseUnit(0.954).suffix).toBe('mV')
    expect(impulseUnit(1e-3).suffix).toBe('mV')
    expect(impulseUnit(9.54e-5).suffix).toBe('µV')
    expect(impulseUnit(1e-9).suffix).toBe('µV')
  })

  it('falls back to volts when there is no measurement', () => {
    expect(impulseUnit(0).suffix).toBe('V')
    expect(impulseUnit(Number.NaN).suffix).toBe('V')
  })

  /**
   * The regression this module exists for. uPlot's default axis formatter is `Intl.NumberFormat`
   * with three fraction digits, so anything below 0.0005 V labels every tick "0" — which is exactly
   * what a 95 µV impulse did, making a perfectly good measurement look like a broken chart.
   *
   * Scaling has to put the plotted values somewhere that formatter can render, i.e. not far below 1.
   */
  it('scales small peaks into a range the default formatter can render', () => {
    for (const volts of [0.954, 0.02, 1.5e-3, 9.54e-5, 4e-6]) {
      const scaled = volts * impulseUnit(volts).factor
      expect(scaled).toBeGreaterThanOrEqual(1)
      expect(scaled).toBeLessThan(1000)
      // What Intl would print for the axis tick: it must not collapse to zero.
      expect(scaled.toFixed(3)).not.toBe('0.000')
    }
  })
})

describe('formatImpulsePeak', () => {
  it('keeps three significant figures at any magnitude', () => {
    expect(formatImpulsePeak(0.954)).toBe('954 mV')
    expect(formatImpulsePeak(9.54e-5)).toBe('95.4 µV')
    expect(formatImpulsePeak(1.0704)).toBe('1.07 V')
    expect(formatImpulsePeak(1.23e-5)).toBe('12.3 µV')
  })

  /**
   * The exact reading the old fixed-decimal version produced for a small impulse. Asserted so the
   * defect cannot come back wearing the same clothes.
   */
  it('never reports a real impulse as zero', () => {
    for (const volts of [9.54e-5, 1e-5, 4e-6, 1e-7]) {
      expect(formatImpulsePeak(volts)).not.toMatch(/^0(\.0+)? /)
    }
  })
})
