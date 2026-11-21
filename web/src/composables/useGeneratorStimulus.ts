import { reactive, ref } from 'vue'
import type { ArbitraryChannel, DcLevelSettings, GeneratorKind, GeneratorTone, ImdMethodName, MultitoneSettings, PeriodicWaveSettings, ToneBurstSettings, WhiteNoiseSettings, EchoChirpSettings, SmoothingOctaves } from '../api/client'
import type { ImdPresetId } from '../imd/presets'
import type { GeneratorLevelUnit } from '../generatorLevel'

/**
 * One screen's *staged* generator form — what is typed into GeneratorChannelPanel, not what
 * DeviceStatus reports. Left and right always play the same stimulus (see GeneratorPanel.vue), so
 * one shared config is correct, and unlike DeviceStatus.generatorLeft/Right this never gets
 * silenced by Stop (see deviceControl.ts's stopRun) — GeneratorMetricsPanel.vue reads it directly
 * for exactly that reason: the Gen summary tile must survive Stop instead of blanking out with the
 * live status.
 *
 * <p><b>A factory, with one instance per screen that composes a stimulus.</b> It was a module-level
 * singleton while the FFT screen was the only such screen. The oscilloscope is the second, and it
 * cannot share: the two screens stage different stimuli for different measurements, and a
 * measurement switch between them would otherwise carry one screen's tone onto the other's. The
 * FFT screen's instance is still a singleton underneath (`useGeneratorStimulus`), so everything
 * that reads it — the Gen tile, stimulusSummary, noiseFigureCaveat — is untouched.</p>
 */
/**
 * The one tone a fresh stimulus plays, and what **Clear** beside the IMD preset puts back.
 *
 * <p>Exported rather than written twice, because the second copy is the one that drifts: a Clear
 * that answered 1 kHz at a level the panel does not otherwise start on would be a different
 * default wearing the same name.</p>
 */
export const DEFAULT_TONE: Readonly<GeneratorTone> = Object.freeze({ freqHz: 1000, ampDbv: -6 })

export function createGeneratorStimulus() {
  const kind = ref<GeneratorKind>('Tones')
  const tones = reactive<GeneratorTone[]>([{ ...DEFAULT_TONE }])
  const multitone = reactive<MultitoneSettings>({ tonesPerOctave: 5, totalRmsDbv: -50, rolloffDbPerOctave: 0 })
  const noise = reactive<WhiteNoiseSettings>({ levelDbv: -20 })
  // All four hard-switched shapes share one staged block, as they share one settings record
  // server-side: the shape is `kind`, so switching between them changes neither the pitch nor the
  // level -- which is what makes the dropdown a comparison rather than four separate setups. The
  // duty is read by the pulse alone and carried by the rest.
  const wave = reactive<PeriodicWaveSettings>({ freqHz: 1000, levelDbv: -20, dutyPercent: 50 })
  // The burst gets its own block rather than sharing the shapes': its level means the carrier while
  // it plays, and its two cycle counts have no counterpart there.
  const burst = reactive<ToneBurstSettings>({ freqHz: 1000, levelDbv: -20, cyclesOn: 5, cyclesOff: 20 })
  // In volts and signed, because a DC level has a sign and dBV cannot carry one. It starts at zero,
  // which is the DAC at rest -- a stimulus nobody asked for should do nothing.
  const dc = reactive<DcLevelSettings>({ volts: 0 })
  // The three settings only, not the readings: what the file turned out to be is a reading of the
  // loaded table, which the server has and the panel takes from device status after a Run. Staging
  // a copy of it would be a second answer to drift from the first.
  const arbitrary = reactive<{ path: string | null; channel: ArbitraryChannel; levelDbv: number }>({
    path: null,
    channel: 'Left',
    levelDbv: -20,
  })
  const chirp = reactive<EchoChirpSettings>({
    amplitudeDbv: -20,
    startFreqHz: 20,
    endFreqHz: 20000,
    smoothing: 'None' as SmoothingOctaves,
  })

  // The unit the level boxes are typed in -- display only, converted at the input's edge (see
  // generatorLevel.ts). Staged and saved with the tones for the same reason the IMD template is:
  // a measurement whose levels are quoted in mV should still read in mV when it is selected again.
  const levelUnit = ref<GeneratorLevelUnit>('Dbv')

  // The two-tone IMD template and analysis choice that go with the tone list — see
  // GeneratorChannelPanel.vue's "IMD preset" and imd/presets.ts. Staged like the tones (saved with
  // the measurement type, restored by App.vue's loadedParams watcher); the analysis itself reaches
  // the pipeline through spectrum-config whenever exactly two sine tones are staged.
  const imd = reactive<{ preset: ImdPresetId; method: ImdMethodName; maxOrder: number }>({
    preset: 'Custom',
    method: 'Smpte',
    maxOrder: 5,
  })

  return { kind, tones, multitone, noise, wave, burst, dc, arbitrary, chirp, imd, levelUnit }
}

/** What one screen's staged generator form looks like, for a panel that is handed one. */
export type GeneratorStimulusStore = ReturnType<typeof createGeneratorStimulus>

/** The FFT screen's own, which is also what a panel that is handed nothing uses. */
const shared = createGeneratorStimulus()

export function useGeneratorStimulus(): GeneratorStimulusStore {
  return shared
}
