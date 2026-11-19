import { describe, expect, it } from 'vitest'
import {
  burstCarrierLineDbv,
  burstCycleFrames,
  burstDutyFraction,
  burstOverallRmsDbv,
  burstPeakVolts,
  burstPeriodFrames,
  burstRepetitionHz,
  MAX_BURST_CYCLES,
} from './toneBurst'
import { wavePeriodFrames } from './periodicWave'

/**
 * The client's copy of `Tonebench.Device.ToneBurst`. It exists for `periodicWave.ts`'s reason: the
 * panel has to say what will be played before the device is ever asked, and the oscilloscope's
 * stimulus trigger needs the repeat in frames or its window drifts. That the two are copies is what
 * these cases pin — every expectation here is the same arithmetic `ToneBurstTests` asserts in C#.
 */
describe('burstCycleFrames', () => {
  it('is the shape family own quantiser, so a burst and a square agree about the frequency', () => {
    for (const freqHz of [1000, 750, 10_000, 700]) {
      expect(burstCycleFrames(freqHz, 48_000)).toBe(wavePeriodFrames(freqHz, 48_000))
      expect(burstCycleFrames(freqHz, 192_000)).toBe(wavePeriodFrames(freqHz, 192_000))
    }
  })

  it('is zero where there is no carrier to gate', () => {
    expect(burstCycleFrames(0, 48_000)).toBe(0)
    expect(burstCycleFrames(1000, 0)).toBe(0)
    expect(burstCycleFrames(Number.NaN, 48_000)).toBe(0)
  })
})

describe('burstPeriodFrames', () => {
  it('is a whole number of carrier cycles, which is what makes the stimulus periodic', () => {
    // 750 Hz at 48 kHz is 64 frames a cycle; 5 on and 20 off is 25 cycles.
    expect(burstPeriodFrames(750, 5, 20, 48_000)).toBe(25 * 64)
    expect(burstRepetitionHz(750, 5, 20, 48_000)).toBeCloseTo(48_000 / (25 * 64), 9)
  })

  it('answers zero rather than a wrong period where there is no carrier', () => {
    expect(burstPeriodFrames(0, 5, 20, 48_000)).toBe(0)
    expect(burstRepetitionHz(0, 5, 20, 48_000)).toBe(0)
  })

  it('clamps the cycle counts rather than overflowing the frame arithmetic', () => {
    // One cycle on at least: a burst of none is silence wearing a frequency.
    expect(burstPeriodFrames(750, 0, 0, 48_000)).toBe(64)
    expect(burstPeriodFrames(750, MAX_BURST_CYCLES + 5000, 0, 48_000)).toBe(MAX_BURST_CYCLES * 64)
  })
})

describe('burstDutyFraction', () => {
  it('is the share of the time the carrier plays', () => {
    expect(burstDutyFraction(5, 20)).toBeCloseTo(0.2, 12)
    expect(burstDutyFraction(1, 1)).toBeCloseTo(0.5, 12)
  })

  it('is one with nothing off, which is the continuous tone the burst is compared against', () => {
    expect(burstDutyFraction(3, 0)).toBe(1)
  })
})

describe('the three levels a burst has', () => {
  /**
   * The level names the carrier while it plays, so the peak does not move as the gate narrows —
   * which is what keeps the output range where it is. The overall rms falls as the square root of
   * the duty, and the carrier *line* a spectrum reads falls as the duty itself.
   */
  it('keeps the peak and moves the other two', () => {
    expect(burstPeakVolts(0)).toBeCloseTo(Math.SQRT2, 12)
    expect(burstPeakVolts(-20)).toBeCloseTo(Math.SQRT2 / 10, 12)

    // A quarter duty: 6.02 dB off the overall rms, 12.04 off the line.
    expect(burstOverallRmsDbv(-10, 1, 3)).toBeCloseTo(-10 - 6.0206, 3)
    expect(burstCarrierLineDbv(-10, 1, 3)).toBeCloseTo(-10 - 12.0412, 3)
  })

  it('leaves both alone at a continuous duty', () => {
    expect(burstOverallRmsDbv(-10, 1, 0)).toBeCloseTo(-10, 12)
    expect(burstCarrierLineDbv(-10, 1, 0)).toBeCloseTo(-10, 12)
  })
})
