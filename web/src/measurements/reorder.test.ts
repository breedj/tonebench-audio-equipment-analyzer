import { describe, expect, it } from 'vitest'
import { dropIndex, moveItem } from './reorder'

describe('moveItem', () => {
  it('moves an item down, shifting the ones it passes up', () => {
    expect(moveItem(['a', 'b', 'c', 'd'], 0, 2)).toEqual(['b', 'c', 'a', 'd'])
  })

  it('moves an item up', () => {
    expect(moveItem(['a', 'b', 'c', 'd'], 3, 1)).toEqual(['a', 'd', 'b', 'c'])
  })

  it('reaches both ends', () => {
    expect(moveItem(['a', 'b', 'c'], 2, 0)).toEqual(['c', 'a', 'b'])
    expect(moveItem(['a', 'b', 'c'], 0, 2)).toEqual(['b', 'c', 'a'])
  })

  it('leaves the list alone for a no-op or out-of-range move', () => {
    expect(moveItem(['a', 'b', 'c'], 1, 1)).toEqual(['a', 'b', 'c'])
    expect(moveItem(['a', 'b', 'c'], 5, 0)).toEqual(['a', 'b', 'c'])
    expect(moveItem(['a', 'b', 'c'], 0, 9)).toEqual(['a', 'b', 'c'])
  })

  it('does not mutate the input', () => {
    const items = ['a', 'b', 'c']
    moveItem(items, 0, 2)
    expect(items).toEqual(['a', 'b', 'c'])
  })
})

describe('dropIndex', () => {
  it('drops before the row when the pointer is in its top half', () => {
    expect(dropIndex(3, 1, false)).toBe(1)
  })

  // Dragging downwards the row is lifted out first, so "after row 3" is index 3, not 4.
  it('accounts for the dragged row being lifted out when moving down', () => {
    expect(dropIndex(0, 3, true)).toBe(3)
    expect(dropIndex(5, 3, true)).toBe(4)
  })

  it('reaches the end of the list from the bottom half of the last row', () => {
    const items = ['a', 'b', 'c', 'd']
    expect(moveItem(items, 0, dropIndex(0, 3, true))).toEqual(['b', 'c', 'd', 'a'])
  })

  it('reaches the start of the list from the top half of the first row', () => {
    const items = ['a', 'b', 'c', 'd']
    expect(moveItem(items, 2, dropIndex(2, 0, false))).toEqual(['c', 'a', 'b', 'd'])
  })

  it('is a no-op on either half of the dragged row itself', () => {
    expect(dropIndex(2, 2, false)).toBe(2)
    expect(dropIndex(2, 2, true)).toBe(2)
  })
})
