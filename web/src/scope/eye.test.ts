import { describe, expect, it } from 'vitest'
import type { ScopeEyeSlice } from '../api/client'
import {
  EYE_DIVISIONS,
  eyeAbsenceNote,
  eyeNote,
  eyeReadings,
  eyeTruncated,
  eyeTruncationNote,
  eyeVoltWindow,
  formatFrequency,
  scopeViewIsEye,
} from './eye'
import { SCOPE_VIEWS, type ScopeView } from './xy'

function slice(overrides: Partial<ScopeEyeSlice> = {}): ScopeEyeSlice {
  return {
    channel: 0,
    folds: [[0, 1, 0], [0, 1, 0]],
    foldFrameCount: 3,
    foldSeconds: 0.001,
    periodHz: 1000,
    foldCount: 2,
    windowFoldCount: 2,
    maxSpreadV: 0,
    spreadAtS: 0,
    meanSpreadV: 0,
    jitterS: 0,
    ...overrides,
  }
}

describe('the eye view', () => {
  it('is one of the views the picker offers', () => {
    const values = SCOPE_VIEWS.map((v) => v.value)
    expect(values).toContain('Eye')
    // Every view the type allows is offered, so a third one cannot be reachable only in storage.
    const all: ScopeView[] = ['Waveform', 'XY', 'Eye']
    expect([...values].sort()).toEqual([...all].sort())
  })

  it('answers whether a view string is the eye', () => {
    expect(scopeViewIsEye('Eye')).toBe(true)
    expect(scopeViewIsEye('Waveform')).toBe(false)
    expect(scopeViewIsEye('XY')).toBe(false)
  })

  it('spans the same eight divisions the waveform view does', () => {
    // So a trace and an eye of the same signal fill the screen the same way and the V/div knob
    // means one thing on both.
    expect(EYE_DIVISIONS).toBe(8)
    expect(eyeVoltWindow(0.25, 0)).toEqual({ min: -1, max: 1 })
    expect(eyeVoltWindow(0.25, 0.5)).toEqual({ min: -1.5, max: 0.5 })
  })

  it('falls back to a window rather than a flat one for a V/div of zero', () => {
    // A zero-height window paints nothing, and zero is reachable by typing.
    expect(eyeVoltWindow(0, 0)).toEqual({ min: -1, max: 1 })
  })
})

describe('eyeAbsenceNote', () => {
  it('says what to do about each of the three reasons there is no eye', () => {
    expect(eyeAbsenceNote(null, false, null)).toContain('Capture a record')
    expect(eyeAbsenceNote(null, true, null)).toContain('no period to fold on')
    expect(eyeAbsenceNote(null, true, 1000)).toContain('fewer than two whole periods')
  })

  it('is absent when there is an eye', () => {
    expect(eyeAbsenceNote(slice(), true, 1000)).toBeNull()
  })
})

describe('eyeTruncated', () => {
  it('is derived from the two counts rather than carried', () => {
    expect(eyeTruncated(slice({ foldCount: 2, windowFoldCount: 2 }))).toBe(false)
    expect(eyeTruncated(slice({ foldCount: 256, windowFoldCount: 900 }))).toBe(true)
  })

  it('says how to move the overlay along the record rather than only that it was cut', () => {
    const note = eyeTruncationNote(slice({ foldCount: 256, windowFoldCount: 900 }))
    expect(note).toContain('256')
    expect(note).toContain('900')
    expect(note).toContain('Narrow the window')

    expect(eyeTruncationNote(slice())).toBeNull()
  })
})

describe('eyeNote', () => {
  it('names the cycles and the fold', () => {
    expect(eyeNote(slice({ foldCount: 64, periodHz: 1000 }))).toBe('64 cycles · 1.000 kHz fold')
  })

  it('says one cycle in the singular', () => {
    expect(eyeNote(slice({ foldCount: 1 }))).toContain('1 cycle ·')
  })

  it('says so when the overlay is the first of the window s cycles', () => {
    expect(eyeNote(slice({ foldCount: 256, windowFoldCount: 900 }))).toContain('of 900 in the window')
  })
})

describe('eyeReadings', () => {
  it('reads nothing where there is no eye', () => {
    expect(eyeReadings(null)).toEqual([])
    expect(eyeReadings(undefined)).toEqual([])
  })

  it('reads the spread, where it is, the mean and the jitter', () => {
    const readings = eyeReadings(
      slice({ maxSpreadV: 0.0012, spreadAtS: 0.00025, meanSpreadV: 0.0008, jitterS: 1.5e-6 }),
    )

    expect(readings.map((r) => r.label)).toEqual(['Spread', 'Where', 'Mean spread', 'Jitter'])
    // The spread is read in its own magnitude's unit, so a millivolt spread is millivolts whatever
    // the V/div knob says -- a reading rather than an axis label.
    expect(readings[0].value).toContain('mV')
    expect(readings[3].value).toContain('µs')
  })

  it('dashes an absent jitter rather than reading it as zero', () => {
    // Zero jitter is a perfectly stable signal, which is the opposite of "too few folds crossed".
    const readings = eyeReadings(slice({ jitterS: null }))
    const jitter = readings.find((r) => r.label === 'Jitter')

    expect(jitter?.value).toBe('—')
    expect(jitter?.title).toContain('too few')
  })

  it('says what Where is for in its own tooltip', () => {
    // The phase of the worst spread is what tells amplitude variation from timing, which is worth
    // as much as the figure itself.
    const where = eyeReadings(slice())!.find((r) => r.label === 'Where')
    expect(where?.title).toContain('amplitude')
    expect(where?.title).toContain('timing')
  })
})

describe('formatFrequency', () => {
  it('reads kilohertz above a kilohertz and hertz below it', () => {
    expect(formatFrequency(1000)).toBe('1.000 kHz')
    expect(formatFrequency(19996)).toBe('19.996 kHz')
    expect(formatFrequency(50)).toBe('50.00 Hz')
  })

  it('dashes a figure that is not one', () => {
    expect(formatFrequency(Number.NaN)).toBe('—')
    expect(formatFrequency(Number.POSITIVE_INFINITY)).toBe('—')
  })
})
