// A textbook loop gain, for the manual's Nyquist and Nichols illustrations.
//
// Not a simulator model and not on any measurement path: the simulator's DUTs are what you measure,
// while this is the thing those two views were invented for — an amplifier's open-loop gain with
// the feedback opened, whose contour has a gain margin and a phase margin worth pointing at. Three
// poles, because two can never reach −180° and so has infinite margins, which would illustrate
// nothing.
//
// Analog throughout (s = jω, no sampling), since the picture is about the shape of a contour rather
// than about what this app's sample rate does to it.

export interface LoopPoint {
  freqHz: number
  re: number
  im: number
  magDb: number
  /** Degrees, unwrapped downward from 0 at DC — the convention the app's charts use. */
  phaseDeg: number
}

/**
 * The illustration's loop: 40 dB at DC, poles at 100 Hz, 10 kHz and 40 kHz. Chosen to be a
 * comfortably stable amplifier — 42° of phase margin and 14 dB of gain margin — because the point
 * of the picture is where those two distances are, and a loop on the edge has nothing to point at.
 */
export const ILLUSTRATION_LOOP = { dcGainDb: 40, poleHz: [100, 10_000, 40_000] } as const

/**
 * `K / ((1 + jf/f1)(1 + jf/f2)(1 + jf/f3))` at one frequency. The phase is the sum of the poles'
 * own arctangents rather than `atan2` of the result, which is what makes it continuous through
 * −180° instead of wrapping there — the same thing the measurement's unwrapper does across bins.
 */
export function loopGainAt(freqHz: number, loop: { dcGainDb: number; poleHz: readonly number[] } = ILLUSTRATION_LOOP): LoopPoint {
  const k = Math.pow(10, loop.dcGainDb / 20)
  let magnitude = k
  let phaseDeg = 0
  for (const pole of loop.poleHz) {
    const w = freqHz / pole
    magnitude /= Math.hypot(1, w)
    phaseDeg -= (Math.atan(w) * 180) / Math.PI
  }
  const rad = (phaseDeg * Math.PI) / 180
  return {
    freqHz,
    re: magnitude * Math.cos(rad),
    im: magnitude * Math.sin(rad),
    magDb: 20 * Math.log10(magnitude),
    phaseDeg,
  }
}

/** The contour over `count` frequencies spaced evenly in log f. */
export function loopGainCurve(fromHz: number, toHz: number, count = 600, loop = ILLUSTRATION_LOOP): LoopPoint[] {
  const a = Math.log10(fromHz)
  const b = Math.log10(toHz)
  const out: LoopPoint[] = []
  for (let i = 0; i < count; i++) out.push(loopGainAt(Math.pow(10, a + ((b - a) * i) / (count - 1)), loop))
  return out
}

/** Where |G| = 1, and the phase margin there: `180 + ∠G`. */
export function gainCrossover(loop = ILLUSTRATION_LOOP): { freqHz: number; phaseMarginDeg: number } {
  const freqHz = solve(1e-2, 1e7, (f) => loopGainAt(f, loop).magDb, 0)
  return { freqHz, phaseMarginDeg: 180 + loopGainAt(freqHz, loop).phaseDeg }
}

/** Where the phase reaches −180°, and the gain margin there: `−|G|` in dB. */
export function phaseCrossover(loop = ILLUSTRATION_LOOP): { freqHz: number; gainMarginDb: number } {
  const freqHz = solve(1e-2, 1e7, (f) => loopGainAt(f, loop).phaseDeg, -180)
  return { freqHz, gainMarginDb: -loopGainAt(freqHz, loop).magDb }
}

/** Bisection on a monotonically falling quantity — both of the above are, for this loop. */
function solve(lowHz: number, highHz: number, valueAt: (f: number) => number, target: number): number {
  let lo = lowHz
  let hi = highHz
  for (let i = 0; i < 200; i++) {
    const mid = Math.sqrt(lo * hi)
    if (valueAt(mid) > target) lo = mid
    else hi = mid
  }
  return Math.sqrt(lo * hi)
}
