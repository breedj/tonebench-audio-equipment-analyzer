import { ref } from 'vue'

/**
 * Module-level singleton (same pattern as useLoadOhms.ts / useGeneratorStimulus.ts): the FFT
 * screen's *staged* sample rate — what SpectrumConfigPanel's dropdown currently shows, which is
 * what Run would ask for, not what the device happens to be streaming at.
 *
 * It is shared rather than passed as a prop because the two panels that care sit side by side under
 * App.vue with no reactive value between them: App.vue reads the rate through
 * `spectrumConfigPanel.getSampleRateHz()`, a method on a child ref, which nothing can watch.
 * GeneratorPanel needs to *react* to it, to stand down at a rate this analyzer cannot play (see
 * generatorRateCeiling.ts).
 *
 * Only the FFT screen writes it, and the Generator panel is only rendered there, so a value left
 * behind by a different measurement kind is never read; SpectrumConfigPanel re-stages it from
 * `loadedParams` on every selection either way.
 */
const stagedSampleRateHz = ref(192_000)

export function useStagedSampleRate() {
  return { stagedSampleRateHz }
}
