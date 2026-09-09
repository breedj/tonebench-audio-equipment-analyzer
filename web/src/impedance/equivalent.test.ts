import { describe, expect, it } from 'vitest'
import {
  dbOhmsToOhms,
  formatCapacitance,
  formatCurrent,
  formatInductance,
  formatOhms,
  formatReactiveComponent,
  ohmsToDbOhms,
  seriesEquivalent,
} from './equivalent'

describe('dB relative to 1 ohm', () => {
  it('round-trips', () => {
    for (const ohms of [0.05, 1, 8, 470, 100000]) {
      expect(dbOhmsToOhms(ohmsToDbOhms(ohms))).toBeCloseTo(ohms, 9)
    }
  })

  it('puts 1 ohm at 0 dB and 10 ohms at 20 dB', () => {
    expect(ohmsToDbOhms(1)).toBeCloseTo(0, 12)
    expect(ohmsToDbOhms(10)).toBeCloseTo(20, 12)
  })
})

describe('seriesEquivalent', () => {
  it('reports a pure resistance as no reactance and no component', () => {
    const z = seriesEquivalent(8, 0, 1000)

    expect(z.resistanceOhms).toBeCloseTo(8, 12)
    expect(z.reactanceOhms).toBeCloseTo(0, 12)
    expect(z.inductanceH).toBeNull()
    expect(z.capacitanceF).toBeNull()
    expect(z.q).toBeCloseTo(0, 12)
  })

  // 6 ohms in series with 1 mH at 955.0 Hz: X = 2*pi*955*1e-3 = 6.0004 ohms, so almost exactly 45
  // degrees — the same fixture the simulator's ImpedanceReactive model uses.
  it('recovers the inductance of a series R and L', () => {
    const freqHz = 955
    const reactance = 2 * Math.PI * freqHz * 1e-3
    const magnitude = Math.hypot(6, reactance)
    const phaseDeg = (Math.atan2(reactance, 6) * 180) / Math.PI

    const z = seriesEquivalent(magnitude, phaseDeg, freqHz)

    expect(z.resistanceOhms).toBeCloseTo(6, 9)
    expect(z.reactanceOhms).toBeCloseTo(reactance, 9)
    expect(z.inductanceH).toBeCloseTo(1e-3, 12)
    expect(z.capacitanceF).toBeNull()
    expect(z.q).toBeCloseTo(reactance / 6, 9)
  })

  // A capacitive point has to come back as a capacitance, not a negative inductance. This is the
  // sign convention the whole readout rests on.
  it('recovers the capacitance of a series R and C', () => {
    const freqHz = 1000
    const farads = 4.7e-6
    const reactance = -1 / (2 * Math.PI * freqHz * farads)
    const magnitude = Math.hypot(3, reactance)
    const phaseDeg = (Math.atan2(reactance, 3) * 180) / Math.PI

    const z = seriesEquivalent(magnitude, phaseDeg, freqHz)

    expect(z.resistanceOhms).toBeCloseTo(3, 9)
    expect(z.reactanceOhms).toBeCloseTo(reactance, 9)
    expect(z.capacitanceF).toBeCloseTo(farads, 15)
    expect(z.inductanceH).toBeNull()
  })

  it('reports a lossless point as infinite Q rather than as an error', () => {
    expect(seriesEquivalent(10, 90, 1000).q).toBe(Infinity)
  })

  it('reports no component at DC, where a reactance names no value', () => {
    const z = seriesEquivalent(10, 45, 0)

    expect(z.inductanceH).toBeNull()
    expect(z.capacitanceF).toBeNull()
  })
})

describe('formatting', () => {
  it('drops into milliohms below one ohm', () => {
    expect(formatOhms(0.15)).toBe('150.0 mΩ')
    expect(formatOhms(8)).toBe('8.000 Ω')
    expect(formatOhms(470)).toBe('470.0 Ω')
    expect(formatOhms(47000)).toBe('47.00 kΩ')
    expect(formatOhms(Number.NaN)).toBe('—')
  })

  it('scales inductance and capacitance to the range each is used in', () => {
    expect(formatInductance(1e-3)).toBe('1.000 mH')
    expect(formatInductance(4.7e-7)).toBe('470.0 nH')
    expect(formatInductance(null)).toBe('—')

    expect(formatCapacitance(4.7e-6)).toBe('4.700 µF')
    expect(formatCapacitance(1e-10)).toBe('100.0 pF')
    expect(formatCapacitance(null)).toBe('—')
  })

  it('picks whichever component the point actually has', () => {
    expect(formatReactiveComponent(seriesEquivalent(10, 45, 1000))).toContain('mH')
    expect(formatReactiveComponent(seriesEquivalent(10, -45, 1000))).toContain('µF')
    expect(formatReactiveComponent(seriesEquivalent(10, 0, 1000))).toBe('—')
  })

  it('formats the drive current in the range this measurement produces', () => {
    expect(formatCurrent(0.000926)).toBe('926.0 µA')
    expect(formatCurrent(1.5e-5)).toBe('15.0 µA')
    expect(formatCurrent(null)).toBe('—')
  })
})
