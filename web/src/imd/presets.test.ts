import { describe, expect, it } from 'vitest'
import { IMD_PRESETS, imdPreset, imdSpecFromStimulus, presetTones, sineEquivalentDbv, toneLevelsDbv } from './presets'

describe('toneLevelsDbv', () => {
  it('splits a 4:1 pair to -1.94 and -13.98 dB below the sine-equivalent level', () => {
    const { lowDbv, highDbv } = toneLevelsDbv(0, 4)
    expect(lowDbv).toBeCloseTo(-1.94, 2)
    expect(highDbv).toBeCloseTo(-13.98, 2)
  })

  it('splits a 1:1 pair to -6.02 dB each', () => {
    const { lowDbv, highDbv } = toneLevelsDbv(24, 1)
    expect(lowDbv).toBeCloseTo(17.98, 2)
    expect(highDbv).toBeCloseTo(17.98, 2)
  })
})

describe('presetTones', () => {
  it('smpte splits the sine-equivalent level 4:1 across 60 Hz and 7 kHz', () => {
    expect(presetTones(imdPreset('Smpte'), -6)).toEqual([
      { freqHz: 60, ampDbv: -7.94 },
      { freqHz: 7000, ampDbv: -19.98 },
    ])
  })

  it('ccif splits it equally across 19 and 20 kHz', () => {
    expect(presetTones(imdPreset('CcifItu'), 0)).toEqual([
      { freqHz: 19000, ampDbv: -6.02 },
      { freqHz: 20000, ampDbv: -6.02 },
    ])
  })

  it('carries the tutorial defaults for every preset', () => {
    expect(IMD_PRESETS.map((p) => [p.value, p.lowHz, p.highHz, p.ratio, p.method, p.defaultMaxOrder])).toEqual([
      ['Custom', 0, 0, 1, 'Smpte', 5],
      ['Smpte', 60, 7000, 4, 'Smpte', 5],
      ['Din', 250, 8000, 4, 'Smpte', 5],
      ['CcifItu', 19000, 20000, 1, 'Ccif', 3],
      ['Ccif18p5', 18500, 19500, 1, 'Ccif', 3],
    ])
  })
})

describe('sineEquivalentDbv', () => {
  it('round-trips preset tones', () => {
    for (const preset of IMD_PRESETS.filter((p) => p.value !== 'Custom')) {
      expect(sineEquivalentDbv(presetTones(preset, -12))).toBeCloseTo(-12, 1)
    }
  })

  it('is null for anything but two tones', () => {
    expect(sineEquivalentDbv([{ freqHz: 1000, ampDbv: 0 }])).toBeNull()
    expect(sineEquivalentDbv([])).toBeNull()
  })
})

describe('imdSpecFromStimulus', () => {
  const analysis = { method: 'Smpte' as const, maxOrder: 5 }

  it('needs exactly two sine tones', () => {
    expect(imdSpecFromStimulus('Tones', [{ freqHz: 1000, ampDbv: -6 }], analysis)).toEqual({ enabled: false })
    expect(
      imdSpecFromStimulus(
        'Multitone',
        [
          { freqHz: 60, ampDbv: -6 },
          { freqHz: 7000, ampDbv: -18 },
        ],
        analysis,
      ),
    ).toEqual({ enabled: false })
    expect(
      imdSpecFromStimulus(
        'Tones',
        [
          { freqHz: 60, ampDbv: -6 },
          { freqHz: 7000, ampDbv: -18 },
          { freqHz: 1000, ampDbv: -6 },
        ],
        analysis,
      ),
    ).toEqual({ enabled: false })
  })

  it('orders the tones low first whichever way they were typed', () => {
    expect(
      imdSpecFromStimulus(
        'Tones',
        [
          { freqHz: 20000, ampDbv: -6 },
          { freqHz: 19000, ampDbv: -6 },
        ],
        { method: 'Ccif', maxOrder: 3 },
      ),
    ).toEqual({
      enabled: true,
      toneLowHz: 19000,
      toneHighHz: 20000,
      method: 'Ccif',
      maxOrder: 3,
    })
  })

  it('refuses a degenerate pair', () => {
    expect(
      imdSpecFromStimulus(
        'Tones',
        [
          { freqHz: 1000, ampDbv: -6 },
          { freqHz: 1000, ampDbv: -6 },
        ],
        analysis,
      ),
    ).toEqual({ enabled: false })
  })
})
