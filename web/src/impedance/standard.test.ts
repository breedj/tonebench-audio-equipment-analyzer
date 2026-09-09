import { describe, expect, it } from 'vitest'
import type { ImpedanceCalibrationJson } from '../api/client'
import { MAX_STANDARD_OHMS, standardOnGrid } from './standard'

const R = 100

/** A standard holding the given impedances: H = Z/(R+Z), which is what a capture actually measures. */
function standardOf(freqHz: number[], ohms: { re: number; im: number }[]): ImpedanceCalibrationJson {
  const real: number[] = []
  const imag: number[] = []

  for (const z of ohms) {
    const dr = R + z.re
    const di = z.im
    const d2 = dr * dr + di * di
    real.push((z.re * dr + z.im * di) / d2)
    imag.push((z.im * dr - z.re * di) / d2)
  }

  return { freqHz, real, imag }
}

const resistive = (freqHz: number[], ohms: number[]) =>
  standardOf(freqHz, ohms.map((r) => ({ re: r, im: 0 })))

describe('standardOnGrid', () => {
  it('recovers the impedance a standard was built from', () => {
    // The round trip that matters: a real short is a fraction of an ohm, and the divider ratio that
    // represents it is a very small number, so this is where a sign or a conjugate slips through.
    const cal = resistive([20, 1000, 20000], [0.12, 0.12, 0.12])
    const out = standardOnGrid(cal, [20, 1000, 20000], R)!.ohms

    for (const v of out) expect(v).toBeCloseTo(0.12, 12)
  })

  it('recovers a purely reactive standard from its magnitude and phase', () => {
    const cal = standardOf([20, 20000], [{ re: 0, im: 5 }, { re: 0, im: 5 }])
    expect(standardOnGrid(cal, [20, 20000], R)!.ohms[0]).toBeCloseTo(5, 12)
  })

  /**
   * The interpolation is on H and not on Z, and the difference is not a rounding detail: between a
   * 1 Ω and a 10 Ω point, interpolating the ratio gives 5.31 Ω at the log midpoint where
   * interpolating the impedance would give 3.16 Ω. H is what was measured, so H is what is
   * interpolated; pinning the number keeps a future "simplification" from quietly swapping them.
   */
  it('interpolates the ratio in log frequency, not the impedance', () => {
    const cal = resistive([100, 10000], [1, 10])
    const out = standardOnGrid(cal, [1000], R)!.ohms

    expect(out[0]).toBeCloseTo(5.308056872037915, 9)
    expect(out[0]).not.toBeCloseTo(Math.sqrt(10), 2)
  })

  it('lands exactly on the stored points it is sampled at', () => {
    const cal = resistive([100, 1000, 10000], [1, 4, 10])
    const out = standardOnGrid(cal, [100, 1000, 10000], R)!.ohms

    expect(out[0]).toBeCloseTo(1, 12)
    expect(out[1]).toBeCloseTo(4, 12)
    expect(out[2]).toBeCloseTo(10, 12)
  })

  it('walks the stored grid forward across many sample points', () => {
    // Guards the moving index: a grid denser than the standard's must not run ahead of its segment.
    const cal = resistive([100, 1000, 10000], [1, 4, 10])
    const grid = Array.from({ length: 50 }, (_, i) => 100 * Math.pow(100, i / 49))
    const out = standardOnGrid(cal, grid, R)!.ohms

    expect(out.every((v) => v != null && v > 0)).toBe(true)
    for (let i = 1; i < out.length; i++) expect(out[i]!).toBeGreaterThan(out[i - 1]!)
  })

  it('is null outside the band the standard was captured over, rather than extrapolated', () => {
    const cal = resistive([100, 10000], [1, 10])
    const out = standardOnGrid(cal, [99.9, 100, 10000, 10001], R)!.ohms

    expect(out[0]).toBeNull()
    expect(out[1]).not.toBeNull()
    expect(out[2]).not.toBeNull()
    expect(out[3]).toBeNull()
  })

  it('bounds an open standard instead of returning Infinity', () => {
    // H -> 1 is an open circuit: a real measurement, and one no axis can plot.
    const cal: ImpedanceCalibrationJson = { freqHz: [20, 20000], real: [1, 1], imag: [0, 0] }
    expect(standardOnGrid(cal, [20, 20000], R)!.ohms).toEqual([MAX_STANDARD_OHMS, MAX_STANDARD_OHMS])
    // No meaningful angle at H = 1, so a gap rather than an invented number.
    expect(standardOnGrid(cal, [20, 20000], R)!.phaseDeg).toEqual([null, null])
  })

  it('scales with the reference resistor it was captured through', () => {
    // Same ratio through a different resistor is a different impedance — which is why the standards
    // are pinned to their resistor rather than reused across fixtures.
    const cal = resistive([100, 10000], [1, 1])
    expect(standardOnGrid(cal, [1000], 200)!.ohms[0]).toBeCloseTo(2, 9)
  })

  it('returns null for a standard it cannot use', () => {
    expect(standardOnGrid(null, [1000], R)).toBeNull()
    expect(standardOnGrid(undefined, [1000], R)).toBeNull()
    expect(standardOnGrid({ freqHz: [100], real: [0.5], imag: [0] }, [100], R)).toBeNull()
    // Ragged, rather than reading off the end of an array.
    expect(standardOnGrid({ freqHz: [100, 200], real: [0.5], imag: [0, 0] }, [150], R)).toBeNull()
    expect(standardOnGrid(resistive([100, 200], [1, 1]), [150], 0)).toBeNull()
  })

  it('accepts a Float64Array grid, which is what the chart holds', () => {
    const cal = resistive([100, 10000], [1, 10])
    const out = standardOnGrid(cal, new Float64Array([100, 10000]), R)!.ohms
    expect(out[0]).toBeCloseTo(1, 12)
    expect(out[1]).toBeCloseTo(10, 12)
  })
})

describe('standardOnGrid phase', () => {
  it('is zero for a resistive standard and +90 for a purely inductive one', () => {
    expect(standardOnGrid(resistive([20, 20000], [0.12, 0.12]), [1000], R)!.phaseDeg[0]).toBeCloseTo(0, 10)

    const reactive = standardOf([20, 20000], [{ re: 0, im: 5 }, { re: 0, im: 5 }])
    expect(standardOnGrid(reactive, [1000], R)!.phaseDeg[0]).toBeCloseTo(90, 10)
  })

  it('is negative for a capacitive standard', () => {
    const cap = standardOf([20, 20000], [{ re: 1, im: -1 }, { re: 1, im: -1 }])
    expect(standardOnGrid(cap, [1000], R)!.phaseDeg[0]).toBeCloseTo(-45, 10)
  })

  /**
   * The real reason this exists. A test lead is a small resistance in series with a little
   * inductance, so its phase is nothing at the bottom of the band and rises steeply once the
   * reactance overtakes the resistance — 1.8 deg at 1 kHz against 31.9 deg at 20 kHz for the leads
   * this fixture was measured with (0.117 ohm + 0.58 uH). That is the error sitting underneath every
   * reading at the top of the band, and it is invisible on a magnitude trace alone.
   */
  it('recovers the rising phase of a lead standard across the band', () => {
    const R_LEAD = 0.117
    const L_HENRY = 0.58e-6
    const freqHz = [1000, 10000, 20000]
    const cal = standardOf(
      freqHz,
      freqHz.map((f) => ({ re: R_LEAD, im: 2 * Math.PI * f * L_HENRY })),
    )

    const curve = standardOnGrid(cal, freqHz, R)!

    expect(curve.ohms[0]).toBeCloseTo(0.11705674068451635, 9)
    expect(curve.ohms[2]).toBeCloseTo(0.13784489788469625, 9)
    expect(curve.phaseDeg[0]).toBeCloseTo(1.7840385986580742, 9)
    expect(curve.phaseDeg[1]).toBeCloseTo(17.300461049313686, 9)
    expect(curve.phaseDeg[2]).toBeCloseTo(31.92077167223488, 9)
  })

  it('interpolates the ratio, so phase between stored points follows H and not the angle', () => {
    // 1 ohm resistive at 100 Hz to 5j at 10 kHz: interpolating H gives 77.48 deg at the log midpoint,
    // where interpolating the angle itself would give 45.
    const cal = standardOf([100, 10000], [{ re: 1, im: 0 }, { re: 0, im: 5 }])
    const curve = standardOnGrid(cal, [1000], R)!

    expect(curve.phaseDeg[0]).toBeCloseTo(77.48130271592672, 9)
    expect(curve.ohms[0]).toBeCloseTo(2.584829528170986, 9)
  })

  it('gaps the phase wherever the magnitude is gapped', () => {
    const cal = resistive([100, 10000], [1, 10])
    const curve = standardOnGrid(cal, [99.9, 1000, 10001], R)!

    expect(curve.phaseDeg[0]).toBeNull()
    expect(curve.phaseDeg[1]).not.toBeNull()
    expect(curve.phaseDeg[2]).toBeNull()
    expect(curve.ohms.length).toBe(curve.phaseDeg.length)
  })
})
