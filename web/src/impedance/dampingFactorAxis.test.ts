import { describe, expect, it } from 'vitest'
import { dampingFactorTicks, formatDampingFactor } from './dampingFactorAxis'

/** The damping factors a set of ohms positions stands for, which is what the axis actually prints. */
function labelled(ticks: number[], nominalOhms: number): number[] {
  return ticks.map((z) => nominalOhms / z)
}

describe('dampingFactorTicks', () => {
  it('places round damping factors at their own impedance', () => {
    const ticks = dampingFactorTicks(0.1, 10, 8)

    for (const z of ticks) expect(z).toBeGreaterThanOrEqual(0.1)
    for (const z of ticks) expect(z).toBeLessThanOrEqual(10)
    // Every label is a 1/2/5 value, which is the point of labelling DF rather than 8/z.
    for (const df of labelled(ticks, 8)) {
      const mantissa = df / Math.pow(10, Math.floor(Math.log10(df) + 1e-9))
      expect([1, 2, 5].some((s) => Math.abs(mantissa - s) < 1e-6)).toBe(true)
    }
  })

  it('is ascending in ohms, which is the axis it is a position on', () => {
    const ticks = dampingFactorTicks(0.1, 10, 8)

    expect(ticks).toEqual([...ticks].sort((a, b) => a - b))
    // ...and therefore descending in damping factor.
    const dfs = labelled(ticks, 8)
    expect(dfs).toEqual([...dfs].sort((a, b) => b - a))
  })

  // The case the whole module exists for: a linear ohms window sitting on zero. There is no
  // mirroring to be had, but every tick is still exactly where its damping factor is.
  it('handles a linear window that reaches zero ohms', () => {
    const ticks = dampingFactorTicks(0, 2, 8)

    expect(ticks.length).toBeGreaterThan(0)
    for (const z of ticks) {
      expect(z).toBeGreaterThan(0)
      expect(z).toBeLessThanOrEqual(2)
    }
    // A real EL84 Zout of about 1 ohm is DF 8, comfortably inside the labelled span.
    const dfs = labelled(ticks, 8)
    expect(Math.min(...dfs)).toBeLessThanOrEqual(8)
    expect(Math.max(...dfs)).toBeGreaterThanOrEqual(8)
  })

  it('keeps the tick count sane on a window spanning decades', () => {
    expect(dampingFactorTicks(0.001, 100, 8, true).length).toBeLessThanOrEqual(12)
    expect(dampingFactorTicks(0.001, 100, 8, true, 6).length).toBeLessThanOrEqual(6)
  })

  // The bug this spacing rule exists for. On a linear axis the ladder bunches towards zero ohms:
  // reported 2026-09-25 as a stack of labels sitting on top of each other at the bottom.
  it('never stacks two labels on a linear window', () => {
    const zMin = 0
    const zMax = 2
    const ticks = dampingFactorTicks(zMin, zMax, 8)

    expect(ticks.length).toBeGreaterThan(1)
    for (let i = 1; i < ticks.length; i++) {
      const gap = (ticks[i] - ticks[i - 1]) / (zMax - zMin)
      expect(gap).toBeGreaterThanOrEqual(0.05)
    }
  })

  it('never stacks two labels on a log window either', () => {
    const ticks = dampingFactorTicks(0.01, 20, 8, true)

    expect(ticks.length).toBeGreaterThan(1)
    const span = Math.log10(20) - Math.log10(0.01)
    for (let i = 1; i < ticks.length; i++) {
      const gap = (Math.log10(ticks[i]) - Math.log10(ticks[i - 1])) / span
      expect(gap).toBeGreaterThanOrEqual(0.05)
    }
  })

  // A log axis spreads the ladder evenly, so it should keep more of it than the linear one does.
  it('keeps more of the ladder on a log axis than on a linear one', () => {
    expect(dampingFactorTicks(0.01, 20, 8, true).length).toBeGreaterThan(dampingFactorTicks(0, 2, 8).length)
  })

  it('has nothing to label on a log window that reaches zero', () => {
    expect(dampingFactorTicks(0, 2, 8, true)).toEqual([])
  })

  it('has nothing to label without a nominal load or a positive window', () => {
    expect(dampingFactorTicks(0.1, 10, 0)).toEqual([])
    expect(dampingFactorTicks(0.1, 10, -8)).toEqual([])
    expect(dampingFactorTicks(0, 0, 8)).toEqual([])
    expect(dampingFactorTicks(Number.NaN, 10, 8)).toEqual([])
  })

  // Halving the ohms top doubles the damping factor at the top of the plot: the impedance window is
  // the control, which is what makes a separate DF window unnecessary.
  it('follows the ohms window', () => {
    const wide = labelled(dampingFactorTicks(0.1, 10, 8), 8)
    const narrow = labelled(dampingFactorTicks(0.1, 5, 8), 8)

    expect(Math.min(...narrow)).toBeGreaterThan(Math.min(...wide))
  })
})

describe('formatDampingFactor', () => {
  it('shows no more precision than the number deserves', () => {
    expect(formatDampingFactor(8)).toBe('8.00')
    expect(formatDampingFactor(42.5)).toBe('42.5')
    expect(formatDampingFactor(286)).toBe('286')
  })

  it('prints nothing for a value that is not one', () => {
    expect(formatDampingFactor(Number.POSITIVE_INFINITY)).toBe('')
    expect(formatDampingFactor(Number.NaN)).toBe('')
  })
})
