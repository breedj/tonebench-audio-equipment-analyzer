import { describe, expect, it } from 'vitest'
import type { DeviceStatus, SimulatedDutModel } from '../api/client'
import { DISTORTING_DUTS, IMPEDANCE_FIXTURES, simulatorHint } from './hints'

function status(over: Partial<DeviceStatus> = {}): DeviceStatus {
  return {
    isSimulated: true,
    simulatedDut: 'TubeAmplifier',
    simulatedHarmonicsEnabled: true,
    simulatedCrosstalkEnabled: false,
    simulatedOutputStage: 'None',
    simulatedLoad: 'Open',
    ...over,
  } as DeviceStatus
}

const ALL_MODELS: SimulatedDutModel[] = [
  'StraightWire', 'TubeAmplifier', 'Loudspeaker', 'BandpassFilter', 'MarginallyStable', 'Oscillating',
  'PhonoRiaa', 'DigitalProcessor', 'TriodeSingleEnded', 'ClassAbCrossover', 'SolidStateClipping',
  'TransformerCoreSaturation', 'ImpedanceResistive', 'ImpedanceReactive', 'ImpedanceLoudspeaker',
  'ImpedanceCapacitive', 'ImpedanceShort', 'ImpedanceOpen',
  'ButterworthLowPass', 'ChebyshevLowPass', 'BesselLowPass', 'EllipticLowPass',
]

describe('simulatorHint', () => {
  it('says nothing on a real analyzer, or with no measurement open', () => {
    expect(simulatorHint('ImdVsAmplitude', status({ isSimulated: false, simulatedDut: null }))).toBeNull()
    expect(simulatorHint(null, status())).toBeNull()
    expect(simulatorHint('FFTSpectrum', status())).toBeNull()
  })

  it('tells an IMD sweep that a linear model has no intermodulation, whatever the Harmonics box says', () => {
    // The 2026-09-21 case: "Tube amp" with Harmonics on drew nothing, and reads as a fault.
    const hint = simulatorHint('ImdVsAmplitude', status({ simulatedDut: 'TubeAmplifier', simulatedHarmonicsEnabled: true }))
    expect(hint).toMatch(/"Tube amp" is a linear model/)
    expect(hint).toMatch(/SE triode/)
    for (const model of DISTORTING_DUTS) expect(simulatorHint('ImdVsAmplitude', status({ simulatedDut: model }))).toBeNull()
  })

  it('tells a THD sweep the difference between a flat stack and no stack at all', () => {
    expect(simulatorHint('ThdVsFrequency', status({ simulatedHarmonicsEnabled: false }))).toMatch(/0 % THD/)
    expect(simulatorHint('ThdVsAmplitude', status({ simulatedHarmonicsEnabled: true }))).toMatch(/flat 0\.51 %/)
    expect(simulatorHint('ThdVsFrequency', status({ simulatedDut: 'ClassAbCrossover', simulatedHarmonicsEnabled: false }))).toBeNull()
  })

  it('sends the impedance screen to the fixtures and the response screens away from them', () => {
    expect(simulatorHint('Impedance', status({ simulatedDut: 'TubeAmplifier' }))).toMatch(/Fixture:/)
    for (const model of IMPEDANCE_FIXTURES) {
      expect(simulatorHint('Impedance', status({ simulatedDut: model }))).toBeNull()
      expect(simulatorHint('FrequencyResponse', status({ simulatedDut: model }))).toMatch(/impedance fixture/)
    }
    expect(simulatorHint('SteppedResponse', status({ simulatedDut: 'StraightWire' }))).toMatch(/straight wire/)
    expect(simulatorHint('ImpulseResponse', status({ simulatedDut: 'Loudspeaker' }))).toBeNull()
  })

  it('asks for the crosstalk box and the output stage knobs', () => {
    expect(simulatorHint('Crosstalk', status({ simulatedCrosstalkEnabled: false }))).toMatch(/Crosstalk is on/)
    expect(simulatorHint('Crosstalk', status({ simulatedCrosstalkEnabled: true }))).toBeNull()
    expect(simulatorHint('OutputImpedance', status({ simulatedOutputStage: 'None' }))).toMatch(/Zout is None/)
    expect(simulatorHint('OutputImpedance', status({ simulatedOutputStage: 'TubeAmp', simulatedLoad: 'Open' }))).toMatch(/Load is Open/)
    expect(simulatorHint('OutputImpedance', status({ simulatedOutputStage: 'TubeAmp', simulatedLoad: 'EightOhms' }))).toBeNull()
  })

  it('has an answer for every model on every kind, never an exception', () => {
    const kinds = ['ThdVsFrequency', 'ThdVsAmplitude', 'ImdVsAmplitude', 'FrequencyResponse', 'SteppedResponse',
      'ImpulseResponse', 'Impedance', 'Crosstalk', 'OutputImpedance', 'FFTSpectrum']
    for (const kind of kinds) {
      for (const model of ALL_MODELS) {
        const hint = simulatorHint(kind, status({ simulatedDut: model }))
        expect(hint === null || typeof hint === 'string').toBe(true)
      }
    }
  })
})
