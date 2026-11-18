import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import {
  DEFAULT_SCOPE_TRIGGER,
  SCOPE_RUN_MODES,
  SCOPE_RUN_MODE_HINTS,
  SCOPE_PULSE_CONDITIONS,
  SCOPE_TRIGGER_KINDS,
  SCOPE_TRIGGER_SLOPE_HINTS,
  SCOPE_TRIGGER_KIND_HINTS,
  SCOPE_TRIGGER_SLOPES,
  SCOPE_TRIGGER_SOURCES,
  SCOPE_TRIGGER_SOURCE_HINTS,
  dropoutProblem,
  fiftyPercentLevel,
  pulseConditionFor,
  pulseWidthBounds,
  pulseWidthFor,
  pulseWidthProblem,
  stimulusSourceProblem,
  suggestedHysteresis,
  triggerChannel,
  triggerIsStimulus,
  triggerIndicator,
  triggerSourceProblem,
  windowIndicator,
  windowLevelsProblem,
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

  it('offers only the trigger sources the server implements', () => {
    // Math exists on the wire and is refused by name; a dropdown entry whose only outcome is a
    // refusal is worse than no entry.
    expect(SCOPE_TRIGGER_SOURCES.map((s) => s.value)).toEqual(['ChannelA', 'ChannelB', 'Stimulus'])
  })

  it('labels the sources as the channels a user sees, not as the enum', () => {
    expect(SCOPE_TRIGGER_SOURCES.map((s) => s.label)).toEqual(['Left', 'Right', 'Stimulus'])
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

describe('the trigger panel binds its voltages without v-model', () => {
  // A defineModel with a listener on it does not take the value assigned to it -- it emits, and the
  // value arrives as a prop on the parent's next render. ScopeConfigPanel assigns during *setup*,
  // restoring a measurement's saved settings, so its first render still draws the old value; and
  // vModelText writes the element from a *mounted* directive hook, which Vue defers to the
  // post-flush queue. The update carrying 0.25 is patched first and writes it into the element, and
  // the deferred mount hook then puts the first render's 0 straight back. So a saved trigger level
  // and hysteresis read zero on the first selection of a measurement, with the chart's trigger line
  // sitting correctly at 0.25 beside them (reported and reproduced 2026-11-17).
  //
  // vModelSelect carries an `updated` hook as well, and the two are deferred in order, so Mode and
  // Source were never affected -- which is why two of the six controls were wrong and four were not.
  //
  // Nothing here is reachable from a unit test: it is a .vue against a real DOM, and this suite runs
  // on `environment: 'node'`. So this reads the component as text, the way annotationNumbersDefault
  // and storedDisplayDefaults do.
  const source = readFileSync(new URL('../components/ScopeConfigPanel.vue', import.meta.url), 'utf8')

  const boxes = source.match(/<input\b[\s\S]*?\/>/g) ?? []
  const numbers = boxes.filter((box) => /type="number"/.test(box))

  it('has no v-model on a number box', () => {
    // A <select> is deliberately left alone: vModelSelect has the updated hook that saves it.
    expect(numbers.length).toBeGreaterThanOrEqual(5)
    expect(numbers.filter((box) => /v-model/.test(box))).toEqual([])
  })

  it('answers every number box with a change handler instead', () => {
    for (const box of numbers) {
      expect(box).toMatch(/@change=/)
      expect(box).toMatch(/:value=|\s:?value="/)
    }
  })
})

describe('the panel declares its models before anything reads them', () => {
  // A `const` is in the temporal dead zone until its own line runs, and two things in this panel's
  // setup read a model: applyStoredParams assigns one while restoring a measurement, and the dirty
  // watcher names one in its source. The accumulation pair was declared *below* both until
  // 2026-11-18, so selecting a measurement whose params_json carries an `accumulation` field --
  // every one a current build saves -- threw `ReferenceError: Cannot access 'accumulation' before
  // initialization`. Vue catches an error thrown inside a watcher, so nothing on screen said so,
  // and it cost two things at once: everything in applyStoredParams after those two lines (the
  // trigger level, its kind, its source, the second bound, the dropout and pulse-width settings,
  // the mode, the record length) and `restoring` left true for ever, which suppresses the dirty
  // watcher for the life of the panel.
  //
  // A .vue against a real DOM again, so this is a text read like the block above it.
  const source = readFileSync(new URL('../components/ScopeConfigPanel.vue', import.meta.url), 'utf8')

  // A declaration, not the prose that names one: this file's own doc comments write
  // <code>defineModel</code> twice, and a bare name would count both of those as models
  // declared at the very top -- which is to say the audit would pass whatever the order is.
  const models = [...source.matchAll(/\bconst \w+ = defineModel</g)].map((m) => m.index ?? -1)
  // The first top-level watcher, found at column 0 so a `watch(` inside a comment or nested in a
  // function cannot stand in for it.
  const firstWatcher = /^watch\(/m.exec(source)?.index ?? -1
  const readers = [source.indexOf('function applyStoredParams'), firstWatcher]

  it('finds the models and the things that read them', () => {
    // Guards the audit itself: a renamed function or a model spelled another way would otherwise
    // make every assertion below vacuously true.
    expect(models.length).toBeGreaterThanOrEqual(8)
    for (const reader of readers) expect(reader).toBeGreaterThan(0)
  })

  it('declares every model above applyStoredParams and above the first watcher', () => {
    expect(Math.max(...models)).toBeLessThan(Math.min(...readers))
  })
})

describe('the trigger kinds', () => {
  it('offers every kind the server implements', () => {
    expect(SCOPE_TRIGGER_KINDS.map((k) => k.value)).toEqual([
      'Edge',
      'PulseWidth',
      'Window',
      'Dropout',
      'Runt',
    ])
  })

  it('has a hint for every kind, and a slope hint too', () => {
    // The word "slope" means something different on every kind -- which stretch IS the pulse, enter
    // or leave, which direction counts as life -- so an empty one is a control that says nothing
    // about what it selects.
    for (const { value } of SCOPE_TRIGGER_KINDS) {
      expect(SCOPE_TRIGGER_KIND_HINTS[value].length).toBeGreaterThan(0)
      expect(SCOPE_TRIGGER_SLOPE_HINTS[value].length).toBeGreaterThan(0)
    }
  })

  it('names Edge first, which is what a scope does unless told otherwise', () => {
    expect(SCOPE_TRIGGER_KINDS[0].value).toBe('Edge')
  })
})

describe('the width condition', () => {
  it('maps shorter-than onto an upper bound alone', () => {
    expect(pulseWidthBounds('ShorterThan', 50e-6, 0)).toEqual({ loS: 0, hiS: 50e-6 })
  })

  it('maps longer-than onto a lower bound and no upper one at all', () => {
    // Null rather than a large number: a bound standing in for "no bound" would quietly stop firing
    // on the pulse that never ends, which is the fault this condition exists for.
    expect(pulseWidthBounds('LongerThan', 50e-6, 0)).toEqual({ loS: 50e-6, hiS: null })
  })

  it('maps between onto both', () => {
    expect(pulseWidthBounds('Between', 30e-6, 120e-6)).toEqual({ loS: 30e-6, hiS: 120e-6 })
  })

  it('reads a stored pair back as the condition it was set with', () => {
    // Or a measurement saved as "shorter than 50 us" reopens as "between 0 and 50", which is the
    // same capture described in a way nobody typed.
    expect(pulseConditionFor(0, 50e-6)).toBe('ShorterThan')
    expect(pulseConditionFor(50e-6, null)).toBe('LongerThan')
    expect(pulseConditionFor(30e-6, 120e-6)).toBe('Between')
  })

  it('reads an absent pair as longer-than, which is the only condition with no upper bound', () => {
    expect(pulseConditionFor(null, null)).toBe('LongerThan')
    expect(pulseConditionFor(undefined, undefined)).toBe('LongerThan')
  })

  it('round-trips every condition through the bounds and back', () => {
    for (const condition of SCOPE_PULSE_CONDITIONS.map((c) => c.value)) {
      const { loS, hiS } = pulseWidthBounds(condition, 30e-6, 120e-6)
      expect(pulseConditionFor(loS, hiS)).toBe(condition)
      expect(pulseWidthFor(condition, loS, hiS)).toBe(30e-6)
    }
  })

  it('refuses an upper bound narrower than one sample, which can never fire', () => {
    // Perfectly reasonable to type and arithmetically impossible: at 192 kS/s a sample arrives
    // every 5.2 us, so no pulse in any record is 2 us wide.
    const interval = 1 / 192_000
    expect(pulseWidthProblem('ShorterThan', 2e-6, 0, interval)).toMatch(/shorter than one sample/)
    expect(pulseWidthProblem('ShorterThan', 50e-6, 0, interval)).toBeNull()
  })

  it('does not refuse a lower bound longer than the record, which fires mid-pulse', () => {
    // The scanner is state over the stream rather than a search inside a window, so "longer than"
    // fires as soon as the width passes the bound whether or not the pulse fits in a record.
    expect(pulseWidthProblem('LongerThan', 10, 0, 1 / 192_000)).toBeNull()
  })

  it('refuses a range the wrong way round, and a width of nothing', () => {
    expect(pulseWidthProblem('Between', 120e-6, 30e-6, 1 / 192_000)).toMatch(/cannot be below/)
    expect(pulseWidthProblem('ShorterThan', 0, 0, 1 / 192_000)).toMatch(/positive time/)
  })
})

describe('the window trigger band', () => {
  it('draws the band between the two levels', () => {
    const { high, low, band } = windowIndicator(0.5, -0.5, 1, -1)

    expect(high.levelV).toBeCloseTo(0.5, 12)
    expect(low.levelV).toBeCloseTo(-0.5, 12)
    expect(band).toEqual({ topV: 0.5, bottomV: -0.5 })
  })

  it('sorts the pair, so typing them the other way round draws the same band', () => {
    // The scanner sorts them too: a picture that disagreed with the capture would be worse than no
    // picture.
    expect(windowIndicator(-0.5, 0.5, 1, -1)).toEqual(windowIndicator(0.5, -0.5, 1, -1))
  })

  it('clamps a level off the window to the edge and marks which way it lies', () => {
    const { high, low } = windowIndicator(4, -0.5, 1, -1)

    expect(high.levelV).toBe(1)
    expect(high.offWindow).toBe('above')
    expect(low.offWindow).toBeNull()
  })

  it('clips the band to the window rather than suppressing it', () => {
    // Half a band hanging off the top still says where the other half is.
    expect(windowIndicator(4, 0.25, 1, -1).band).toEqual({ topV: 1, bottomV: 0.25 })
  })

  it('draws no band at all when none of it is on screen', () => {
    // Clamping it would paint a hairline on the edge and claim the band was there.
    expect(windowIndicator(4, 3, 1, -1).band).toBeNull()
  })

  it('refuses two equal levels, which is no band rather than a narrow one', () => {
    expect(windowLevelsProblem(0.5, 0.5)).toMatch(/two different levels/)
    expect(windowLevelsProblem(0.5, -0.5)).toBeNull()
    expect(windowLevelsProblem(Number.NaN, -0.5)).toMatch(/voltages/)
  })
})

describe('the dropout timeout', () => {
  it('refuses a timeout of nothing', () => {
    // Zero fires on every sample on which no edge happened, which is almost all of them -- a
    // trigger that fires constantly while claiming to report an absence.
    expect(dropoutProblem(0)).toMatch(/above zero/)
    expect(dropoutProblem(-1)).toMatch(/above zero/)
    expect(dropoutProblem(Number.NaN)).toMatch(/above zero/)
    expect(dropoutProblem(0.02)).toBeNull()
  })
})

describe('the runt thresholds', () => {
  it('refuses two equal thresholds in its own words', () => {
    // The same check as the window's, and deliberately not the same sentence: a message about a
    // "window" on the runt screen sends a reader looking for a control that is not there.
    const runt = windowLevelsProblem(0.5, 0.5, 'Runt')
    expect(runt).toMatch(/runt/i)
    expect(runt).not.toMatch(/window/i)
    expect(windowLevelsProblem(0.5, -0.2, 'Runt')).toBeNull()
  })

  it('still says window when it is one', () => {
    expect(windowLevelsProblem(0.5, 0.5)).toMatch(/window/i)
  })
})

describe('the stimulus source', () => {
  it('is offered last, after the two that name a channel', () => {
    // It is the odd one: the others say which input to watch, this one watches nothing at all.
    expect(SCOPE_TRIGGER_SOURCES.map((s) => s.value)).toEqual(['ChannelA', 'ChannelB', 'Stimulus'])
    expect(triggerIsStimulus('Stimulus')).toBe(true)
    expect(triggerIsStimulus('ChannelA')).toBe(false)
  })

  it('has a hint for every source on the wire, Math included', () => {
    for (const source of ['ChannelA', 'ChannelB', 'Math', 'Stimulus'] as const)
      expect(SCOPE_TRIGGER_SOURCE_HINTS[source].length).toBeGreaterThan(0)
  })

  it('accepts the stimuli the generator panel can stage with an exact repeat', () => {
    // The chirp is the one with no feature an edge trigger could lock to, which is what the source
    // is for; the two waves buy an exact trigger point rather than an interpolated one.
    for (const kind of ['EchoChirp', 'Square', 'Triangle']) expect(stimulusSourceProblem(kind)).toBeNull()
  })

  it('refuses noise, because nothing here can stage a PERIODIC one', () => {
    // A periodic noise sequence does have a repeat and the server takes one, but the generator
    // endpoint's noise block carries a level and no period -- so noise staged from a screen is the
    // continuous kind. Accepting it here would be a client that accepts what the server refuses.
    expect(stimulusSourceProblem('PinkNoise')).toMatch(/repeat is exact/)
    expect(stimulusSourceProblem('WhiteNoise')).toMatch(/repeat is exact/)
  })

  it('refuses a plain tone, and says what to do instead', () => {
    // fs/f is a whole number of frames only for frequencies nobody types, and a tone has a
    // crossing to trigger on anyway -- which the message says, rather than leaving a reader
    // wondering why the obvious stimulus is the one that will not do.
    const problem = stimulusSourceProblem('Tones')
    expect(problem).toMatch(/repeat is exact/)
    expect(problem).toMatch(/crossing to trigger on/)
  })

  it('refuses a stimulus that is not staged at all', () => {
    expect(stimulusSourceProblem(null)).toMatch(/repeat is exact/)
    expect(stimulusSourceProblem(undefined)).toMatch(/repeat is exact/)
    expect(stimulusSourceProblem('Off')).toMatch(/repeat is exact/)
  })

  it('does not complain that it is watching an uncaptured channel', () => {
    // It watches no channel at all, so it has nothing to be missing -- and a refusal there would
    // be about a control the run does not read.
    expect(triggerSourceProblem('Stimulus', false, false)).toBeNull()
  })
})
