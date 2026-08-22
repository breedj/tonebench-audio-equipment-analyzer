import { onMounted, onUnmounted, ref } from 'vue'
import { api, type DeviceRanges, type DeviceStatus } from '../api/client'

const POLL_INTERVAL_MS = 2000

export function useDevice() {
  const status = ref<DeviceStatus | null>(null)
  const ranges = ref<DeviceRanges | null>(null)
  const error = ref<string | null>(null)
  let pollHandle: ReturnType<typeof setInterval> | undefined

  // Whether the message currently in `error` is this poll's own. The 2 s poll shares one error line
  // with every user action in App.vue, and clearing it unconditionally on success wiped whatever the
  // last click had reported within two seconds — long enough to see only if you were looking. Most
  // visibly with the status bar's Connect button, whose whole output is that one line. A poll now
  // only clears what a poll put there; action errors stay until the next action clears them.
  let pollFailed = false

  async function refreshStatus() {
    try {
      status.value = await api.getDevice()
      if (pollFailed) {
        error.value = null
        pollFailed = false
      }
    } catch (e) {
      error.value = (e as Error).message
      pollFailed = true
    }
  }

  async function refreshRanges() {
    ranges.value = await api.getDeviceRanges()
  }

  onMounted(async () => {
    await Promise.all([refreshStatus(), refreshRanges()])
    pollHandle = setInterval(refreshStatus, POLL_INTERVAL_MS)
  })

  onUnmounted(() => {
    if (pollHandle) clearInterval(pollHandle)
  })

  return { status, ranges, error, refreshStatus }
}
