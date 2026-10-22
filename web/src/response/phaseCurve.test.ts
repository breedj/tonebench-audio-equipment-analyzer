import { describe, expect, it } from 'vitest'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import {
  PHASE_AXIS_SPLITS,
  PHASE_FLOOR_BELOW_REFERENCE_DB,
  PHASE_SCALE_RANGE,
  delayDegPerHz,
  phaseFloorDb,
  phaseSeries,
  phaseSeriesUnwrapped,
  wrapPhaseDeg,
} from './phaseCurve'

/**
 * A trace whose magnitude falls away linearly from its reference, with a phase that reads its own
 * column index — so a blanked column is unmistakable in the output.
 */
function trace(overrides: Partial<ResponseTrace> = {}): ResponseTrace {
  const columns = 5
  const magnitudeMaxDb = [0, -20, -50, -70, -200]
  return {
    kind: 'Left',
    startFreqHz: 10,
    endFreqHz: 100,
    freqHz: [10, 20, 40, 80, 100],
    magnitudeMinDb: magnitudeMaxDb,
    magnitudeMaxDb,
    phaseDeg: Array.from({ length: columns }, (_, i) => i),
    coherence: Array.from({ length: columns }, () => 1),
    groupDelayS: null,
    phaseDelayS: null,
    oneDb: { lowHz: null, highHz: null, widthHz: null, lowClamped: false, highClamped: false },
    threeDb: { lowHz: null, highHz: null, widthHz: null, lowClamped: false, highClamped: false },
    peakDb: 0,
    peakFreqHz: 10,
    referenceDb: 0,
    referenceMode: 'BandMedian',
    referenceFellBackToPeak: false,
    ...overrides,
  } as ResponseTrace
}

const NO_RESTORE = { restoreDelay: false, latencySamples: null, sampleRateHz: null }

describe('the phase floor', () => {
  it('is measured down from the trace\'s bandwidth reference', () => {
    expect(phaseFloorDb(trace({ referenceDb: -12 }), 'PerTrace')).toBe(-12 + PHASE_FLOOR_BELOW_REFERENCE_DB)
  })

  it('falls back to the peak on a row written before the reference was recorded', () => {
    expect(phaseFloorDb(trace({ referenceDb: null, peakDb: -3 }), 'PerTrace')).toBe(-3 + PHASE_FLOOR_BELOW_REFERENCE_DB)
  })

  // A stepped sweep measures one tone at a time and removes no delay, so its phase is a reading at
  // every point it took; blanking it would delete a measurement rather than a wind-up.
  it('applies to a deconvolved run only', () => {
    expect(phaseFloorDb(trace(), 'ChannelDifference')).toBe(Number.NEGATIVE_INFINITY)
    expect(phaseFloorDb(trace(), 'DutResponse')).toBe(Number.NEGATIVE_INFINITY)
    expect(phaseFloorDb(null, 'PerTrace')).toBe(Number.NEGATIVE_INFINITY)
  })
})

describe('the phase series', () => {
  it('blanks the columns below the floor, as gaps rather than zeros', () => {
    // Reference 0 dB, so the floor is −60: the −70 and −200 columns go.
    expect(phaseSeries(trace(), 'PerTrace', NO_RESTORE, 5)).toEqual([0, 1, 2, null, null])
  })

  it('keeps every column of a stepped run', () => {
    expect(phaseSeries(trace(), 'ChannelDifference', NO_RESTORE, 5)).toEqual([0, 1, 2, 3, 4])
  })

  it('puts the removed delay back as a lag of 360·f·τ', () => {
    const restore = { restoreDelay: true, latencySamples: 48, sampleRateHz: 48_000 }
    // 360 × 48 / 48000 = 0.36° per Hz, so the 10 Hz column loses 3.6° and the 20 Hz one 7.2°.
    const curve = phaseSeries(trace(), 'PerTrace', restore, 5)
    expect(curve[0]).toBeCloseTo(0 - 3.6, 10)
    expect(curve[1]).toBeCloseTo(1 - 7.2, 10)
    expect(curve[3]).toBeNull()
  })

  it('pads a trace shorter than the shared grid, and answers all-null for no trace', () => {
    expect(phaseSeries(trace(), 'PerTrace', NO_RESTORE, 7)).toEqual([0, 1, 2, null, null, null, null])
    expect(phaseSeries(null, 'PerTrace', NO_RESTORE, 3)).toEqual([null, null, null])
  })

  it('drops a column whose phase is not a number', () => {
    expect(phaseSeries(trace({ phaseDeg: [0, NaN, 2, 3, 4] }), 'PerTrace', NO_RESTORE, 5)).toEqual([0, null, 2, null, null])
  })
})

describe('wrapping', () => {
  it('brings a phase into (−180°, 180°]', () => {
    expect(wrapPhaseDeg(181)).toBeCloseTo(-179, 10)
    expect(wrapPhaseDeg(-181)).toBeCloseTo(179, 10)
    expect(wrapPhaseDeg(540)).toBeCloseTo(180, 10)
    expect(wrapPhaseDeg(-540)).toBeCloseTo(180, 10)
    expect(wrapPhaseDeg(-3600.5)).toBeCloseTo(-0.5, 10)
  })

  // The boundary belongs to the top, as the server's WholeTurnsDeg has it — one convention, so a
  // trace anchored there and wrapped here cannot land on opposite edges of the plot.
  it('keeps ±180 at the top of the window and leaves an in-range phase alone', () => {
    expect(wrapPhaseDeg(180)).toBe(180)
    expect(wrapPhaseDeg(-180)).toBe(180)
    expect(wrapPhaseDeg(-179.999)).toBeCloseTo(-179.999, 10)
    expect(wrapPhaseDeg(0)).toBe(0)
  })

  it('is applied to the drawn curve, after the delay is restored', () => {
    // 360 × 48 / 48000 = 0.36°/Hz: the 20 Hz column's 1° becomes −6.2°, and a phase pushed past the
    // edge comes back on the other side rather than running off the axis.
    const restore = { restoreDelay: true, latencySamples: 48, sampleRateHz: 48_000 }
    const curve = phaseSeries(trace({ phaseDeg: [179, 1, 2, 3, 4] }), 'PerTrace', restore, 5)
    expect(curve[0]).toBeCloseTo(179 - 3.6, 10)
    expect(curve[1]).toBeCloseTo(1 - 7.2, 10)
    const wrapped = phaseSeries(trace({ phaseDeg: [200, 1, 2, 3, 4] }), 'PerTrace', NO_RESTORE, 5)
    expect(wrapped[0]).toBeCloseTo(-160, 10)
  })
})

describe('the fixed axis', () => {
  // The scale is twice the axis' height, which is what puts the phase in the bottom half of the
  // plot: +180° lands exactly at mid-height.
  it('spans twice what it labels, with the labels in the bottom half', () => {
    const [min, max] = PHASE_SCALE_RANGE
    expect(min).toBe(-180)
    expect(max - min).toBe(720)
    expect((180 - min) / (max - min)).toBe(0.5)
    expect([...PHASE_AXIS_SPLITS]).toEqual([-180, -90, 0, 90, 180])
    for (const split of PHASE_AXIS_SPLITS) expect(split).toBeGreaterThanOrEqual(min)
    for (const split of PHASE_AXIS_SPLITS) expect(split).toBeLessThanOrEqual(180)
  })

  it('cannot draw a sample in the empty half, which is why this chart blanks nothing', () => {
    // The impedance chart had to blank the samples outside its phase window (phaseInWindow in
    // impedance/phaseAxis.ts) -- there the window is the user's, so a curve routinely leaves it and
    // was being drawn in the half the axis does not label. Asked the same day whether this chart has
    // the same fault (Joost, 2026-10-22). It cannot: the window here is fixed, and the wrap is the
    // last thing applied to every drawn sample, so each one lands in the half that carries the
    // labels. This is that check, kept rather than reported -- clipping code here would be dead, and
    // the invariant it would stand in for is this one.
    const [min, max] = PHASE_SCALE_RANGE
    const labelledTop = min + (max - min) / 2

    for (const raw of [0, 1, 179.999, 180, 181, -180, -181, 540, -540, 1e4, -1e4, -311068]) {
      const drawn = wrapPhaseDeg(raw)
      expect(drawn).toBeGreaterThan(min)
      expect(drawn).toBeLessThanOrEqual(labelledTop)
    }

    // And through the path the chart actually draws, with a delay restored on top -- which is what
    // puts the turns there in the first place.
    const restore = { restoreDelay: true, latencySamples: 4800, sampleRateHz: 48_000 }
    const curve = phaseSeries(trace({ phaseDeg: [1e5, -1e5, 180, -180, 0] }), 'PerTrace', restore, 5)
    for (const value of curve) {
      if (value === null) continue
      expect(value).toBeGreaterThan(min)
      expect(value).toBeLessThanOrEqual(labelledTop)
    }
  })
})

describe('the restored delay', () => {
  it('is zero unless there is a delay and the checkbox asks for it', () => {
    expect(delayDegPerHz({ restoreDelay: false, latencySamples: 48, sampleRateHz: 48_000 })).toBe(0)
    expect(delayDegPerHz({ restoreDelay: true, latencySamples: null, sampleRateHz: 48_000 })).toBe(0)
    expect(delayDegPerHz({ restoreDelay: true, latencySamples: 48, sampleRateHz: 0 })).toBe(0)
    expect(delayDegPerHz({ restoreDelay: true, latencySamples: Number.NaN, sampleRateHz: 48_000 })).toBe(0)
  })

  it('is 360·τ/fs', () => {
    expect(delayDegPerHz({ restoreDelay: true, latencySamples: 48, sampleRateHz: 48_000 })).toBeCloseTo(0.36, 12)
  })
})

describe('the unwrapped series', () => {
  // What a stored curve is drawn from: it is resampled onto the live curve's grid, and
  // interpolating an already-wrapped phase across its own ±180° step would put a column in the
  // middle of the jump -- a phase the DUT never had. So the wrap is the last step, after the
  // interpolation, and this is the series that goes into it.
  it('leaves the turns on, where the wrapped one takes them off', () => {
    const subject = trace({ phaseDeg: [-200, -400, 0, 0, 0] })
    const raw = phaseSeriesUnwrapped(subject, 'PerTrace', NO_RESTORE, 5)
    const wrapped = phaseSeries(subject, 'PerTrace', NO_RESTORE, 5)

    expect(raw[0]).toBe(-200)
    expect(raw[1]).toBe(-400)
    expect(wrapped[0]).toBe(160)
    expect(wrapped[1]).toBe(-40)
  })

  it('blanks the same columns the wrapped one blanks', () => {
    const raw = phaseSeriesUnwrapped(trace(), 'PerTrace', NO_RESTORE, 5)
    const wrapped = phaseSeries(trace(), 'PerTrace', NO_RESTORE, 5)
    expect(raw.map((v) => v === null)).toEqual(wrapped.map((v) => v === null))
  })
})
