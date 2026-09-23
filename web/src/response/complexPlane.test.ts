import { describe, expect, it } from 'vitest'
import { columnMagnitudeDb, dbToRatio, toComplexCurve } from './complexPlane'
import { makeTrace } from './testTrace'

const WIDE = { fromHz: 0, toHz: Infinity }

describe('dbToRatio', () => {
  it('divides by 20, not by 10', () => {
    expect(dbToRatio(0)).toBeCloseTo(1, 12)
    expect(dbToRatio(20)).toBeCloseTo(10, 12)
    expect(dbToRatio(-6.020599913)).toBeCloseTo(0.5, 9)
  })
})

describe('toComplexCurve', () => {
  /**
   * One asymmetric point does most of the work here. 6.0206 dB is a ratio of 2 and 30° puts the
   * point at (√3, 1) — so re ≠ im, both are positive, and neither is 0 or 1. That single assertion
   * fails on a /10 divisor, on degrees-vs-radians, on swapped re/im, and on a sign error in im.
   *
   * A 0 dB / 0° assertion passes against three of those four, which is why there isn't one.
   */
  it('places a point from its magnitude and phase', () => {
    const trace = makeTrace({ freqHz: [1000], magnitudeMaxDb: [6.020599913], phaseDeg: [30] })

    const [point] = toComplexCurve(trace, WIDE)

    expect(point.re).toBeCloseTo(Math.sqrt(3), 9)
    expect(point.im).toBeCloseTo(1, 9)
    expect(point.magDb).toBeCloseTo(6.020599913, 9)
    expect(point.phaseDeg).toBeCloseTo(30, 9)
  })

  it('puts a negative phase below the real axis', () => {
    const trace = makeTrace({ freqHz: [1000], magnitudeMaxDb: [-6.020599913], phaseDeg: [-90] })

    const [point] = toComplexCurve(trace, WIDE)

    expect(point.re).toBeCloseTo(0, 9)
    expect(point.im).toBeCloseTo(-0.5, 9)
  })

  /**
   * The unwrapper hands out phases carrying an arbitrary whole number of turns. In the plane that
   * has to be exactly harmless — this is what lets the Nyquist view ignore a problem the phase
   * margin has to solve.
   */
  it('is invariant to whole turns of unwrapped phase', () => {
    const plain = toComplexCurve(makeTrace({ freqHz: [1000], magnitudeMaxDb: [3], phaseDeg: [-40] }), WIDE)
    const wound = toComplexCurve(makeTrace({ freqHz: [1000], magnitudeMaxDb: [3], phaseDeg: [-760] }), WIDE)

    expect(wound[0].re).toBeCloseTo(plain[0].re, 12)
    expect(wound[0].im).toBeCloseTo(plain[0].im, 12)
  })

  it('drops points outside the plotted window', () => {
    const trace = makeTrace({
      freqHz: [10, 100, 1000, 10000],
      magnitudeMaxDb: [0, 0, 0, 0],
      phaseDeg: [0, 0, 0, 0],
    })

    const curve = toComplexCurve(trace, { fromHz: 50, toHz: 5000 })

    expect(curve.map((p) => p.freqHz)).toEqual([100, 1000])
  })

  it('drops non-finite columns rather than emitting NaN points', () => {
    const trace = makeTrace({
      freqHz: [10, 100, 1000],
      magnitudeMaxDb: [0, Number.NaN, 0],
      phaseDeg: [0, 0, Number.NaN],
    })

    expect(toComplexCurve(trace, WIDE).map((p) => p.freqHz)).toEqual([10])
  })

  it('drops columns below the coherence floor, and keeps them when no floor is asked for', () => {
    const trace = makeTrace({
      freqHz: [10, 100, 1000],
      magnitudeMaxDb: [0, 0, 0],
      phaseDeg: [0, 0, 0],
      coherence: [1, 0.2, 0.99],
    })

    expect(toComplexCurve(trace, WIDE).map((p) => p.freqHz)).toEqual([10, 100, 1000])
    expect(toComplexCurve(trace, { ...WIDE, minCoherence: 0.9 }).map((p) => p.freqHz)).toEqual([10, 1000])
  })

  it('drops a column at the estimator’s zero floor, whose phase is not a measurement', () => {
    // The simulated Bessel over a 96 kHz span: −600 dB (an exactly-zero bin) above 50 kHz with
    // coherence 1.0 and an unwrapped phase of −332 878°. See MIN_MEASURED_DB.
    const trace = makeTrace({
      freqHz: [1000, 43_000, 71_000],
      magnitudeMaxDb: [0, -110, -600],
      phaseDeg: [0, 2411, -332_878],
      coherence: [1, 1, 1],
    })

    expect(toComplexCurve(trace, { ...WIDE, minCoherence: 0.5 }).map((p) => p.freqHz)).toEqual([1000, 43_000])
  })

  it('drops a column whose phase the server held, 120 dB under the trace’s own peak', () => {
    // The simulated Butterworth over a 96 kHz span: the response reaches −121 dB at 29.9 kHz, which
    // is where FrequencyResponseEstimator.PhaseFloorRelative stops unwrapping, and every column
    // above it carries that same frozen phase. The magnitude is still plottable and the Bode chart
    // still draws it; a complex point needs both halves. See MIN_MEASURED_RELATIVE_DB.
    const trace = makeTrace({
      freqHz: [1000, 24_000, 29_900, 33_000],
      magnitudeMaxDb: [0, -110.7, -121.1, -160],
      phaseDeg: [0, 2117, 2679, 2679],
      coherence: [1, 1, 1, 1],
    })

    expect(toComplexCurve(trace, { ...WIDE, minCoherence: 0.5 }).map((p) => p.freqHz)).toEqual([1000, 24_000])
  })

  it('measures that floor from the whole trace, not from the plotted window', () => {
    // A window sitting entirely in a DUT's stopband has its own, much lower peak; taking the floor
    // from it would raise the floor with the window and keep exactly the held columns this exists
    // to drop. The server's floor is relative to the whole trace, so this one is too.
    const trace = makeTrace({
      freqHz: [1000, 24_000, 29_900],
      magnitudeMaxDb: [0, -110.7, -121.1],
      phaseDeg: [0, 2117, 2679],
      coherence: [1, 1, 1],
    })

    expect(toComplexCurve(trace, { fromHz: 20_000, toHz: 96_000 }).map((p) => p.freqHz)).toEqual([24_000])
  })

  describe('the removed chirp delay', () => {
    /**
     * `φ_shown = φ_true + 360·f·τ/fs`, so restoring it is frequency-dependent. Asserted at two
     * frequencies in a 4:1 ratio because a constant-offset implementation — the obvious wrong one —
     * passes any single-frequency check.
     */
    it('is undone in proportion to frequency', () => {
      const trace = makeTrace({ freqHz: [1000, 4000], magnitudeMaxDb: [0, 0], phaseDeg: [0, 0] })

      const curve = toComplexCurve(trace, { ...WIDE, latencySamples: 48, sampleRateHz: 48000 })

      // 360 * 48 / 48000 = 0.36 degrees per Hz.
      expect(curve[0].phaseDeg).toBeCloseTo(-360, 9)
      expect(curve[1].phaseDeg).toBeCloseTo(-1440, 9)
    })

    it('is the identity when there is no delay to restore', () => {
      const trace = makeTrace({ freqHz: [1000], magnitudeMaxDb: [0], phaseDeg: [-33] })

      expect(toComplexCurve(trace, { ...WIDE, latencySamples: null, sampleRateHz: 48000 })[0].phaseDeg).toBe(-33)
      expect(toComplexCurve(trace, { ...WIDE, latencySamples: 48, sampleRateHz: null })[0].phaseDeg).toBe(-33)
    })
  })

  it('adds 180 degrees when the loop sign is inverted', () => {
    const trace = makeTrace({ freqHz: [1000], magnitudeMaxDb: [0], phaseDeg: [-120] })

    const [point] = toComplexCurve(trace, { ...WIDE, invertSign: true })

    expect(point.phaseDeg).toBeCloseTo(60, 9)
    expect(point.re).toBeCloseTo(0.5, 9)
  })
})

describe('columnMagnitudeDb', () => {
  /**
   * The chirp decimates magnitude as a per-column min/max but samples phase at the column's
   * *centre*. Pairing the column max with a centre-bin phase inflates |G| exactly where the trace
   * is ripply, which on a loop gain is where it approaches unity — so the bias lands straight on
   * the gain crossover. The mid is the honest partner.
   *
   * This assertion exists to stop the convention being changed by accident; it is not a claim that
   * the mid is the only defensible choice.
   */
  it('takes the mid of a decimated column, not its max', () => {
    const trace = makeTrace({
      freqHz: [1000],
      magnitudeMaxDb: [0],
      magnitudeMinDb: [-10],
      phaseDeg: [0],
    })

    expect(columnMagnitudeDb(trace, 0)).toBeCloseTo(-5, 12)
    expect(toComplexCurve(trace, WIDE)[0].magDb).toBeCloseTo(-5, 12)
  })

  it('falls back to whichever bound is finite', () => {
    const trace = makeTrace({
      freqHz: [1000, 2000],
      magnitudeMaxDb: [0, Number.NaN],
      magnitudeMinDb: [Number.NaN, -4],
      phaseDeg: [0, 0],
    })

    expect(columnMagnitudeDb(trace, 0)).toBe(0)
    expect(columnMagnitudeDb(trace, 1)).toBe(-4)
  })
})
