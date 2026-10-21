import { describe, expect, it } from 'vitest'
import { MAX_AVERAGE_COUNT, MIN_AVERAGE_COUNT, averageCountError } from './averaging'

describe('averageCountError', () => {
  it('accepts the whole range the server accepts', () => {
    for (const count of [MIN_AVERAGE_COUNT, 2, 10, 19, MAX_AVERAGE_COUNT]) {
      expect(averageCountError(count)).toBeNull()
    }
  })

  it('refuses either side of it, naming the range', () => {
    expect(averageCountError(0)).toBe(`Averages must be between 1 and ${MAX_AVERAGE_COUNT}.`)
    expect(averageCountError(MAX_AVERAGE_COUNT + 1)).toBe(`Averages must be between 1 and ${MAX_AVERAGE_COUNT}.`)
  })

  it('is the ceiling the server was raised to, not the old one', () => {
    // Mirrors FrequencyResponseRequest.MaxAverageCount. A panel that stops short of it refuses a
    // count the server would have taken, which is the half of a drift nobody reports.
    expect(MAX_AVERAGE_COUNT).toBe(20)
  })
})
