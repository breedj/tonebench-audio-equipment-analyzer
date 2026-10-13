import { describe, expect, it } from 'vitest'
import {
  formatBandwidth,
  formatCorner,
  formatDb,
  formatDelay,
  formatHz,
  formatRatio,
  splitUnit,
} from './format'

const reading = (over: Partial<Parameters<typeof formatBandwidth>[0]> = {}) => ({
  dropDb: 3,
  symmetric: false,
  lowerHz: 8.8,
  upperHz: 63200,
  bandwidthHz: 63191.2,
  lowerClamped: false,
  upperClamped: false,
  ...over,
})

describe('formatHz', () => {
  it('switches to kHz above 1000', () => {
    expect(formatHz(4.4)).toBe('4.40 Hz')
    expect(formatHz(461.4)).toBe('461.4 Hz')
    expect(formatHz(1000)).toBe('1.00 kHz')
    expect(formatHz(63200)).toBe('63.2 kHz')
  })

  it('renders a non-finite frequency as a dash rather than NaN', () => {
    expect(formatHz(Number.NaN)).toBe('—')
    expect(formatHz(Infinity)).toBe('—')
  })
})

describe('formatCorner', () => {
  it('prints a measured corner as a plain frequency', () => {
    expect(formatCorner(8.8, false, 'lower')).toBe('8.80 Hz')
    expect(formatCorner(63200, false, 'upper')).toBe('63.2 kHz')
  })

  it('marks a clamped corner, because it was never reached', () => {
    // The distinction the previous app lost: this is where the search stopped, not a corner. A
    // bare number there reads as a measurement of something that was not measured.
    expect(formatCorner(96000, true, 'upper')).toBe('> 96.0 kHz')
    expect(formatCorner(4, true, 'lower')).toBe('< 4.00 Hz')
  })
})

describe('formatBandwidth', () => {
  it('prints a fully measured bandwidth plainly', () => {
    expect(formatBandwidth(reading())).toBe('63.2 kHz')
  })

  it('marks a bandwidth as a lower bound when either end ran off the measured band', () => {
    expect(formatBandwidth(reading({ upperClamped: true }))).toBe('> 63.2 kHz')
    expect(formatBandwidth(reading({ lowerClamped: true }))).toBe('> 63.2 kHz')
  })
})

describe('formatRatio', () => {
  it('converts dB to the linear ratio the previous app showed beside it', () => {
    expect(formatRatio(34.49)).toBe('53.03 x')
    expect(formatRatio(20)).toBe('10.00 x')
    expect(formatRatio(0)).toBe('1.000 x')
    expect(formatRatio(-6)).toBe('0.501 x')
  })

  it('renders a non-finite gain as a dash', () => {
    expect(formatRatio(Number.NaN)).toBe('—')
  })
})

describe('formatDb', () => {
  it('always shows two decimals and a unit', () => {
    expect(formatDb(34.49)).toBe('34.49 dB')
    expect(formatDb(-0.1)).toBe('-0.10 dB')
    expect(formatDb(Number.NaN)).toBe('—')
  })

  it('never prints a negative zero', () => {
    // A flat loopback's gain lands a hair under zero every run, and "-0.00 dB" reads as a signed
    // quantity where there is none.
    expect(formatDb(-0.0001)).toBe('0.00 dB')
    expect(formatDb(-0)).toBe('0.00 dB')
    expect(formatDb(-0.02)).toBe('-0.02 dB')
  })
})

describe('formatDelay', () => {
  it('picks the unit from the size of the delay', () => {
    expect(formatDelay(2.0833e-3)).toBe('2.083 ms')
    expect(formatDelay(12.34e-6)).toBe('12.3 µs')
    expect(formatDelay(250e-9)).toBe('250 ns')
  })

  it('keeps the sign, since an excess delay can be negative', () => {
    expect(formatDelay(-12.34e-6)).toBe('-12.3 µs')
    expect(formatDelay(-1.5e-3)).toBe('-1.500 ms')
  })

  it('shows a dash for a result that never recorded a delay', () => {
    expect(formatDelay(null)).toBe('—')
    expect(formatDelay(undefined)).toBe('—')
    expect(formatDelay(Number.NaN)).toBe('—')
    expect(formatDelay(0)).toBe('0 ns')
  })
})

describe('splitUnit', () => {
  it('splits a reading at its unit', () => {
    expect(splitUnit('52.55 Hz')).toEqual({ value: '52.55', unit: 'Hz' })
    expect(splitUnit('27.2 kHz')).toEqual({ value: '27.2', unit: 'kHz' })
    expect(splitUnit('4.41 dB')).toEqual({ value: '4.41', unit: 'dB' })
    expect(splitUnit('22.780 ms')).toEqual({ value: '22.780', unit: 'ms' })
  })

  it('keeps a clamped corner marker with the number', () => {
    expect(splitUnit('> 52.2 kHz')).toEqual({ value: '> 52.2', unit: 'kHz' })
    expect(splitUnit('< 11.38 Hz')).toEqual({ value: '< 11.38', unit: 'Hz' })
  })

  it('leaves a value with no unit alone rather than making one up', () => {
    expect(splitUnit('—')).toEqual({ value: '—', unit: '' })
    expect(splitUnit('')).toEqual({ value: '', unit: '' })
    // A bare pair of numbers must not read its second as a unit.
    expect(splitUnit('1 2')).toEqual({ value: '1 2', unit: '' })
  })
})
