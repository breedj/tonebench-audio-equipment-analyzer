import { ref } from 'vue'
import { api, type SpectrumConfig, type SpectrumConfigRequest } from '../api/client'
import { useProjects } from './useProjects'

// Module-level (not inside the composable function) so every caller — SpectrumConfigPanel,
// SpectrumConfigPanel's L/R checkboxes, MetricsPanel/SystemMetricsPanel's Sys: tiles — shares one
// fetched config instead of drifting out of sync with independent copies. Same singleton-ref
// shape as useDevice.ts's module... except useDevice re-creates its refs per call; here we
// deliberately share one instance because multiple components edit the *same* server-side config.
const config = ref<SpectrumConfig | null>(null)
const error = ref<string | null>(null)
let loadPromise: Promise<void> | null = null

export function useSpectrumConfig() {
  async function load() {
    // Coalesce concurrent callers (several components mount in the same tick, each wanting the
    // config before it's fetched once) into the single in-flight request.
    if (loadPromise) return loadPromise

    loadPromise = (async () => {
      try {
        config.value = await api.getSpectrumConfig()
        error.value = null
      } catch (e) {
        error.value = (e as Error).message
      } finally {
        loadPromise = null
      }
    })()

    return loadPromise
  }

  async function apply(patch: SpectrumConfigRequest) {
    try {
      config.value = await api.setSpectrumConfig(patch)
      // Spectrum settings apply live and immediately (unlike generator/acquisition's stage-until-Run),
      // so a genuine edit here is also the moment the currently-open measurement type goes dirty.
      useProjects().markDirty()
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  if (config.value === null)
    load()

  return { config, error, load, apply }
}
