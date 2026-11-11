import { computed, ref } from 'vue'
import { api, type ClockDriftRequest, type ClockDriftStatus } from '../api/client'

/**
 * Drives the clock-drift test and holds its result.
 *
 * <p>A module-level store like the other runners', so the dialog can be closed and reopened over a
 * running test without losing it — the test takes a minute, and a user who closes the dialog to
 * look at something else should not have to start again.</p>
 *
 * <p>Polled rather than streamed: a reading lands about once a second, where the WebSocket frames
 * exist for things landing twenty times a second. The poll runs only while the test does, plus one
 * read when the dialog opens so it can re-attach to a test already in progress.</p>
 */
const POLL_MS = 400

const status = ref<ClockDriftStatus | null>(null)
const error = ref<string | null>(null)
const busy = ref(false)

let timer: ReturnType<typeof setTimeout> | null = null

function stopPolling() {
  if (timer) clearTimeout(timer)
  timer = null
}

async function poll() {
  try {
    status.value = await api.getClockDriftStatus()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
    stopPolling()
    return
  }

  // Keeps polling while the test runs, and stops of its own accord when it finishes -- so a test
  // that ends while the dialog is shut does not leave a timer running for the session's life.
  if (status.value?.running) timer = setTimeout(() => void poll(), POLL_MS)
  else stopPolling()
}

export function useClockDrift() {
  async function refresh() {
    stopPolling()
    await poll()
  }

  async function start(request: ClockDriftRequest) {
    busy.value = true
    error.value = null
    try {
      status.value = await api.startClockDrift(request)
      stopPolling()
      timer = setTimeout(() => void poll(), POLL_MS)
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    } finally {
      busy.value = false
    }
  }

  async function cancel() {
    busy.value = true
    try {
      status.value = await api.cancelClockDrift()
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    } finally {
      busy.value = false
      stopPolling()
    }
  }

  async function clear() {
    try {
      status.value = await api.clearClockDrift()
      error.value = null
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    }
  }

  return {
    status,
    error,
    busy,
    running: computed(() => status.value?.running === true),
    points: computed(() => status.value?.points ?? []),
    refresh,
    start,
    cancel,
    clear,
  }
}
