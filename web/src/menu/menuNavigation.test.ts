import { describe, expect, it } from 'vitest'
import { firstEnabledIndex, lastEnabledIndex, nextEnabledIndex } from './menuNavigation'

const items = [{ disabled: false }, { disabled: true }, { disabled: false }, { disabled: true }]

describe('nextEnabledIndex', () => {
  it('skips disabled items and wraps', () => {
    expect(nextEnabledIndex(items, 0, 1)).toBe(2)
    expect(nextEnabledIndex(items, 2, 1)).toBe(0)
    expect(nextEnabledIndex(items, 0, -1)).toBe(2)
    expect(nextEnabledIndex(items, 2, -1)).toBe(0)
  })

  it('enters the list from outside at either end', () => {
    expect(firstEnabledIndex(items)).toBe(0)
    expect(lastEnabledIndex(items)).toBe(2)
    expect(firstEnabledIndex([{ disabled: true }, { disabled: false }])).toBe(1)
  })

  it('returns null when nothing is enabled or the list is empty', () => {
    expect(nextEnabledIndex([], null, 1)).toBeNull()
    expect(nextEnabledIndex([{ disabled: true }], 0, 1)).toBeNull()
    expect(firstEnabledIndex([{ disabled: true }])).toBeNull()
  })
})
