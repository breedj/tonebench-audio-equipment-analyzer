import { ref } from 'vue'

// Module-level singleton — one amplifier load shared by both channels' power figures, edited once
// in GeneratorPanel rather than twice (previously each of MetricsPanel's L/R instances kept its
// own local 8-ohm default, which could silently drift apart).
/**
 * The load a measurement assumes when nothing has said otherwise -- 8 ohm, the nominal of most
 * loudspeakers and therefore of most amplifier datasheets. Exported because every screen that
 * restores a stored load has to fall back to the same figure: a measurement saved before the FFT
 * screen stored one at all (FftSpectrumParameters.LoadOhms) must read as the default rather than
 * as whatever the previous measurement left in this singleton.
 */
export const DEFAULT_LOAD_OHMS = 8

const loadOhms = ref(DEFAULT_LOAD_OHMS)

export function useLoadOhms() {
  return { loadOhms }
}
