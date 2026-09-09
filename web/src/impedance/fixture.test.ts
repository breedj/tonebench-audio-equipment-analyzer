import { describe, expect, it } from 'vitest'
import type { ImpedanceCalibrationJson, ImpedanceFixtureConfig } from '../api/client'
import { matchesStoredFixture, sameCalibration } from './fixture'

function cal(freqHz: number[], real: number[], imag: number[]): ImpedanceCalibrationJson {
  return { freqHz, real, imag }
}

const OPEN = cal([20, 1000, 20000], [1.0, 0.999, 0.99], [0, 0.001, 0.01])
const SHORT = cal([20, 1000, 20000], [0.0012, 0.0013, 0.004], [0, 0.0002, 0.003])

function fixture(over: Partial<ImpedanceFixtureConfig> = {}): ImpedanceFixtureConfig {
  return {
    referenceOhms: 99.73,
    calibrationLevelValue: 0,
    levelUnit: 'Dbv',
    openCal: OPEN,
    shortCal: SHORT,
    capturedLowHz: 12.5,
    capturedHighHz: 91200,
    capturedUtc: '2026-09-09T17:42:00.500Z',
    hasCalibration: true,
    ...over,
  }
}

describe('sameCalibration', () => {
  it('matches a curve against a value-identical copy', () => {
    // The case that matters: a curve parsed out of params_json is a different object every time.
    const copy = cal([...OPEN.freqHz], [...OPEN.real], [...OPEN.imag])
    expect(sameCalibration(OPEN, copy)).toBe(true)
  })

  it('is true for two nulls and false for one', () => {
    expect(sameCalibration(null, null)).toBe(true)
    expect(sameCalibration(OPEN, null)).toBe(false)
    expect(sameCalibration(null, OPEN)).toBe(false)
  })

  it('notices a single differing point anywhere in the curve', () => {
    const last = cal([...OPEN.freqHz], [...OPEN.real], [...OPEN.imag])
    last.imag[2] = 0.0100001
    expect(sameCalibration(OPEN, last)).toBe(false)

    const middle = cal([...OPEN.freqHz], [...OPEN.real], [...OPEN.imag])
    middle.real[1] = 0.998
    expect(sameCalibration(OPEN, middle)).toBe(false)
  })

  it('notices a different frequency grid even when the values agree', () => {
    expect(sameCalibration(OPEN, cal([20, 1001, 20000], [...OPEN.real], [...OPEN.imag]))).toBe(false)
  })

  it('is false for different lengths', () => {
    expect(sameCalibration(OPEN, cal([20, 1000], [1.0, 0.999], [0, 0.001]))).toBe(false)
  })

  it('is false for a ragged curve rather than reading off the end of an array', () => {
    expect(sameCalibration(OPEN, cal([20, 1000, 20000], [1.0, 0.999], [0, 0.001, 0.01]))).toBe(false)
  })
})

describe('matchesStoredFixture', () => {
  const local = { referenceOhms: 99.73, openCal: OPEN, shortCal: SHORT }

  it('is true when the stored fixture already holds exactly this calibration', () => {
    expect(matchesStoredFixture(local, fixture())).toBe(true)
  })

  it('is false when nothing is stored', () => {
    expect(matchesStoredFixture(local, null)).toBe(false)
    expect(matchesStoredFixture(local, fixture({ hasCalibration: false }))).toBe(false)
  })

  /**
   * The reference resistor is part of the identity, not an afterthought: the standards are only
   * valid at the resistance they were captured through, so the same curves against a different
   * resistor are a different fixture and do need storing.
   */
  it('is false when only the reference resistor differs', () => {
    expect(matchesStoredFixture({ ...local, referenceOhms: 100 }, fixture())).toBe(false)
  })

  it('is false after a fresh open capture', () => {
    const recaptured = cal([...OPEN.freqHz], [...OPEN.real], [...OPEN.imag])
    recaptured.real[0] = 0.9999
    expect(matchesStoredFixture({ ...local, openCal: recaptured }, fixture())).toBe(false)
  })

  it('is false when the short is added locally but not stored', () => {
    expect(matchesStoredFixture(local, fixture({ shortCal: null }))).toBe(false)
  })

  it('is true for an open-only fixture matched by an open-only measurement', () => {
    expect(
      matchesStoredFixture({ ...local, shortCal: null }, fixture({ shortCal: null })),
    ).toBe(true)
  })
})
