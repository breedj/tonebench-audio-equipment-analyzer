import { describe, expect, it } from 'vitest'
import {
  DEFAULT_SCOPE_AVERAGE_COUNT,
  MAX_SCOPE_AVERAGE_COUNT,
  MIN_SCOPE_AVERAGE_COUNT,
  SCOPE_ACCUMULATIONS,
  SCOPE_ACCUMULATION_HINTS,
  accumulationBadge,
  accumulationCombines,
  accumulationProgress,
  accumulationSettleSeconds,
  accumulationUnavailableNote,
  clampAverageCount,
  modeCanAccumulate,
  type ScopeAccumulation,
} from './acquisition'

describe('the accumulation modes', () => {
  it('offers each one once, Normal first', () => {
    expect(SCOPE_ACCUMULATIONS.map((m) => m.value)).toEqual(['Normal', 'Average', 'Envelope'])
  })

  it('says what each one does, and what the two that combine need', () => {
    for (const mode of SCOPE_ACCUMULATIONS) {
      expect(SCOPE_ACCUMULATION_HINTS[mode.value].length).toBeGreaterThan(20)
    }
    // The two halves of the averaging claim, which is what a reader acts on.
    expect(SCOPE_ACCUMULATION_HINTS.Average).toContain('√N')
    expect(SCOPE_ACCUMULATION_HINTS.Average).toContain('standing still')
  })

  it('knows which modes combine records', () => {
    expect(accumulationCombines('Normal')).toBe(false)
    expect(accumulationCombines('Average')).toBe(true)
    expect(accumulationCombines('Envelope')).toBe(true)
  })
})

describe('which run modes can accumulate', () => {
  it('is Auto and Normal, and nothing else', () => {
    // Mirrors OscilloscopeRequest.Validate, so the control is disabled rather than the run refused.
    expect(modeCanAccumulate('Auto')).toBe(true)
    expect(modeCanAccumulate('Normal')).toBe(true)
    expect(modeCanAccumulate('Single')).toBe(false)
    expect(modeCanAccumulate('Roll')).toBe(false)
  })

  it('says why, differently for the two that cannot', () => {
    expect(accumulationUnavailableNote('Auto')).toBeNull()
    expect(accumulationUnavailableNote('Single')).toContain('one record')
    expect(accumulationUnavailableNote('Roll')).toContain('overlap')
  })
})

describe('clampAverageCount', () => {
  it('holds the analyzer s own bounds', () => {
    expect(clampAverageCount(1)).toBe(MIN_SCOPE_AVERAGE_COUNT)
    expect(clampAverageCount(10_000)).toBe(MAX_SCOPE_AVERAGE_COUNT)
    expect(clampAverageCount(16)).toBe(16)
    expect(clampAverageCount(16.4)).toBe(16)
  })

  it('answers the default for a box that is not a number', () => {
    // An emptied box is '' rather than NaN under Vue's .number modifier, but a NaN still reaches
    // here from arithmetic -- and a count of NaN would be refused by the analyzer.
    expect(clampAverageCount(Number.NaN)).toBe(DEFAULT_SCOPE_AVERAGE_COUNT)
  })

  it('has a default inside its own bounds', () => {
    expect(DEFAULT_SCOPE_AVERAGE_COUNT).toBeGreaterThanOrEqual(MIN_SCOPE_AVERAGE_COUNT)
    expect(DEFAULT_SCOPE_AVERAGE_COUNT).toBeLessThanOrEqual(MAX_SCOPE_AVERAGE_COUNT)
  })
})

describe('accumulationProgress', () => {
  it('counts up rather than showing a fraction', () => {
    // The figure a reader wants is how many records are in the trace: it is what says whether the
    // noise floor on screen is the one the count promises.
    expect(accumulationProgress('Average', 3, 16)).toBe('Averaging 3 of 16')
    expect(accumulationProgress('Average', 16, 16)).toBe('Averaged over 16 records')
  })

  it('counts an envelope by what it has seen', () => {
    // An envelope keeps the worst case rather than the last N, so there is no fraction to show.
    expect(accumulationProgress('Envelope', 5, 16)).toBe('Envelope of 5 records')
    expect(accumulationProgress('Envelope', 40, 16)).toBe('Envelope of 40 records')
  })

  it('says nothing under Normal, or for a count of one', () => {
    expect(accumulationProgress('Normal', 1, 1)).toBeNull()
    expect(accumulationProgress('Average', 1, 1)).toBeNull()
  })
})

describe('accumulationBadge', () => {
  it('is absent under Normal', () => {
    expect(accumulationBadge('Normal', 1, 1)).toBeNull()
  })

  it('says how many records are in the trace, and the caveat in its tooltip', () => {
    const filling = accumulationBadge('Average', 4, 16)
    expect(filling?.label).toBe('Averaging 4/16')
    expect(filling?.detail).toContain('square root')
    expect(filling?.detail).toContain('trigger')

    expect(accumulationBadge('Average', 16, 16)?.label).toBe('Averaged ×16')
  })

  it('tells an envelope reader which readings are absent', () => {
    // An envelope is not a waveform, and what the picture cannot say for itself is that the math
    // channel, the X-Y figure and the eye are all refused on one.
    const badge = accumulationBadge('Envelope', 9, 16)
    expect(badge?.label).toBe('Envelope ×9')
    expect(badge?.detail).toContain('not a waveform')
    expect(badge?.detail).toContain('X–Y')
  })
})

describe('accumulationSettleSeconds', () => {
  it('is the capture times the count', () => {
    // Worth saying before the run: 256 of the one-second captures this app allows is over four
    // minutes, and a display nobody waits for is one nobody believes.
    expect(accumulationSettleSeconds('Average', 16, 0.02)).toBeCloseTo(0.32, 12)
    expect(accumulationSettleSeconds('Envelope', 256, 1)).toBeCloseTo(256, 12)
  })

  it('is absent where it does not apply', () => {
    expect(accumulationSettleSeconds('Normal', 16, 0.02)).toBeNull()
    expect(accumulationSettleSeconds('Average', 16, 0)).toBeNull()
  })

  it('uses the clamped count rather than whatever was typed', () => {
    const settle = accumulationSettleSeconds('Average', 10_000, 0.01)
    expect(settle).toBeCloseTo(MAX_SCOPE_AVERAGE_COUNT * 0.01, 12)
  })
})

describe('the modes the type allows', () => {
  it('are the three the picker offers and no others', () => {
    const all: ScopeAccumulation[] = ['Normal', 'Average', 'Envelope']
    expect([...SCOPE_ACCUMULATIONS.map((m) => m.value)].sort()).toEqual([...all].sort())
  })
})
