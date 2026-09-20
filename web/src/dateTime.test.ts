import { describe, expect, it } from 'vitest'
import { DATE_TIME_FORMATS, formatDateTime, toDateTimeFormatId } from './dateTime'

describe('formatDateTime', () => {
  // Local-time constructor, so the expected strings hold in every time zone the test runs in.
  const afternoon = new Date(2026, 8, 5, 14, 7, 9) // 5 Sep 2026 14:07:09
  const midnight = new Date(2026, 11, 25, 0, 30, 0) // 25 Dec 2026 00:30:00
  const noon = new Date(2026, 0, 1, 12, 0, 0)

  it('writes the fixed formats with zero padding', () => {
    expect(formatDateTime(afternoon, 'iso')).toBe('2026-09-05 14:07')
    expect(formatDateTime(afternoon, 'iso-seconds')).toBe('2026-09-05 14:07:09')
    expect(formatDateTime(afternoon, 'dmy')).toBe('05-09-2026 14:07')
    expect(formatDateTime(afternoon, 'dmy-seconds')).toBe('05-09-2026 14:07:09')
    expect(formatDateTime(afternoon, 'dmy-text')).toBe('5 Sep 2026 14:07')
  })

  it('writes the 12-hour formats with 12 for midnight and noon', () => {
    expect(formatDateTime(afternoon, 'mdy')).toBe('09/05/2026 2:07 PM')
    expect(formatDateTime(afternoon, 'mdy-text')).toBe('Sep 5, 2026 2:07 PM')
    expect(formatDateTime(midnight, 'mdy')).toBe('12/25/2026 12:30 AM')
    expect(formatDateTime(noon, 'mdy-text')).toBe('Jan 1, 2026 12:00 PM')
  })

  it('leaves the system format to the locale', () => {
    expect(formatDateTime(afternoon, 'system')).toBe(afternoon.toLocaleString())
  })

  it('writes nothing for an invalid date', () => {
    for (const { id } of DATE_TIME_FORMATS) expect(formatDateTime(new Date(NaN), id)).toBe('')
  })
})

describe('toDateTimeFormatId', () => {
  it('accepts every offered id and falls back to system for anything else', () => {
    for (const { id } of DATE_TIME_FORMATS) expect(toDateTimeFormatId(id)).toBe(id)
    expect(toDateTimeFormatId('rfc-2822')).toBe('system')
    expect(toDateTimeFormatId(undefined)).toBe('system')
    expect(toDateTimeFormatId(42)).toBe('system')
  })
})
