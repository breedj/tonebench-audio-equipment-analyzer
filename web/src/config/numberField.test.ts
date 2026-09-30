import { describe, expect, it } from 'vitest'
import { commitNumber } from './numberField'

const GAIN = { min: -60, max: 60, fallback: 0 }
const DELAY = { min: 0, max: 10_000, fallback: 0 }

describe('commitNumber', () => {
  it('takes a plain number through unchanged', () => {
    expect(commitNumber('-3', GAIN)).toBe(-3)
    expect(commitNumber('300', DELAY)).toBe(300)
  })

  it('accepts a negative value, which is the case that was reported broken', () => {
    // The bug was not in the parsing -- it was the field being clobbered on re-render -- but a
    // regression here would look identical to the user, so it is pinned alongside.
    expect(commitNumber('-3', GAIN)).toBe(-3)
    expect(commitNumber('-0.5', GAIN)).toBe(-0.5)
  })

  it('reads an emptied field as the fallback rather than rejecting it', () => {
    // Clearing a box and tabbing away is how people say "none of this".
    expect(commitNumber('', GAIN)).toBe(0)
    expect(commitNumber('   ', DELAY)).toBe(0)
  })

  it('falls back on anything that is not a number', () => {
    // A half-typed minus sign is the realistic one: it reaches a change handler on blur.
    expect(commitNumber('-', GAIN)).toBe(0)
    expect(commitNumber('abc', GAIN)).toBe(0)
    expect(commitNumber('NaN', GAIN)).toBe(0)
    expect(commitNumber('Infinity', DELAY)).toBe(0)
  })

  it('clamps rather than refusing, so a typo lands on the bound and is visible', () => {
    expect(commitNumber('600', GAIN)).toBe(60)
    expect(commitNumber('-600', GAIN)).toBe(-60)
    expect(commitNumber('99999', DELAY)).toBe(10_000)
    expect(commitNumber('-5', DELAY)).toBe(0)
  })

  it('keeps a value sitting exactly on a bound', () => {
    expect(commitNumber('60', GAIN)).toBe(60)
    expect(commitNumber('-60', GAIN)).toBe(-60)
    expect(commitNumber('0', DELAY)).toBe(0)
  })
})
