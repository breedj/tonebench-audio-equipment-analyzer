import { describe, expect, it } from 'vitest'
import {
  adaptiveFftSize,
  adaptiveSampleRateHz,
  estimateDurationSeconds,
  estimatePointCount,
  formatDuration,
  logPerOctave,
  mergePreviewSeries,
  nearestSweepPoint,
  percentToDb,
  sweepDistortionDb,
  sweepTraceLabel,
} from './points'

describe('logPerOctave', () => {
  it('returns stepsPerOctave*octaves + 1 points across a whole number of octaves', () => {
    // 20 -> 80 Hz is exactly 2 octaves, so 5 steps/octave gives 10 steps and 11 endpoints.
    const values = logPerOctave(20, 80, 5)
    expect(values).toHaveLength(11)
    expect(values[0]).toBeCloseTo(20, 9)
    expect(values[values.length - 1]).toBeCloseTo(80, 9)
  })

  it('is ascending with a constant ratio between neighbours', () => {
    const values = logPerOctave(20, 20000, 6)
    const firstRatio = values[1] / values[0]
    for (let i = 2; i < values.length - 1; i++) {
      expect(values[i] / values[i - 1]).toBeCloseTo(firstRatio, 9)
    }
  })

  it('still returns several points for a span narrower than one octave', () => {
    const values = logPerOctave(1000, 1200, 5)
    expect(values.length).toBeGreaterThanOrEqual(3)
    expect(values[values.length - 1]).toBeCloseTo(1200, 9)
  })

  it('returns nothing for a degenerate range', () => {
    expect(logPerOctave(0, 1000, 5)).toEqual([])
    expect(logPerOctave(1000, 100, 5)).toEqual([])
    expect(logPerOctave(20, 20000, 0)).toEqual([])
  })
})

describe('estimatePointCount', () => {
  it('drops points above 98% of Nyquist', () => {
    // At 48 kHz the ceiling is 23.52 kHz, so a sweep asked to run to 40 kHz is truncated.
    const truncated = estimatePointCount(1000, 40000, 3, 48000)
    const whole = logPerOctave(1000, 40000, 3).length
    expect(truncated).toBeLessThan(whole)
  })

  it('keeps every point of an audio-band sweep at 192 kHz', () => {
    expect(estimatePointCount(20, 20000, 3, 192000)).toBe(logPerOctave(20, 20000, 3).length)
  })
})

describe('estimateDurationSeconds', () => {
  it('scales with the point count', () => {
    const one = estimateDurationSeconds(1, 65536, 48000, 4)
    const ten = estimateDurationSeconds(10, 65536, 48000, 4)
    expect(ten).toBeCloseTo(one * 10, 9)
  })

  it('is zero for a sweep with no points', () => {
    expect(estimateDurationSeconds(0, 65536, 48000, 4)).toBe(0)
  })
})

describe('formatDuration', () => {
  it('formats seconds, whole minutes and mixed', () => {
    expect(formatDuration(45)).toBe('45 s')
    expect(formatDuration(120)).toBe('2 m')
    expect(formatDuration(150)).toBe('2 m 30 s')
    expect(formatDuration(0)).toBe('—')
  })
})

describe('percentToDb', () => {
  it('maps 100% to 0 dB and 1% to -40 dB', () => {
    expect(percentToDb(100)).toBeCloseTo(0, 9)
    expect(percentToDb(1)).toBeCloseTo(-40, 9)
    expect(percentToDb(0.1)).toBeCloseTo(-60, 9)
  })

  it('stays finite at zero rather than returning -Infinity', () => {
    expect(Number.isFinite(percentToDb(0))).toBe(true)
  })
})

describe('sweepDistortionDb', () => {
  // The bench reading this was written from: a fundamental at +9 dBV with H2 at -51.08 dBV, out of
  // a DUT whose gain there is 14.53 dB. The harmonic is 60.08 dB under the fundamental, and the dB
  // view puts it at -45.55 — which is the same harmonic read against the -5.53 dBV the generator
  // was actually putting in. That identity is the whole point of the compensation.
  it('refers a harmonic to the generator rather than to the DUT output', () => {
    const h2Dbc = -51.08 - 9
    const gainDb = 14.53
    expect(sweepDistortionDb(100 * Math.pow(10, h2Dbc / 20), gainDb)).toBeCloseTo(-45.55, 6)
    expect(sweepDistortionDb(100 * Math.pow(10, h2Dbc / 20), gainDb)).toBeCloseTo(-51.08 - (9 - gainDb), 6)
  })

  it('is plain dBc through a unity-gain DUT', () => {
    expect(sweepDistortionDb(1, 0)).toBeCloseTo(-40, 9)
    expect(sweepDistortionDb(0.1, 0)).toBeCloseTo(-60, 9)
  })

  it('moves every figure by the same gain, whatever its size', () => {
    for (const percent of [100, 1, 0.1, 0.001]) {
      expect(sweepDistortionDb(percent, 20) - percentToDb(percent)).toBeCloseTo(20, 9)
      expect(sweepDistortionDb(percent, -6) - percentToDb(percent)).toBeCloseTo(-6, 9)
    }
  })

  // A sweep stored before gain_db was written reads NaN there (useMeasurements.toSweepPoints), and
  // its Magn trace is already absent. A gap says so; silently plotting the uncompensated dBc would
  // put that trace a whole DUT gain away from every other run on the chart.
  it('is NaN where the point carries no gain', () => {
    expect(sweepDistortionDb(0.1, Number.NaN)).toBeNaN()
    expect(sweepDistortionDb(0.1, Number.POSITIVE_INFINITY)).toBeNaN()
  })
})

describe('nearestSweepPoint', () => {
  const points = [
    { channel: 0, freqHz: 100 },
    { channel: 0, freqHz: 1000 },
    { channel: 0, freqHz: 10000 },
    { channel: 1, freqHz: 500 },
  ]

  it('finds the closest point by frequency on the requested channel', () => {
    expect(nearestSweepPoint(points, 0, 1200)?.freqHz).toBe(1000)
    expect(nearestSweepPoint(points, 0, 50)?.freqHz).toBe(100)
    expect(nearestSweepPoint(points, 0, 50000)?.freqHz).toBe(10000)
  })

  it('only considers the requested channel, even when another channel is closer', () => {
    // 500 Hz is exactly channel 1's point, but only channel 0 was asked for.
    expect(nearestSweepPoint(points, 0, 500)?.freqHz).toBe(100)
  })

  it('returns null for a channel with no points at all', () => {
    expect(nearestSweepPoint(points, 1, 1000)?.freqHz).toBe(500)
    expect(nearestSweepPoint([], 0, 1000)).toBeNull()
  })
})

describe('mergePreviewSeries', () => {
  it('reads both channels straight through when their x grids are identical', () => {
    // The common FFT-preview case: both channels decimated with the same bin count/DisplayLoFreqHz.
    const left = { x: [100, 200, 300], min: [-90, -80, -70], max: [-85, -75, -65] }
    const right = { x: [100, 200, 300], min: [-91, -81, -71], max: [-86, -76, -66] }

    const merged = mergePreviewSeries(left, right)

    expect(merged.x).toEqual([100, 200, 300])
    expect(merged.leftMin).toEqual([-90, -80, -70])
    expect(merged.rightMax).toEqual([-86, -76, -66])
  })

  it('unions mismatched x grids and snaps each channel to its own nearest point', () => {
    // The scope-preview case: each channel triggers off its own zero crossing, so the two windows
    // can differ in both length and alignment. Every union x still gets a value from each channel
    // that has any data at all -- "nearest" never comes up empty, unlike a channel that's absent
    // entirely (see the null test below).
    const left = { x: [0, 0.001], min: [-1, -0.5], max: [1, 0.5] }
    const right = { x: [0.0005, 0.0015], min: [-2, -1], max: [2, 1] }

    const merged = mergePreviewSeries(left, right)

    expect(merged.x).toEqual([0, 0.0005, 0.001, 0.0015])
    // On an exact tie (0.0005 is equidistant from both of left's points), the earlier index wins.
    expect(merged.leftMax).toEqual([1, 1, 0.5, 0.5])
    expect(merged.rightMax).toEqual([2, 2, 2, 1])
  })

  it('reads an absent channel back as null at every x, not zero', () => {
    const left = { x: [100, 200], min: [-90, -80], max: [-85, -75] }

    const merged = mergePreviewSeries(left, null)

    expect(merged.x).toEqual([100, 200])
    expect(merged.rightMin).toEqual([null, null])
    expect(merged.rightMax).toEqual([null, null])
  })

  it('returns an empty series when neither channel has data', () => {
    expect(mergePreviewSeries(null, null)).toEqual({ x: [], leftMin: [], leftMax: [], rightMin: [], rightMax: [] })
  })
})

describe('adaptiveSampleRateHz', () => {
  it('leaves room for five harmonics of the top point by default', () => {
    // A THD sweep measures D2..D5, so the rate has to carry 5x the highest tone. Mirrors
    // SweepResolution.HarmonicHeadroomOrder.
    expect(adaptiveSampleRateHz(20000)).toBe(192000)
    expect(adaptiveSampleRateHz(4000)).toBe(48000)
  })

  it('needs only the tone itself when asked for one harmonic', () => {
    // What a stepped-sine response asks for: it reads no harmonics, so a 20 kHz run fits in 48 kHz
    // rather than 192 kHz -- a quarter of the USB and FFT load. Mirrors
    // SteppedResponseRequest.EffectiveSampleRateHz, and the two must agree or the panel's duration
    // estimate and analysis readout describe a run the server does not perform.
    expect(adaptiveSampleRateHz(20000, 1)).toBe(48000)
    expect(adaptiveSampleRateHz(40000, 1)).toBe(96000)
    expect(adaptiveSampleRateHz(90000, 1)).toBe(192000)
  })

  it('falls back to the highest rate when nothing fits', () => {
    expect(adaptiveSampleRateHz(200000, 1)).toBe(192000)
  })
})

describe('adaptiveFftSize', () => {
  it('clamps to the record-time ceiling the sweeps use, by default', () => {
    // 0.6827 s at every rate, so 1.465 Hz bins at every rate.
    expect(adaptiveFftSize(1, 48000)).toBe(32768)
    expect(adaptiveFftSize(1, 96000)).toBe(65536)
    expect(adaptiveFftSize(1, 192000)).toBe(131072)
  })

  it('reaches the raised ceiling only where the ladder asks for it', () => {
    // The stepped-sine response passes a raised ceiling when its start frequency is below what the
    // standard one can measure. Mirrors SteppedResponseRequest.FftSizeAt — a point the standard
    // ceiling already serves must be sized identically either way, or the low end of a sweep would
    // drag the whole run's record length up with it.
    expect(adaptiveFftSize(2, 48000, 131072)).toBe(131072)
    expect(adaptiveFftSize(1000, 48000, 131072)).toBe(adaptiveFftSize(1000, 48000))
    expect(adaptiveFftSize(1000, 48000, 131072)).toBe(4096)
  })
})

describe('estimateDurationSeconds', () => {
  it('counts the extra record a stepped-sine point spends', () => {
    // The record armed by each stimulus change is thrown away, so a stepped point is two records
    // plus its averages where a sweep point is one. Under-counting it would promise half the time.
    const sweep = estimateDurationSeconds(10, 32768, 48000, 1)
    const stepped = estimateDurationSeconds(10, 32768, 48000, 1, 0.5, 0.065, 2)

    const recordSeconds = 32768 / 48000
    expect(stepped - sweep).toBeCloseTo(10 * recordSeconds, 6)
  })
})

describe('sweepTraceLabel', () => {
  it('names the THD family by harmonic, with an n prefix under the normalized method', () => {
    expect(sweepTraceLabel('thd', 'Conventional')).toBe('THD')
    expect(sweepTraceLabel('d2', 'Conventional')).toBe('D2')
    expect(sweepTraceLabel('d6plus', 'FrequencyNormalized')).toBe('nD6+')
    expect(sweepTraceLabel('magnitude', 'FrequencyNormalized')).toBe('Magn')
  })

  it('names the IMD family by order, and never with an n prefix', () => {
    expect(sweepTraceLabel('thd', 'Conventional', 'imd')).toBe('IMD')
    expect(sweepTraceLabel('d2', 'Conventional', 'imd')).toBe('IM2')
    expect(sweepTraceLabel('d5', 'Conventional', 'imd')).toBe('IM5')
    expect(sweepTraceLabel('d6plus', 'Conventional', 'imd')).toBe('IM6+')
    expect(sweepTraceLabel('d3', 'FrequencyNormalized', 'imd')).toBe('IM3')
    expect(sweepTraceLabel('noise', 'Conventional', 'imd')).toBe('Noise')
    expect(sweepTraceLabel('magnitude', 'Conventional', 'imd')).toBe('Magn')
  })
})
