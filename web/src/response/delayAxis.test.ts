import { describe, expect, it } from 'vitest'
import { chooseDelayUnit, delayFitStepUs, delayLabel, delayValuesOf, isDelayView, unitToUs, usToUnit } from './delayAxis'
import { makeTrace } from './testTrace'

describe('chooseDelayUnit', () => {
  it('reads in microseconds until either edge reaches a millisecond', () => {
    expect(chooseDelayUnit(500, -500)).toBe('µs')
    expect(chooseDelayUnit(999, 0)).toBe('µs')
    expect(chooseDelayUnit(1000, 0)).toBe('ms')
    expect(chooseDelayUnit(20, -2500)).toBe('ms')
  })

  it('converts a microsecond window into the unit and back without drift', () => {
    expect(usToUnit(2500, 'ms')).toBe(2.5)
    expect(usToUnit(2500, 'µs')).toBe(2500)
    expect(unitToUs(2.5, 'ms')).toBe(2500)
    expect(unitToUs(usToUnit(1234, 'ms'), 'ms')).toBe(1234)
  })
})

describe('delayFitStepUs', () => {
  it('picks the largest 1-2-5 step that still gives at least four divisions', () => {
    expect(delayFitStepUs(100)).toBe(20)
    expect(delayFitStepUs(19)).toBe(2)
    expect(delayFitStepUs(4)).toBe(1)
    expect(delayFitStepUs(12000)).toBe(2000)
  })

  it('goes below a microsecond, so a tight window in milliseconds still gets ticks', () => {
    // A 12 ms DSP delay fitted to its 30 µs of ripple is a 0.04 ms tall axis: with 0.1 ms as the
    // finest tick it drew one label.
    expect(delayFitStepUs(0.04)).toBe(0.01)
  })

  it('never returns a zero step for a flat or empty fit', () => {
    expect(delayFitStepUs(0)).toBe(1)
    expect(delayFitStepUs(Number.NaN)).toBe(1)
  })
})

describe('delayValuesOf', () => {
  const trace = makeTrace({ freqHz: [100, 1000], magnitudeMaxDb: [0, 0], phaseDeg: [0, 0], groupDelayS: [1e-6, 2e-6], phaseDelayS: [3e-6, 4e-6] })

  it('selects the curve the view names and nothing for None', () => {
    expect(delayValuesOf(trace, 'Group')).toEqual([1e-6, 2e-6])
    expect(delayValuesOf(trace, 'Phase')).toEqual([3e-6, 4e-6])
    expect(delayValuesOf(trace, 'None')).toBeNull()
    expect(delayValuesOf(null, 'Group')).toBeNull()
  })

  it('is null for a stored result that never recorded the curve', () => {
    const older = makeTrace({ freqHz: [100, 1000], magnitudeMaxDb: [0, 0], phaseDeg: [0, 0] })
    expect(delayValuesOf(older, 'Group')).toBeNull()
  })
})

describe('labels and guards', () => {
  it('captions the difference case, like the phase toggle does', () => {
    expect(delayLabel('Group')).toBe('Group delay')
    expect(delayLabel('Phase', true)).toBe('Phase delay difference')
  })

  it('accepts only the three views, so an older params_json reads as None', () => {
    expect(isDelayView('Group')).toBe(true)
    expect(isDelayView('group')).toBe(false)
    expect(isDelayView(null)).toBe(false)
  })
})
