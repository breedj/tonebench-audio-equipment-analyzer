import { describe, expect, it } from 'vitest'
import type { RecentProject } from '../api/client'
import { formatLastOpened, sortRecent } from './recent'

function entry(path: string, lastOpenedUtc: string): RecentProject {
  return { path, name: path, lastOpenedUtc, exists: true }
}

describe('sortRecent', () => {
  it('orders most recent first without mutating the input', () => {
    const input = [entry('a', '2026-09-17T10:00:00Z'), entry('b', '2026-09-19T10:00:00Z'), entry('c', '2026-09-18T10:00:00Z')]
    expect(sortRecent(input).map((e) => e.path)).toEqual(['b', 'c', 'a'])
    expect(input.map((e) => e.path)).toEqual(['a', 'b', 'c'])
  })
})

describe('formatLastOpened', () => {
  const now = new Date(2026, 8, 19, 15, 0, 0) // local time, a Saturday

  it('says today, yesterday, a weekday, or a date', () => {
    expect(formatLastOpened(new Date(2026, 8, 19, 9, 5).toISOString(), now, 'en-US')).toMatch(/^today /)
    expect(formatLastOpened(new Date(2026, 8, 18, 23, 59).toISOString(), now, 'en-US')).toMatch(/^yesterday /)
    expect(formatLastOpened(new Date(2026, 8, 15, 8, 0).toISOString(), now, 'en-US')).toMatch(/^Tuesday /)
    expect(formatLastOpened(new Date(2026, 8, 1, 8, 0).toISOString(), now, 'en-US')).toMatch(/2026/)
  })

  it('is empty for garbage', () => {
    expect(formatLastOpened('not a date', now, 'en-US')).toBe('')
  })
})
