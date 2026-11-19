import type { GeneratorKind } from './api/client'

/**
 * The shape-family period quantisation, client side — a second implementation of
 * `Tonebench.Device.PeriodicWave`. The two must agree about *which* frequency is played; that
 * they are two copies is what the periodicWave.test.ts cases pin. (The app's other client-side
 * duplicate, attenuation.ts' copy of the input-range ladder, is gone: its one reader was the
 * browser's ranging watcher, which is `SessionInputRanger` now.)
 *
 * It exists because two things on screen have to know the played frequency before (and after) the
 * device is ever asked: the Generator panel, which shows it beneath the typed value, and the
 * oscilloscope's trigger, which needs the real period or its window drifts — 10 kHz asked for at
 * 192 kHz plays at 9.6 kHz, and a trigger 4 % out walks the trace across the screen.
 */

/** Matches PeriodicWave.MinPeriodFrames. Two samples high and two low, i.e. a ceiling of fs/4. */
export const MIN_WAVE_PERIOD_FRAMES = 4

/** Matches PeriodicWave.MinDutyPercent / MaxDutyPercent — a pulse that is never high, or always, is silence. */
export const MIN_DUTY_PERCENT = 1
export const MAX_DUTY_PERCENT = 99

/** True for the kinds that are a hard-switched waveform rather than a sum of tones. */
export function isWaveKind(kind: GeneratorKind): boolean {
  return kind === 'Square' || kind === 'Triangle' || kind === 'Sawtooth' || kind === 'Pulse'
}

/**
 * True for the shapes that carry every harmonic rather than the odd ones alone.
 *
 * A square and a triangle are half-wave symmetric — the second half of a period is the negative of
 * the first — which is exactly the condition that kills every even harmonic. A sawtooth is not, and
 * a pulse is only at 50 %. The distinction is what the sawtooth is *chosen* for: its even harmonics
 * are what makes it the shape to reach for when a DUT's second-order behaviour is the subject.
 */
export function waveHasEvenHarmonics(kind: GeneratorKind): boolean {
  return kind === 'Sawtooth' || kind === 'Pulse'
}

/**
 * One cycle as a whole, even number of frames, or 0 when there is no waveform to play. Even so the
 * two halves of a square hold the same number of samples — an odd period is a DC offset and a full
 * set of even harmonics on a waveform that should have neither. A sawtooth would be happy with an
 * odd period and gets the even one anyway, so all four shapes at one setting play one frequency.
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

/**
 * How many frames of a period a pulse spends high: the duty rounded to a whole sample, never 0 and
 * never the whole period. Mirrors PeriodicWave.WidthFrames — a width that fell between samples
 * would not repeat, which is the same rule the period itself follows.
 */
export function pulseWidthFrames(dutyPercent: number, periodFrames: number): number {
  if (periodFrames < MIN_WAVE_PERIOD_FRAMES) return 0
  const width = Math.round((periodFrames * resolveDutyPercent(dutyPercent)) / 100)
  return Math.min(Math.max(width, 1), periodFrames - 1)
}

/**
 * The duty to actually use for a stored or typed one: clamped to the range a pulse has, and **50
 * for anything that is not a positive number at all**.
 *
 * That second half mirrors `PeriodicWave.ResolveDutyPercent`, and it is a compatibility rule rather
 * than a clamp: a `params_json` written before this field existed has no `dutyPercent`, and the
 * server's source-generated resolver hands an absent `init` property back as 0 rather than as the
 * record's own 50. Nobody can have typed a 0 — the endpoint refuses anything outside 1..99 — so a
 * non-positive duty means "not stated".
 */
export function resolveDutyPercent(dutyPercent: number): number {
  if (!Number.isFinite(dutyPercent) || dutyPercent <= 0) return 50
  return Math.min(Math.max(dutyPercent, MIN_DUTY_PERCENT), MAX_DUTY_PERCENT)
}

/** The duty a pulse really plays, in percent — shown beneath what was typed, like the frequency. */
export function pulseEffectiveDutyPercent(dutyPercent: number, periodFrames: number): number {
  const width = pulseWidthFrames(dutyPercent, periodFrames)
  return width <= 0 ? 0 : (100 * width) / periodFrames
}

/** Harmonics below Nyquist, counting the fundamental — what sets the edge, so what the panel quotes. */
export function waveHarmonicsBelowNyquist(kind: GeneratorKind, freqHz: number, sampleRateHz: number): number {
  const period = wavePeriodFrames(freqHz, sampleRateHz)
  if (period <= 0) return 0
  const all = period / 2 - 1
  if (all < 1) return 1
  return waveHasEvenHarmonics(kind) ? all : Math.ceil(all / 2)
}

/**
 * Peak volts for a level in dBV rms — the level is rms for every kind here, so the shapes at one
 * level have different peaks (square and pulse 1x, sine sqrt(2), triangle and sawtooth sqrt(3)).
 * Mirrors PeriodicWave.PeakVolts, and the panel shows it because the peak is what a DUT is actually
 * driven to and what the output range has to clear.
 *
 * A pulse's peak is its rms at *every* duty, which is the property that makes the duty control safe
 * to turn: narrowing a pulse changes its spectrum and its DC and leaves its loudness alone.
 */
export function wavePeakVolts(kind: GeneratorKind, levelDbv: number): number {
  const rms = Math.pow(10, levelDbv / 20)
  if (kind === 'Square' || kind === 'Pulse') return rms
  if (kind === 'Triangle' || kind === 'Sawtooth') return Math.sqrt(3) * rms
  return 0
}

/**
 * The DC a shape carries, in volts: zero for everything but a pulse away from 50 %.
 *
 * A bipolar pulse of duty d sits at `peak(2d - 1)` on average, which at a 10 % duty is -0.8 of peak
 * — most of the waveform rather than a trim. Shown rather than removed: removing it would mean
 * moving the two levels apart by different amounts, and then the peak a reader sets would be
 * neither of them. Both analyzer inputs are AC-coupled, so this never reaches a *reading* — it
 * reaches the DUT, which is the point of saying it out loud.
 */
export function waveDcVolts(kind: GeneratorKind, levelDbv: number, dutyPercent: number, periodFrames: number): number {
  if (kind !== 'Pulse' || periodFrames < MIN_WAVE_PERIOD_FRAMES) return 0
  const width = pulseWidthFrames(dutyPercent, periodFrames)
  return wavePeakVolts(kind, levelDbv) * ((2 * width) / periodFrames - 1)
}

/**
 * An ideal shape's own total harmonic content, as a percentage of its fundamental. Square: the
 * fundamental holds 0.9003 of the rms, so the rest is 48.34 %. Triangle: 0.9926, so 12.24 %.
 * Sawtooth: 0.7797, so 80.31 %. Pulse: the square's figure divided by sin(pi.d), so it climbs
 * steeply as the pulse narrows.
 *
 * <p>This is the figure the <em>noise-integrated</em> tiles would read -- THD+N and SNR, which count
 * everything in the band -- and not what the THD tile shows: that one sums only the harmonics the
 * analyzer tracks (a dozen by default), so it reads lower and by an amount that depends on a
 * setting. Quote this where the whole band is meant, and waveIdealHarmonicsDbc where a line-by-line
 * comparison is.</p>
 */
export function waveIdealThdPercent(kind: GeneratorKind, dutyPercent = 50): number | null {
  const square = 4 / (Math.PI * Math.SQRT2)
  const fundamental =
    kind === 'Square'
      ? square
      : kind === 'Triangle'
        ? (8 / (Math.PI * Math.PI * Math.SQRT2)) * Math.sqrt(3)
        : kind === 'Sawtooth'
          ? (2 / Math.PI) * Math.sqrt(3) * Math.SQRT1_2
          : kind === 'Pulse'
            ? square * Math.sin((Math.PI * dutyPercent) / 100)
            : null
  if (fundamental === null || fundamental <= 0 || fundamental >= 1) return null
  return (Math.sqrt(1 - fundamental * fundamental) / fundamental) * 100
}

/**
 * The levels an ideal shape's first few harmonics sit at, relative to its own fundamental: 1/n for
 * a square and a sawtooth, 1/n^2 for a triangle, and `sin(n.pi.d)/(n.sin(pi.d))` for a pulse, whose
 * nulls at every 1/d-th harmonic are its signature.
 *
 * The orders offered follow the shape: odd only where the even ones are absent by construction, so
 * a table of "H2: nothing" is never printed.
 *
 * This is the comparison the harmonics table supports directly, and it is why these stimuli are
 * worth having: the departure from these levels is the DUT's own high-frequency behaviour, read off
 * one capture. Note the real series sits slightly *above* them towards Nyquist, because the ideal
 * harmonics above it fold back and add -- see Tonebench.Device.PeriodicWave.
 */
export function waveIdealHarmonicsDbc(
  kind: GeneratorKind,
  dutyPercent = 50,
  orders?: number[],
): { order: number; dbc: number }[] {
  if (!isWaveKind(kind)) return []
  const list = orders ?? (waveHasEvenHarmonics(kind) ? [2, 3, 4] : [3, 5, 7])

  if (kind === 'Pulse') {
    const d = dutyPercent / 100
    const first = Math.sin(Math.PI * d)
    if (!(first > 0)) return []
    // The orders quoted are the first three a pulse of this width actually HAS. A pulse nulls every
    // 1/d-th harmonic, and a row reading "-324 dBc" would be floating-point noise presented as a
    // measurement -- while the nulls themselves are the thing a reader looks at to confirm the duty
    // is what they set, which is what the panel's duty readout says in numbers instead.
    const quoted = orders ?? pulseOrdersWithContent(d)
    return quoted.map((order) => ({
      order,
      dbc: 20 * Math.log10(Math.abs(Math.sin(order * Math.PI * d)) / (order * first)),
    }))
  }

  const power = kind === 'Triangle' ? 2 : 1
  return list.map((order) => ({ order, dbc: -20 * power * Math.log10(order) }))
}

/** The first three harmonics a pulse of duty `d` is not nulling — see waveIdealHarmonicsDbc. */
function pulseOrdersWithContent(d: number): number[] {
  const found: number[] = []
  for (let order = 2; order <= 64 && found.length < 3; order++) {
    if (Math.abs(Math.sin(order * Math.PI * d)) > 1e-9) found.push(order)
  }
  return found
}
