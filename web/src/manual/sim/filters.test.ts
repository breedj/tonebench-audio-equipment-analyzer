import { describe, expect, it } from 'vitest'
import { magnitude, type Cascade } from './biquad'
import {
  analogEquivalentHz,
  BESSEL_DELAY_AT_DC,
  BUTTERWORTH,
  besselLowPass,
  butterworthDb,
  butterworthLowPass,
  chebyshevDb,
  chebyshevLowPass,
  ELLIPTIC,
  ellipticLowPass,
  FILTER_CORNER_HZ,
  filterSections,
  firstOrderDb,
  firstOrderLowPass,
  FIRST_ORDER,
  butterworthPhaseDeg,
  secondOrderDb,
  secondOrderLowPass,
  SECOND_ORDER,
} from './filters'

// The same checks FilterDutTests runs on the C#, so the manual's curves and the simulator's samples
// can only drift apart together.
const FS = 192_000
const FC = FILTER_CORNER_HZ

function groupDelayS(dut: Cascade, freqHz: number): number {
  const lo = dut.responseAt(freqHz - 1)
  const hi = dut.responseAt(freqHz + 1)
  let d = Math.atan2(hi.im, hi.re) - Math.atan2(lo.im, lo.re)
  if (d > Math.PI) d -= 2 * Math.PI
  if (d < -Math.PI) d += 2 * Math.PI
  return -d / (2 * Math.PI * 2)
}

describe('the classic filter prototypes', () => {
  it('Butterworth follows its closed form at the analog-equivalent frequency', () => {
    const dut = butterworthLowPass(FS)
    for (const f of [100, 1000, 2000, 3000, 4000, 8000]) {
      expect(dut.magnitudeDb(f)).toBeCloseTo(butterworthDb(analogEquivalentHz(f, FS)), 6)
    }
    for (const fs of [48_000, 96_000, 192_000]) expect(butterworthLowPass(fs).magnitudeDb(FC)).toBeCloseTo(-3.0103, 2)
  })

  it('Chebyshev follows its closed form and ripples between 0 and −1 dB', () => {
    const dut = chebyshevLowPass(FS)
    for (const f of [500, 1000, 1500, 3000, 4000]) {
      expect(dut.magnitudeDb(f)).toBeCloseTo(chebyshevDb(analogEquivalentHz(f, FS)), 6)
    }
    for (const k of [0, 1, 2]) {
      expect(dut.magnitudeDb(FC * Math.cos((k * Math.PI) / 5))).toBeCloseTo(-1, 2)
      expect(dut.magnitudeDb(Math.max(FC * Math.cos(((2 * k + 1) * Math.PI) / 10), 1))).toBeCloseTo(0, 2)
    }
  })

  it('Bessel holds its group delay flat to the corner and is −3 dB there', () => {
    const dut = besselLowPass(FS)
    const expected = BESSEL_DELAY_AT_DC / (2 * Math.PI * FC)
    expect(expected).toBeCloseTo(193e-6, 6)
    for (const f of [50, 200, 500, 1000, 1500, 2000]) expect(groupDelayS(dut, f) / expected).toBeCloseTo(1, 1)
    expect(dut.magnitudeDb(FC)).toBeCloseTo(-3.0103, 1)
  })

  it('elliptic is −1 dB at the corner, never above −60 dB in its stopband, and has its two nulls', () => {
    const dut = ellipticLowPass(FS)
    expect(dut.magnitudeDb(FC)).toBeCloseTo(-1, 1)
    for (let f = 20; f < FC; f *= 1.05) {
      expect(dut.magnitudeDb(f)).toBeLessThanOrEqual(0.02)
      expect(dut.magnitudeDb(f)).toBeGreaterThanOrEqual(-1.02)
    }
    for (let f = 2 * FC; f < FS / 2; f *= 1.01) expect(dut.magnitudeDb(f)).toBeLessThanOrEqual(-59.9)
    for (const zero of ELLIPTIC.zeroOmegas) {
      let deepest = 0
      for (let f = zero * FC * 0.99; f < zero * FC * 1.01; f += 0.5) {
        deepest = Math.min(deepest, 20 * Math.log10(magnitude(dut.responseAt(f))))
      }
      expect(deepest).toBeLessThan(-110)
    }
  })

  it('every filter is unity at DC', () => {
    for (const build of [butterworthLowPass, chebyshevLowPass, besselLowPass, ellipticLowPass]) {
      expect(build(FS).magnitudeDb(1)).toBeCloseTo(0, 2)
    }
  })
})

/**
 * The two orders that exist to be checked against — the same assertions `FilterDutTests` makes on
 * the C# side, so the manual's curves and the simulator's samples can only drift together.
 */
describe('the first- and second-order filters', () => {
  it('match their closed forms at the analog-equivalent frequency', () => {
    const first = firstOrderLowPass(FS)
    const second = secondOrderLowPass(FS)
    for (const f of [100, 1000, FC, 4000, 20000]) {
      const analog = analogEquivalentHz(f, FS)
      expect(first.magnitudeDb(f)).toBeCloseTo(firstOrderDb(analog), 3)
      expect(second.magnitudeDb(f)).toBeCloseTo(secondOrderDb(analog), 3)
    }
  })

  it('are −3.01 dB at the corner, which both share with the fifth-order Butterworth', () => {
    expect(firstOrderLowPass(FS).magnitudeDb(FC)).toBeCloseTo(-3.0103, 3)
    expect(secondOrderLowPass(FS).magnitudeDb(FC)).toBeCloseTo(-3.0103, 3)
    expect(butterworthLowPass(FS).magnitudeDb(FC)).toBeCloseTo(-3.0103, 3)
  })

  /**
   * Half the order's total phase at the corner — the reading the manual tells a user to identify an
   * order by, and the one the complex-plane views have to reproduce.
   */
  it('are −45° and −90° at the corner', () => {
    expect(firstOrderLowPass(FS).phaseDeg(FC)).toBeCloseTo(-45, 1)
    expect(secondOrderLowPass(FS).phaseDeg(FC)).toBeCloseTo(-90, 1)
    expect(butterworthPhaseDeg(FC, 1)).toBeCloseTo(-45, 6)
    expect(butterworthPhaseDeg(FC, 2)).toBeCloseTo(-90, 6)
  })

  /**
   * An even-order prototype is one section, not two with an inert one appended: `realPole` 0 means
   * "no real pole", and realising it as `0/(s+0)` would be a section of gain zero — silence.
   */
  it('build one section each, and the fifth order still builds three', () => {
    expect(filterSections(SECOND_ORDER, FC, FS)).toHaveLength(1)
    expect(filterSections(FIRST_ORDER, FC, FS)).toHaveLength(1)
    expect(filterSections(BUTTERWORTH, FC, FS)).toHaveLength(3)
    expect(secondOrderLowPass(FS).magnitudeDb(1)).toBeCloseTo(0, 6)
  })
})
