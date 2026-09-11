import { describe, expect, it } from 'vitest'
import { Cascade, lowPass } from './biquad'
import { bandwidth, impulseCurve, linearThdPercent, noiseFloorDbvPerBin, toneStems } from './curves'
import { tubeAmplifier } from './models'

// The manual's charts are drawn from this port of the simulator's biquads, not from the C# that
// makes the samples. These tests pin the port to numbers measured *through the app* against the
// real model on 2026-09-11 (a 96 kHz-span chirp, 1/24-octave smoothing, passband reference:
// −3 dB 11.38 Hz – 52.23 kHz, ±1 dB 52.5 Hz – 27.2 kHz, peak +4.39 dB at 37.8 kHz; a 20 Hz – 20 kHz
// stepped sweep at 48 kHz: +0.95 dB and −39.0° at 20 kHz; a THD sweep at −20 dBV: 0.586 % at
// 19 Hz, 0.512 % at 1 kHz, 0.709 % at 20 kHz) — so a coefficient that drifts from SimulatedDut.For
// fails here rather than quietly drawing the wrong picture.

describe('biquad port', () => {
  it('a Butterworth low-pass is −3.01 dB at its corner', () => {
    const lp = new Cascade([lowPass(1000, Math.SQRT1_2, 48000)], 48000)
    expect(lp.magnitudeDb(1000)).toBeCloseTo(-3.01, 1)
    expect(lp.magnitudeDb(10)).toBeCloseTo(0, 3)
  })

  it('the impulse of a cascade sums to its DC gain', () => {
    const lp = new Cascade([lowPass(1000, Math.SQRT1_2, 48000)], 48000)
    const h = lp.impulse(4096)
    let sum = 0
    for (const v of h) sum += v
    expect(sum).toBeCloseTo(1, 4)
  })
})

describe('tube amplifier model', () => {
  const dut = tubeAmplifier(192000)

  it('is unity through the passband and 4 dB up on the transformer resonance', () => {
    expect(dut.magnitudeDb(1000)).toBeCloseTo(0, 1)
    expect(dut.magnitudeDb(38000)).toBeGreaterThan(3.5)
    expect(dut.magnitudeDb(38000)).toBeLessThan(4.5)
  })

  it('reads the corners the app measured against it', () => {
    const bw = bandwidth(dut, 4.4, 91200)
    expect(bw.threeDbLowHz).toBeCloseTo(11.38, 0)
    expect(bw.threeDbHighHz! / 1000).toBeCloseTo(52.23, 0)
    expect(bw.oneDbLowHz).toBeCloseTo(52.5, 0)
    expect(bw.oneDbHighHz! / 1000).toBeCloseTo(27.2, 0)
    expect(Math.abs(bw.referenceDb)).toBeLessThan(0.05)
  })

  it('at 48 kHz clamps its high-frequency sections to 21.6 kHz, as the stepped sweep measured', () => {
    const at48k = tubeAmplifier(48000)
    expect(at48k.magnitudeDb(20015.6)).toBeCloseTo(0.95, 1)
    expect(at48k.phaseDeg(20015.6)).toBeCloseTo(-39.0, 0)
  })

  it('reads the generator stack as 0.51 % THD at 1 kHz, a little more where the response shapes the harmonics', () => {
    const nyquist = 0.98 * 96000
    expect(linearThdPercent(dut, 1000, nyquist)).toBeCloseTo(0.511, 2)
    // At 19 Hz the fundamental sits on the HP slope and H2 on the 45 Hz lift.
    expect(linearThdPercent(dut, 19.04, nyquist)).toBeCloseTo(0.586, 1)
    // At 20 kHz H2 lands on the 38 kHz resonance.
    expect(linearThdPercent(dut, 20015.6, nyquist)).toBeCloseTo(0.709, 1)
  })

  it('puts the 1 kHz tone and its stack where the FFT screen draws them', () => {
    const stems = toneStems(dut, 1000, -6, 96000)
    expect(stems[0]).toEqual([1000, expect.closeTo(-6, 1)])
    expect(stems[1]).toEqual([2000, expect.closeTo(-52, 1)])
    expect(stems[2]).toEqual([3000, expect.closeTo(-66, 1)])
  })

  it('has a causal impulse that peaks at t = 0 and rings only briefly', () => {
    const curve = impulseCurve(dut, 0.1, 0.5)
    const peak = curve.find(([, y]) => y === 1)!
    expect(peak[0]).toBeCloseTo(0, 1)
    // Everything before zero is empty, and 0.4 ms in the 38 kHz ring is gone.
    for (const [t, y] of curve) if (t < 0) expect(y).toBe(0)
    const tail = curve.filter(([t]) => t > 0.4)
    for (const [, y] of tail) expect(Math.abs(y)).toBeLessThan(0.02)
  })
})

describe('noise floor', () => {
  it('is about −144 dBV per bin at 192 kHz / 65536 / Hann', () => {
    expect(noiseFloorDbvPerBin(192000, 65536)).toBeCloseTo(-143.6, 0)
  })
})
