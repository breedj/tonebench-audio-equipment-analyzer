import { describe, expect, it } from 'vitest'
import { NICHOLS_SEED_STEP_DB, seedNicholsGain, seedPoleZeroWindow } from './autoWindow'

describe('seedNicholsGain', () => {
  /**
   * Outward, not nearest: the seeded window is the one the user is about to see, and a window that
   * had quietly clipped the top of the curve would read as the control having moved the trace.
   */
  it('snaps outward to whole steps, so the fit is still contained', () => {
    const seeded = seedNicholsGain({ kind: 'Nichols', topDb: 27.37, bottomDb: -43.12 })

    expect(seeded.topDb).toBe(30)
    expect(seeded.bottomDb).toBe(-45)
    expect(seeded.topDb % NICHOLS_SEED_STEP_DB).toBe(0)
  })

  it('does not put a negative zero in the box', () => {
    expect(String(seedNicholsGain({ kind: 'Nichols', topDb: -0.4, bottomDb: -12 }).topDb)).toBe('0')
  })

  it('falls back on a window with nothing to fit', () => {
    const seeded = seedNicholsGain({ kind: 'Nichols', topDb: -Infinity, bottomDb: Infinity })

    expect(Number.isFinite(seeded.topDb)).toBe(true)
    expect(seeded.topDb).toBeGreaterThan(seeded.bottomDb)
  })
})

describe('seedPoleZeroWindow', () => {
  /**
   * Three significant figures rather than one grid, because this axis spans decades by design: the
   * ordinary amplifier has a 12 Hz coupling pole and a 46 kHz transformer pole, and a step that
   * reads well at the top is coarser than everything at the bottom.
   */
  it('rounds each bound outward to three significant figures', () => {
    const seeded = seedPoleZeroWindow({
      kind: 'PoleZero',
      xMinHz: -57863.2,
      xMaxHz: 12.345,
      yMinHz: -0.004321,
      yMaxHz: 57863.2,
    })

    expect(seeded.xMinHz).toBe(-57900)
    expect(seeded.xMaxHz).toBe(12.4)
    expect(seeded.yMinHz).toBe(-0.00433)
    expect(seeded.yMaxHz).toBe(57900)
  })

  it('contains the window it was given, on every bound', () => {
    const fitted = { kind: 'PoleZero' as const, xMinHz: -46012.7, xMaxHz: 118.03, yMinHz: -38411.9, yMaxHz: 38411.9 }
    const seeded = seedPoleZeroWindow(fitted)

    expect(seeded.xMinHz).toBeLessThanOrEqual(fitted.xMinHz)
    expect(seeded.xMaxHz).toBeGreaterThanOrEqual(fitted.xMaxHz)
    expect(seeded.yMinHz).toBeLessThanOrEqual(fitted.yMinHz)
    expect(seeded.yMaxHz).toBeGreaterThanOrEqual(fitted.yMaxHz)
  })

  it('leaves a zero bound alone rather than taking the log of it', () => {
    const seeded = seedPoleZeroWindow({ kind: 'PoleZero', xMinHz: -100, xMaxHz: 0, yMinHz: 0, yMaxHz: 0 })

    expect(seeded.xMaxHz).toBe(0)
    expect(seeded.yMinHz).toBe(0)
    expect(seeded.yMaxHz).toBe(0)
  })
})
