import { reactive, ref } from 'vue'
import type { GeneratorKind, GeneratorTone, MultitoneSettings, WhiteNoiseSettings, EchoChirpSettings, SmoothingOctaves } from '../api/client'

// Module-level singleton (same pattern as useLoadOhms.ts / useSpectrumConfig.ts): the *staged*
// generator form — what's typed into GeneratorChannelPanel, not what DeviceStatus reports. Left
// and right always play the same stimulus (see GeneratorPanel.vue), so one shared config is
// correct, and unlike DeviceStatus.generatorLeft/Right this never gets silenced by Stop (see
// deviceControl.ts's stopRun) — GeneratorMetricsPanel.vue reads it directly for exactly that
// reason: the Gen summary tile must survive Stop instead of blanking out with the live status.
const kind = ref<GeneratorKind>('Tones')
const tones = reactive<GeneratorTone[]>([{ freqHz: 1000, ampDbv: -6 }])
const multitone = reactive<MultitoneSettings>({ tonesPerOctave: 5, totalRmsDbv: -50, rolloffDbPerOctave: 0 })
const noise = reactive<WhiteNoiseSettings>({ levelDbv: -20 })
const chirp = reactive<EchoChirpSettings>({
  amplitudeDbv: -20,
  startFreqHz: 20,
  endFreqHz: 20000,
  smoothing: 'None' as SmoothingOctaves,
})

export function useGeneratorStimulus() {
  return { kind, tones, multitone, noise, chirp }
}
