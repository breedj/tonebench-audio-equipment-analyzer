import { describe, expect, it } from 'vitest'
import { analyseCapacitor, analyseInductor, isDutType } from './component'

/**
 * A real capacitor: C in series with its ESR and its ESL. That series chain is what gives a real
 * part its self-resonance — below it the capacitance dominates, above it the lead inductance does,
 * and at it the reactances cancel and |Z| is the ESR alone.
 */
function capacitorCurve(
  { farads = 10e-6, esrOhms = 0.05, eslHenry = 1e-6 } = {},
  { fromHz = 20, toHz = 90_000, perOctave = 24 } = {},
) {
  return sweep(fromHz, toHz, perOctave, (f) => {
    const w = 2 * Math.PI * f
    return { r: esrOhms, x: w * eslHenry - 1 / (w * farads) }
  })
}

/**
 * A real inductor: L with its winding resistance, paralleled by the inter-winding capacitance that
 * gives it a self-resonance — where |Z| peaks rather than dips.
 */
function inductorCurve(
  { henries = 10e-3, dcrOhms = 2, shuntFarads = 250e-12 } = {},
  { fromHz = 20, toHz = 200_000, perOctave = 24 } = {},
) {
  return sweep(fromHz, toHz, perOctave, (f) => {
    const w = 2 * Math.PI * f
    // (R + jwL) in parallel with 1/(jwC).
    const sr = dcrOhms
    const sx = w * henries
    const cx = -1 / (w * shuntFarads)

    // Z = Zs·Zc / (Zs + Zc), with Zc purely reactive.
    const nr = sr * 0 - sx * cx
    const ni = sr * cx + sx * 0
    const dr = sr
    const di = sx + cx
    const den = dr * dr + di * di
    return { r: (nr * dr + ni * di) / den, x: (ni * dr - nr * di) / den }
  })
}

function sweep(
  fromHz: number,
  toHz: number,
  perOctave: number,
  at: (f: number) => { r: number; x: number },
): { freqHz: number[]; ohms: number[]; phaseDeg: number[] } {
  const freqHz: number[] = []
  const ohms: number[] = []
  const phaseDeg: number[] = []

  const steps = Math.round(Math.log2(toHz / fromHz) * perOctave)
  for (let i = 0; i <= steps; i++) {
    const f = fromHz * Math.pow(2, i / perOctave)
    const { r, x } = at(f)
    freqHz.push(f)
    ohms.push(Math.hypot(r, x))
    phaseDeg.push((Math.atan2(x, r) * 180) / Math.PI)
  }

  return { freqHz, ohms, phaseDeg }
}

describe('isDutType', () => {
  it('accepts the four types and nothing else', () => {
    expect(isDutType('Capacitor')).toBe(true)
    expect(isDutType('None')).toBe(true)
    expect(isDutType('Resistor')).toBe(false)
    expect(isDutType(undefined)).toBe(false)
  })
})

describe('analyseCapacitor', () => {
  it('recovers C, SRF, ESR and ESL from a curve built out of them', () => {
    const c = capacitorCurve({ farads: 10e-6, esrOhms: 0.05, eslHenry: 1e-6 })
    const r = analyseCapacitor(c.freqHz, c.ohms, c.phaseDeg)

    expect(r.capacitance!.value).toBeGreaterThan(9.9e-6)
    expect(r.capacitance!.value).toBeLessThan(10.1e-6)

    // SRF = 1/(2*pi*sqrt(L*C)) = 50.3 kHz, which the sweep reaches.
    const expectedSrf = 1 / (2 * Math.PI * Math.sqrt(1e-6 * 10e-6))
    expect(r.srfHz!).toBeGreaterThan(expectedSrf * 0.97)
    expect(r.srfHz!).toBeLessThan(expectedSrf * 1.03)

    // At resonance the reactances cancel and |Z| is the ESR alone.
    expect(r.esrOhms!).toBeGreaterThan(0.045)
    expect(r.esrOhms!).toBeLessThan(0.06)

    expect(r.esl!.value).toBeGreaterThan(0.9e-6)
    expect(r.esl!.value).toBeLessThan(1.1e-6)
  })

  it('reports the band each value came from, so a reading is never quoted without one', () => {
    const c = capacitorCurve()
    const r = analyseCapacitor(c.freqHz, c.ohms, c.phaseDeg)

    expect(r.capacitance!.fromHz).toBeGreaterThanOrEqual(20)
    expect(r.capacitance!.toHz).toBeLessThan(r.srfHz!)
    expect(r.capacitance!.points).toBeGreaterThan(3)
    expect(r.esl!.fromHz).toBeGreaterThan(r.srfHz!)
  })

  it('still measures C when the sweep stops well below self-resonance', () => {
    // The ordinary case: a film capacitor measured to 20 kHz never reaches its SRF.
    const c = capacitorCurve({}, { fromHz: 20, toHz: 20_000, perOctave: 24 })
    const r = analyseCapacitor(c.freqHz, c.ohms, c.phaseDeg)

    expect(r.capacitance!.value).toBeGreaterThan(9.9e-6)
    expect(r.capacitance!.value).toBeLessThan(10.1e-6)

    // And says plainly that it has nothing to report rather than guessing.
    expect(r.srfHz).toBeNull()
    expect(r.esrOhms).toBeNull()
    expect(r.esl).toBeNull()
  })

  it('flags a dissipation factor that is below what the fixture can resolve', () => {
    // A good film capacitor: 5 mohm ESR against a kilohm of reactance is tan delta ~ 1e-5, which is
    // three orders below the residual phase of a calibrated two-channel fixture.
    const good = capacitorCurve({ farads: 100e-9, esrOhms: 0.005 }, { fromHz: 20, toHz: 20_000 })
    expect(analyseCapacitor(good.freqHz, good.ohms, good.phaseDeg).dissipationTrustworthy).toBe(false)

    // A tired electrolytic: ohms of ESR against tens of ohms of reactance is readily measurable.
    const tired = capacitorCurve({ farads: 10e-6, esrOhms: 3 }, { fromHz: 20, toHz: 20_000 })
    const r = analyseCapacitor(tired.freqHz, tired.ohms, tired.phaseDeg)
    expect(r.dissipationTrustworthy).toBe(true)
    expect(r.dissipationFactor!).toBeGreaterThan(0.01)
  })

  it('does not call a resistor noisy about zero a self-resonance', () => {
    // The case the crossing-clearance guard exists for. A real resistor's measured phase wobbles a
    // couple of degrees either side of zero, so it crosses over and over; without the guard the
    // first of those wobbles is reported as a self-resonant frequency, with an ESR to match.
    let seed = 7
    const wobble = sweep(20, 20_000, 24, () => {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff
      const deg = ((seed / 0x7fffffff) * 6 - 3) * (Math.PI / 180)
      return { r: 8 * Math.cos(deg), x: 8 * Math.sin(deg) }
    })

    expect(analyseCapacitor(wobble.freqHz, wobble.ohms, wobble.phaseDeg).srfHz).toBeNull()
    expect(analyseInductor(wobble.freqHz, wobble.ohms, wobble.phaseDeg).srfHz).toBeNull()
  })

  it('will not fit a value to one or two qualifying points', () => {
    // Clearly capacitive at exactly two frequencies and resistive everywhere else. Two points is a
    // line through noise, not a fit, and a capacitance quoted from it would carry a band as narrow
    // as the accident that produced it.
    const freqHz = [100, 200, 400, 800, 1600, 3200]
    const phaseDeg = [-1, -80, -80, -2, -1, -1]
    const ohms = [8, 500, 250, 8, 8, 8]

    expect(analyseCapacitor(freqHz, ohms, phaseDeg).capacitance).toBeNull()
  })

  it('finds nothing in a flat resistor, which is not a capacitor at all', () => {
    const flat = sweep(20, 20_000, 24, () => ({ r: 8, x: 0 }))
    const r = analyseCapacitor(flat.freqHz, flat.ohms, flat.phaseDeg)

    expect(r.capacitance).toBeNull()
    expect(r.srfHz).toBeNull()
    expect(r.esrOhms).toBeNull()
  })
})

describe('analyseInductor', () => {
  it('recovers L, DCR and SRF from a curve built out of them', () => {
    const c = inductorCurve({ henries: 10e-3, dcrOhms: 2, shuntFarads: 250e-12 })
    const r = analyseInductor(c.freqHz, c.ohms, c.phaseDeg)

    expect(r.inductance!.value).toBeGreaterThan(9.8e-3)
    expect(r.inductance!.value).toBeLessThan(10.2e-3)

    // The real part, so the reactance still present at 20 Hz does not inflate it.
    expect(r.dcrOhms!).toBeGreaterThan(1.98)
    expect(r.dcrOhms!).toBeLessThan(2.02)
    expect(r.dcrTrustworthy).toBe(true)

    const expectedSrf = 1 / (2 * Math.PI * Math.sqrt(10e-3 * 250e-12))
    expect(r.srfHz!).toBeGreaterThan(expectedSrf * 0.9)
    expect(r.srfHz!).toBeLessThan(expectedSrf * 1.1)

    // An inductor's self-resonance is a peak, not a dip -- far above the winding resistance.
    expect(r.zAtSrfOhms!).toBeGreaterThan(r.dcrOhms! * 100)

    expect(r.q!).toBeGreaterThan(1)
  })

  it('says so when the sweep never reaches down to where the resistance dominates', () => {
    // Started at 2 kHz, where 10 mH is already 126 ohms against 2 ohms of winding: the lowest |Z| in
    // the sweep is a reactance, and calling it DCR would be out by a factor of sixty.
    const c = inductorCurve({}, { fromHz: 2000, toHz: 100_000, perOctave: 24 })
    const r = analyseInductor(c.freqHz, c.ohms, c.phaseDeg)

    expect(r.dcrTrustworthy).toBe(false)
    expect(r.inductance!.value).toBeGreaterThan(9.5e-3)
  })

  it('finds nothing in a flat resistor', () => {
    const flat = sweep(20, 20_000, 24, () => ({ r: 8, x: 0 }))
    const r = analyseInductor(flat.freqHz, flat.ohms, flat.phaseDeg)

    expect(r.inductance).toBeNull()
    expect(r.srfHz).toBeNull()
  })
})
