import { describe, expect, it } from 'vitest'
import {
  activeLevel,
  applyStoredLevel,
  createLevelForm,
  DEFAULT_LEVEL_BY_MODE,
  isPowerMode,
  isPowerUnit,
  LEVEL_MODES,
  levelByModeOf,
  levelInvalid,
  levelTargetVolt,
  levelUnitLabel,
  levelUnitsFor,
  levelWatts,
  POWER_LEVEL_UNITS,
  setLevelUnit,
  setLevelValue,
  solvesGeneratorLevel,
  VOLTAGE_LEVEL_UNITS,
} from './levelMode'

describe('the unit lists', () => {
  it('offers watts and milliwatts for a power level, and volts for the other two modes', () => {
    expect(levelUnitsFor('OutputPower')).toBe(POWER_LEVEL_UNITS)
    expect(levelUnitsFor('GeneratorVoltage')).toBe(VOLTAGE_LEVEL_UNITS)
    expect(levelUnitsFor('OutputVoltage')).toBe(VOLTAGE_LEVEL_UNITS)
  })

  it('keeps the two lists disjoint, so a unit names exactly one kind of level', () => {
    for (const unit of VOLTAGE_LEVEL_UNITS) expect(isPowerUnit(unit.value)).toBe(false)
    for (const unit of POWER_LEVEL_UNITS) expect(isPowerUnit(unit.value)).toBe(true)
  })

  it('writes a unit the way a caption does', () => {
    expect(levelUnitLabel('Dbv')).toBe('dBV')
    expect(levelUnitLabel('MilliVolt')).toBe('mV')
    expect(levelUnitLabel('Watt')).toBe('W')
    expect(levelUnitLabel('MilliWatt')).toBe('mW')
  })

  it('agrees with the mode predicates', () => {
    expect(LEVEL_MODES.map((m) => m.value)).toEqual(['GeneratorVoltage', 'OutputVoltage', 'OutputPower'])
    expect(isPowerMode('OutputPower')).toBe(true)
    expect(isPowerMode('OutputVoltage')).toBe(false)
    expect(solvesGeneratorLevel('GeneratorVoltage')).toBe(false)
    expect(solvesGeneratorLevel('OutputVoltage')).toBe(true)
    expect(solvesGeneratorLevel('OutputPower')).toBe(true)
  })

  it('has a slot for every mode, each in its own kind of unit', () => {
    for (const mode of LEVEL_MODES) {
      const form = createLevelForm({ mode: mode.value })
      const { unit } = activeLevel(form)
      expect(levelUnitsFor(mode.value).some((u) => u.value === unit)).toBe(true)
    }
  })
})

describe('per-mode levels', () => {
  // The report, word for word: a -20 dBV generator level became "-20 W" on switching to the power
  // mode, and power cannot be negative.
  it('does not carry a generator voltage over into the power mode', () => {
    const form = createLevelForm({ mode: 'GeneratorVoltage' })
    setLevelValue(form, -20)

    form.mode = 'OutputPower'

    expect(activeLevel(form)).toEqual({ value: 1, unit: 'Watt' })
    expect(levelInvalid(form, 8)).toBeNull()
  })

  it('brings each mode back to what it was last set to', () => {
    const form = createLevelForm({ mode: 'GeneratorVoltage' })
    setLevelValue(form, -12)
    form.mode = 'OutputVoltage'
    setLevelValue(form, 2.5)
    setLevelUnit(form, 'Volt')
    form.mode = 'OutputPower'
    setLevelValue(form, 250)
    setLevelUnit(form, 'MilliWatt')

    form.mode = 'GeneratorVoltage'
    expect(activeLevel(form)).toEqual({ value: -12, unit: 'Dbv' })
    form.mode = 'OutputVoltage'
    expect(activeLevel(form)).toEqual({ value: 2.5, unit: 'Volt' })
    form.mode = 'OutputPower'
    expect(activeLevel(form)).toEqual({ value: 250, unit: 'MilliWatt' })
  })

  it('refuses a unit the selected mode does not offer, rather than storing it', () => {
    const form = createLevelForm({ mode: 'GeneratorVoltage' })
    setLevelUnit(form, 'Watt')
    expect(activeLevel(form).unit).toBe('Dbv')

    form.mode = 'OutputPower'
    setLevelUnit(form, 'Dbv')
    expect(activeLevel(form).unit).toBe('Watt')
  })

  it('reports the three remembered levels for params_json', () => {
    const form = createLevelForm({ mode: 'OutputPower' })
    setLevelValue(form, 5)
    expect(levelByModeOf(form)).toEqual({ ...DEFAULT_LEVEL_BY_MODE, outputPowerValue: 5 })
  })
})

describe('applyStoredLevel', () => {
  it('reloads each mode from the stored memory', () => {
    const form = createLevelForm({ mode: 'GeneratorVoltage' })

    applyStoredLevel(form, {
      levelMode: 'OutputPower',
      levelValue: 100,
      levelUnit: 'MilliWatt',
      levelByMode: {
        generatorValue: -30,
        generatorUnit: 'Dbv',
        outputVoltageValue: 1.5,
        outputVoltageUnit: 'Volt',
        outputPowerValue: 100,
        outputPowerUnit: 'MilliWatt',
      },
    })

    expect(form.mode).toBe('OutputPower')
    expect(activeLevel(form)).toEqual({ value: 100, unit: 'MilliWatt' })
    form.mode = 'GeneratorVoltage'
    expect(activeLevel(form)).toEqual({ value: -30, unit: 'Dbv' })
    form.mode = 'OutputVoltage'
    expect(activeLevel(form)).toEqual({ value: 1.5, unit: 'Volt' })
  })

  // A measurement saved before this existed has only the active pair, and it must come back
  // unchanged -- with the other two modes at their defaults rather than at its figure.
  it('seeds the selected mode from a stored row that has no per-mode memory', () => {
    const form = createLevelForm({ mode: 'GeneratorVoltage' })

    applyStoredLevel(form, { levelMode: 'OutputVoltage', levelValue: 350, levelUnit: 'MilliVolt' })

    expect(activeLevel(form)).toEqual({ value: 350, unit: 'MilliVolt' })
    form.mode = 'OutputPower'
    expect(activeLevel(form)).toEqual({ value: 1, unit: 'Watt' })
  })

  // The active pair is what the run used, so it wins over a memory that disagrees with it -- the
  // two can only disagree if something wrote one of them without the other.
  it('overlays the active pair onto the selected mode', () => {
    const form = createLevelForm({ mode: 'GeneratorVoltage' })

    applyStoredLevel(form, {
      levelMode: 'OutputPower',
      levelValue: 2,
      levelUnit: 'Watt',
      levelByMode: { outputPowerValue: 999, outputPowerUnit: 'MilliWatt' },
    })

    expect(activeLevel(form)).toEqual({ value: 2, unit: 'Watt' })
  })

  it('ignores a stored shape that holds none of this, rather than writing undefined over the defaults', () => {
    const form = createLevelForm({ mode: 'OutputVoltage', outputVoltageValue: 0 })

    // An FFT measurement's params_json parses perfectly well and carries none of these fields.
    applyStoredLevel(form, JSON.parse('{"fftSize":65536,"window":"Hann"}'))

    expect(form).toEqual(createLevelForm({ mode: 'OutputVoltage', outputVoltageValue: 0 }))
  })

  it('drops a stored unit that does not belong to its slot', () => {
    const form = createLevelForm({ mode: 'GeneratorVoltage' })

    applyStoredLevel(form, { levelByMode: { generatorUnit: 'Watt', outputPowerUnit: 'Dbv' } })

    expect(form.generatorUnit).toBe('Dbv')
    expect(form.outputPowerUnit).toBe('Watt')
  })
})

describe('the power arithmetic', () => {
  it('reads a milliwatt target as the watts it is', () => {
    const form = createLevelForm({ mode: 'OutputPower', outputPowerValue: 250, outputPowerUnit: 'MilliWatt' })
    expect(levelWatts(form)).toBeCloseTo(0.25, 12)
    // V = sqrt(P.R): 250 mW into 32 ohm is 2.828 V.
    expect(levelTargetVolt(form, 32)).toBeCloseTo(2.8284271, 6)
  })

  it('reads a watt target the same way', () => {
    const form = createLevelForm({ mode: 'OutputPower', outputPowerValue: 1, outputPowerUnit: 'Watt' })
    expect(levelTargetVolt(form, 8)).toBeCloseTo(2.8284271, 6)
  })

  it('has no watts for a voltage mode', () => {
    const form = createLevelForm({ mode: 'OutputVoltage', outputVoltageValue: 2, outputVoltageUnit: 'Volt' })
    expect(levelWatts(form)).toBe(0)
    expect(levelTargetVolt(form, 8)).toBe(0)
  })
})

describe('levelInvalid', () => {
  it('names the unit the box is actually showing', () => {
    const form = createLevelForm({ mode: 'OutputPower', outputPowerValue: 0, outputPowerUnit: 'MilliWatt' })
    expect(levelInvalid(form, 8)).toBe('Output power must be above 0 mW.')

    setLevelUnit(form, 'Watt')
    expect(levelInvalid(form, 8)).toBe('Output power must be above 0 W.')
  })

  it('wants a load behind a power target', () => {
    const form = createLevelForm({ mode: 'OutputPower' })
    expect(levelInvalid(form, 0)).toBe('A target power needs a load above 0 Ω.')
    expect(levelInvalid(form, 8)).toBeNull()
  })

  it('allows a negative dBV and refuses a negative volt', () => {
    const form = createLevelForm({ mode: 'GeneratorVoltage', generatorValue: -20 })
    expect(levelInvalid(form, 0)).toBeNull()

    setLevelUnit(form, 'Volt')
    expect(levelInvalid(form, 0)).toBe('A level in V must be above 0.')
    setLevelValue(form, 0.5)
    expect(levelInvalid(form, 0)).toBeNull()
  })

  it('refuses an empty box', () => {
    const form = createLevelForm({ mode: 'GeneratorVoltage' })
    setLevelValue(form, Number.NaN)
    expect(levelInvalid(form, 8)).toBe('The level must be a number.')
  })
})
