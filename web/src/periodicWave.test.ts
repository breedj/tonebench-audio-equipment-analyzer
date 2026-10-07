import { describe, expect, it } from 'vitest'
import {
  isWaveKind,
  waveEffectiveFreqHz,
  waveHarmonicsBelowNyquist,
  waveIdealHarmonicsDbc,
  waveIdealThdPercent,
  wavePeakVolts,
  wavePeriodFrames,
} from './periodicWave'

// The figures below are the same ones Tonebench.Device.Tests.PeriodicWaveTests asserts against the
// C# copy. That is the point of having them twice: the panel's reading and the device's synthesis
// have to name the same frequency, or the oscilloscope triggers on a period nothing is playing.
describe('wavePeriodFrames', () => {
  it('is the nearest whole even period', () => {
    expect(wavePeriodFrames(1000, 48_000)).toBe(48)
    expect(wavePeriodFrames(700, 48_000)).toBe(68)
    expect(wavePeriodFrames(1000, 192_000)).toBe(192)
  })

  it('never returns an odd period, which would put a DC offset on a square', () => {
    for (const freqHz of [333, 700, 1234, 5678, 9999]) {
      for (const fs of [48_000, 96_000, 192_000]) {
        expect(wavePeriodFrames(freqHz, fs) % 2).toBe(0)
      }
    }
  })

  it('floors at four frames, so fs/4 is the ceiling', () => {
    expect(wavePeriodFrames(40_000, 48_000)).toBe(4)
    expect(waveEffectiveFreqHz(40_000, 48_000)).toBe(12_000)
  })

  it('has nothing to play without a frequency and a rate', () => {
    expect(wavePeriodFrames(0, 48_000)).toBe(0)
    expect(wavePeriodFrames(-1, 48_000)).toBe(0)
    expect(wavePeriodFrames(NaN, 48_000)).toBe(0)
    expect(wavePeriodFrames(1000, 0)).toBe(0)
    expect(waveEffectiveFreqHz(0, 48_000)).toBe(0)
  })
})

describe('waveEffectiveFreqHz', () => {
  it('is exact where the rate divides, and rounded where it does not', () => {
    expect(waveEffectiveFreqHz(1000, 48_000)).toBe(1000)
    expect(waveEffectiveFreqHz(1000, 192_000)).toBe(1000)
    // The case the panel exists to show: 10 kHz is not available at either rate.
    expect(waveEffectiveFreqHz(10_000, 192_000)).toBeCloseTo(9600, 6)
    expect(waveEffectiveFreqHz(10_000, 48_000)).toBeCloseTo(12_000, 6)
  })
})

describe('waveHarmonicsBelowNyquist', () => {
  it('counts the odd harmonics that fit', () => {
    expect(waveHarmonicsBelowNyquist(1000, 48_000)).toBe(12)
    expect(waveHarmonicsBelowNyquist(750, 48_000)).toBe(16)
    expect(waveHarmonicsBelowNyquist(1000, 192_000)).toBe(48)
    // At the ceiling there is only the fundamental -- a four-sample square is a sine.
    expect(waveHarmonicsBelowNyquist(40_000, 48_000)).toBe(1)
  })
})

describe('wavePeakVolts', () => {
  it('reads each shape own crest factor, not a sine', () => {
    // 0 dBV = 1 Vrms: a square peaks at 1 V, a triangle at sqrt(3).
    expect(wavePeakVolts('Square', 0)).toBeCloseTo(1, 12)
    expect(wavePeakVolts('Triangle', 0)).toBeCloseTo(Math.sqrt(3), 12)
    expect(wavePeakVolts('Tones', 0)).toBe(0)
  })
})

describe('waveIdealThdPercent', () => {
  it('is the textbook figure for each shape', () => {
    expect(waveIdealThdPercent('Square')!).toBeCloseTo(48.34, 2)
    expect(waveIdealThdPercent('Triangle')!).toBeCloseTo(12.1, 1)
    expect(waveIdealThdPercent('Tones')).toBeNull()
  })
})

describe('waveIdealHarmonicsDbc', () => {
  it('is 1/n for a square and 1/n squared for a triangle', () => {
    expect(waveIdealHarmonicsDbc('Square')).toEqual([
      { order: 3, dbc: expect.closeTo(-9.54, 2) },
      { order: 5, dbc: expect.closeTo(-13.98, 2) },
      { order: 7, dbc: expect.closeTo(-16.9, 2) },
    ])
    expect(waveIdealHarmonicsDbc('Triangle')).toEqual([
      { order: 3, dbc: expect.closeTo(-19.08, 2) },
      { order: 5, dbc: expect.closeTo(-27.96, 2) },
      { order: 7, dbc: expect.closeTo(-33.8, 2) },
    ])
  })

  it('has nothing to say about a stimulus that is not one of the two shapes', () => {
    expect(waveIdealHarmonicsDbc('Tones')).toEqual([])
  })
})

describe('isWaveKind', () => {
  it('is exactly the two hard-switched shapes', () => {
    expect(isWaveKind('Square')).toBe(true)
    expect(isWaveKind('Triangle')).toBe(true)
    for (const kind of ['Off', 'Tones', 'Multitone', 'WhiteNoise', 'EchoChirp'] as const) {
      expect(isWaveKind(kind)).toBe(false)
    }
  })
})
