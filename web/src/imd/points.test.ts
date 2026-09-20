import { describe, expect, it } from 'vitest'
import type { StoredSweepPoint } from '../api/client'
import type { ImdSweepPointFrame } from '../streaming/protocol'
import { imdFrameToDistortionPoint, imdStatusPointToDistortionPoint, toImdSweepPoints } from './points'

const frame: ImdSweepPointFrame = {
  header: { msgType: 11, channel: 0, flags: 0, seq: 3, x0: 58.59, dx: 7001.95, nPoints: 5 },
  channel: 0,
  pointIndex: 3,
  toneLowHz: 58.59,
  toneHighHz: 7001.95,
  generatorSineEquivalentDbv: -20,
  toneLowDbv: -21.94,
  toneHighDbv: -33.98,
  sineEquivalentDbv: -19.9,
  combinedRmsDbv: -22.2,
  gainDb: 0.1,
  powerWatt: 0.00128,
  imdPercent: 0.05,
  noisePercent: 0.001,
  orderPercent: [0.03, 0.02, 0.01, 0.005, 0.003, 0.004],
  previewFft: { x: [100], min: [-90], max: [-80] },
  previewScope: null,
}

describe('imdFrameToDistortionPoint', () => {
  it('maps the IMD figures onto the distortion slots', () => {
    const p = imdFrameToDistortionPoint(frame)
    expect(p.thdPercent).toBe(0.05)
    expect(p.harmonicPercent).toEqual([0.03, 0.02, 0.01, 0.005])
    expect(p.fundamentalDbv).toBe(-19.9)
    expect(p.generatorDbv).toBe(-20)
    expect(p.freqHz).toBe(58.59)
    expect(p.gainDb).toBe(0.1)
    expect(p.powerWatt).toBe(0.00128)
    expect(p.noisePercent).toBe(0.001)
    expect(Number.isNaN(p.thdNPercent)).toBe(true)
    expect(Number.isNaN(p.snrDb)).toBe(true)
    expect(p.normalizedThdPercent).toBeNull()
    expect(p.previewFft).toEqual(frame.previewFft)
  })

  it('power-sums orders six and up into the 6+ slot', () => {
    const p = imdFrameToDistortionPoint(frame)
    expect(p.d6PlusPercent).toBeCloseTo(Math.sqrt(0.003 ** 2 + 0.004 ** 2), 9)
    expect(imdFrameToDistortionPoint({ ...frame, orderPercent: [0.03] }).d6PlusPercent).toBe(0)
  })
})

describe('imdStatusPointToDistortionPoint', () => {
  it('reads the status endpoint\'s point the same way', () => {
    const p = imdStatusPointToDistortionPoint({
      channel: 1,
      generatorSineEquivalentDbv: -10,
      toneLowDbv: -11.94,
      toneHighDbv: -23.98,
      sineEquivalentDbv: -9.8,
      combinedRmsDbv: -12.1,
      gainDb: 0.2,
      powerWatt: 0.013,
      imdPercent: 0.2,
      orderPercent: [0.15, 0.1],
      noisePercent: 0.002,
      toneLowHz: 250,
      toneHighHz: 8000,
      previewFft: null,
      previewScope: null,
    })
    expect(p.channel).toBe(1)
    expect(p.thdPercent).toBe(0.2)
    expect(p.harmonicPercent).toEqual([0.15, 0.1])
    expect(p.d6PlusPercent).toBe(0)
    expect(p.freqHz).toBe(250)
  })
})

describe('toImdSweepPoints', () => {
  const stored: StoredSweepPoint[] = [
    {
      seqIndex: 0,
      freqHz: 58.59,
      metricsByChannel: {
        0: {
          fundamental_hz: 58.59,
          fundamental_dbv: -19.9,
          gain_db: 0.1,
          power_watt: 0.00128,
          noise_percent: 0.001,
          generator_dbv: -20,
          generator_volt: 0.1,
          imd_percent: 0.05,
          im2_percent: 0.03,
          im3_percent: 0.02,
          im5_percent: 0.999,
        },
      },
      previewsByChannel: { 0: { fft: { x: [100], min: [-90], max: [-80] } } },
    },
  ]

  it('maps the stored rows onto the distortion slots', () => {
    const [p] = toImdSweepPoints(stored)
    expect(p!.thdPercent).toBe(0.05)
    expect(p!.fundamentalDbv).toBe(-19.9)
    expect(p!.generatorDbv).toBe(-20)
    expect(p!.previewFft).toEqual({ x: [100], min: [-90], max: [-80] })
    expect(p!.previewScope).toBeNull()
  })

  it('stops at the first missing order', () => {
    const [p] = toImdSweepPoints(stored)
    // im4 is absent, so im5 is never reached: the orders are contiguous or they are over.
    expect(p!.harmonicPercent).toEqual([0.03, 0.02])
    expect(p!.d6PlusPercent).toBe(0)
  })
})
