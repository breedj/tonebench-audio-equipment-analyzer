import type { GeneratorKind } from './api/client'

/**
 * The square/triangle period quantisation, client side — a second implementation of
 * `Tonebench.Device.PeriodicWave`, the way attenuation.ts is a second implementation of
 * InputRangeSelector's ladder. The two must agree about *which* frequency is played; that they are
 * two copies is what the periodicWave.test.ts cases pin.
 *
 * It exists because two things on screen have to know the played frequency before (and after) the
 * device is ever asked: the Generator panel, which shows it beneath the typed value, and the
 * oscilloscope's trigger, which needs the real period or its window drifts — 10 kHz asked for at
 * 192 kHz plays at 9.6 kHz, and a trigger 4 % out walks the trace across the screen.
 */

/** Matches PeriodicWave.MinPeriodFrames. Two samples high and two low, i.e. a ceiling of fs/4. */
export const MIN_WAVE_PERIOD_FRAMES = 4

/** True for the kinds that are a hard-switched waveform rather than a sum of tones. */
export function isWaveKind(kind: GeneratorKind): boolean {
  return kind === 'Square' || kind === 'Triangle'
}

/**
 * One cycle as a whole, even number of frames, or 0 when there is no waveform to play. Even so the
 * two halves of a square hold the same number of samples — an odd period is a DC offset and a full
 * set of even harmonics on a waveform that should have neither.
 */
export function wavePeriodFrames(freqHz: number, sampleRateHz: number): number {
  if (!Number.isFinite(freqHz) || freqHz <= 0 || !Number.isFinite(sampleRateHz) || sampleRateHz <= 0) return 0
  // Round the half period and double it, so the grid is one frame rather than two — see the C# copy.
  const half = Math.round(sampleRateHz / (2 * freqHz))
  const period = Math.max(half * 2, MIN_WAVE_PERIOD_FRAMES)
  return Number.isSafeInteger(period) ? period : 0
}

/** The frequency actually played for a requested one: fs / period, or 0 when nothing is. */
export function waveEffectiveFreqHz(freqHz: number, sampleRateHz: number): number {
  const period = wavePeriodFrames(freqHz, sampleRateHz)
  return period <= 0 ? 0 : sampleRateHz / period
}

/** Odd harmonics below Nyquist, counting the fundamental — what sets the edge, so what the panel quotes. */
export function waveHarmonicsBelowNyquist(freqHz: number, sampleRateHz: number): number {
  const period = wavePeriodFrames(freqHz, sampleRateHz)
  return period <= 0 ? 0 : Math.ceil(period / 2 / 2)
}

/**
 * Peak volts for a level in dBV rms — the level is rms for every kind here, so the three shapes at
 * one level have three different peaks (square 1x, sine sqrt(2), triangle sqrt(3)). Mirrors
 * PeriodicWave.PeakVolts, and the panel shows it because the peak is what a DUT is actually driven
 * to and what the output range has to clear.
 */
export function wavePeakVolts(kind: GeneratorKind, levelDbv: number): number {
  const rms = Math.pow(10, levelDbv / 20)
  if (kind === 'Square') return rms
  if (kind === 'Triangle') return Math.sqrt(3) * rms
  return 0
}

/**
 * An ideal shape's own total harmonic content, as a percentage of its fundamental. Square: the
 * fundamental holds 0.9003 of the rms, so the rest is 48.34 %. Triangle: 0.9926, so 12.24 %.
 *
 * <p>This is the figure the <em>noise-integrated</em> tiles would read -- THD+N and SNR, which count
 * everything in the band -- and not what the THD tile shows: that one sums only the harmonics the
 * analyzer tracks (a dozen by default), so it reads lower and by an amount that depends on a
 * setting. Quote this where the whole band is meant, and waveIdealHarmonicsDbc where a line-by-line
 * comparison is.</p>
 */
export function waveIdealThdPercent(kind: GeneratorKind): number | null {
  const fundamental = kind === 'Square'
    ? 4 / (Math.PI * Math.SQRT2)
    : kind === 'Triangle'
      ? (8 / (Math.PI * Math.PI * Math.SQRT2)) * Math.sqrt(3)
      : null
  if (fundamental === null) return null
  return (Math.sqrt(1 - fundamental * fundamental) / fundamental) * 100
}

/**
 * The levels an ideal shape's first few odd harmonics sit at, relative to its own fundamental:
 * 1/n for a square (-9.5, -14.0, -16.9 dBc) and 1/n^2 for a triangle (-19.1, -28.0, -33.8).
 *
 * This is the comparison the harmonics table supports directly, and it is why these stimuli are
 * worth having: the departure from these levels is the DUT's own high-frequency behaviour, read off
 * one capture. Note the real series sits slightly *above* them towards Nyquist, because the ideal
 * harmonics above it fold back and add -- see Tonebench.Device.PeriodicWave.
 */
export function waveIdealHarmonicsDbc(kind: GeneratorKind, orders: number[] = [3, 5, 7]): { order: number; dbc: number }[] {
  if (!isWaveKind(kind)) return []
  const power = kind === 'Square' ? 1 : 2
  return orders.map((order) => ({ order, dbc: -20 * power * Math.log10(order) }))
}
