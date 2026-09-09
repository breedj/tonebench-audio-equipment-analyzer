import { describe, expect, it } from 'vitest'
import { ohmTickLabel, ohmTickLabels, signedOhmTickLabel } from './ohmAxis'

describe('ohmTickLabel', () => {
  it('switches to milliohms below one ohm', () => {
    expect(ohmTickLabel(0.01)).toBe('10m')
    expect(ohmTickLabel(0.15)).toBe('150m')
    expect(ohmTickLabel(0.8)).toBe('800m')
  })

  it('leaves the ohms decade bare', () => {
    expect(ohmTickLabel(1)).toBe('1')
    expect(ohmTickLabel(8)).toBe('8')
    expect(ohmTickLabel(60)).toBe('60')
    expect(ohmTickLabel(470)).toBe('470')
  })

  it('switches to kilohms above a thousand', () => {
    expect(ohmTickLabel(1000)).toBe('1k')
    expect(ohmTickLabel(47000)).toBe('47k')
    expect(ohmTickLabel(470000)).toBe('470k')
  })

  it('switches to megohms for a valve grid input', () => {
    expect(ohmTickLabel(1e6)).toBe('1M')
    expect(ohmTickLabel(2.2e6)).toBe('2.2M')
  })

  /**
   * The reason the unit is chosen per tick rather than per axis: the ladder can straddle a boundary,
   * and one unit across the whole axis would print "0.8" or "2000" at one end of it.
   */
  it('can straddle a unit boundary within one axis', () => {
    expect(ohmTickLabels([0.5, 0.8, 1, 2])).toEqual(['500m', '800m', '1', '2'])
  })

  it('drops the trailing zeros toPrecision leaves behind', () => {
    expect(ohmTickLabel(2)).toBe('2')
    expect(ohmTickLabel(20)).toBe('20')
    expect(ohmTickLabel(1.5)).toBe('1.5')
  })

  it('has no label for a value a log axis cannot show', () => {
    expect(ohmTickLabel(0)).toBeNull()
    expect(ohmTickLabel(-5)).toBeNull()
    expect(ohmTickLabel(null)).toBeNull()
    expect(ohmTickLabel(Number.NaN)).toBeNull()
  })
})

describe('signedOhmTickLabel', () => {
  it('keeps zero, which is the most important tick on a reactance axis', () => {
    expect(signedOhmTickLabel(0)).toBe('0')
  })

  it('carries the sign, because reactance goes both ways', () => {
    expect(signedOhmTickLabel(40)).toBe('40')
    expect(signedOhmTickLabel(-40)).toBe('-40')
    expect(signedOhmTickLabel(-0.5)).toBe('-500m')
  })

  it('has no label for a value that is not a number', () => {
    expect(signedOhmTickLabel(null)).toBeNull()
    expect(signedOhmTickLabel(Number.NaN)).toBeNull()
  })
})
