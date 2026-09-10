import { describe, expect, it } from 'vitest'
import { formatBandwidth, formatCorner, formatDb, formatHz, formatRatio } from './format'

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
