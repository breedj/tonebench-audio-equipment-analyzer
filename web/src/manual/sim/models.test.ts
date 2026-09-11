import { describe, expect, it } from 'vitest'
import { burstDecaySlice, impedanceCurves, smoothedMagnitude } from './curves'
import * as m from './models'
import type { Cascade } from './biquad'
import { oscillatorSpectrum } from './oscillator'
import { clipper, coreSaturation, crossover, harmonicsOfSine, thdVsAmplitude, triode } from './waveshapers'

// Every figure here was measured through the app against the simulator on 2026-09-11 (24 kHz
// span, 1/24-octave smoothing on the response screens; the impedance screen's defaults; the THD
// sweeps' defaults at the level named). A model in models.ts or waveshapers.ts that drifts from
// SimulatedDut.For fails here rather than quietly drawing a picture the simulator no longer makes.

const db = (dut: Cascade, f: number, smoothing = 24) => 20 * Math.log10(smoothedMagnitude(dut, f, smoothing))

describe('linear models against the app', () => {
  it('loudspeaker: +4.59 dB at 230 Hz, −2.67 at 2 kHz, +1.24 at 15 kHz, −15.3 at 22 kHz', () => {
    const d = m.loudspeaker(48000)
    expect(db(d, 230)).toBeCloseTo(4.59, 0)
    expect(db(d, 2000)).toBeCloseTo(-2.67, 0)
    expect(db(d, 15000)).toBeCloseTo(1.24, 0)
    expect(db(d, 22000)).toBeCloseTo(-15.3, 0)
    // Unsmoothed, the reflection comb puts 15 kHz several dB away from the smoothed figure.
    expect(Math.abs(db(d, 15000, 0) - 1.24)).toBeGreaterThan(1)
  })

  it('phono RIAA: +19.25 dB at 20 Hz, −13.94 at 10 kHz, −19.63 at 20 kHz', () => {
    const d = m.phonoRiaa(48000)
    expect(db(d, 20)).toBeCloseTo(19.25, 1)
    expect(db(d, 10000)).toBeCloseTo(-13.94, 1)
    expect(db(d, 20000)).toBeCloseTo(-19.63, 1)
  })

  it('DSP processor: −2.84 dB at 20 kHz, 576 samples of delay at 48 kHz', () => {
    const d = m.digitalProcessor(48000)
    expect(db(d, 20000)).toBeCloseTo(-2.84, 0)
    expect(db(d, 10000)).toBeCloseTo(0, 1)
    // The impulse peaks at the delay.
    const h = d.impulse(700)
    let peak = 0
    for (let i = 1; i < h.length; i++) if (Math.abs(h[i]!) > Math.abs(h[peak]!)) peak = i
    expect(peak).toBeGreaterThanOrEqual(576)
    expect(peak).toBeLessThanOrEqual(578)
  })

  it('bandpass: −0.34 dB at the centre and 57 Hz wide at −3 dB through 1/24-octave smoothing, exact without', () => {
    const d = m.bandpassFilter(48000)
    // The port smooths by sampling the curve rather than the app's bins, so a peak this sharp lands
    // within 0.15 dB of the measured −0.34 rather than on it.
    expect(db(d, 1000)).toBeCloseTo(-0.34, 0)
    expect(db(d, 1000, 0)).toBeCloseTo(0, 2)
    expect(db(d, 500)).toBeCloseTo(-29.5, 0)
    // The half-power points of a Q-20 resonance sit at 1000 × (1 ± 1/40).
    expect(db(d, 975, 0)).toBeCloseTo(-3, 0)
    expect(db(d, 1025, 0)).toBeCloseTo(-3, 0)
    expect(burstDecaySlice(d, 1000, 30).at(-1)![1] - burstDecaySlice(d, 1000, 30)[20 * 8]![1]).toBeCloseTo(-10 * m.BANDPASS_DB_PER_PERIOD, 1)
  })

  it('marginally stable: a +12 dB peak that reads +2.2 dB through 1/24-octave smoothing', () => {
    const d = m.marginallyStable(48000)
    expect(db(d, 1000, 0)).toBeCloseTo(12, 1)
    expect(db(d, 1000)).toBeCloseTo(2.2, 0)
    expect(db(d, 900, 0)).toBeCloseTo(0, 2)
  })
})

describe('impedance fixtures against the app', () => {
  const at = (z: ReturnType<typeof impedanceCurves>, f: number) => {
    let b = 0
    for (let i = 0; i < z.magnitudeOhms.length; i++) if (Math.abs(z.magnitudeOhms[i]![0] - f) < Math.abs(z.magnitudeOhms[b]![0] - f)) b = i
    return { ohms: z.magnitudeOhms[b]![1], deg: z.phaseDeg[b]![1] }
  }

  it('8 Ω reads 8.000 Ω at 0.00° everywhere', () => {
    const z = impedanceCurves(m.fixtureResistive(48000), 100, 10, 22800, 500)
    for (const [, ohms] of z.magnitudeOhms) expect(ohms).toBeCloseTo(8, 6)
    for (const [, deg] of z.phaseDeg) expect(deg).toBeCloseTo(0, 6)
  })

  it('6 Ω + 1 mH: 8.49 Ω at 45° at 955 Hz, and the warped 350 Ω at 20 kHz the app measured', () => {
    const z = impedanceCurves(m.fixtureReactive(48000), 100, 10, 22800, 4000)
    expect(at(z, 955).ohms).toBeCloseTo(8.49, 1)
    expect(at(z, 955).deg).toBeCloseTo(45, 0)
    expect(at(z, 20000).ohms).toBeGreaterThan(340)
    expect(at(z, 20000).ohms).toBeLessThan(365)
  })

  it('47 µF: 0.416 Ω at −44° at 10 kHz, 0.306 Ω at 20 kHz', () => {
    const z = impedanceCurves(m.fixtureCapacitive(48000), 100, 10, 22800, 4000)
    expect(at(z, 10000).ohms).toBeCloseTo(0.416, 2)
    expect(at(z, 10000).deg).toBeCloseTo(-44, 0)
    expect(at(z, 20000).ohms).toBeCloseTo(0.306, 2)
  })

  it('loudspeaker: 60.0 Ω at 0° at 45 Hz, 6.40 Ω at 10 kHz', () => {
    const z = impedanceCurves(m.fixtureLoudspeaker(48000), 100, 10, 22800, 4000)
    expect(at(z, 45).ohms).toBeCloseTo(60, 0)
    expect(Math.abs(at(z, 45).deg)).toBeLessThan(1)
    expect(at(z, 10000).ohms).toBeCloseTo(6.4, 1)
  })

  it('short: 0.120 Ω flat, 0.128 Ω at 20° by 10 kHz', () => {
    const z = impedanceCurves(m.fixtureShort(48000), 100, 10, 22800, 4000)
    expect(at(z, 100).ohms).toBeCloseTo(0.12, 3)
    expect(at(z, 10000).ohms).toBeCloseTo(0.128, 2)
    expect(at(z, 10000).deg).toBeCloseTo(20, 0)
  })
})

describe('nonlinear models against the app', () => {
  it('SE triode: 0.716 % at 1 V rms, and 1 dB per dB below it', () => {
    const at1V = harmonicsOfSine(triode, Math.SQRT2, 1000, 192000)
    // The app's points sit about 2 % under the polynomial (the ADC's soft clip on the peaks).
    expect(at1V.thdPercent).toBeCloseTo(0.716, 1)
    const at100mV = harmonicsOfSine(triode, 0.1 * Math.SQRT2, 1000, 192000)
    expect(at1V.harmonicsPercent[0]! / at100mV.harmonicsPercent[0]!).toBeCloseTo(10, 0)
  })

  it('class AB: a 3.4 % hump near −32 dBV, nothing at 1 V, −1.9 dB of gain in the notch', () => {
    const c = thdVsAmplitude(crossover, 0.001, 1)
    const peak = c.thd.reduce((a, b) => (b[1] > a[1] ? b : a))
    expect(peak[1]).toBeCloseTo(3.44, 0)
    // The sweep's own grid decides where the app's peak lands; the curve's true peak is 0.7 dB higher.
    expect(Math.abs(20 * Math.log10(peak[0]) - -31.8)).toBeLessThan(1)
    expect(c.thd.at(-1)![1]).toBeLessThan(0.002)
    const r = harmonicsOfSine(crossover, 0.001 * Math.SQRT2, 1000, 192000)
    expect(20 * Math.log10(r.fundamentalPeakV / (0.001 * Math.SQRT2))).toBeCloseTo(-1.93, 1)
  })

  it('solid state: nothing at 676 mV, 13 % at 1 V rms', () => {
    expect(harmonicsOfSine(clipper, 0.676 * Math.SQRT2, 1000, 192000).thdPercent).toBeLessThan(0.002)
    expect(harmonicsOfSine(clipper, Math.SQRT2, 1000, 192000).thdPercent).toBeCloseTo(13.4, 0)
  })

  it('output transformer: 4.9 % at 19 Hz and 0 dBV, 0.75 % at 61 Hz, nothing at 1 kHz', () => {
    const thd = (f: number) => harmonicsOfSine(coreSaturation(192000), Math.SQRT2, f, 192000, 8, Infinity, 0.25).thdPercent
    expect(thd(19.04)).toBeCloseTo(4.87, 0)
    expect(thd(60.6)).toBeCloseTo(0.75, 0)
    expect(thd(1000)).toBeLessThan(0.002)
  })

  it('oscillator: sings at 1009 Hz at −9 dBV with odd harmonics only', () => {
    const s = oscillatorSpectrum(48000)
    expect(s.fundamentalHz).toBeCloseTo(1009.3, 0)
    expect(s.lines[0]!.dbv).toBeCloseTo(-9.0, 1)
    expect(s.lines[1]!.dbv).toBeLessThan(-100)
    expect(s.lines[2]!.dbv - s.lines[0]!.dbv).toBeCloseTo(-52.7, 0)
  })
})
