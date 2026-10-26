import { computed, ref, shallowRef } from 'vue'
import {
  api,
  type DecaySurface,
  type FrequencyResponseTraceJson,
  type HarmonicCurveJson,
  type ImpulseReferenceCaptureRequest,
  type ImpulseResponseStartRequest,
  type ImpulseBurstDecayRequest,
  type ImpulseCsdRequest,
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

/**
 * The two decay surfaces. Not part of the status: they are analyses of the record rather than parts
 * of it, they are recomputed whenever a setting changes without a measurement happening, and each
 * one is far larger than everything else the poll carries.
 */
const csdSurface = shallowRef<DecaySurface | null>(null)
const burstSurface = shallowRef<DecaySurface | null>(null)

/**
 * The same two analyses for the <em>other</em> runs on screen, keyed by stored measurement id.
 *
 * <p>A decay surface is a heatmap and cannot overlay, so comparing runs means a chart each — which
 * is what the impulse screen draws for the two decay views since 2026-10-26. The pair above stays
 * the primary subject's (the live run, or the first checked history item when nothing is live) so
 * that everything already reading it — the screenshot gate, the notes under the chart, the report
 * — goes on meaning what it meant; these are the extras stacked under it.</p>
 *
 * <p>Replaced rather than mutated on every write, because a <c>shallowRef</c> holding a Map does
 * not notice a <c>set</c> into it.</p>
 */
const csdExtras = shallowRef<ReadonlyMap<number, DecaySurface>>(new Map())
const burstExtras = shallowRef<ReadonlyMap<number, DecaySurface>>(new Map())
const decayError = ref<string | null>(null)
const decayComputing = ref(false)

/** One request token per extra subject — see computeExtraCsd. */
const extraTokens = new Map<number, number>()

/**
 * Which decay request is the current one. A burst decay takes a noticeable fraction of a second, so
 * dragging a slider queues several — and without this the surface on screen is whichever *finished*
 * last rather than whichever was *asked for* last.
 */
let decayToken = 0

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

/** Throws away surfaces computed from an impulse response that is no longer the one on screen. */
function invalidateDecay() {
  decayToken++
  csdSurface.value = null
  burstSurface.value = null
  csdExtras.value = new Map()
  burstExtras.value = new Map()
  decayError.value = null
}

/**
 * Drops the extra surfaces of runs that are no longer on screen.
 *
 * <p>Called with the ids still wanted rather than with the one removed, so unchecking several at
 * once, switching measurement and closing a project all go through the same line. A surface is
 * megabytes, so this is worth doing rather than letting them accumulate behind a Map.</p>
 */
function pruneDecayExtras(keep: ReadonlySet<number>): void {
  for (const [map, ref] of [[csdExtras.value, csdExtras], [burstExtras.value, burstExtras]] as const) {
    if ([...map.keys()].every((id) => keep.has(id))) continue
    ref.value = new Map([...map].filter(([id]) => keep.has(id)))
  }
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
    invalidateDecay()
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
    invalidateDecay()
    status.value = await api.clearImpulseResponse()
  }

  /**
   * Hands the runner the measurement now on screen: the run it was holding is parked under the
   * measurement that made it, and whatever the arriving one had parked comes back. Posted on
   * arrival at this screen only — see ImpulseResponseRunner.SwitchOwner and RunParking.
   *
   * <p>The decay surfaces are dropped with the switch rather than kept per owner: they are
   * analyses of whichever impulse was on screen, each is far larger than the status, and the panel
   * recomputes one as soon as it is asked for.</p>
   */
  async function switchOwner(owner: string | null): Promise<void> {
    invalidateDecay()
    status.value = await api.setImpulseResponseOwner(owner)
  }

  /**
   * Recomputes one decay surface from the impulse response already measured. Both of these share
   * one in-flight slot and one error line: only one is on screen at a time, and a failed request is
   * almost always a settings problem whose message is the useful part of the response.
   */
  async function computeCsd(request: ImpulseCsdRequest): Promise<void> {
    const token = ++decayToken
    decayComputing.value = true
    try {
      const surface = await api.computeImpulseCsd(request)
      if (token !== decayToken) return
      csdSurface.value = surface
      decayError.value = null
    } catch (e) {
      if (token !== decayToken) return
      csdSurface.value = null
      decayError.value = e instanceof Error ? e.message : String(e)
    } finally {
      if (token === decayToken) decayComputing.value = false
    }
  }

  async function computeBurstDecay(request: ImpulseBurstDecayRequest): Promise<void> {
    const token = ++decayToken
    decayComputing.value = true
    try {
      const surface = await api.computeImpulseBurstDecay(request)
      if (token !== decayToken) return
      burstSurface.value = surface
      decayError.value = null
    } catch (e) {
      if (token !== decayToken) return
      burstSurface.value = null
      decayError.value = e instanceof Error ? e.message : String(e)
    } finally {
      if (token === decayToken) decayComputing.value = false
    }
  }

  /**
   * The same two analyses run against a **stored** result's impulse response instead of the runner's.
   *
   * <p>They exist because a decay surface is a heatmap: it cannot overlay, so a checked history item
   * can only be drawn when there is no live measurement to draw instead. Same in-flight slot and same
   * error line as the live pair — only one surface is ever on screen.</p>
   */
  async function computeStoredCsd(measurementId: number, request: ImpulseCsdRequest): Promise<void> {
    const token = ++decayToken
    decayComputing.value = true
    try {
      const surface = await api.computeStoredImpulseCsd(measurementId, request)
      if (token !== decayToken) return
      csdSurface.value = surface
      decayError.value = null
    } catch (e) {
      if (token !== decayToken) return
      csdSurface.value = null
      decayError.value = e instanceof Error ? e.message : String(e)
    } finally {
      if (token === decayToken) decayComputing.value = false
    }
  }

  /**
   * The two analyses for an <em>extra</em> subject — a checked history item stacked under the
   * primary chart. Its own token rather than the shared one: these run alongside the primary and a
   * shared token would have each of them cancelling the others as they were queued.
   */
  async function computeExtraCsd(measurementId: number, request: ImpulseCsdRequest): Promise<void> {
    const token = extraTokens.get(measurementId) ?? 0
    const mine = token + 1
    extraTokens.set(measurementId, mine)
    try {
      const surface = await api.computeStoredImpulseCsd(measurementId, request)
      if (extraTokens.get(measurementId) !== mine) return
      csdExtras.value = new Map(csdExtras.value).set(measurementId, surface)
    } catch {
      // Deliberately silent, unlike the primary's: the one error line on this screen belongs to
      // the chart the settings are being turned against, and a stacked comparison that fails is
      // better shown as the empty chart it will be than as a message about the main one.
      if (extraTokens.get(measurementId) !== mine) return
      const next = new Map(csdExtras.value)
      next.delete(measurementId)
      csdExtras.value = next
    }
  }

  async function computeExtraBurstDecay(measurementId: number, request: ImpulseBurstDecayRequest): Promise<void> {
    const token = extraTokens.get(measurementId) ?? 0
    const mine = token + 1
    extraTokens.set(measurementId, mine)
    try {
      const surface = await api.computeStoredImpulseBurstDecay(measurementId, request)
      if (extraTokens.get(measurementId) !== mine) return
      burstExtras.value = new Map(burstExtras.value).set(measurementId, surface)
    } catch {
      if (extraTokens.get(measurementId) !== mine) return
      const next = new Map(burstExtras.value)
      next.delete(measurementId)
      burstExtras.value = next
    }
  }

  async function computeStoredBurstDecay(measurementId: number, request: ImpulseBurstDecayRequest): Promise<void> {
    const token = ++decayToken
    decayComputing.value = true
    try {
      const surface = await api.computeStoredImpulseBurstDecay(measurementId, request)
      if (token !== decayToken) return
      burstSurface.value = surface
      decayError.value = null
    } catch (e) {
      if (token !== decayToken) return
      burstSurface.value = null
      decayError.value = e instanceof Error ? e.message : String(e)
    } finally {
      if (token === decayToken) decayComputing.value = false
    }
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

  /**
   * The one line the measurement status bar shows — "Measuring · pass 2 · record 1 of 4" — in the
   * shape every other runner's `progressLabel` has, so `statusHeadline`/`statusDetail` split it the
   * same way. This runner had none until the bar arrived, because its screen printed nothing about
   * its progress at all: a chirp pass is seconds long and the phase word alone never changed.
   */
  const progressLabel = computed<string>(() => {
    const s = status.value
    if (!s || s.phase === 'Idle') return ''
    if (s.phase === 'Measuring') {
      const record = s.averageCount > 1 ? ` · record ${s.averagesTaken} of ${s.averageCount}` : ''
      const pass = s.continuous ? ` · pass ${s.passesCompleted + 1}` : ''
      return `Measuring${pass}${record}`
    }
    if (s.phase === 'DeterminingLevel') return 'Determining generator level'
    if (s.phase === 'DeterminingRange') return 'Determining input range'
    if (s.phase === 'Analyzing') return 'Analyzing'
    if (s.phase === 'Finished' && s.passesCompleted > 1) return `Finished · ${s.passesCompleted} passes`
    return s.phase
  })

  return {
    status,
    running,
    phase,
    progressLabel,
    averagesTaken: computed(() => status.value?.averagesTaken ?? 0),
    averageCount: computed(() => status.value?.averageCount ?? 0),
    generatorAmpDbv: computed(() => status.value?.generatorAmpDbv ?? Number.NaN),
    binHz: computed(() => status.value?.binHz ?? 0),
    csdSurface,
    burstSurface,
    decayError,
    decayComputing,
    computeCsd,
    computeBurstDecay,
    computeStoredCsd,
    computeStoredBurstDecay,
    csdExtras,
    burstExtras,
    computeExtraCsd,
    computeExtraBurstDecay,
    pruneDecayExtras,
    invalidateDecay,
    traces,
    impulses,
    impulse,
    harmonics,
    hasResult,
    gateResolutionHz,
    references,
    capturingReference,
    error,
    /**
     * Why the *run* stopped, as the runner put it — distinct from `error`, which belongs to the
     * reference-capture requests this composable also makes. The status has always carried it and
     * nothing read it, so a failed impulse run showed the bare word "Failed" where the other five
     * output-referred screens print the reason underneath their chart. Noticed while adding the
     * 80 %-of-level stop (OutputLevelReach), whose whole purpose is the message.
     */
    runError: computed(() => status.value?.error ?? null),
    refresh,
    start,
    cancel,
    clear,
    switchOwner,
    refreshReferences,
    captureReference,
    deleteReference,
  }
}
