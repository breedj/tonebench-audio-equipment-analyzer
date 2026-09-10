import { describe, expect, it } from 'vitest'
import { findExtremes, findResonance } from './resonance'

/**
 * A driver's impedance as the textbook models it: the voice coil in series with a parallel motional
 * resonance, plus the coil's own inductance.
 *
 * <p>`Z = Re + jωLe + Rm/(1 + jQ(f/fs − fs/f))` — at fs the motional term is purely real and the
 * curve peaks at `Re + Rm`, which is what makes the recovered numbers checkable against the ones
 * that went in rather than against a previous run of the same code.</p>
 */
function driverCurve(
  { reOhms = 6.4, rmOhms = 53.6, fsHz = 45, qms = 2.5, leHenry = 0 } = {},
  { fromHz = 5, toHz = 20000, perOctave = 48 } = {},
): { freqHz: number[]; ohms: number[] } {
  const freqHz: number[] = []
  const ohms: number[] = []

  const steps = Math.round(Math.log2(toHz / fromHz) * perOctave)
  for (let i = 0; i <= steps; i++) {
    const f = fromHz * Math.pow(2, i / perOctave)
    const detune = qms * (f / fsHz - fsHz / f)
    const denom = 1 + detune * detune

    const re = reOhms + (rmOhms * 1) / denom
    const im = 2 * Math.PI * f * leHenry - (rmOhms * detune) / denom

    freqHz.push(f)
    ohms.push(Math.hypot(re, im))
  }

  return { freqHz, ohms }
}

describe('findExtremes', () => {
  it('reports the smallest and largest |Z| and where they are', () => {
    const { freqHz, ohms } = driverCurve()
    const e = findExtremes(freqHz, ohms)

    expect(e).not.toBeNull()
    // The peak is Re + Rm at fs.
    expect(e!.maxOhms).toBeCloseTo(60, 1)
    expect(e!.maxFreqHz).toBeCloseTo(45, 0)

    // With no coil inductance the curve settles back onto Re, approached from above.
    expect(e!.minOhms).toBeGreaterThan(6.4)
    expect(e!.minOhms).toBeLessThan(6.6)
  })

  it('is null for an empty trace, and ignores non-positive samples', () => {
    expect(findExtremes([], [])).toBeNull()
    expect(findExtremes([10, 20, 30], [Number.NaN, 0, -1])).toBeNull()

    const e = findExtremes([10, 20, 30], [Number.NaN, 8, 12])
    expect(e!.minOhms).toBe(8)
    expect(e!.maxOhms).toBe(12)
  })
})

describe('findResonance', () => {
  it('recovers the parameters the curve was built from', () => {
    const { freqHz, ohms } = driverCurve({ reOhms: 6.4, rmOhms: 53.6, fsHz: 45, qms: 2.5 })
    const r = findResonance(freqHz, ohms)

    expect(r).not.toBeNull()
    expect(r!.fsHz).toBeCloseTo(45, 0)
    expect(r!.zMaxOhms).toBeCloseTo(60, 1)
    expect(r!.reOhms).toBeCloseTo(6.4, 1)
    expect(r!.r0).toBeCloseTo(60 / 6.4, 1)

    // The skirts sit either side of fs at |Z| = sqrt(Re·Zmax).
    expect(r!.f1Hz!).toBeLessThan(45)
    expect(r!.f2Hz!).toBeGreaterThan(45)

    // Qms back within a couple of per cent of the 2.5 that went in. The classic width construction
    // is a narrow-band approximation, so this is a tolerance on the physics rather than on floats.
    expect(r!.qms!).toBeGreaterThan(2.4)
    expect(r!.qms!).toBeLessThan(2.6)

    // Against the model's own constants rather than against the returned fields, which would only
    // be asserting that the implementation agrees with itself. For this model r0 - 1 is Rm/Re, so
    // Qes = Qms·Re/Rm and Qts is the parallel combination.
    const expectedQes = (2.5 * 6.4) / 53.6
    const expectedQts = (2.5 * expectedQes) / (2.5 + expectedQes)

    expect(r!.qes!).toBeGreaterThan(expectedQes * 0.96)
    expect(r!.qes!).toBeLessThan(expectedQes * 1.04)
    expect(r!.qts!).toBeGreaterThan(expectedQts * 0.96)
    expect(r!.qts!).toBeLessThan(expectedQts * 1.04)
    expect(r!.qts!).toBeLessThan(r!.qes!)
  })

  it('tracks a different Q rather than reporting the same number twice', () => {
    const loose = findResonance(...curveArgs({ qms: 1.2 }))
    const tight = findResonance(...curveArgs({ qms: 6 }))

    expect(loose!.qms!).toBeGreaterThan(1.1)
    expect(loose!.qms!).toBeLessThan(1.35)
    expect(tight!.qms!).toBeGreaterThan(5.7)
    expect(tight!.qms!).toBeLessThan(6.3)
  })

  it('is null for a flat load, which has no resonance to find', () => {
    const freqHz: number[] = []
    const ohms: number[] = []
    for (let i = 0; i <= 200; i++) {
      freqHz.push(20 * Math.pow(2, i / 20))
      ohms.push(7.9)
    }

    expect(findResonance(freqHz, ohms)).toBeNull()
  })

  it('is null when the peak is a rising tail rather than a peak', () => {
    // An inductor: |Z| climbs to the end of the sweep and never comes back down.
    const freqHz: number[] = []
    const ohms: number[] = []
    for (let i = 0; i <= 200; i++) {
      const f = 20 * Math.pow(2, i / 20)
      freqHz.push(f)
      ohms.push(Math.hypot(2, 2 * Math.PI * f * 1e-3))
    }

    expect(findResonance(freqHz, ohms)).toBeNull()
  })

  it('is null when the sweep stops just above the peak, leaving nowhere to read Re', () => {
    // A genuine peak that has started to come down -- r0 would be 60/30 = 2, comfortably past the
    // minimum-resonance gate -- but with only two samples above it, the "minimum above resonance"
    // is whichever of those two happened to be lowest rather than the baseline of the curve. Re
    // would be reported far too high and every Q with it.
    const freqHz = [10, 20, 30, 40, 45, 50, 55]
    const ohms = [7, 9, 20, 50, 60, 40, 30]

    expect(findResonance(freqHz, ohms)).toBeNull()
  })

  it('is null for a bump too small to be a resonance', () => {
    // r0 = 1.2, under MIN_RESONANCE_RATIO. Qes goes as 1/(r0 - 1), so a bump like this would
    // otherwise report a huge Q for what is very nearly a flat curve.
    const r = findResonance(...curveArgs({ reOhms: 8, rmOhms: 1.6, qms: 2 }))
    expect(r).toBeNull()
  })

  it('reports fs and Re but no Q when the sweep does not reach a skirt', () => {
    // Started above the lower skirt, so f1 is off the bottom of the measured band.
    const r = findResonance(...curveArgs({ fsHz: 45, qms: 2.5 }, { fromHz: 42, toHz: 20000 }))

    expect(r).not.toBeNull()
    expect(r!.fsHz).toBeCloseTo(45, 0)
    expect(r!.f1Hz).toBeNull()
    expect(r!.qms).toBeNull()
    expect(r!.qes).toBeNull()
    expect(r!.qts).toBeNull()
  })

  it('is null for a trace with too few points to have a shape', () => {
    expect(findResonance([10, 20, 30], [5, 9, 5])).toBeNull()
  })
})

function curveArgs(
  driver?: Parameters<typeof driverCurve>[0],
  grid?: Parameters<typeof driverCurve>[1],
): [number[], number[]] {
  const { freqHz, ohms } = driverCurve(driver, grid)
  return [freqHz, ohms]
}
