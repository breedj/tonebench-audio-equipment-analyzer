import { describe, expect, it } from 'vitest'
import { gainCrossover, ILLUSTRATION_LOOP, loopGainAt, loopGainCurve, phaseCrossover } from './loopGain'

describe('the illustration loop gain', () => {
  it('is its DC gain at DC and falls 18 dB per octave well above every pole', () => {
    expect(loopGainAt(0.001).magDb).toBeCloseTo(ILLUSTRATION_LOOP.dcGainDb, 6)
    expect(loopGainAt(0.001).phaseDeg).toBeCloseTo(0, 2)
    expect(loopGainAt(1e6).magDb - loopGainAt(2e6).magDb).toBeCloseTo(18.06, 1)
  })

  it('is 45° down at each pole and heads for −270°, without wrapping at −180°', () => {
    // One pole contributes exactly −45° at its own frequency; the other two are what the rest is.
    expect(loopGainAt(100).phaseDeg).toBeCloseTo(-45 - (Math.atan(100 / 10_000) * 180) / Math.PI - (Math.atan(100 / 40_000) * 180) / Math.PI, 6)
    expect(loopGainAt(1e7).phaseDeg).toBeGreaterThan(-270)
    expect(loopGainAt(1e7).phaseDeg).toBeLessThan(-269)
    // Continuous through the critical phase: atan2 would have jumped to +180 here.
    const past = loopGainCurve(1000, 20_000, 400).map((p) => p.phaseDeg)
    for (let i = 1; i < past.length; i++) expect(past[i]!).toBeLessThan(past[i - 1]!)
  })

  it('has the margins the pages quote', () => {
    const gain = gainCrossover()
    const phase = phaseCrossover()
    expect(gain.freqHz).toBeCloseTo(7757, -1)
    expect(gain.phaseMarginDeg).toBeCloseTo(42.0, 1)
    expect(phase.freqHz).toBeCloseTo(20_125, -2)
    expect(phase.gainMarginDb).toBeCloseTo(14.1, 1)
    // Both margins positive, i.e. the contour passes to the right of −1: a stable loop, which is
    // what the illustration has to be for the margins to be the distances it points at.
    expect(loopGainAt(phase.freqHz).re).toBeGreaterThan(-1)
  })

  it('walks the contour from the positive real axis into the origin', () => {
    const curve = loopGainCurve(0.1, 1e6, 500)
    expect(curve[0]!.re).toBeCloseTo(100, 0)
    expect(curve[0]!.im).toBeCloseTo(0, 0)
    expect(Math.hypot(curve[curve.length - 1]!.re, curve[curve.length - 1]!.im)).toBeLessThan(1e-3)
  })
})
