// The `Oscillating` model: `Biquad.Resonator`'s pole pair outside the unit circle, run through
// `SimulatedDut.NextOscillatorSample` with the rail clamp inside the recursion. Autonomous — the
// input is never read — so its output can be produced here exactly, and its spectrum read off the
// settled limit cycle the way the FFT screen would.

export const OSCILLATION_POLE_HZ = 1000
export const OSCILLATION_EXCESS = 0.01
export const OSCILLATION_RAIL_VOLTS = 0.5
export const OSCILLATION_SEED = 0.01

/** The limit cycle of the oscillator at `sampleRateHz`: `seconds` of output after `settleSeconds` are discarded. */
export function oscillatorOutput(sampleRateHz: number, seconds: number, settleSeconds = 3): Float64Array {
  const f0 = Math.min(Math.max(OSCILLATION_POLE_HZ, 1), sampleRateHz * 0.45)
  const w0 = (2 * Math.PI * f0) / sampleRateHz
  const radius = 1 + OSCILLATION_EXCESS * w0 * w0
  const a1 = -2 * radius * Math.cos(w0)
  const a2 = radius * radius

  let state0 = OSCILLATION_SEED
  let state1 = 0
  const step = (): number => {
    const y = Math.min(Math.max(state0, -OSCILLATION_RAIL_VOLTS), OSCILLATION_RAIL_VOLTS)
    state0 = -a1 * y + state1
    state1 = -a2 * y
    return y
  }

  for (let i = 0; i < Math.round(settleSeconds * sampleRateHz); i++) step()
  const out = new Float64Array(Math.round(seconds * sampleRateHz))
  for (let i = 0; i < out.length; i++) out[i] = step()
  return out
}

export interface OscillatorSpectrum {
  /** Where the limit cycle actually sits, Hz — about 1 % above the pole. */
  fundamentalHz: number
  /** The fundamental and its harmonics as the FFT screen would list them: frequency and dBV (rms). */
  lines: { freqHz: number; dbv: number }[]
}

/**
 * The limit cycle's fundamental and harmonics. The frequency is found from the zero crossings of
 * the settled output, then each line is read by direct DFT over an integer number of cycles.
 */
export function oscillatorSpectrum(sampleRateHz: number, orders = 9): OscillatorSpectrum {
  const y = oscillatorOutput(sampleRateHz, 0.5)

  let crossings = 0
  let first = -1
  let last = -1
  for (let i = 1; i < y.length; i++) {
    if (y[i - 1]! < 0 && y[i]! >= 0) {
      if (first < 0) first = i
      last = i
      crossings++
    }
  }
  const cycles = crossings - 1
  const fundamentalHz = (cycles * sampleRateHz) / (last - first)

  const n = last - first
  const lines: { freqHz: number; dbv: number }[] = []
  for (let k = 1; k <= orders; k++) {
    const w = (2 * Math.PI * fundamentalHz * k) / sampleRateHz
    let re = 0
    let im = 0
    for (let i = first; i < last; i++) {
      re += y[i]! * Math.cos(w * (i - first))
      im -= y[i]! * Math.sin(w * (i - first))
    }
    const peak = (2 * Math.hypot(re, im)) / n
    lines.push({ freqHz: k * fundamentalHz, dbv: 20 * Math.log10(peak / Math.SQRT2) })
  }
  return { fundamentalHz, lines }
}
