import { describe, expect, it } from 'vitest'
import { sweepOverlayScreen, type SweepOverlayScreen } from './overlayScope'
import { sweepOverlays } from './sweepOverlayStyles'
import type { OverlayEntry } from '../composables/useMeasurements'
import type { ThdSweepPoint } from '../api/client'
import {
  CROSSTALK_KIND,
  FFT_SPECTRUM_KIND,
  FREQUENCY_RESPONSE_KIND,
  IMD_VS_AMPLITUDE_KIND,
  IMPEDANCE_KIND,
  IMPULSE_RESPONSE_KIND,
  MEASUREMENT_KINDS,
  THD_VS_AMPLITUDE_KIND,
  THD_VS_FREQUENCY_KIND,
} from './kinds'

describe('sweepOverlayScreen', () => {
  /**
   * The reported bug (2026-10-17): a stored THD-vs-frequency run showed up on a THD-vs-amplitude
   * chart — and in its cursor readings and both preview charts — because all three sweep kinds
   * store their points in one shape and the filter only asked for the shape.
   */
  it('keeps a frequency sweep off the amplitude screen, though their points are the same shape', () => {
    expect(sweepOverlayScreen(THD_VS_FREQUENCY_KIND)).not.toBe(sweepOverlayScreen(THD_VS_AMPLITUDE_KIND))
  })

  it('keeps an IMD sweep off the THD amplitude screen, which would label its figures as THD', () => {
    expect(sweepOverlayScreen(IMD_VS_AMPLITUDE_KIND)).not.toBe(sweepOverlayScreen(THD_VS_AMPLITUDE_KIND))
  })

  it('claims nothing for a kind that stores no sweep points', () => {
    for (const kind of [FFT_SPECTRUM_KIND, FREQUENCY_RESPONSE_KIND, IMPEDANCE_KIND, IMPULSE_RESPONSE_KIND, CROSSTALK_KIND])
      expect(sweepOverlayScreen(kind)).toBeNull()
    expect(sweepOverlayScreen(null)).toBeNull()
    expect(sweepOverlayScreen(undefined)).toBeNull()
  })

  /**
   * The same property curveOverlayScreen's own test asserts: every kind the app can create lands on
   * at most one screen, and here no two kinds share one at all.
   */
  it('gives each sweep kind a screen of its own and every other kind none', () => {
    const byScreen = new Map<SweepOverlayScreen, string[]>()

    for (const { kind } of MEASUREMENT_KINDS) {
      const screen = sweepOverlayScreen(kind)
      if (screen === null) continue
      byScreen.set(screen, [...(byScreen.get(screen) ?? []), kind])
    }

    expect(Object.fromEntries(byScreen)).toEqual({
      [THD_VS_FREQUENCY_KIND]: [THD_VS_FREQUENCY_KIND],
      [THD_VS_AMPLITUDE_KIND]: [THD_VS_AMPLITUDE_KIND],
      [IMD_VS_AMPLITUDE_KIND]: [IMD_VS_AMPLITUDE_KIND],
    })
  })
})

describe('sweepOverlays', () => {
  const point = { freqHz: 1000, channel: 0 } as unknown as ThdSweepPoint

  // `points: null` means the field is absent — an explicit `undefined` would take the default,
  // which is how the first cut of this test asserted the opposite of what it meant to.
  function entry(id: number, kind: string, visible = true, points: ThdSweepPoint[] | null = [point]): OverlayEntry {
    return { id, visible, sweepPoints: points ?? undefined, detail: { kind, name: `run ${id}` } } as unknown as OverlayEntry
  }

  it('draws only the open screen’s own kind, whatever else is checked on', () => {
    const list = [
      entry(1, THD_VS_AMPLITUDE_KIND),
      entry(2, THD_VS_FREQUENCY_KIND),
      entry(3, IMD_VS_AMPLITUDE_KIND),
      entry(4, THD_VS_AMPLITUDE_KIND),
    ]

    expect(sweepOverlays(list, THD_VS_AMPLITUDE_KIND).map((o) => o.id)).toEqual([1, 4])
    expect(sweepOverlays(list, THD_VS_FREQUENCY_KIND).map((o) => o.id)).toEqual([2])
    expect(sweepOverlays(list, IMD_VS_AMPLITUDE_KIND).map((o) => o.id)).toEqual([3])
  })

  it('still skips an unchecked run, and one carrying no points at all', () => {
    const list = [
      entry(1, THD_VS_AMPLITUDE_KIND, false),
      entry(2, THD_VS_AMPLITUDE_KIND, true, null),
      entry(3, THD_VS_AMPLITUDE_KIND, true, []),
      entry(4, THD_VS_AMPLITUDE_KIND),
    ]

    expect(sweepOverlays(list, THD_VS_AMPLITUDE_KIND).map((o) => o.id)).toEqual([4])
  })

  /** What the response and impulse charts pass: they draw no sweep, so they get none. */
  it('draws nothing at all for a screen that is not a sweep', () => {
    expect(sweepOverlays([entry(1, THD_VS_AMPLITUDE_KIND)], null)).toEqual([])
  })
})
