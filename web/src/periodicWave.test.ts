import { describe, expect, it } from 'vitest'
import {
  isWaveKind,
  pulseEffectiveDutyPercent,
  pulseWidthFrames,
  waveDcVolts,
  waveEffectiveFreqHz,
  waveHarmonicsBelowNyquist,
  waveHasEvenHarmonics,
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
  it('counts the odd harmonics that fit, for the shapes that hold only those', () => {
    expect(waveHarmonicsBelowNyquist('Square', 1000, 48_000)).toBe(12)
    expect(waveHarmonicsBelowNyquist('Square', 750, 48_000)).toBe(16)
    expect(waveHarmonicsBelowNyquist('Triangle', 1000, 192_000)).toBe(48)
    // At the ceiling there is only the fundamental -- a four-sample square is a sine.
    expect(waveHarmonicsBelowNyquist('Square', 40_000, 48_000)).toBe(1)
  })

  it('counts every harmonic for the shapes that hold every one', () => {
    // 48 frames a cycle: harmonics 1..23 fit below Nyquist, of which 12 are odd.
    expect(waveHarmonicsBelowNyquist('Sawtooth', 1000, 48_000)).toBe(23)
    expect(waveHarmonicsBelowNyquist('Pulse', 1000, 48_000)).toBe(23)
  })
})

describe('wavePeakVolts', () => {
  it('reads each shape own crest factor, not a sine', () => {
    // 0 dBV = 1 Vrms: a square peaks at 1 V, a triangle at sqrt(3).
    expect(wavePeakVolts('Square', 0)).toBeCloseTo(1, 12)
    expect(wavePeakVolts('Triangle', 0)).toBeCloseTo(Math.sqrt(3), 12)
    expect(wavePeakVolts('Sawtooth', 0)).toBeCloseTo(Math.sqrt(3), 12)
    expect(wavePeakVolts('Tones', 0)).toBe(0)
  })

  /**
   * Every sample of a pulse is +-peak whatever its width, so narrowing it changes its spectrum and
   * its DC and leaves its loudness, its peak and the output range it needs exactly where they were.
   * That is the property that makes the duty control safe to turn — a unipolar pulse's rms would
   * fall as the square root of the duty, driving the DUT 20 dB harder at 1 %.
   */
  it('does not move a pulse peak with its duty', () => {
    expect(wavePeakVolts('Pulse', 0)).toBeCloseTo(1, 12)
    expect(wavePeakVolts('Pulse', -6)).toBeCloseTo(wavePeakVolts('Square', -6), 12)
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

  it('has nothing to say about a stimulus that is not one of the shapes', () => {
    expect(waveIdealHarmonicsDbc('Tones')).toEqual([])
  })

  /**
   * A sawtooth falls as 1/n like a square and holds the even orders too, so the orders it is
   * quoted at are different — a table of "H2: nothing" is never printed for a shape that has no
   * even harmonics, and never omitted for one that does.
   */
  it('quotes a sawtooth at the orders it actually has', () => {
    expect(waveIdealHarmonicsDbc('Sawtooth')).toEqual([
      { order: 2, dbc: expect.closeTo(-6.02, 2) },
      { order: 3, dbc: expect.closeTo(-9.54, 2) },
      { order: 4, dbc: expect.closeTo(-12.04, 2) },
    ])
  })

  /**
   * And a pulse's ladder moves with its width — its signature is the nulls at every 1/d-th
   * harmonic, which is what a reader looks for to confirm the duty is what they set.
   */
  it('quotes the orders a pulse of that width actually has', () => {
    // At 50 % a pulse IS a square, so its ladder is the square's -- the odd orders, the even ones
    // being the nulls. A row reading "-324 dBc" there would be floating-point noise presented as a
    // measurement, which is why the nulls are skipped rather than printed.
    const square = waveIdealHarmonicsDbc('Square')
    const half = waveIdealHarmonicsDbc('Pulse', 50)
    expect(half.map((h) => h.order)).toEqual(square.map((h) => h.order))
    half.forEach((h, i) => expect(h.dbc).toBeCloseTo(square[i].dbc, 9))

    // A quarter duty nulls every fourth harmonic instead, so H2 appears and H4 does not.
    expect(waveIdealHarmonicsDbc('Pulse', 25)).toEqual([
      { order: 2, dbc: expect.closeTo(-3.01, 2) },
      { order: 3, dbc: expect.closeTo(-9.54, 2) },
      { order: 5, dbc: expect.closeTo(-13.98, 2) },
    ])
  })
})

describe('waveHasEvenHarmonics', () => {
  /**
   * A square and a triangle are half-wave symmetric — the second half of a period is the negative
   * of the first — which is exactly the condition that kills every even harmonic. A sawtooth is
   * not, and a pulse is only at 50 %. It is the whole reason the sawtooth is worth having.
   */
  it('is the shapes that are not half-wave symmetric', () => {
    expect(waveHasEvenHarmonics('Sawtooth')).toBe(true)
    expect(waveHasEvenHarmonics('Pulse')).toBe(true)
    expect(waveHasEvenHarmonics('Square')).toBe(false)
    expect(waveHasEvenHarmonics('Triangle')).toBe(false)
  })
})

describe('pulseWidthFrames', () => {
  /**
   * A whole number of samples for the same reason the period is one, and never 0 or the whole
   * period: a pulse that is never high and one that is always high are both silence wearing a
   * frequency, and the control that produced them would read as broken rather than as refused.
   */
  it('is a whole number of samples and never none or all of them', () => {
    expect(pulseWidthFrames(50, 64)).toBe(32)
    expect(pulseWidthFrames(25, 64)).toBe(16)
    expect(pulseWidthFrames(1, 64)).toBe(1)
    expect(pulseWidthFrames(0.5, 64)).toBe(1)
    expect(pulseWidthFrames(200, 64)).toBe(63)
    expect(pulseWidthFrames(Number.NaN, 64)).toBe(32)
  })

  /**
   * A duty of zero is not a width anybody typed — the endpoint refuses anything outside 1..99 — so
   * it means "not stated", which is what a measurement saved before the field existed reads back
   * as. See the C# copy's ResolveDutyPercent for why an absent property comes back as 0 rather than
   * as the record's own 50.
   */
  it('reads a duty of zero as not stated rather than as the narrowest pulse there is', () => {
    expect(pulseWidthFrames(0, 64)).toBe(32)
    expect(pulseWidthFrames(-5, 64)).toBe(32)
    expect(pulseEffectiveDutyPercent(0, 64)).toBeCloseTo(50, 9)
  })

  it('reports the duty really played, which at a short period is coarse', () => {
    expect(pulseEffectiveDutyPercent(50, 64)).toBeCloseTo(50, 9)
    // Four frames a cycle: 30 % rounds to one frame, i.e. 25 %.
    expect(pulseEffectiveDutyPercent(30, 4)).toBeCloseTo(25, 9)
  })
})

describe('waveDcVolts', () => {
  /**
   * A bipolar pulse of duty d sits at peak(2d - 1) on average, which at a 10 % duty is -0.8 of
   * peak. Shown rather than removed: both analyzer inputs block it, so it never reaches a reading
   * — it reaches the DUT, which is the point of saying it out loud.
   */
  it('is the pulse own offset and nothing else has one', () => {
    expect(waveDcVolts('Pulse', 0, 50, 64)).toBeCloseTo(0, 12)
    expect(waveDcVolts('Pulse', 0, 25, 64)).toBeCloseTo(-0.5, 12)
    expect(waveDcVolts('Pulse', 0, 75, 64)).toBeCloseTo(0.5, 12)

    for (const kind of ['Square', 'Triangle', 'Sawtooth'] as const) {
      expect(waveDcVolts(kind, 0, 25, 64)).toBe(0)
    }
  })
})

describe('isWaveKind', () => {
  it('is exactly the hard-switched shapes', () => {
    for (const kind of ['Square', 'Triangle', 'Sawtooth', 'Pulse'] as const) {
      expect(isWaveKind(kind)).toBe(true)
    }
    for (const kind of ['Off', 'Tones', 'Multitone', 'WhiteNoise', 'EchoChirp', 'ToneBurst', 'DcLevel'] as const) {
      expect(isWaveKind(kind)).toBe(false)
    }
  })
})
