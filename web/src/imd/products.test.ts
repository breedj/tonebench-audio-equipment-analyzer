import { describe, expect, it } from 'vitest'
import { imdProductLabel } from './products'

describe('imdProductLabel', () => {
  const smpte = { toneLowHz: 60, toneHighHz: 7000 }
  const ccif = { toneLowHz: 19000, toneHighHz: 20000 }

  it('names SMPTE sidebands by their offset from the carrier', () => {
    expect(imdProductLabel('Smpte', { m: -1, n: 1 }, smpte)).toBe('f2 − f1')
    expect(imdProductLabel('Smpte', { m: 1, n: 3 }, smpte)).toBe('f2 + 3·f1')
  })

  it('writes CCIF difference products with the larger term first', () => {
    expect(imdProductLabel('Ccif', { m: 1, n: 1 }, ccif)).toBe('f2 − f1')
    expect(imdProductLabel('Ccif', { m: 2, n: 1 }, ccif)).toBe('2·f1 − f2')
    expect(imdProductLabel('Ccif', { m: 1, n: 2 }, ccif)).toBe('2·f2 − f1')
    expect(imdProductLabel('Ccif', { m: 2, n: 2 }, ccif)).toBe('2·f2 − 2·f1')
  })

  it('follows the actual tones, not just m and n, for a widely spaced custom pair', () => {
    expect(imdProductLabel('Ccif', { m: 2, n: 1 }, { toneLowHz: 1000, toneHighHz: 10000 })).toBe('f2 − 2·f1')
  })
})
