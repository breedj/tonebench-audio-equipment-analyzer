import { describe, expect, it } from 'vitest'
import {
  DEFAULT_SCOPE_TRIGGER,
  SCOPE_RUN_MODES,
  SCOPE_RUN_MODE_HINTS,
  SCOPE_TRIGGER_SLOPES,
  SCOPE_TRIGGER_SOURCES,
  fiftyPercentLevel,
  suggestedHysteresis,
  triggerChannel,
  triggerIndicator,
  triggerSourceProblem,
} from './triggerSpec'

describe('the dropdowns', () => {
  it('offers Auto first, because it is the only mode that always draws something', () => {
    // Normal waits for a crossing for ever, which on a dead input is indistinguishable from a
    // scope that has hung.
    expect(SCOPE_RUN_MODES[0].value).toBe('Auto')
  })

  it('describes every mode it offers', () => {
    for (const mode of SCOPE_RUN_MODES) expect(SCOPE_RUN_MODE_HINTS[mode.value]).toBeTruthy()
  })

  it('offers only the two trigger sources the server implements', () => {
    // Math and Stimulus exist on the wire and are refused by name; a dropdown entry whose only
    // outcome is a refusal is worse than no entry.
    expect(SCOPE_TRIGGER_SOURCES.map((s) => s.value)).toEqual(['ChannelA', 'ChannelB'])
  })

  it('labels the sources as the channels a user sees, not as the enum', () => {
    expect(SCOPE_TRIGGER_SOURCES.map((s) => s.label)).toEqual(['Left', 'Right'])
  })

  it('offers all three slopes', () => {
    expect(SCOPE_TRIGGER_SLOPES.map((s) => s.value)).toEqual(['Rising', 'Falling', 'Either'])
  })

  it('starts on a setting every one of those lists contains', () => {
    expect(SCOPE_TRIGGER_SOURCES.some((s) => s.value === DEFAULT_SCOPE_TRIGGER.source)).toBe(true)
    expect(SCOPE_TRIGGER_SLOPES.some((s) => s.value === DEFAULT_SCOPE_TRIGGER.slope)).toBe(true)
  })
})

describe('DEFAULT_SCOPE_TRIGGER', () => {
  it('matches what a brand-new measurement is seeded with server-side', () => {
    // DefaultOscilloscopeParametersJson writes these too, and the client's defaults never get a
    // look in there -- so the two are written twice and have to agree.
    expect(DEFAULT_SCOPE_TRIGGER).toEqual({
      kind: 'Edge',
      source: 'ChannelA',
      slope: 'Rising',
      levelV: 0,
      hysteresisV: 0,
      holdoffS: 0,
    })
  })
})

describe('triggerChannel', () => {
  it('maps the source onto the captured channel index', () => {
    expect(triggerChannel('ChannelA')).toBe(0)
    expect(triggerChannel('ChannelB')).toBe(1)
  })
})

describe('triggerSourceProblem', () => {
  it('is silent when the trigger can see its channel', () => {
    expect(triggerSourceProblem('ChannelA', true, false)).toBeNull()
    expect(triggerSourceProblem('ChannelB', false, true)).toBeNull()
  })

  it('names the channel the trigger watches but nothing captures', () => {
    expect(triggerSourceProblem('ChannelB', true, false)).toContain('right channel')
    expect(triggerSourceProblem('ChannelA', false, true)).toContain('left channel')
  })
})

describe('triggerIndicator', () => {
  it('draws the line at the level when it is on the window', () => {
    const indicator = triggerIndicator(0.5, 2, -2)
    expect(indicator.levelV).toBe(0.5)
    expect(indicator.offWindow).toBeNull()
  })

  it('clamps a level above the window to the top and says which way it went', () => {
    // A scope that drops its indicator when the window is scrolled away is a scope whose trigger
    // cannot be found again -- and the level is still in force.
    const indicator = triggerIndicator(5, 2, -2)
    expect(indicator.levelV).toBe(2)
    expect(indicator.offWindow).toBe('above')
  })

  it('clamps a level below the window to the bottom', () => {
    const indicator = triggerIndicator(-5, 2, -2)
    expect(indicator.levelV).toBe(-2)
    expect(indicator.offWindow).toBe('below')
  })

  it('tolerates the window being given either way round', () => {
    expect(triggerIndicator(5, -2, 2)).toEqual(triggerIndicator(5, 2, -2))
  })

  it('draws no band when the hysteresis is zero, there being nothing to draw', () => {
    expect(triggerIndicator(0, 2, -2, 0).band).toBeNull()
  })

  it('draws the hysteresis band around the level', () => {
    // The one control whose effect is otherwise completely invisible.
    expect(triggerIndicator(0, 2, -2, 0.25).band).toEqual({ topV: 0.25, bottomV: -0.25 })
  })

  it('clips a band hanging off the window instead of suppressing it', () => {
    const band = triggerIndicator(1.9, 2, -2, 0.5).band
    expect(band).toEqual({ topV: 2, bottomV: 1.4 })
  })

  it('drops a band that is entirely off the window rather than drawing a hairline on the edge', () => {
    // Clamped, it would be a line on the axis claiming the level was there.
    expect(triggerIndicator(5, 2, -2, 0.1).band).toBeNull()
  })

  it('answers a non-finite level with the middle of the window and no claim about it', () => {
    const indicator = triggerIndicator(Number.NaN, 2, -2, 0.5)
    expect(indicator.levelV).toBe(0)
    expect(indicator.offWindow).toBeNull()
    expect(indicator.band).toBeNull()
  })
})

describe('fiftyPercentLevel', () => {
  it('is the midpoint of the extremes, not the mean', () => {
    // A square wave at a 10 % duty cycle has a mean near its base, and a trigger set there fires
    // on the noise around the base rather than on the edge.
    expect(fiftyPercentLevel(1, -1)).toBe(0)
    expect(fiftyPercentLevel(10, 0)).toBe(5)
  })

  it('is null when there is nothing measured to take it from', () => {
    expect(fiftyPercentLevel(Number.NaN, 0)).toBeNull()
  })
})

describe('suggestedHysteresis', () => {
  it('is a fiftieth of the peak-to-peak', () => {
    expect(suggestedHysteresis(1, -1)).toBeCloseTo(0.04, 12)
  })

  it('is null on a flat trace, which has no span to take a fraction of', () => {
    expect(suggestedHysteresis(1, 1)).toBeNull()
    expect(suggestedHysteresis(Number.NaN, -1)).toBeNull()
  })
})
