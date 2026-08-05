import { onMounted, onUnmounted, ref } from 'vue'
import { api, type DeviceRanges, type DeviceStatus } from '../api/client'

const POLL_INTERVAL_MS = 2000

export function useDevice() {
  const status = ref<DeviceStatus | null>(null)
  const ranges = ref<DeviceRanges | null>(null)
  const error = ref<string | null>(null)
  let pollHandle: ReturnType<typeof setInterval> | undefined

  async function refreshStatus() {
    try {
      status.value = await api.getDevice()
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
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
