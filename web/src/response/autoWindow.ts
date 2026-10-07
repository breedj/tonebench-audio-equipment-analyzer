import type { PoleZeroWindowHz } from './splaneScale'

/**
 * The window a complex-plane chart fitted for itself, in the units the display panel's own boxes
 * hold — so unticking **Auto** starts from where the curve actually is rather than from a default
 * that has nothing to do with this DUT.
 *
 * `ResponsePolarChart` exposes it (the same arrangement as the curve `ResponsePoleZeroPanel` reads:
 * App.vue passes the mounted chart's exposed value on to the panel), because the fit depends on the
 * drawn points, the radius warp and the fitted roots — all of which live in the chart. A control
 * whose first value is a guess is the control that looks broken, which is the whole lesson of the
 * uPlot range callbacks in CLAUDE.md.
 *
 * Separate from `response/display.ts` because this is not stored state: it is read off whatever is
 * on screen now, and it is never written to `params_json`.
 */
export interface NicholsAutoWindow {
  kind: 'Nichols'
  topDb: number
  bottomDb: number
}

export interface PoleZeroAutoWindow extends PoleZeroWindowHz {
  kind: 'PoleZero'
}

export type ResponseAutoWindow = NicholsAutoWindow | PoleZeroAutoWindow

/** The grid a seeded gain window is snapped out to, matching the Bode chart's own dB step. */
export const NICHOLS_SEED_STEP_DB = 5

/**
 * A fitted gain window as a pair of numbers worth putting in a box: snapped **outward** to whole
 * steps, so the seeded window still contains everything the fit contained.
 *
 * The fit's own padding is a fraction of the span (1.37 dB on a typical loop), and `-13.37` in a
 * field reads as a measurement rather than as a choice — which it then invites editing by hand into
 * something worse.
 */
export function seedNicholsGain(window: NicholsAutoWindow): { topDb: number; bottomDb: number } {
  const step = NICHOLS_SEED_STEP_DB
  const hi = Math.max(window.topDb, window.bottomDb)
  const lo = Math.min(window.topDb, window.bottomDb)
  if (!Number.isFinite(hi) || !Number.isFinite(lo)) return { topDb: 20, bottomDb: -40 }
  // + 0 so a Math.ceil of a small negative does not put "-0" in the box.
  return { topDb: Math.ceil(hi / step) * step + 0, bottomDb: Math.floor(lo / step) * step + 0 }
}

/**
 * A fitted pole–zero window as four frequencies worth putting in a box: each bound rounded
 * **outward** to three significant figures.
 *
 * Significant figures rather than a fixed grid, because this axis spans decades by design — the
 * ordinary amplifier has a 12 Hz coupling pole and a 46 kHz transformer pole — so one step size is
 * either useless at the bottom or useless at the top.
 */
export function seedPoleZeroWindow(window: PoleZeroAutoWindow): PoleZeroWindowHz {
  return {
    xMinHz: roundOutward(window.xMinHz, 'down'),
    xMaxHz: roundOutward(window.xMaxHz, 'up'),
    yMinHz: roundOutward(window.yMinHz, 'down'),
    yMaxHz: roundOutward(window.yMaxHz, 'up'),
  }
}

const SEED_SIGNIFICANT_FIGURES = 3

function roundOutward(hz: number, direction: 'up' | 'down'): number {
  if (!Number.isFinite(hz) || hz === 0) return 0

  const magnitude = Math.pow(10, Math.floor(Math.log10(Math.abs(hz))) - (SEED_SIGNIFICANT_FIGURES - 1))
  const rounded = direction === 'up' ? Math.ceil(hz / magnitude) * magnitude : Math.floor(hz / magnitude) * magnitude
  // The division reintroduces the float dust the round was meant to remove (12.3 / 0.1 is 122.999…).
  return Number(rounded.toPrecision(12))
}
