import { ref } from 'vue'

// Module-level singleton — one amplifier load shared by both channels' power figures, edited once
// in GeneratorPanel rather than twice (previously each of MetricsPanel's L/R instances kept its
// own local 8-ohm default, which could silently drift apart).
const loadOhms = ref(8)

export function useLoadOhms() {
  return { loadOhms }
}
