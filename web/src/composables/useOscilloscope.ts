import { computed, ref, shallowRef } from 'vue'
import {
  api,
  type OscilloscopeStartRequest,
  type OscilloscopeStatus,
  type ResponsePhaseName,
  type ScopeRecordResponse,
} from '../api/client'
import {
  captureRates as deriveCaptureRates,
  pushRateSample,
  type ScopeCaptureRates,
  type ScopeRateSample,
} from '../scope/captureRates'
import type { CursorGate } from '../scope/cursors'
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

/**
 * The <b>whole</b> record, for the zoom bar to draw.
 *
 * <p>Kept beside the drawn slice rather than derived from it, because a zoom is answered with the
 * window asked for and that answer <em>replaces</em> what the client holds — so anything wanting
 * the measurement rather than the view is left with the slice. That is
 * <code>spectrumOverview</code>'s own lesson one screen along, and here it is load-bearing rather
 * than a nicety: the strip cannot show where a box sits in a record it does not have.</p>
 *
 * <p>Re-fetched only when a new record is committed, never when the window moves — which is what
 * keeps a drag at one request per window instead of two. While the chart is showing the whole
 * record the two are the same fetch and this is simply that answer.</p>
 */
const overview = shallowRef<ScopeRecordResponse | null>(null)

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
 * The cursor window the measurements are gated to, or null for the whole drawn window.
 *
 * <p>Here beside the display window rather than with the cursors themselves, because it is the
 * other thing a fetch is keyed on: it rides on the <em>same</em> request as the slice, so the
 * figures and the picture they qualify can never come from two different records. Moving a cursor
 * therefore re-fetches, which is also the point — gating client-side over min/max columns would be
 * a reading of the decimation rather than a measurement.</p>
 */
const gate = shallowRef<CursorGate | null>(null)

/**
 * Whether the fetch asks for the residual too.
 *
 * <p>Here rather than read from the display rail directly, for the gate's reason: it is a third
 * thing a fetch is keyed on, and it rides on the same request as the picture so the residual on
 * screen is always of the record under it.</p>
 */
const wantResidual = ref(false)

/**
 * What the fetch asks the two math channels for — one token each, or null for off.
 *
 * <p>The fourth thing a fetch is keyed on, and here for the gate's and the residual's reason: a
 * derived trace rides on the <em>same</em> request as the waveforms it was derived from, so the
 * math traces on screen are always of the record under them. Off is null rather than the string,
 * so the commonest state asks for nothing at all.</p>
 */
const wantMaths = shallowRef<readonly (string | null)[]>([null, null])

/**
 * Whether the fetch asks for the X–Y figure.
 *
 * <p>The fifth thing a fetch is keyed on, and the one that costs the most: it is the only slice
 * that cannot be summarised, so it carries sample pairs rather than columns. Asked for only by the
 * view that draws it.</p>
 */
const wantXy = ref(false)

/**
 * Whether the fetch asks for the eye diagram.
 *
 * <p>The costliest of the lot: folds of samples rather than one trace. Asked for only by the view
 * that draws it, like the figure above.</p>
 */
const wantEye = ref(false)

/**
 * The display path the fetch asks for, as query parameters.
 *
 * <p>The sixth thing a fetch is keyed on, and the one that changes the <em>trace</em> rather than
 * adding to it: the analyzer applies it to the whole record before slicing, so the picture and
 * every figure beside it are of the same processed waveform. See scope/signalPath.ts.</p>
 */
const wantPath = shallowRef<Record<string, string>>({})

/**
 * How many real samples either side of the window the fetch asks for.
 *
 * <p>Context for the sin(x)/x reconstruction's ends, and nothing else reads it — see
 * <code>ScopeChannelSlice.leadSamples</code>. Zero for every other drawing mode, so their fetches
 * are byte-identical to what they were before this existed, which is the point of asking for it
 * rather than always sending it.</p>
 */
const wantMargin = ref(0)

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

/** The same for the overview, which only has to follow the record and not the window. */
let overviewFetchedFor = -1

/**
 * Distinct records that have reached `record.value`, i.e. the chart and its persistence buffer.
 *
 * Counted here rather than taken from the server, which cannot see it: this is the third of the
 * three capture rates, and the one that decides how fast an intensity-graded picture builds.
 */
let recordsDrawn = 0

let rateSamples: ScopeRateSample[] = []

/** The three capture rates over the last couple of seconds, or null while there is nothing to divide by. */
const captureRateReadings = shallowRef<ScopeCaptureRates | null>(null)

function resetCaptureRates() {
  recordsDrawn = 0
  rateSamples = []
  captureRateReadings.value = null
}

let pollHandle: ReturnType<typeof setInterval> | null = null

function stopPolling() {
  // Cleared here rather than left to age out, so the badge vanishes on STOP instead of freezing a
  // rate nothing is measuring any more -- a stale number on an idle screen reads exactly like a
  // live one, which is the fault the drop counters already cost a bench session over.
  resetCaptureRates()

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

  // Decided BEFORE the early return, and the whole reason this is not just `!force`. Eight callers
  // pass force -- setWindow, setGate, setPath, setMath, setXy, setEye, cancel, switchOwner -- and
  // every one of them assigns record.value again with no new capture behind it. Counting those
  // would make dragging the trigger level inflate the drawn rate, so the diagnostic would report
  // the opposite of the truth precisely while somebody was watching it.
  const isNewRecord = taken !== fetchedFor
  if (!force && !isNewRecord) return

  const token = ++fetchToken
  try {
    const next = await api.getOscilloscopeRecord(
      window.value ?? undefined,
      maxColumns.value,
      gate.value ?? undefined,
      wantResidual.value,
      wantMaths.value,
      wantXy.value,
      wantEye.value,
      wantPath.value,
      wantMargin.value,
    )
    if (token !== fetchToken) return
    record.value = next
    if (isNewRecord) recordsDrawn++
    fetchedFor = taken

    // The whole record for the strip. The windowed fetch above *is* the whole record when nothing
    // is zoomed, so that is the common case and it costs nothing; a zoomed one needs its own, and
    // only once per committed record.
    if (window.value === null) {
      overview.value = next
      overviewFetchedFor = taken
    } else if (taken !== overviewFetchedFor || overview.value === null) {
      // No gate and no residual: the strip draws the waveform and nothing else, and the residual
      // is a least-squares sine fit per channel that nothing here would read.
      const whole = await api.getOscilloscopeRecord(undefined, maxColumns.value)
      if (token !== fetchToken) return
      overview.value = whole
      overviewFetchedFor = taken
    }

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
  else {
    record.value = null
    overview.value = null
  }

  // After the fetch above, so this sample's drawn count includes the record this very poll
  // collected. performance.now() rather than Date.now() -- monotonic -- and stamped when the
  // status arrived rather than when setInterval fired: the interval can overlap an async callback,
  // and timing off the tick would make a slow poll read high.
  rateSamples = pushRateSample(rateSamples, {
    atMs: performance.now(),
    committed: next.recordsCommitted ?? 0,
    collected: next.recordsTaken,
    drawn: recordsDrawn,
  })
  captureRateReadings.value = deriveCaptureRates(rateSamples)

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
  overviewFetchedFor = -1

  // Here rather than in start/clear/switchOwner one by one: all three already funnel through this,
  // it already means "forget what is on screen", and the server restarts all three counters at the
  // same three moments. A fourth caller therefore cannot forget it.
  resetCaptureRates()
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
    overview.value = null
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
    overview.value = null
    status.value = await api.setOscilloscopeOwner(owner)
    if (status.value.hasRecord) await fetchRecord(true)
  }

  /**
   * Gates the measurements to a window of the record — the cursor pair — or to nothing.
   *
   * <p>Re-fetches at once rather than waiting for the next poll, because a stopped scope has no
   * next poll and dragging a cursor on a held record is the commonest way this is used.</p>
   */
  async function setGate(next: CursorGate | null): Promise<void> {
    const before = gate.value
    if (before?.fromS === next?.fromS && before?.toS === next?.toS) return
    gate.value = next
    await fetchRecord(true)
  }

  /** Turns the residual trace on or off, and re-fetches so it arrives with the record it is of. */
  async function setResidual(on: boolean): Promise<void> {
    if (wantResidual.value === on) return
    wantResidual.value = on
    await fetchRecord(true)
  }

  /**
   * Picks what the two math channels compute, and re-fetches so the derived traces arrive with the
   * record they were derived from.
   *
   * <p>Both at once rather than one call per channel: they ride on one request, so two calls would
   * be two fetches of the same record, and the second would be of a <em>later</em> record on a
   * running scope — two math traces drawn against one waveform but not against each other.</p>
   *
   * <p>Re-fetches at once for the gate's reason: a stopped scope has no next poll, and changing an
   * operation on a held record is the commonest way this is used.</p>
   */
  async function setMaths(specs: readonly (string | null)[]): Promise<void> {
    const next = [specs[0] || null, specs[1] || null]
    if (next[0] === wantMaths.value[0] && next[1] === wantMaths.value[1]) return
    wantMaths.value = next
    await fetchRecord(true)
  }

  /**
   * Turns the X–Y figure on or off, and re-fetches so it arrives with the record it is of.
   *
   * <p>Switching <em>to</em> the goniometer on a held record has to fetch at once: a stopped scope
   * has no next poll, and a view that came up empty until the next capture would read as a broken
   * one.</p>
   */
  async function setXy(on: boolean): Promise<void> {
    if (wantXy.value === on) return
    wantXy.value = on
    await fetchRecord(true)
  }

  /**
   * Turns the eye diagram on or off, and re-fetches so it arrives with the record it is of.
   *
   * <p>The goniometer's reason exactly: switching to it on a held record has no next poll to wait
   * for, and a view that came up empty until the next capture would read as a broken one.</p>
   */
  async function setEye(on: boolean): Promise<void> {
    if (wantEye.value === on) return
    wantEye.value = on
    await fetchRecord(true)
  }

  /**
   * Sets the display path, and re-fetches so the trace on screen is the one the path describes.
   *
   * <p>Compared as a whole rather than field by field, because a path is one thing: turning the
   * coupling on and moving a corner are the same kind of change and both have to reach the
   * analyzer before the next picture is drawn.</p>
   */
  async function setPath(query: Record<string, string>): Promise<void> {
    const next = JSON.stringify(query)
    if (JSON.stringify(wantPath.value) === next) return
    wantPath.value = query
    await fetchRecord(true)
  }

  /**
   * Sets how much context either side the fetch asks for, and re-fetches.
   *
   * <p>Driven by the drawing mode: sin(x)/x wants it, the other two have no use for it. Re-fetches
   * on a change for the eye's reason — switching the mode on a held record has no next poll to
   * wait for, and a trace that kept its old ends until the next capture would read as the fix not
   * having worked.</p>
   */
  async function setMargin(samples: number): Promise<void> {
    const next = Math.max(0, Math.round(samples))
    if (wantMargin.value === next) return
    wantMargin.value = next
    await fetchRecord(true)
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
    overview,
    error,
    window,
    gate,
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
    /** What the trigger engine is doing, for the trigger-state badge. See scope/triggerState.ts. */
    engineState: computed(() => status.value?.engineState ?? null),
    /** And whether its pre-trigger is filled, which is Armed against Ready. */
    preTriggerReady: computed(() => status.value?.preTriggerReady ?? false),
    /**
     * Committed, collected and drawn per second, or null while a run is stopped or the window is
     * too short to divide by. See `scope/captureRates.ts`.
     */
    captureRates: captureRateReadings,
    sampleRateHz: computed(() => status.value?.sampleRateHz ?? 0),
    /** Why the run stopped, as the runner put it — distinct from `error`, which belongs to this composable's own requests. */
    runError: computed(() => status.value?.error ?? null),
    refresh,
    fetchRecord,
    setWindow,
    setGate,
    setMaths,
    setXy,
    setEye,
    setPath,
    setMargin,
    setResidual,
    start,
    retune,
    cancel,
    clear,
    switchOwner,
  }
}
