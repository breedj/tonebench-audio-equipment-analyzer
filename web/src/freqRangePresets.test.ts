import { describe, expect, it } from 'vitest'
import {
  FREQ_FROM_PRESETS,
  FREQ_TO_PRESETS,
  freqRangeAfterFromChange,
  freqRangeAfterToChange,
} from './freqRangePresets'

describe('the two preset ladders', () => {
  it('are ascending and free of duplicates', () => {
    for (const presets of [FREQ_FROM_PRESETS, FREQ_TO_PRESETS]) {
      expect([...presets]).toEqual([...new Set(presets)].sort((a, b) => a - b))
    }
  })

  // The pairing rule answers a From at or above To by moving To up, so every From must have
  // somewhere to push it to -- see freqRangeAfterFromChange's own remarks.
  it('leave every From entry with a To above it', () => {
    const highestTo = Math.max(...FREQ_TO_PRESETS)
    for (const from of FREQ_FROM_PRESETS) expect(from).toBeLessThan(highestTo)
  })

  it('leave every To entry with a From below it', () => {
    const lowestFrom = Math.min(...FREQ_FROM_PRESETS)
    for (const to of FREQ_TO_PRESETS) expect(to).toBeGreaterThan(lowestFrom)
  })

  it('overlap, which is the whole point of the change', () => {
    // A window inside one decade -- 5 kHz to 10 kHz -- has to be selectable from both lists.
    expect(FREQ_FROM_PRESETS).toContain(5000)
    expect(FREQ_TO_PRESETS).toContain(10000)
    // ... and so does a window entirely below 1 kHz.
    expect(FREQ_FROM_PRESETS).toContain(20)
    expect(FREQ_TO_PRESETS).toContain(100)
  })
})

describe('freqRangeAfterFromChange', () => {
  const range = { fromHz: 20, toHz: 20000 }

  it('takes a pick below To as it is', () => {
    expect(freqRangeAfterFromChange(1000, range, FREQ_TO_PRESETS)).toEqual({ fromHz: 1000, toHz: 20000 })
  })

  it('pushes To up to the next entry above a pick that would reverse the window', () => {
    expect(freqRangeAfterFromChange(50000, { fromHz: 20, toHz: 20000 }, FREQ_TO_PRESETS)).toEqual({
      fromHz: 50000,
      toHz: 100000,
    })
  })

  it('pushes To up for a pick that merely equals it, which is an empty window', () => {
    expect(freqRangeAfterFromChange(20000, { fromHz: 20, toHz: 20000 }, FREQ_TO_PRESETS)).toEqual({
      fromHz: 20000,
      toHz: 50000,
    })
  })

  it('uses the list it is given, so the experimental 200 kHz entry is reachable', () => {
    const offered = [...FREQ_TO_PRESETS, 200000]
    expect(freqRangeAfterFromChange(100000, { fromHz: 20, toHz: 20000 }, offered)).toEqual({
      fromHz: 100000,
      toHz: 200000,
    })
    // Without it, the same pick has nowhere to push To and is refused rather than half-applied.
    expect(freqRangeAfterFromChange(100000, { fromHz: 20, toHz: 20000 }, FREQ_TO_PRESETS)).toEqual({
      fromHz: 20,
      toHz: 20000,
    })
  })
})

describe('freqRangeAfterToChange', () => {
  it('takes a pick above From as it is', () => {
    expect(freqRangeAfterToChange(2000, { fromHz: 20, toHz: 20000 }, FREQ_FROM_PRESETS)).toEqual({
      fromHz: 20,
      toHz: 2000,
    })
  })

  it('pulls From down to the next entry below a pick that would reverse the window', () => {
    expect(freqRangeAfterToChange(100, { fromHz: 500, toHz: 20000 }, FREQ_FROM_PRESETS)).toEqual({
      fromHz: 50,
      toHz: 100,
    })
  })

  it('pulls From down for a pick that merely equals it', () => {
    expect(freqRangeAfterToChange(500, { fromHz: 500, toHz: 20000 }, FREQ_FROM_PRESETS)).toEqual({
      fromHz: 200,
      toHz: 500,
    })
  })

  it('refuses a pick with no From below it rather than leaving the window reversed', () => {
    expect(freqRangeAfterToChange(10, { fromHz: 500, toHz: 20000 }, [500, 1000])).toEqual({
      fromHz: 500,
      toHz: 20000,
    })
  })
})
