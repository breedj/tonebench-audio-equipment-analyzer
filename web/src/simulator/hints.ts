import type { InjectionKey, Ref } from 'vue'
import type { DeviceStatus, SimulatedDutModel } from '../api/client'
import {
  CROSSTALK_KIND,
  FREQUENCY_RESPONSE_KIND,
  IMD_VS_AMPLITUDE_KIND,
  IMPEDANCE_KIND,
  IMPULSE_RESPONSE_KIND,
  OUTPUT_IMPEDANCE_KIND,
  STEPPED_RESPONSE_KIND,
  THD_VS_AMPLITUDE_KIND,
  THD_VS_FREQUENCY_KIND,
} from '../measurements/kinds'

/**
 * What the simulator's status-bar knobs have to say for a given measurement to produce anything
 * worth looking at — and the line the measurement screen shows when they do not.
 *
 * The simulator has seventeen DUT models and four toggles, and which of them matter is different
 * for every measurement: an IMD sweep into the "Tube amp" model draws nothing, because that model
 * is a linear filter and only four models distort at all; an impedance run into an amplifier model
 * inverts a divider that is not there; a crosstalk sweep reads a −200 dB floor until the Crosstalk
 * box is on. None of that is visible from the screen that stays empty, and it cost a session on
 * 2026-09-21. So each kind names its precondition here, as a pure function with tests, and the
 * layout shows the answer above the chart — in simulated mode only, gone the moment the setup fits.
 */

/** The models with a nonlinearity of their own — `SimulatedDut.SuppliesOwnDistortion` server-side. */
export const DISTORTING_DUTS: readonly SimulatedDutModel[] = [
  'TriodeSingleEnded',
  'ClassAbCrossover',
  'SolidStateClipping',
  'TransformerCoreSaturation',
]

/** The fixtures for the impedance screen: a reference resistor with a load, on the right input. */
export const IMPEDANCE_FIXTURES: readonly SimulatedDutModel[] = [
  'ImpedanceResistive',
  'ImpedanceReactive',
  'ImpedanceLoudspeaker',
  'ImpedanceCapacitive',
  'ImpedanceShort',
  'ImpedanceOpen',
]

const DISTORTING_LIST = 'SE triode, Class AB (crossover), Solid state (clips at −3 dBV) or Output transformer (saturating)'

const DUT_LABELS: Partial<Record<SimulatedDutModel, string>> = {
  StraightWire: 'Straight wire',
  TubeAmplifier: 'Tube amp',
  Loudspeaker: 'Loudspeaker',
  BandpassFilter: 'Bandpass',
  MarginallyStable: 'Marginally stable',
  Oscillating: 'Oscillating',
  PhonoRiaa: 'Phono stage (RIAA)',
  DigitalProcessor: 'DSP processor (12 ms)',
  TriodeSingleEnded: 'SE triode',
  ClassAbCrossover: 'Class AB (crossover)',
  SolidStateClipping: 'Solid state (clips at −3 dBV)',
  TransformerCoreSaturation: 'Output transformer (saturating)',
  ImpedanceResistive: 'Fixture: 8 Ω',
  ImpedanceReactive: 'Fixture: 6 Ω + 1 mH',
  ImpedanceCapacitive: 'Fixture: 47 µF',
  ImpedanceLoudspeaker: 'Fixture: loudspeaker',
  ImpedanceShort: 'Fixture: short (leads)',
  ImpedanceOpen: 'Fixture: open',
}

function label(model: SimulatedDutModel): string {
  return DUT_LABELS[model] ?? model
}

/**
 * The hint for `kind` under the simulator state in `status`, or null when there is nothing to say —
 * including whenever the analyzer is not the simulator, since a real bench is the user's own.
 */
export function simulatorHint(kind: string | null | undefined, status: DeviceStatus | null | undefined): string | null {
  if (!kind || !status || !status.isSimulated) return null
  const dut = status.simulatedDut
  if (!dut) return null
  const isFixture = IMPEDANCE_FIXTURES.includes(dut)
  const distorts = DISTORTING_DUTS.includes(dut)

  switch (kind) {
    case THD_VS_FREQUENCY_KIND:
    case THD_VS_AMPLITUDE_KIND:
      if (distorts) return null
      if (status.simulatedHarmonicsEnabled === false) {
        return `The simulated "${label(dut)}" does not distort and the generator's Harmonics stack is off, so every point reads 0 % THD and nothing is drawn. Turn on Harmonics in the status bar for a fixed 0.51 % stack, or pick a distorting model: ${DISTORTING_LIST}.`
      }
      return `The simulated "${label(dut)}" does not distort; what this sweep reads is the generator's fixed Harmonics stack, a flat 0.51 % at every point. For a level- or frequency-dependent curve pick a distorting model: ${DISTORTING_LIST}.`

    case IMD_VS_AMPLITUDE_KIND:
      if (distorts) return null
      return `The simulated "${label(dut)}" is a linear model and produces no intermodulation — the Harmonics box adds harmonics of each tone, not the sidebands this measures — so every point reads 0 % and nothing is drawn. Pick a distorting model: ${DISTORTING_LIST}.`

    case IMPEDANCE_KIND:
      if (isFixture) return null
      return `The simulated "${label(dut)}" is a DUT on the left input, not an impedance fixture: inverting the divider that is not there reads as a meaningless (often negative) impedance. Pick one of the Fixture: models in the status bar — 8 Ω, 6 Ω + 1 mH, 47 µF or loudspeaker; short and open are the calibration standards.`

    case CROSSTALK_KIND:
      if (status.simulatedCrosstalkEnabled === false) {
        return 'The simulator’s channels are perfectly isolated until Crosstalk is on in the status bar — every point reads the −200 dB floor. Turn it on for the modelled coupling (−70 dB floor, rising above 6 kHz).'
      }
      return null

    case OUTPUT_IMPEDANCE_KIND:
      if (status.simulatedOutputStage === 'None') {
        return 'Zout is None in the status bar, so the simulated amplifier is an ideal source and the measurement reads 0 Ω. Pick an output stage under Zout (Solid state 0.03 Ω + 1.5 µH, or Tube amp 2 Ω + 0.3 mH), set Load to the first load, and switch it to the second at the CONTINUE pause.'
      }
      if (status.simulatedLoad === 'Open') {
        return 'Load is Open in the status bar: the first pass needs a load on the simulated amplifier. Set Load to 8 Ω (or whichever Load 1 the settings name), and switch it to Load 2 at the CONTINUE pause.'
      }
      return null

    case FREQUENCY_RESPONSE_KIND:
    case STEPPED_RESPONSE_KIND:
    case IMPULSE_RESPONSE_KIND:
      if (isFixture) {
        return `The simulated "${label(dut)}" is an impedance fixture on the right input; the response measurements read the left. Pick a DUT model — Tube amp, Loudspeaker, Phono stage (RIAA) or DSP processor each have a response worth measuring.`
      }
      if (dut === 'StraightWire') {
        return 'The simulated DUT is a straight wire: flat, unity and delay-free, so every curve here is a straight line. Tube amp, Loudspeaker, Phono stage (RIAA) or DSP processor (12 ms) each have a response worth measuring.'
      }
      return null

    default:
      return null
  }
}

/**
 * How the hint learns the device status: App.vue owns the one polled `useDevice()` instance (the
 * composable is per call, not a singleton), and provides it under this key for
 * `SimulatorHint.vue`, which sits inside every `MeasurementLayout`.
 */
export const DEVICE_STATUS_KEY: InjectionKey<Readonly<Ref<DeviceStatus | null>>> = Symbol('deviceStatus')
