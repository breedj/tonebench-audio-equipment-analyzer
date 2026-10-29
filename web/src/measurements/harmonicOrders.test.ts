import { describe, expect, it } from 'vitest'
import {
  allHarmonicOrders,
  harmonicOrderColor,
  harmonicOrdersWith,
  HARMONIC_ORDERS,
  parseHarmonicOrders,
} from './harmonicOrders'

describe('the harmonic orders', () => {
  it('are the ones the analysis can extract', () => {
    // ImpulseResponseRequest.MinHarmonicOrder..MaxSupportedHarmonicOrder.
    expect([...HARMONIC_ORDERS]).toEqual([2, 3, 4, 5])
  })

  it('all start shown, as a fresh array the caller may keep', () => {
    const first = allHarmonicOrders()
    first.push(99)
    expect(allHarmonicOrders()).toEqual([2, 3, 4, 5])
  })

  it('have a colour each, in both themes', () => {
    const light = new Set(HARMONIC_ORDERS.map((o) => harmonicOrderColor(o, false)))
    const dark = new Set(HARMONIC_ORDERS.map((o) => harmonicOrderColor(o, true)))
    expect(light.size).toBe(HARMONIC_ORDERS.length)
    expect(dark.size).toBe(HARMONIC_ORDERS.length)
    // The two tables are separate values, not one reused — a swatch has to read on either ground.
    for (const order of HARMONIC_ORDERS) {
      expect(harmonicOrderColor(order, true)).not.toBe(harmonicOrderColor(order, false))
    }
  })
})

describe('parseHarmonicOrders', () => {
  it('reads a stored list, sorted and deduplicated', () => {
    expect(parseHarmonicOrders([5, 2, 2])).toEqual([2, 5])
  })

  it('keeps an empty list, which is every curve switched off', () => {
    expect(parseHarmonicOrders([])).toEqual([])
  })

  it('says nothing where the document says nothing', () => {
    expect(parseHarmonicOrders(undefined)).toBeNull()
    expect(parseHarmonicOrders(null)).toBeNull()
    expect(parseHarmonicOrders(3)).toBeNull()
    expect(parseHarmonicOrders('2,3')).toBeNull()
  })

  it('reads a list it cannot understand as nothing stored, rather than as nothing shown', () => {
    // A hand-edited or newer document must not blank the chart with no way of telling why.
    expect(parseHarmonicOrders(['2', '3'])).toBeNull()
    expect(parseHarmonicOrders([7, 9])).toBeNull()
    // One recognised order among junk is still an answer about that order.
    expect(parseHarmonicOrders([7, 3])).toEqual([3])
  })
})

describe('harmonicOrdersWith', () => {
  it('adds and removes, keeping the list sorted', () => {
    expect(harmonicOrdersWith([2, 5], 3, true)).toEqual([2, 3, 5])
    expect(harmonicOrdersWith([2, 3, 5], 3, false)).toEqual([2, 5])
  })

  it('is idempotent either way', () => {
    expect(harmonicOrdersWith([2, 3], 3, true)).toEqual([2, 3])
    expect(harmonicOrdersWith([2, 3], 4, false)).toEqual([2, 3])
  })

  it('returns a new array rather than editing the one it was given', () => {
    const shown = [2, 3]
    const next = harmonicOrdersWith(shown, 4, true)
    expect(shown).toEqual([2, 3])
    expect(next).not.toBe(shown)
  })
})
