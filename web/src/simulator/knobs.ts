import type { DeviceStatus } from '../api/client'
import {
  CROSSTALK_KIND,
  FFT_SPECTRUM_KIND,
  FREQUENCY_RESPONSE_KIND,
  IMD_VS_AMPLITUDE_KIND,
  IMPEDANCE_KIND,
  IMPULSE_RESPONSE_KIND,
  MEASUREMENT_KINDS,
  OUTPUT_IMPEDANCE_KIND,
  STEPPED_RESPONSE_KIND,
  THD_VS_AMPLITUDE_KIND,
  THD_VS_FREQUENCY_KIND,
} from '../measurements/kinds'

/**
 * Which of the simulator's status-bar knobs the open measurement has any use for. The bar used to
 * show all of them on every screen, which is clutter and — worse — a false offer: a Harmonics box
 * that cannot change a chirp-driven reading is a control that answers nothing.
 *
 * Two tiers, and the second one is the load-bearing half.
 *
 * **Inert — hidden whatever it says.** The knob provably cannot change what this measurement reads.
 * Only Harmonics is ever in this tier: the stack is added to each generator *tone*, and
 * `SignalSynthesizer` applies it on the `GeneratorKind.Tones` path alone — `EchoChirp`, the two
 * noise kinds, `Multitone`, `Square` and `Triangle` get no stack at all. So the four kinds that
 * read harmonic content are the four that may show it, and on a stepped sine (response, crosstalk,
 * output impedance) the stack is synthesized but every one of those reads the tone bin alone, so it
 * changes no reported number.
 *
 * **Not the subject — hidden while at its no-effect value, shown the moment it is engaged.**
 * Crosstalk and the Zout/Load pair. `SimulatedOutputStage` is applied to channel 0 whenever it is
 * not unity, i.e. on *every* left-channel measurement and not only the load-step one — so hiding an
 * engaged pair would leave the divider in the path with nothing on screen naming it, which is a
 * worse fault than the clutter this function exists to remove. Same for an engaged Crosstalk, which
 * is a real ~−70 dB contaminant in every two-channel ratio.
 *
 * Note what is deliberately *not* here: the DUT model. The one knob whose applicability depends on
 * it is Harmonics under the four self-distorting models, where `SimulatedDevice` forces the stack
 * off whatever the profile says — and that stays the status bar's existing dim-with-a-tooltip,
 * because "off because this DUT distorts on its own" is worth reading. No knob is hidden on DUT
 * grounds.
 */
export type SimulatorKnob = 'noise' | 'hum' | 'harmonics' | 'crosstalk' | 'outputStage' | 'load'

/** The four kinds whose reading can carry the generator's harmonic stack. */
const HARMONICS_ARE_READ: readonly string[] = [
  FFT_SPECTRUM_KIND,
  THD_VS_FREQUENCY_KIND,
  THD_VS_AMPLITUDE_KIND,
  IMD_VS_AMPLITUDE_KIND,
]

/**
 * The kinds that read both inputs, where channel coupling lands in the result: every two-channel
 * ratio, plus the FFT screen, which draws both spectra side by side and is where a −70 dB coupling
 * line is something you go to look at. The three per-channel distortion sweeps are left out — they
 * read one channel's tone and its harmonics, and the leak is the same tone again.
 */
const CROSSTALK_IS_READ: readonly string[] = [
  FFT_SPECTRUM_KIND,
  FREQUENCY_RESPONSE_KIND,
  STEPPED_RESPONSE_KIND,
  IMPULSE_RESPONSE_KIND,
  IMPEDANCE_KIND,
  CROSSTALK_KIND,
  OUTPUT_IMPEDANCE_KIND,
]

/** Every knob shown: no measurement open, an unrecognised kind, or the bar's own reveal button. */
export function allSimulatorKnobsVisible(): Record<SimulatorKnob, boolean> {
  return { noise: true, hum: true, harmonics: true, crosstalk: true, outputStage: true, load: true }
}

/**
 * Which knobs to show for `kind` under the simulator state in `status`. A complete record rather
 * than a predicate per knob, so a knob cannot be left out of the table and silently default to
 * hidden; `knobs.test.ts` pins one row per measurement kind.
 *
 * No kind — the welcome screen, the trash view — and any kind this build does not know show
 * everything: nothing is established about what applies, and the safe answer is the old behaviour.
 */
export function visibleSimulatorKnobs(
  kind: string | null | undefined,
  status: DeviceStatus | null | undefined,
): Record<SimulatorKnob, boolean> {
  if (!kind || !MEASUREMENT_KINDS.some((k) => k.kind === kind)) return allSimulatorKnobsVisible()

  // Either field, not both: the stage is unity until Zout *and* Load are both set, but showing the
  // pair only then makes "Zout: 2 Ω + 0.3 mH, Load: Open" hide the very box needed to finish the
  // setup. They are shown and hidden together for the same reason.
  const stageEngaged =
    (status?.simulatedOutputStage ?? 'None') !== 'None' || (status?.simulatedLoad ?? 'Open') !== 'Open'
  const stage = kind === OUTPUT_IMPEDANCE_KIND || stageEngaged

  return {
    // Hum and noise are added to both channels after the DUT, the output stage and the crosstalk, so
    // they set the floor, the SNR and THD+N figures and the coherence of every run there is. They
    // apply everywhere, and are spelled out here rather than left out of the record so that changing
    // that is a one-line edit with no template change.
    noise: true,
    hum: true,
    harmonics: HARMONICS_ARE_READ.includes(kind),
    crosstalk: CROSSTALK_IS_READ.includes(kind) || (status?.simulatedCrosstalkEnabled ?? false),
    outputStage: stage,
    load: stage,
  }
}
