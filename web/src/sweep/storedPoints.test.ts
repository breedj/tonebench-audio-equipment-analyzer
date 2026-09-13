import { describe, expect, it } from 'vitest'
import { toSweepPoints } from '../composables/useMeasurements'
import type { StoredSweepPoint } from '../api/client'

/**
 * Reading a saved sweep back onto the chart. The metric names here are the ones
 * MeasurementSaveService.BuildSweepMetrics writes — if those two ever drift, a stored sweep plots
 * as a chart full of gaps rather than as an error, so this pins the names on the client side.
 */
function stored(freqHz: number, metrics: Record<string, number>, channel = 0): StoredSweepPoint {
  return { seqIndex: 0, freqHz, metricsByChannel: { [channel]: metrics }, previewsByChannel: null }
}

const full = {
  fundamental_hz: 1000,
  fundamental_dbv: -20,
  gain_db: -0.5,
  power_watt: 0.00125,
  thd_percent: 0.01,
  thd_n_percent: 0.02,
  snr_db: 90,
  h2_percent: 0.008,
  h3_percent: 0.004,
  h4_percent: 0.002,
  h5_percent: 0.001,
  d6plus_percent: 0.0005,
  noise_percent: 0.0001,
}

describe('toSweepPoints', () => {
  it('maps every metric the chart plots', () => {
    const [point] = toSweepPoints([stored(1000, full)])

    expect(point.freqHz).toBe(1000)
    expect(point.channel).toBe(0)
    expect(point.gainDb).toBe(-0.5) // Magnitude
    expect(point.thdPercent).toBe(0.01)
    expect(point.harmonicPercent).toEqual([0.008, 0.004, 0.002, 0.001]) // D2..D5
    expect(point.d6PlusPercent).toBe(0.0005)
    expect(point.noisePercent).toBe(0.0001)
    expect(point.fundamentalDbv).toBe(-20)
    expect(point.thdNPercent).toBe(0.02)
    expect(point.snrDb).toBe(90)
    expect(point.powerWatt).toBe(0.00125)
  })

  it('reads the frequency-normalized figures where the save wrote them, and null where it did not', () => {
    // A normalized run writes nthd/nh2..nh5/nd6plus only where the point had a reference; a
    // conventional run writes none. Absent must come back null (a gap), never 0 or NaN-as-a-value.
    const [conventional] = toSweepPoints([stored(1000, full)])
    expect(conventional.normalizedThdPercent).toBeNull()
    expect(conventional.normalizedHarmonicPercent).toEqual([null, null, null, null])
    expect(conventional.normalizedD6PlusPercent).toBeNull()

    const [normalized] = toSweepPoints([
      stored(1000, { ...full, nthd_percent: 0.02, nh2_percent: 0.016, nh3_percent: 0.008, nd6plus_percent: 0.001 }),
    ])
    expect(normalized.normalizedThdPercent).toBe(0.02)
    expect(normalized.normalizedHarmonicPercent).toEqual([0.016, 0.008, null, null])
    expect(normalized.normalizedD6PlusPercent).toBe(0.001)
    // The conventional figures ride along unchanged.
    expect(normalized.thdPercent).toBe(0.01)
  })

  it('splits a stereo capture into one point per channel', () => {
    const points = toSweepPoints([
      { seqIndex: 0, freqHz: 1000, metricsByChannel: { 0: full, 1: { ...full, gain_db: -1.5 } }, previewsByChannel: null },
    ])

    expect(points).toHaveLength(2)
    expect(points.map((p) => p.channel).sort()).toEqual([0, 1])
    expect(points.find((p) => p.channel === 1)?.gainDb).toBe(-1.5)
  })

  // A harmonic above Nyquist is not measured at that frequency, so the save has no row for it —
  // the list stops there rather than padding with zeros, which would plot as -infinity dBc.
  it('stops the harmonic list at the highest order actually stored', () => {
    const { h4_percent: _h4, h5_percent: _h5, ...upToD3 } = full
    const [point] = toSweepPoints([stored(19000, upToD3)])

    expect(point.harmonicPercent).toEqual([0.008, 0.004])
  })

  it('reads a metric the save never wrote as NaN, not 0', () => {
    const { noise_percent: _noise, ...withoutNoise } = full
    const [point] = toSweepPoints([stored(1000, withoutNoise)])

    expect(point.noisePercent).toBeNaN()
  })

  it('returns nothing for a result with no points', () => {
    expect(toSweepPoints([])).toEqual([])
  })

  it('carries each channel its own preview series', () => {
    const fft = { x: [100, 200], min: [-90, -80], max: [-85, -75] }
    const scope = { x: [0, 0.0005], min: [-1, -0.9], max: [1, 0.9] }
    const point: StoredSweepPoint = {
      seqIndex: 0,
      freqHz: 1000,
      metricsByChannel: { 0: full, 1: full },
      previewsByChannel: { 0: { fft, scope }, 1: { fft } },
    }

    const [ch0, ch1] = toSweepPoints([point]).sort((a, b) => a.channel - b.channel)

    expect(ch0.previewFft).toEqual(fft)
    expect(ch0.previewScope).toEqual(scope)

    // Channel 1 was only given an FFT preview above -- must read its own missing scope as null,
    // not fall back to channel 0's.
    expect(ch1.previewFft).toEqual(fft)
    expect(ch1.previewScope).toBeNull()
  })

  it('reads a stored point with no previewsByChannel at all as null previews on every channel', () => {
    const [point] = toSweepPoints([stored(1000, full)])

    expect(point.previewFft).toBeNull()
    expect(point.previewScope).toBeNull()
  })
})
