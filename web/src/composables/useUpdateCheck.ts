import { ref, watch } from 'vue'
import { api } from '../api/client'
import { readStoredPreference, writeStoredPreference } from '../browserStorage'

// Module-level singleton (same pattern as useCaptureDelay.ts/useAppTheme.ts) — App.vue's banner
// and ConfigDialog's opt-out checkbox must agree on one value. A pure client preference (whether
// to phone GitHub at all), so it lives in localStorage rather than any server-side setting —
// docs/tonebench-stack-spec.md §8 calls for this to be opt-out for air-gapped lab machines.

const ENABLED_KEY = 'tonebench.updateCheckEnabled'
const DISMISSED_KEY = 'tonebench.updateDismissedVersion'
const RECHECK_INTERVAL_MS = 6 * 60 * 60 * 1000

function loadEnabled(): boolean {
  return readStoredPreference(ENABLED_KEY) !== 'false'
}

const updateCheckEnabled = ref<boolean>(loadEnabled())
const dismissedVersion = ref<string | null>(readStoredPreference(DISMISSED_KEY))
const latestVersion = ref<string | null>(null)
const latestUrl = ref<string | null>(null)
const updateAvailable = ref(false)

watch(updateCheckEnabled, (enabled) => {
  writeStoredPreference(ENABLED_KEY, String(enabled))
})

watch(dismissedVersion, (version) => {
  if (version) writeStoredPreference(DISMISSED_KEY, version)
})

let pollHandle: ReturnType<typeof setInterval> | undefined

async function check() {
  if (!updateCheckEnabled.value) return
  try {
    const status = await api.getUpdateStatus()
    latestVersion.value = status.latestVersion
    latestUrl.value = status.latestUrl
    updateAvailable.value = status.updateAvailable
  } catch {
    // Offline bench machine must never surface an error over this — same silent-failure
    // contract as the server-side UpdateCheckService.
  }
}

export function useUpdateCheck() {
  if (!pollHandle) {
    check()
    pollHandle = setInterval(check, RECHECK_INTERVAL_MS)
  }

  function dismiss() {
    if (latestVersion.value) dismissedVersion.value = latestVersion.value
  }

  return { updateCheckEnabled, updateAvailable, latestVersion, latestUrl, dismissedVersion, dismiss }
}
