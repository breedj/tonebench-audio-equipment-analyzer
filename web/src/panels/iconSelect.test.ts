import { describe, expect, it } from 'vitest'
import {
  firstEnabledIconSelectIndex,
  hasEnabledIconSelectOption,
  lastEnabledIconSelectIndex,
  moveIconSelectIndex,
  openIconSelectIndex,
  type IconSelectOption,
} from './iconSelect'

const options: IconSelectOption<string>[] = [
  { value: 'a', label: 'A' },
  { value: 'b', label: 'B' },
  { value: 'c', label: 'C' },
]

const withGap: IconSelectOption<string>[] = [
  { value: 'a', label: 'A' },
  { value: 'b', label: 'B', disabled: true },
  { value: 'c', label: 'C' },
]

describe('moveIconSelectIndex', () => {
  it('steps one at a time in either direction', () => {
    expect(moveIconSelectIndex(options, 0, 1)).toBe(1)
    expect(moveIconSelectIndex(options, 1, 1)).toBe(2)
    expect(moveIconSelectIndex(options, 2, -1)).toBe(1)
  })

  it('wraps, so the last option is one press from the first', () => {
    expect(moveIconSelectIndex(options, 2, 1)).toBe(0)
    expect(moveIconSelectIndex(options, 0, -1)).toBe(2)
  })

  it('skips an option a mouse could not press', () => {
    expect(moveIconSelectIndex(withGap, 0, 1)).toBe(2)
    expect(moveIconSelectIndex(withGap, 2, -1)).toBe(0)
  })

  it('enters an empty list at the end the key is pointing away from', () => {
    expect(moveIconSelectIndex(options, -1, 1)).toBe(0)
    expect(moveIconSelectIndex(options, -1, -1)).toBe(2)
  })

  it('answers -1 rather than an index when nothing can be chosen', () => {
    expect(moveIconSelectIndex([], 0, 1)).toBe(-1)
    expect(
      moveIconSelectIndex(
        [
          { value: 'a', label: 'A', disabled: true },
          { value: 'b', label: 'B', disabled: true },
        ],
        0,
        1,
      ),
    ).toBe(-1)
  })

  it('stays put on a list of one', () => {
    const one: IconSelectOption<string>[] = [{ value: 'a', label: 'A' }]
    expect(moveIconSelectIndex(one, 0, 1)).toBe(0)
    expect(moveIconSelectIndex(one, 0, -1)).toBe(0)
  })

  it('reads an index outside the list as no index at all', () => {
    expect(moveIconSelectIndex(options, 99, 1)).toBe(0)
    expect(moveIconSelectIndex(options, Number.NaN, 1)).toBe(0)
  })
})

describe('the ends of the list', () => {
  it('finds the first and last that can be chosen', () => {
    expect(firstEnabledIconSelectIndex(options)).toBe(0)
    expect(lastEnabledIconSelectIndex(options)).toBe(2)
  })

  it('steps over a disabled end', () => {
    const edged: IconSelectOption<string>[] = [
      { value: 'a', label: 'A', disabled: true },
      { value: 'b', label: 'B' },
      { value: 'c', label: 'C', disabled: true },
    ]
    expect(firstEnabledIconSelectIndex(edged)).toBe(1)
    expect(lastEnabledIconSelectIndex(edged)).toBe(1)
  })

  it('answers -1 for a list with nothing in it', () => {
    expect(firstEnabledIconSelectIndex([])).toBe(-1)
    expect(lastEnabledIconSelectIndex([])).toBe(-1)
    expect(hasEnabledIconSelectOption([])).toBe(false)
    expect(hasEnabledIconSelectOption(withGap)).toBe(true)
  })
})

describe('openIconSelectIndex', () => {
  it('opens on the value the control already holds', () => {
    expect(openIconSelectIndex(options, 'c')).toBe(2)
  })

  it('opens on a selected option even when it is disabled, so the list and the button agree', () => {
    expect(openIconSelectIndex(withGap, 'b')).toBe(1)
  })

  it('falls back to the first that can be chosen when the value is not in the list', () => {
    expect(openIconSelectIndex(options, 'z')).toBe(0)
    expect(openIconSelectIndex([], 'z')).toBe(-1)
  })
})
