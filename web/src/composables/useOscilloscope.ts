import { computed, ref, shallowRef } from 'vue'
import {
  api,
  type OscilloscopeStartRequest,
  type OscilloscopeStatus,
  type ResponsePhaseName,
  type ScopeRecordResponse,
} from '../api/client'
import type { ScopeTimeWindow } from '../scope/timeAxis'

/**
 * The oscilloscope screen's state, as a module-level singleton so a run survives navigating between
 * measurements — the arrangement every other runner's composable uses, and what the server's own
 * <code>SwitchOwner</code> parking is the other half of.
 *
 * <p><b>Driven by polling, and by a fetch per record.</b> The status endpoint carries the phase and
 * the record counter; the record itself is fetched separately, because it is by far the largest
 * thing here and because the <em>window</em> decides what comes back — a tight window is answered
 * with the waveform rather than with a summary of it, which is what makes a rise time believable.
 * The binary frames the wire format defines are what this becomes when the broadcast hub learns to
 * send them; the shape of this composable does not change when it does, since both answer the same
 * question ("the slice for this window") and the client holds only the view either way.</p>
 */

/**
 * How often the status is polled while a run is going.
 *
 * <p>Faster than the other runners' 250 ms, because here the poll is also what notices a new record:
 * an Auto run at a 20 ms record can commit one several times a second, and a scope whose trace
 * updates four times a second reads as a scope that is struggling.</p>
 */
const POLL_INTERVAL_MS = 120

const status = shallowRef<OscilloscopeStatus | null>(null)

/**
 * The slice on screen. <code>shallowRef</code> deliberately: it holds two arrays of up to a few
 * thousand floats per channel and is replaced wholesale on every fetch, so making it deeply
 * reactive would walk all of them several times a second for nothing.
 */
const record = shallowRef<ScopeRecordResponse | null>(null)

/** Why a start was refused, or a fetch failed. Cleared by the next successful one. */
const error = ref<string | null>(null)

/**
 * The window the fetch asks for, or null for the whole record.
 *
 * <p>Held here rather than in the chart because it is what a fetch is keyed on: the chart's drag,
 * the display panel's boxes and a Reset zoom all write it, and whichever wrote last is what the next
 * record is cut to. One value with several writers, the arrangement the zoom bar will need.</p>
 */
const window = shallowRef<ScopeTimeWindow | null>(null)

/** How many columns the fetch asks for — the chart's own width, so a column is about a pixel. */
const maxColumns = ref(1600)

/**
 * Which fetch is the current one.
 *
 * <p>Dragging a window queues several and they do not come back in order; without this the slice on
 * screen is whichever <em>finished</em> last rather than whichever was <em>asked for</em> last, so
 * a drag settles on a window the user has already dragged past. The decay surfaces' own token, for
 * the same reason.</p>
 */
let fetchToken = 0

/** The record counter the slice on screen was fetched for, so an unchanged record is not re-fetched. */
let fetchedFor = -1

let pollHandle: ReturnType<typeof setInterval> | null = null

function stopPolling() {
  if (pollHandle !== null) {
    clearInterval(pollHandle)
    pollHandle = null
  }
}

/** True while the runner is armed, filling or rolling — the phases that are not an end state. */
function isRunning(next: OscilloscopeStatus | null): boolean {
  return next?.phase === 'WaitingForTrigger' || next?.phase === 'Measuring'
}

async function fetchRecord(force = false): Promise<void> {
  const taken = status.value?.recordsTaken ?? -1
  if (!force && taken === fetchedFor) return

  const token = ++fetchToken
  try {
    const next = await api.getOscilloscopeRecord(window.value ?? undefined, maxColumns.value)
    if (token !== fetchToken) return
    record.value = next
    fetchedFor = taken
    error.value = null
  } catch (e) {
    if (token !== fetchToken) return
    error.value = e instanceof Error ? e.message : String(e)
  }
}

async function refresh(): Promise<void> {
  const next = await api.getOscilloscopeStatus()
  status.value = next
  if (next.hasRecord) await fetchRecord()
  else record.value = null
  if (!isRunning(next)) stopPolling()
}

function startPolling() {
  if (pollHandle !== null) return
  pollHandle = setInterval(() => {
    void refresh().catch(() => {
      // A dropped poll is not worth surfacing: the next one either succeeds or the run has ended
      // and the button state comes back from the following refresh.
    })
  }, POLL_INTERVAL_MS)
}

/** Forgets the slice on screen so the next poll fetches a fresh one whatever the counter says. */
function invalidateRecord() {
  fetchToken++
  fetchedFor = -1
}

export function useOscilloscope() {
  async function start(request: OscilloscopeStartRequest): Promise<void> {
    error.value = null
    invalidateRecord()
    try {
      await api.startOscilloscope(request)
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
      throw e
    }
    // Poll at once so the phase changes on the click rather than a tenth of a second later.
    await refresh()
    startPolling()
  }

  /**
   * Hands the running capture new settings, in place.
   *
   * <p>No <code>invalidateRecord</code> and no clearing: the record on screen is the last thing
   * that was actually captured, and a level moved past the signal is precisely when it has to stay
   * there — in Normal nothing will trigger again, and a blank chart would read as the scope having
   * broken rather than as the trigger having been set where nothing crosses it.</p>
   */
  async function retune(request: OscilloscopeStartRequest): Promise<void> {
    status.value = await api.retuneOscilloscope(request)
    error.value = null
  }

  async function cancel(): Promise<void> {
    status.value = await api.cancelOscilloscope()
    stopPolling()
    // A cancelled run keeps whatever it caught, which is the half of a stopped capture worth
    // reading -- so the slice is re-fetched rather than dropped.
    await fetchRecord(true)
  }

  async function clear(): Promise<void> {
    invalidateRecord()
    record.value = null
    status.value = await api.clearOscilloscope()
  }

  /**
   * Hands the runner the measurement now on screen: the run it was holding is parked under the
   * measurement that made it, and whatever the arriving one had parked comes back. Posted on arrival
   * at this screen only — see <code>OscilloscopeRunner.SwitchOwner</code>.
   */
  async function switchOwner(owner: string | null): Promise<void> {
    invalidateRecord()
    record.value = null
    status.value = await api.setOscilloscopeOwner(owner)
    if (status.value.hasRecord) await fetchRecord(true)
  }

  /** Asks for a different slice of the same record — a zoom, a reset, or a resized chart. */
  async function setWindow(next: ScopeTimeWindow | null, columns?: number): Promise<void> {
    window.value = next
    if (columns !== undefined && columns > 0) maxColumns.value = Math.round(columns)
    await fetchRecord(true)
  }

  const running = computed(() => isRunning(status.value))
  const phase = computed<ResponsePhaseName>(() => status.value?.phase ?? 'Idle')
  const hasRecord = computed(() => record.value !== null)

  /**
   * The record's own span, which is what a window is clamped against and what the zoom strip's axis
   * will be. Null before anything is captured — the chart then draws the axis the <em>settings</em>
   * ask for, since an empty scope still has to show where the next capture will land.
   */
  const recordBounds = computed<ScopeTimeWindow | null>(() => {
    const current = record.value
    if (!current || !(current.toS > current.fromS)) return null
    return { fromS: current.fromS, toS: current.toS }
  })

  /**
   * The one line the measurement status bar shows, in the shape every other runner's
   * <code>progressLabel</code> has so that <code>statusHeadline</code>/<code>statusDetail</code>
   * split it the same way.
   *
   * <p>Armed says so rather than counting, because there is nothing to count: the scope is watching
   * and may watch for ever. Once records are coming the count is the only thing that moves, and it
   * is what tells a Normal run that is finding its trigger from one that is not.</p>
   */
  const progressLabel = computed<string>(() => {
    const s = status.value
    if (!s || s.phase === 'Idle') return ''
    if (s.phase === 'WaitingForTrigger') return 'Waiting for trigger'
    if (s.phase === 'Measuring') {
      return s.recordsTaken > 0 ? `Capturing · record ${s.recordsTaken}` : 'Capturing'
    }
    if (s.phase === 'Finished') {
      const count = s.recordsTaken > 1 ? ` · ${s.recordsTaken} records` : ''
      return `Finished${count}`
    }
    return s.phase
  })

  return {
    status,
    record,
    error,
    window,
    running,
    phase,
    hasRecord,
    recordBounds,
    progressLabel,
    recordsTaken: computed(() => status.value?.recordsTaken ?? 0),
    /** The record on hand spans a dropped chunk or block — two non-adjacent pieces side by side. */
    spliced: computed(() => status.value?.spliced ?? false),
    /** Auto's timeout took it; nothing crossed the level, so the trace is aligned to nothing. */
    forced: computed(() => status.value?.forced ?? false),
    sampleRateHz: computed(() => status.value?.sampleRateHz ?? 0),
    /** Why the run stopped, as the runner put it — distinct from `error`, which belongs to this composable's own requests. */
    runError: computed(() => status.value?.error ?? null),
    refresh,
    fetchRecord,
    setWindow,
    start,
    retune,
    cancel,
    clear,
    switchOwner,
  }
}
