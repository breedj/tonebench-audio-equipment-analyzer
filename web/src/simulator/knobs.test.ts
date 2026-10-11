import { describe, expect, it } from 'vitest'
import type { DeviceStatus } from '../api/client'
import { MEASUREMENT_KINDS } from '../measurements/kinds'
import { allSimulatorKnobsVisible, type SimulatorKnob, visibleSimulatorKnobs } from './knobs'

function status(over: Partial<DeviceStatus> = {}): DeviceStatus {
  return {
    isSimulated: true,
    deviceKind: 'Simulated',
    simulatedDut: 'TubeAmplifier',
    simulatedNoiseEnabled: true,
    simulatedHumEnabled: false,
    simulatedHarmonicsEnabled: true,
    simulatedCrosstalkEnabled: false,
    simulatedOutputStage: 'None',
    simulatedLoad: 'Open',
    ...over,
  } as DeviceStatus
}

const ALL_KNOBS = Object.keys(allSimulatorKnobsVisible()) as SimulatorKnob[]

function shown(kind: string | null, over: Partial<DeviceStatus> = {}): SimulatorKnob[] {
  const knobs = visibleSimulatorKnobs(kind, status(over))
  return ALL_KNOBS.filter((knob) => knobs[knob])
}

/**
 * One row per measurement kind, at the knobs' own default values — the matrix `knobs.ts` describes,
 * as data. Harmonics on the four kinds that read harmonic content; crosstalk on the FFT screen and
 * every two-channel ratio; the Zout/Load pair only where it is the subject.
 */
const EXPECTED: Record<string, SimulatorKnob[]> = {
  FFTSpectrum: ['noise', 'hum', 'harmonics', 'crosstalk'],
  ThdVsFrequency: ['noise', 'hum', 'harmonics'],
  ThdVsAmplitude: ['noise', 'hum', 'harmonics'],
  ImdVsAmplitude: ['noise', 'hum', 'harmonics'],
  FrequencyResponse: ['noise', 'hum', 'crosstalk'],
  SteppedResponse: ['noise', 'hum', 'crosstalk'],
  ImpulseResponse: ['noise', 'hum', 'crosstalk'],
  Impedance: ['noise', 'hum', 'crosstalk'],
  Crosstalk: ['noise', 'hum', 'crosstalk'],
  OutputImpedance: ['noise', 'hum', 'crosstalk', 'outputStage', 'load'],
}

describe('visibleSimulatorKnobs', () => {
  it('shows each kind exactly the knobs it reads', () => {
    for (const [kind, expected] of Object.entries(EXPECTED)) {
      expect(shown(kind), kind).toEqual(expected)
    }
  })

  it('has a row for every kind in the picker, and no row for a kind that is not one', () => {
    // The kinds.test.ts precedent: the thing that breaks silently is a kind added to
    // MEASUREMENT_KINDS and left out of the table, which would fall through to a partial answer.
    expect(MEASUREMENT_KINDS.map((k) => k.kind).sort()).toEqual(Object.keys(EXPECTED).sort())
  })

  it('never hides an engaged Crosstalk box, on any kind', () => {
    // The box is a real ~-70 dB contaminant wherever it is on, so a kind that does not read coupling
    // may hide it only while it is off.
    for (const kind of Object.keys(EXPECTED)) {
      expect(shown(kind, { simulatedCrosstalkEnabled: true }), kind).toContain('crosstalk')
    }
  })

  it('never hides an engaged Zout/Load pair, on any kind', () => {
    // The output stage divides channel 0 on every left-channel measurement, not only the load-step
    // one. Hiding it there would leave the divider in the path with nothing on screen naming it.
    for (const kind of Object.keys(EXPECTED)) {
      const engaged = shown(kind, { simulatedOutputStage: 'TubeAmp', simulatedLoad: 'EightOhms' })
      expect(engaged, kind).toContain('outputStage')
      expect(engaged, kind).toContain('load')
    }
  })

  it('shows the pair on either field, so a half-set stage can still be finished', () => {
    // Unity needs both knobs at their defaults, but a pair shown only when *both* are set makes
    // "Zout: 2 ohm + 0.3 mH, Load: Open" hide the box needed to finish the setup.
    const halfSet = shown('FFTSpectrum', { simulatedOutputStage: 'TubeAmp', simulatedLoad: 'Open' })
    expect(halfSet).toContain('outputStage')
    expect(halfSet).toContain('load')

    const loadOnly = shown('FFTSpectrum', { simulatedOutputStage: 'None', simulatedLoad: 'EightOhms' })
    expect(loadOnly).toContain('outputStage')
    expect(loadOnly).toContain('load')
  })

  it('shows the pair together or not at all', () => {
    for (const kind of Object.keys(EXPECTED)) {
      const knobs = visibleSimulatorKnobs(kind, status())
      expect(knobs.outputStage, kind).toBe(knobs.load)
    }
  })

  it('shows Noise and Hum on every kind, whatever they are set to', () => {
    for (const kind of Object.keys(EXPECTED)) {
      const knobs = visibleSimulatorKnobs(kind, status({ simulatedNoiseEnabled: false, simulatedHumEnabled: false }))
      expect(knobs.noise, kind).toBe(true)
      expect(knobs.hum, kind).toBe(true)
    }
  })

  it('shows everything with no measurement open, or for a kind it does not know', () => {
    expect(shown(null)).toEqual(ALL_KNOBS)
    expect(shown('SomethingLater')).toEqual(ALL_KNOBS)
    expect(visibleSimulatorKnobs('FFTSpectrum', null)).toEqual({
      ...allSimulatorKnobsVisible(),
      outputStage: false,
      load: false,
    })
  })
})
