import { describe, expect, it } from 'vitest'
import { resample } from './resample'

describe('resample', () => {
  it('resample_IdenticalGrid_IsIdentity', () => {
    const freqHz = [10, 100, 1000, 10000]
    const value = [-140, -120, -100, -80]

    const result = resample({ freqHz, value }, freqHz)

    result.forEach((v, i) => expect(v).toBeCloseTo(value[i], 9))
  })

  it('resample_InterpolatesInLogFrequency', () => {
    // Two points a decade apart; the midpoint in LOG space (sqrt(10*100) ~= 31.62 Hz) must land
    // exactly halfway between the two values -- a linear-in-Hz interpolation would not (31.62 Hz
    // is barely 1/4 of the way from 10 to 100 on a linear scale).
    const freqHz = [10, 100]
    const value = [-140, -100]
    const midLogFreq = Math.sqrt(10 * 100)

    const result = resample({ freqHz, value }, [midLogFreq])

    expect(result[0]).toBeCloseTo(-120, 6)
  })

  it('resample_OutsideSourceRange_ClampsToEdgeValues', () => {
    const freqHz = [100, 1000]
    const value = [-90, -60]

    const result = resample({ freqHz, value }, [10, 10000])

    expect(result[0]).toBe(-90)
    expect(result[1]).toBe(-60)
  })

  it('resample_EmptySource_ReturnsZeros', () => {
    const result = resample({ freqHz: [], value: [] }, [10, 100])
    expect(Array.from(result)).toEqual([0, 0])
  })
})
