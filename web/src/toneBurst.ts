import { wavePeriodFrames } from './periodicWave'

/**
 * The tone burst's arithmetic, client side — a second implementation of
 * `Tonebench.Device.ToneBurst`, for the reason `periodicWave.ts` is one of `PeriodicWave`: the
 * panel has to say what will be played before the device is ever asked, and the oscilloscope's
 * stimulus trigger needs the repeat period in frames or its window drifts.
 *
 * The carrier goes through the shape family's own quantiser, so a burst and a square asked for the
 * same frequency play the same frequency — which is what makes "the same tone, gated" a true
 * description of the comparison.
 */

/** Matches ToneBurst.MinCyclesOn / MinCyclesOff / MaxCycles. */
export const MIN_BURST_CYCLES_ON = 1
export const MIN_BURST_CYCLES_OFF = 0
export const MAX_BURST_CYCLES = 100_000

/** One cycle of the carrier, in frames. */
export function burstCycleFrames(freqHz: number, sampleRateHz: number): number {
  return wavePeriodFrames(freqHz, sampleRateHz)
}

/** How long one on-plus-off group lasts, in frames, or 0 where there is no carrier to gate. */
export function burstPeriodFrames(
  freqHz: number,
  cyclesOn: number,
  cyclesOff: number,
  sampleRateHz: number,
): number {
  const cycle = burstCycleFrames(freqHz, sampleRateHz)
  if (cycle <= 0) return 0
  const on = clampCycles(cyclesOn, MIN_BURST_CYCLES_ON)
  const off = clampCycles(cyclesOff, MIN_BURST_CYCLES_OFF)
  const frames = (on + off) * cycle
  return Number.isSafeInteger(frames) ? frames : 0
}

/** How often the burst repeats, in Hz — the figure a scope's timebase is set from. */
export function burstRepetitionHz(
  freqHz: number,
  cyclesOn: number,
  cyclesOff: number,
  sampleRateHz: number,
): number {
  const period = burstPeriodFrames(freqHz, cyclesOn, cyclesOff, sampleRateHz)
  return period <= 0 ? 0 : sampleRateHz / period
}

/** The fraction of the time the carrier is actually playing. */
export function burstDutyFraction(cyclesOn: number, cyclesOff: number): number {
  const on = clampCycles(cyclesOn, MIN_BURST_CYCLES_ON)
  const total = on + clampCycles(cyclesOff, MIN_BURST_CYCLES_OFF)
  return total <= 0 ? 0 : on / total
}

/**
 * Peak volts for the carrier's level in dBV rms. A sine's conversion, because the thing being
 * gated is a sine — and the peak does not move as the gate narrows, which is what keeps the output
 * range where it is.
 */
export function burstPeakVolts(levelDbv: number): number {
  return Math.SQRT2 * Math.pow(10, levelDbv / 20)
}

/**
 * The whole stimulus's rms, dBV: the carrier's level plus `10.log10(duty)`. What a broadband level
 * meter reads, and the figure to compare against a continuous tone when the question is energy
 * rather than amplitude.
 */
export function burstOverallRmsDbv(levelDbv: number, cyclesOn: number, cyclesOff: number): number | null {
  const duty = burstDutyFraction(cyclesOn, cyclesOff)
  return duty <= 0 ? null : levelDbv + 10 * Math.log10(duty)
}

/**
 * The level the *carrier line* reads in a spectrum, dBV: the gate's own DC term times the carrier,
 * i.e. the level plus `20.log10(duty)`. The rest of the energy is in the sidebands the gating puts
 * at multiples of the repetition rate either side of the carrier, which is the burst's spectrum and
 * not an error — a burst is a time-domain stimulus, read on the scope screen.
 */
export function burstCarrierLineDbv(levelDbv: number, cyclesOn: number, cyclesOff: number): number | null {
  const duty = burstDutyFraction(cyclesOn, cyclesOff)
  return duty <= 0 ? null : levelDbv + 20 * Math.log10(duty)
}

function clampCycles(value: number, floor: number): number {
  if (!Number.isFinite(value)) return floor
  return Math.min(Math.max(Math.round(value), floor), MAX_BURST_CYCLES)
}
