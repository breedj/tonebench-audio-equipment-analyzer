import { computed, ref, shallowRef } from 'vue'
import {
  api,
  type FrequencyResponseTraceJson,
  type HarmonicCurveJson,
  type ImpulseReferenceCaptureRequest,
  type ImpulseResponseStartRequest,
  type ImpulseResponseStatus,
  type ImpulseTraceJson,
  type LoopbackReferenceInfo,
  type ResponsePhaseName,
} from '../api/client'

/**
 * The impulse-response screen's state, as a module-level singleton so the run survives navigating
 * between measurements — the same arrangement as `useFrequencyResponse`.
 *
 * <b>Driven by polling rather than by the WebSocket.</b> Every other measurement streams because it
 * has something to show while it runs: a sweep gains a point every second, a chirp response gains a
 * whole pass. An impulse-response run produces nothing until its records are folded and analysed, so
 * there is no partial result to push — what the UI needs during a run is the phase and the record
 * counter, which the status endpoint already carries. Polling stops the moment the run does.
 */

/** How often the status is polled while a run is going. Fast enough that the phase label tracks, slow enough to be invisible. */
const POLL_INTERVAL_MS = 250

const status = shallowRef<ImpulseResponseStatus | null>(null)
const references = ref<LoopbackReferenceInfo[]>([])
const capturingReference = ref(false)
const error = ref<string | null>(null)

let pollHandle: ReturnType<typeof setInterval> | null = null

function stopPolling() {
  if (pollHandle !== null) {
    clearInterval(pollHandle)
    pollHandle = null
  }
}

async function refresh(): Promise<void> {
  const next = await api.getImpulseResponseStatus()
  status.value = next
  if (!next.running) stopPolling()
}

function startPolling() {
  if (pollHandle !== null) return
  pollHandle = setInterval(() => {
    void refresh().catch(() => {
      // A dropped poll is not worth surfacing: the next one either succeeds or the run has ended and
      // the button state comes back from the following refresh.
    })
  }, POLL_INTERVAL_MS)
}

export function useImpulseResponse() {
  async function start(request: ImpulseResponseStartRequest): Promise<void> {
    error.value = null
    await api.startImpulseResponse(request)
    // Poll immediately so the phase label changes on the click rather than a quarter second later.
    await refresh()
    startPolling()
  }

  async function cancel(): Promise<void> {
    status.value = await api.cancelImpulseResponse()
    stopPolling()
  }

  async function clear(): Promise<void> {
    status.value = await api.clearImpulseResponse()
  }

  async function refreshReferences(): Promise<void> {
    references.value = (await api.listLoopbackReferences()).references
  }

  /**
   * Captures a loopback calibration, which blocks for the whole run — several records at minimum,
   * because a calibration divided into every later measurement has to be quieter than they are.
   */
  async function captureReference(request: ImpulseReferenceCaptureRequest): Promise<LoopbackReferenceInfo> {
    capturingReference.value = true
    try {
      const info = await api.captureLoopbackReference(request)
      await refreshReferences()
      return info
    } finally {
      capturingReference.value = false
      // The capture runs through the same runner, so its terminal phase is what the screen shows.
      await refresh().catch(() => undefined)
    }
  }

  async function deleteReference(id: number): Promise<void> {
    await api.deleteLoopbackReference(id)
    await refreshReferences()
  }

  const running = computed(() => status.value?.running ?? false)
  const phase = computed<ResponsePhaseName>(() => status.value?.phase ?? 'Idle')
  const traces = computed<FrequencyResponseTraceJson[]>(() => status.value?.traces ?? [])
  const impulses = computed<ImpulseTraceJson[]>(() => status.value?.impulses ?? [])
  const harmonics = computed<HarmonicCurveJson[]>(() => status.value?.harmonics ?? [])

  /** The left channel's impulse — the DUT's own. The gain one only exists with a measured reference. */
  const impulse = computed<ImpulseTraceJson | null>(
    () => impulses.value.find((i) => i.kind === 'Left') ?? impulses.value[0] ?? null,
  )

  const hasResult = computed(() => impulses.value.length > 0)

  /**
   * What the gate's length means for the gated magnitude: below this the curve describes the window
   * rather than the DUT. Read off the *status* rather than the config form, so it always describes
   * the data on screen and not whatever the panel has been changed to since.
   */
  const gateResolutionHz = computed(() => status.value?.gateResolutionHz ?? 0)

  return {
    status,
    running,
    phase,
    traces,
    impulses,
    impulse,
    harmonics,
    hasResult,
    gateResolutionHz,
    references,
    capturingReference,
    error,
    refresh,
    start,
    cancel,
    clear,
    refreshReferences,
    captureReference,
    deleteReference,
  }
}
