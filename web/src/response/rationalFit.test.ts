import { describe, expect, it } from 'vitest'
import type { ComplexPoint } from './complexPlane'
import { fitRational, kneeOrder, suggestOrder, type OrderFit } from './rationalFit'
import { polyRoots, trimLeading } from './poly'
import { logGrid } from './testTrace'

/**
 * Samples an analytic H(s) onto a log grid, as `toComplexCurve` would produce it.
 *
 * Fixtures are *designed filters with known poles*, which is the whole point: the fit is only worth
 * having if it returns the components you built, so every assertion below is against a closed form
 * rather than against a previous run's output.
 */
function sample(h: (omega: number) => { re: number; im: number }, freqHz: number[]): ComplexPoint[] {
  return freqHz.map((f) => {
    const value = h(2 * Math.PI * f)
    return {
      freqHz: f,
      re: value.re,
      im: value.im,
      magDb: 20 * Math.log10(Math.hypot(value.re, value.im)),
      phaseDeg: (Math.atan2(value.im, value.re) * 180) / Math.PI,
    }
  })
}

/** 1 / (1 + s/ω0) — one real pole. */
const onePole = (f0: number) => (omega: number) => {
  const d = { re: 1, im: omega / (2 * Math.PI * f0) }
  const mag = d.re * d.re + d.im * d.im
  return { re: d.re / mag, im: -d.im / mag }
}

/** 1 / (1 + s/(Q·ω0) + (s/ω0)²) — a conjugate pole pair at f0 with the given Q. */
const twoPole = (f0: number, q: number) => (omega: number) => {
  const x = omega / (2 * Math.PI * f0)
  const d = { re: 1 - x * x, im: x / q }
  const mag = d.re * d.re + d.im * d.im
  return { re: d.re / mag, im: -d.im / mag }
}

/** (s/ω0)² + 1 over the same denominator: a notch, i.e. a zero pair on the jω axis. */
const notch = (f0: number, q: number) => (omega: number) => {
  const x = omega / (2 * Math.PI * f0)
  const n = { re: 1 - x * x, im: 0 }
  const d = { re: 1 - x * x, im: x / q }
  const mag = d.re * d.re + d.im * d.im
  return {
    re: (n.re * d.re + n.im * d.im) / mag,
    im: (n.im * d.re - n.re * d.im) / mag,
  }
}

const GRID = logGrid(20, 20000, 8)

describe('polyRoots', () => {
  it('finds the roots of a real quadratic', () => {
    // (x - 2)(x + 3) = x² + x − 6
    const roots = polyRoots([-6, 1, 1]).map((r) => r.re).sort((a, b) => a - b)

    expect(roots[0]).toBeCloseTo(-3, 9)
    expect(roots[1]).toBeCloseTo(2, 9)
  })

  it('finds a complex conjugate pair', () => {
    // x² + 1
    const roots = polyRoots([1, 0, 1])

    expect(roots.map((r) => Math.abs(r.im)).sort()).toEqual([1, 1].map((v) => expect.closeTo(v, 9)) as never)
    expect(roots.every((r) => Math.abs(r.re) < 1e-9)).toBe(true)
  })

  /**
   * The trimming case, and it is not hygiene. A low pass has all its zeros at infinity, so the
   * fitted numerator's top coefficients arrive at round-off; untrimmed, the root finder answers with
   * enormous garbage roots rather than "there are no finite zeros".
   */
  it('ignores leading coefficients that are round-off', () => {
    expect(trimLeading([1, 2, 1e-18, 3e-19])).toEqual([1, 2])
    expect(polyRoots([-6, 1, 1, 1e-19])).toHaveLength(2)
  })
})

describe('fitRational', () => {
  it('recovers a single real pole', () => {
    const fit = fitRational(sample(onePole(1000), GRID), 0, 1)!

    expect(fit.poles).toHaveLength(1)
    expect(fit.poles[0].freqHz).toBeCloseTo(1000, 0)
    expect(fit.poles[0].isPair).toBe(false)
    expect(fit.poles[0].q).toBeNull()
    expect(fit.rmsErrorDb).toBeLessThan(1e-6)
  })

  /**
   * A Butterworth pair is Q = 0.7071 by definition, so this asserts the Q the datasheet would quote,
   * not merely that a pole was found near 1 kHz. Q is what a magnitude-only fit cannot get right,
   * which is the reason phase is being used at all.
   */
  it('recovers a conjugate pair with its Q', () => {
    const fit = fitRational(sample(twoPole(1000, Math.SQRT1_2), GRID), 0, 2)!

    expect(fit.poles).toHaveLength(1)
    expect(fit.poles[0].isPair).toBe(true)
    expect(fit.poles[0].freqHz).toBeCloseTo(1000, 0)
    expect(fit.poles[0].q).toBeCloseTo(Math.SQRT1_2, 4)
    // zeta = 1/(2Q), so a Butterworth pair is 0.7071 in *both* — the two agreeing is itself a check
    // that the pole's real and imaginary parts were not mixed up.
    expect(fit.poles[0].zeta).toBeCloseTo(1 / (2 * Math.SQRT1_2), 4)
    expect(fit.rmsErrorDb).toBeLessThan(1e-6)
  })

  it('recovers a high-Q resonance', () => {
    const fit = fitRational(sample(twoPole(2500, 10), GRID), 0, 2)!

    expect(fit.poles[0].freqHz).toBeCloseTo(2500, 0)
    expect(fit.poles[0].q).toBeCloseTo(10, 2)
  })

  it('recovers a notch as a zero pair on top of a pole pair', () => {
    const fit = fitRational(sample(notch(800, 4), GRID), 2, 2)!

    expect(fit.poles).toHaveLength(1)
    expect(fit.zeros).toHaveLength(1)
    expect(fit.poles[0].freqHz).toBeCloseTo(800, 0)
    expect(fit.zeros[0].freqHz).toBeCloseTo(800, 0)
    // A jω-axis zero has no damping at all — that is what makes the notch infinitely deep.
    expect(Math.abs(fit.zeros[0].zeta)).toBeLessThan(1e-4)
  })

  /**
   * A low pass has no finite zeros. Reporting two of them — which an untrimmed numerator does — would
   * invent components that are not in the circuit.
   */
  it('reports no finite zeros for an all-pole filter', () => {
    const fit = fitRational(sample(twoPole(1000, Math.SQRT1_2), GRID), 2, 2)!

    expect(fit.zeros).toHaveLength(0)
  })

  it('reports the DC gain', () => {
    const fit = fitRational(sample(onePole(1000), GRID), 0, 1)!

    expect(fit.dcGainDb).toBeCloseTo(0, 6)
  })

  /**
   * Frequency normalisation is what makes the three-decade case solvable at all. A pole at the very
   * bottom of a 20 Hz–20 kHz sweep is the worst conditioned position, so it is the one asserted.
   */
  it('handles a pole at the bottom of a three-decade sweep', () => {
    const fit = fitRational(sample(onePole(25), GRID), 0, 1)!

    expect(fit.poles[0].freqHz).toBeCloseTo(25, 1)
    expect(fit.rmsErrorDb).toBeLessThan(1e-6)
  })

  it('marks a right-half-plane root rather than hiding it', () => {
    // 1/(1 − s/ω0): an unstable pole. A stable filter never produces one, so it has to be visible.
    const unstable = (omega: number) => {
      const d = { re: 1, im: -omega / (2 * Math.PI * 1000) }
      const mag = d.re * d.re + d.im * d.im
      return { re: d.re / mag, im: -d.im / mag }
    }
    const fit = fitRational(sample(unstable, GRID), 0, 1)!

    expect(fit.poles[0].isRhp).toBe(true)
  })

  /**
   * Over-ordering is the failure mode that looks like success: the extra poles land on top of extra
   * zeros and the error improves, so only cancellation detection distinguishes a real 4th-order
   * filter from a 2nd-order one fitted at order 4.
   */
  it('flags a pole and zero that cancel when the order is too high', () => {
    const fit = fitRational(sample(twoPole(1000, Math.SQRT1_2), GRID), 4, 4)!

    expect(fit.cancellations.length).toBeGreaterThan(0)
  })

  /**
   * The test that decides whether this is usable on measurements rather than only on algebra.
   *
   * Perturbs a Butterworth pair by 0.05 dB and 0.5° per point — around what this instrument actually
   * delivers on a clean stepped sweep — and requires the pole to survive. Levy's method is known to
   * be noise-sensitive because it minimises the *equation* error rather than the response error;
   * this is what shows the SK reweighting has removed enough of that to matter. A deterministic PRNG
   * so a passing build stays passing.
   */
  it('recovers a pole from noisy data', () => {
    let seed = 12345
    const random = () => {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff
      return seed / 0x7fffffff - 0.5
    }

    const clean = sample(twoPole(1000, Math.SQRT1_2), GRID)
    const noisy = clean.map((point) => {
      const magDb = point.magDb + 0.05 * 2 * random()
      const phaseDeg = point.phaseDeg + 0.5 * 2 * random()
      const mag = Math.pow(10, magDb / 20)
      const rad = (phaseDeg * Math.PI) / 180
      return { ...point, magDb, phaseDeg, re: mag * Math.cos(rad), im: mag * Math.sin(rad) }
    })

    const fit = fitRational(noisy, 0, 2)!

    expect(fit.poles[0].freqHz).toBeGreaterThan(980)
    expect(fit.poles[0].freqHz).toBeLessThan(1020)
    expect(fit.poles[0].q!).toBeCloseTo(Math.SQRT1_2, 1)
    expect(fit.rmsErrorDb).toBeLessThan(0.1)
  })

  it('returns null rather than a fabricated model when there is too little data', () => {
    expect(fitRational(sample(onePole(1000), [100, 200]), 2, 2)).toBeNull()
    expect(fitRational([], 0, 1)).toBeNull()
  })
})

describe('kneeOrder', () => {
  /**
   * Two sweeps of the *same* amplifier, taken minutes apart: the GM70 driver stage, 5 Hz–80 kHz at
   * −25 dBV, 65 points. Identical settings, different noise realisation.
   *
   * They are here as a pair because the pair is the requirement. An earlier version subtracted the
   * cancelling pairs from the last cliff and returned 5 for run A and 4 for run B — the two runs
   * disagree only in whether order 7 shows 2 or 3 cancellations, and a rule whose answer moves with
   * the noise is not a rule. The walk-back-up step is what makes both come out at 5.
   */
  const RUN_A: OrderFit[] = [
    { order: 1, rmsErrorDb: 0.91, cancellations: 0 },
    { order: 2, rmsErrorDb: 0.178, cancellations: 0 },
    { order: 3, rmsErrorDb: 0.177, cancellations: 0 },
    { order: 4, rmsErrorDb: 0.176, cancellations: 1 },
    { order: 5, rmsErrorDb: 0.065, cancellations: 1 },
    { order: 6, rmsErrorDb: 0.033, cancellations: 1 },
    { order: 7, rmsErrorDb: 0.007, cancellations: 2 },
    { order: 8, rmsErrorDb: 0.008, cancellations: 4 },
  ]

  const RUN_B: OrderFit[] = [
    { order: 1, rmsErrorDb: 0.91, cancellations: 0 },
    { order: 2, rmsErrorDb: 0.178, cancellations: 0 },
    { order: 3, rmsErrorDb: 0.177, cancellations: 0 },
    { order: 4, rmsErrorDb: 0.176, cancellations: 1 },
    { order: 5, rmsErrorDb: 0.061, cancellations: 1 },
    { order: 6, rmsErrorDb: 0.032, cancellations: 2 },
    { order: 7, rmsErrorDb: 0.007, cancellations: 3 },
    { order: 8, rmsErrorDb: 0.008, cancellations: 3 },
  ]

  /** A third sweep, same settings again. Order 5 fitted worse here (0.123), so the guard climbs to 6. */
  const RUN_C: OrderFit[] = [
    { order: 1, rmsErrorDb: 0.91, cancellations: 0 },
    { order: 2, rmsErrorDb: 0.18, cancellations: 0 },
    { order: 3, rmsErrorDb: 0.177, cancellations: 0 },
    { order: 4, rmsErrorDb: 0.177, cancellations: 1 },
    { order: 5, rmsErrorDb: 0.123, cancellations: 0 },
    { order: 6, rmsErrorDb: 0.039, cancellations: 1 },
    { order: 7, rmsErrorDb: 0.008, cancellations: 2 },
    { order: 8, rmsErrorDb: 0.007, cancellations: 2 },
  ]

  it('is deterministic on each measured curve', () => {
    expect(kneeOrder(RUN_A)).toBe(5)
    expect(kneeOrder(RUN_B)).toBe(5)
    expect(kneeOrder(RUN_C)).toBe(6)
  })

  /**
   * The honest claim, and the one worth pinning. Three sweeps of the same amplifier land within one
   * order of each other with usable fit errors (0.065, 0.061, 0.039 dB rms), where the old rule
   * pinned 7-8 every time and reported its cancelling pairs as components. Order selection on a
   * noisy measurement of a high-order system is not stable to better than ±1, and pretending
   * otherwise would mean tuning the rule to one dataset.
   */
  it('keeps three sweeps of the same amplifier within one order', () => {
    const picks = [RUN_A, RUN_B, RUN_C].map(kneeOrder)

    expect(Math.max(...picks) - Math.min(...picks)).toBeLessThanOrEqual(1)
    for (const pick of picks) {
      expect(pick).toBeGreaterThanOrEqual(5)
      expect(pick).toBeLessThanOrEqual(6)
    }
  })

  it('does not stop at an early cliff followed by a plateau', () => {
    // Order 2 is the single biggest ratio in every run; taking it under-fits at 1.33 dB peak error.
    for (const run of [RUN_A, RUN_B, RUN_C]) expect(kneeOrder(run)).not.toBe(2)
  })

  it('does not keep an order whose extra poles cancel', () => {
    // Orders 7 and 8 have the lowest error in every run, and 2-4 cancelling pairs.
    for (const run of [RUN_A, RUN_B, RUN_C]) {
      expect(kneeOrder(run)).not.toBe(7)
      expect(kneeOrder(run)).not.toBe(8)
    }
  })

  it('ignores an order that made the fit worse', () => {
    expect(kneeOrder(RUN_A)).toBeLessThan(8)
  })

  /**
   * The guard that stops the discount from under-fitting: order 4 is 0.176 dB against a best of
   * 0.007, twenty-five times worse, so the walk continues to 5 rather than accepting it.
   */
  it('refuses a discount that costs an order of magnitude of accuracy', () => {
    const heavilyCancelling: OrderFit[] = [
      { order: 1, rmsErrorDb: 1, cancellations: 0 },
      { order: 2, rmsErrorDb: 0.5, cancellations: 0 },
      { order: 3, rmsErrorDb: 0.001, cancellations: 2 },
    ]

    // 3 − 2 = 1, but order 1 is 1000x worse than the best, so it climbs back to 3.
    expect(kneeOrder(heavilyCancelling)).toBe(3)
  })

  it('takes the discount when it is nearly free', () => {
    const cheapDiscount: OrderFit[] = [
      { order: 1, rmsErrorDb: 1, cancellations: 0 },
      { order: 2, rmsErrorDb: 0.01, cancellations: 0 },
      { order: 3, rmsErrorDb: 0.009, cancellations: 1 },
      { order: 4, rmsErrorDb: 0.002, cancellations: 1 },
    ]

    // Last cliff is 4 (4.5x), minus its one cancellation is 3, and 0.009 is inside 10x of 0.002.
    expect(kneeOrder(cheapDiscount)).toBe(3)
  })

  it('returns the fitted order itself when nothing cancels', () => {
    expect(
      kneeOrder([
        { order: 1, rmsErrorDb: 1, cancellations: 0 },
        { order: 2, rmsErrorDb: 0.01, cancellations: 0 },
        { order: 3, rmsErrorDb: 0.0099, cancellations: 0 },
      ]),
    ).toBe(2)
  })

  /**
   * A first-order system is fitted exactly at order 1, so every step above it divides round-off by
   * round-off. Without the significance floor those ratios are arbitrary.
   */
  it('stays at the lowest order when nothing improves', () => {
    expect(
      kneeOrder([
        { order: 1, rmsErrorDb: 1e-15, cancellations: 0 },
        { order: 2, rmsErrorDb: 1e-15, cancellations: 1 },
        { order: 3, rmsErrorDb: 2e-15, cancellations: 2 },
      ]),
    ).toBe(1)

    expect(
      kneeOrder([
        { order: 2, rmsErrorDb: 0.4, cancellations: 0 },
        { order: 3, rmsErrorDb: 0.38, cancellations: 0 },
      ]),
    ).toBe(2)
  })

  it('never returns below the lowest order tried', () => {
    expect(
      kneeOrder([
        { order: 2, rmsErrorDb: 1, cancellations: 0 },
        { order: 3, rmsErrorDb: 0.01, cancellations: 9 },
      ]),
    ).toBe(3)
  })

  it('handles degenerate input', () => {
    expect(kneeOrder([])).toBe(2)
    expect(kneeOrder([{ order: 3, rmsErrorDb: 0.5, cancellations: 0 }])).toBe(3)
  })
})

describe('suggestOrder', () => {
  it('picks the true order of a second-order filter, not the highest tried', () => {
    const { order } = suggestOrder(sample(twoPole(1000, Math.SQRT1_2), GRID), 6)

    expect(order).toBe(2)
  })

  it('picks a higher order when the data genuinely needs it', () => {
    // Two cascaded pairs at well-separated frequencies: a 4th-order response.
    const cascade = (omega: number) => {
      const a = twoPole(300, 0.8)(omega)
      const b = twoPole(6000, 0.8)(omega)
      return { re: a.re * b.re - a.im * b.im, im: a.re * b.im + a.im * b.re }
    }
    const { order } = suggestOrder(sample(cascade, GRID), 6)

    expect(order).toBe(4)
  })
})
