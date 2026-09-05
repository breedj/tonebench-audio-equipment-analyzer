import { computed, ref, shallowRef, type Ref } from 'vue'
import {
  api,
  type FrequencyResponseStartRequest,
  type FrequencyResponseStatus,
  type FrequencyResponseTraceJson,
  type ResponsePhaseKindName,
  type ResponseTraceKindName,
  type SteppedResponseStartRequest,
} from '../api/client'
import {
  ResponsePhase,
  ResponseTraceKind,
  STREAM_FLAG_STEPPED_RESPONSE,
  type FrequencyResponseFrame,
  type FrequencyResponseProgressFrame,
} from '../streaming/protocol'

/**
 * The frequency-response measurements' client-side state.
 *
 * Module-level, the same stand-in for a store the rest of this app uses (see useSweep.ts): the
 * chart, the config panel, the readout table and the progress line are separate components all
 * looking at one measurement.
 *
 * The measurement runs on the server, so this is a mirror and never the source of truth. It is fed
 * two ways and needs both: live FrequencyResponse/Progress frames off the existing /ws/stream
 * socket while a run is going, and `refresh()` against the status endpoint for anything that
 * happened before this browser was looking — a reload mid-run, or a second tab.
 *
 * **One state per measurement, not one shared between them.** A chirp and a stepped sine produce the
 * same three curves and share every component that draws them, which made a single set of refs look
 * sufficient — it is not. They are two measurements with two server-side runners, and sharing the
 * refs meant running one put its curves on the other's screen: switching kinds does not change
 * `isFrequencyResponse` (it is true for both), so nothing re-polled and the previous kind's traces
 * simply stayed. `target` picks which state the verbs and the exported refs read; a live frame picks
 * its own by the origin flag it carries, so a background run can never paint the screen you are on.
 */

/** One curve as the UI holds it. Plain arrays rather than the wire's Float32Array, so a stored result and a live one are the same shape. */
export interface ResponseTrace {
  kind: ResponseTraceKindName
  startFreqHz: number
  endFreqHz: number
  freqHz: number[]
  magnitudeMinDb: number[]
  magnitudeMaxDb: number[]
  phaseDeg: number[]
  coherence: number[]
  oneDb: ResponseBandwidthView
  threeDb: ResponseBandwidthView
  peakDb: number
  peakFreqHz: number
}

export interface ResponseBandwidthView {
  lowerHz: number
  upperHz: number
  bandwidthHz: number
  lowerClamped: boolean
  upperClamped: boolean
}

/** Which measurement these verbs drive. Set from App.vue's selected measurement kind. */
export type ResponseTarget = 'Chirp' | 'Stepped'

interface ResponseState {
  running: Ref<boolean>
  phase: Ref<ResponsePhase>
  continuous: Ref<boolean>
  passesCompleted: Ref<number>
  averagesTaken: Ref<number>
  averageCount: Ref<number>
  generatorAmpDbv: Ref<number>
  binHz: Ref<number>
  sampleRateHz: Ref<number>
  fftSize: Ref<number>
  latencySamples: Ref<number | null>
  error: Ref<string | null>
  pointsMeasured: Ref<number>
  pointCount: Ref<number>
  traces: Ref<ResponseTrace[]>
  phaseKind: Ref<ResponsePhaseKindName>
  rightChannelIsReference: Ref<boolean>
}

function createResponseState(): ResponseState {
  return {
    running: ref(false),
    phase: ref<ResponsePhase>(ResponsePhase.Idle),
    continuous: ref(false),
    passesCompleted: ref(0),
    averagesTaken: ref(0),
    averageCount: ref(0),
    generatorAmpDbv: ref(0),
    binHz: ref(0),
    sampleRateHz: ref(0),
    fftSize: ref(0),
    latencySamples: ref<number | null>(null),
    error: ref<string | null>(null),

    // A stepped run's progress is points, not records — and it has an average count of its own, so
    // neither counter can stand in for the other.
    pointsMeasured: ref(0),
    pointCount: ref(0),

    // shallowRef + whole-array replacement: a curve is immutable once measured, and replacing the
    // array is what makes the chart's watcher fire without deep-watching thousands of numbers.
    traces: shallowRef<ResponseTrace[]>([]),

    // 'PerTrace' is the chirp's behaviour and the server's own default, so a status from a build
    // that predates the field reads as the behaviour that build had.
    phaseKind: ref<ResponsePhaseKindName>('PerTrace'),
    rightChannelIsReference: ref(false),
  }
}

const states: Record<ResponseTarget, ResponseState> = {
  Chirp: createResponseState(),
  Stepped: createResponseState(),
}

const target = ref<ResponseTarget>('Chirp')
const active = computed(() => states[target.value])

// Read-only views onto whichever measurement is on screen. Every consumer of this store only reads
// these — the writes all happen below, against a state named explicitly — so proxying them as
// computeds keeps the destructuring API components already use (`const { traces } = ...`) working
// unchanged, while making it impossible for one measurement to write into the other's.
const running = computed(() => active.value.running.value)
const phase = computed(() => active.value.phase.value)
const continuous = computed(() => active.value.continuous.value)
const passesCompleted = computed(() => active.value.passesCompleted.value)
const averagesTaken = computed(() => active.value.averagesTaken.value)
const averageCount = computed(() => active.value.averageCount.value)
const generatorAmpDbv = computed(() => active.value.generatorAmpDbv.value)
const binHz = computed(() => active.value.binHz.value)
const sampleRateHz = computed(() => active.value.sampleRateHz.value)
const fftSize = computed(() => active.value.fftSize.value)
const latencySamples = computed(() => active.value.latencySamples.value)
const error = computed(() => active.value.error.value)
const pointsMeasured = computed(() => active.value.pointsMeasured.value)
const pointCount = computed(() => active.value.pointCount.value)
const traces = computed(() => active.value.traces.value)
const phaseKind = computed(() => active.value.phaseKind.value)
const rightChannelIsReference = computed(() => active.value.rightChannelIsReference.value)

/**
 * Whether the measurement on screen has a phase to offer at all — the one rule behind the toggle,
 * its range fields and whether the chart is asked to draw the series, so the three cannot disagree.
 *
 * The empty-chart case is deliberately exempt: a fresh measurement type has run nothing, so it has
 * no phase yet by definition, and hiding the controls there would make it look as though this
 * measurement never had one.
 */
const phaseAvailable = computed(
  () => active.value.phaseKind.value !== 'None' || active.value.traces.value.length === 0,
)

const phaseLabel: Record<ResponsePhase, string> = {
  [ResponsePhase.Idle]: 'Idle',
  [ResponsePhase.DeterminingLevel]: 'Determining level',
  [ResponsePhase.Measuring]: 'Measuring',
  [ResponsePhase.Finished]: 'Finished',
  [ResponsePhase.Cancelled]: 'Cancelled',
  [ResponsePhase.Failed]: 'Failed',
  [ResponsePhase.DeterminingRange]: 'Determining input range',
  [ResponsePhase.Sweeping]: 'Sweeping',
}

const phaseNames: Record<string, ResponsePhase> = {
  Idle: ResponsePhase.Idle,
  DeterminingLevel: ResponsePhase.DeterminingLevel,
  Measuring: ResponsePhase.Measuring,
  Finished: ResponsePhase.Finished,
  Cancelled: ResponsePhase.Cancelled,
  Failed: ResponsePhase.Failed,
  DeterminingRange: ResponsePhase.DeterminingRange,
  Sweeping: ResponsePhase.Sweeping,
}

const traceKindNames: Record<ResponseTraceKind, ResponseTraceKindName> = {
  [ResponseTraceKind.Left]: 'Left',
  [ResponseTraceKind.Right]: 'Right',
  [ResponseTraceKind.Gain]: 'Gain',
}

const progressLabel = computed(() => {
  if (phase.value === ResponsePhase.DeterminingLevel) return 'Determining generator level'
  if (phase.value === ResponsePhase.DeterminingRange) return 'Determining input range'

  // A stepped run counts points, and it needs to: at a few seconds per point the phase alone never
  // changes for minutes and looks frozen.
  if (phase.value === ResponsePhase.Sweeping) {
    const average = averageCount.value > 1 ? ` · ${averageCount.value} averages` : ''
    return pointCount.value > 0
      ? `Point ${Math.min(pointsMeasured.value + 1, pointCount.value)} of ${pointCount.value}${average}`
      : 'Sweeping'
  }

  if (phase.value === ResponsePhase.Measuring) {
    const record =
      averageCount.value > 1 ? ` · record ${averagesTaken.value} of ${averageCount.value}` : ''
    // The pass number is what tells a continuous run apart from a stuck one: with one average and a
    // short record, "Measuring" alone never changes and looks frozen.
    const pass = continuous.value ? ` · pass ${passesCompleted.value + 1}` : ''
    return `Measuring${pass}${record}`
  }

  if (phase.value === ResponsePhase.Finished && passesCompleted.value > 1) {
    return `Stopped after ${passesCompleted.value} passes`
  }

  if (phase.value === ResponsePhase.Finished && pointCount.value > 0) {
    return `Finished · ${pointsMeasured.value} points`
  }

  // A stepped run cancelled halfway still measured the part it covered, and the chart is showing it.
  if (phase.value === ResponsePhase.Cancelled && pointsMeasured.value > 0) {
    return `Stopped after ${pointsMeasured.value} of ${pointCount.value} points`
  }

  return phaseLabel[phase.value] ?? 'Idle'
})

function fromJson(trace: FrequencyResponseTraceJson): ResponseTrace {
  return {
    kind: trace.kind,
    startFreqHz: trace.startFreqHz,
    endFreqHz: trace.endFreqHz,
    freqHz: trace.freqHz,
    magnitudeMinDb: trace.magnitudeMinDb,
    magnitudeMaxDb: trace.magnitudeMaxDb,
    phaseDeg: trace.phaseDeg,
    coherence: trace.coherence,
    oneDb: { ...trace.oneDb },
    threeDb: { ...trace.threeDb },
    peakDb: trace.peakDb,
    peakFreqHz: trace.peakFreqHz,
  }
}

function fromFrame(frame: FrequencyResponseFrame): ResponseTrace {
  return {
    kind: traceKindNames[frame.kind] ?? 'Gain',
    startFreqHz: frame.startFreqHz,
    endFreqHz: frame.endFreqHz,
    freqHz: Array.from(frame.freqHz),
    magnitudeMinDb: Array.from(frame.magnitudeMinDb),
    magnitudeMaxDb: Array.from(frame.magnitudeMaxDb),
    phaseDeg: Array.from(frame.phaseDeg),
    coherence: Array.from(frame.coherence),
    oneDb: { ...frame.oneDb },
    threeDb: { ...frame.threeDb },
    peakDb: frame.peakDb,
    peakFreqHz: frame.peakFreqHz,
  }
}

/**
 * Which measurement a live frame belongs to, from the origin flag the server sets — never from
 * `target`. The two runners emit identical frame types, so routing on whichever screen happens to be
 * open is a guess that puts a background run's curves on the foreground measurement's chart. This is
 * the fact instead. See StreamFlags.SteppedResponse.
 */
function stateForFrame(flags: number): ResponseState {
  return (flags & STREAM_FLAG_STEPPED_RESPONSE) !== 0 ? states.Stepped : states.Chirp
}

function applyStatus(state: ResponseState, status: FrequencyResponseStatus) {
  state.running.value = status.running
  state.phase.value = phaseNames[status.phase] ?? ResponsePhase.Idle
  state.continuous.value = status.continuous
  state.passesCompleted.value = status.passesCompleted
  state.averagesTaken.value = status.averagesTaken
  state.averageCount.value = status.averageCount
  state.generatorAmpDbv.value = status.generatorAmpDbv
  state.binHz.value = status.binHz
  state.sampleRateHz.value = status.sampleRateHz
  state.fftSize.value = status.fftSize
  state.latencySamples.value = status.latencySamples
  state.error.value = status.error
  state.pointsMeasured.value = status.pointsMeasured
  state.pointCount.value = status.pointCount
  state.traces.value = status.traces.map(fromJson)

  // Guarded, unlike the rest: absent on a status from a build that predates the field, where the
  // right answer is to keep the default rather than write undefined over it.
  if (status.phaseKind) state.phaseKind.value = status.phaseKind
  state.rightChannelIsReference.value = status.rightChannelIsReference === true
}

/**
 * Folds one live curve in, replacing whatever was there for the same kind. Keyed by kind rather
 * than appended, so a rerun replaces the previous curves instead of stacking three more on top.
 */
function ingestTrace(frame: FrequencyResponseFrame) {
  const state = stateForFrame(frame.header.flags)
  const trace = fromFrame(frame)
  const rest = state.traces.value.filter((t) => t.kind !== trace.kind)
  state.traces.value = [...rest, trace]

  // A continuous run sends a whole set of curves per pass. The Left frame is the first of each set,
  // so counting those is what advances the pass number without needing a frame of its own.
  if (state.continuous.value && trace.kind === 'Left') state.passesCompleted.value += 1
}

function ingestProgress(frame: FrequencyResponseProgressFrame) {
  const state = stateForFrame(frame.header.flags)
  const stepped = state === states.Stepped

  state.phase.value = frame.phase
  state.generatorAmpDbv.value = frame.generatorAmpDbv
  state.running.value =
    frame.phase === ResponsePhase.Measuring ||
    frame.phase === ResponsePhase.Sweeping ||
    frame.phase === ResponsePhase.DeterminingLevel ||
    frame.phase === ResponsePhase.DeterminingRange

  // Both probes reuse this frame with their own pass counts, which are neither records nor points —
  // taking their total would rewrite the readout with a probe's numbers and leave it wrong for the
  // rest of the run.
  const isProbe =
    frame.phase === ResponsePhase.DeterminingLevel || frame.phase === ResponsePhase.DeterminingRange

  if (isProbe) return

  // The frame has one (taken, total) pair and the two measurements mean different things by it:
  // records for a chirp, points for a stepped sweep. Decided by the frame's own origin, so one
  // arriving while the other screen is open is still counted in its own units.
  if (stepped) {
    state.pointsMeasured.value = frame.taken
    state.pointCount.value = frame.total
  } else {
    state.averagesTaken.value = frame.taken
    state.averageCount.value = frame.total
  }
}

export function useFrequencyResponse() {
  return {
    target,
    running,
    phase,
    continuous,
    passesCompleted,
    averagesTaken,
    averageCount,
    generatorAmpDbv,
    binHz,
    sampleRateHz,
    fftSize,
    latencySamples,
    error,
    traces,
    progressLabel,
    pointsMeasured,
    pointCount,
    phaseKind,
    rightChannelIsReference,
    phaseAvailable,

    trace: (kind: ResponseTraceKindName) => traces.value.find((t) => t.kind === kind) ?? null,

    ingestTrace,
    ingestProgress,

    /**
     * Points this measurement at one of the two runners. Set from the selected measurement kind, and
     * before `refresh()` — the verbs below all read it, so a target left stale would poll one
     * runner's status into the other one's screen.
     */
    setTarget(next: ResponseTarget) {
      target.value = next
    },

    async refresh() {
      applyStatus(
        active.value,
        target.value === 'Stepped'
          ? await api.getSteppedResponseStatus()
          : await api.getFrequencyResponseStatus(),
      )
    },

    /**
     * Clears the previous curves locally the moment a run starts, rather than waiting for the new
     * ones: a chart still showing the last measurement while a new one is under way reads as though
     * nothing is happening.
     *
     * Pinned to the chirp's own state rather than the active one — this verb is kind-specific, so it
     * must not depend on `target` having been set first.
     */
    async start(req: FrequencyResponseStartRequest) {
      const state = states.Chirp
      state.error.value = null
      state.traces.value = []
      state.averagesTaken.value = 0
      state.passesCompleted.value = 0
      state.continuous.value = req.continuous
      state.running.value = true
      state.phase.value = ResponsePhase.Measuring

      // From the request just sent rather than waited for from the server: nothing polls status
      // during a run, so the panel would otherwise be gated on the previous run's settings.
      state.phaseKind.value = 'PerTrace'
      state.rightChannelIsReference.value = req.rightChannelIsReference

      try {
        const started = await api.startFrequencyResponse(req)
        state.binHz.value = started.binHz
        state.sampleRateHz.value = started.sampleRateHz
        state.fftSize.value = started.fftSize
        state.averageCount.value = started.averageCount
        return started
      } catch (e) {
        state.running.value = false
        state.phase.value = ResponsePhase.Failed
        state.error.value = e instanceof Error ? e.message : String(e)
        throw e
      }
    },

    /**
     * Starts a stepped-sine run. Clears the previous curves locally for the same reason `start`
     * does: a chart still showing the last measurement while a new one is under way reads as though
     * nothing is happening — and here that would last minutes.
     */
    async startStepped(req: SteppedResponseStartRequest) {
      const state = states.Stepped
      state.error.value = null
      state.traces.value = []
      state.continuous.value = false
      state.passesCompleted.value = 0
      state.pointsMeasured.value = 0
      state.pointCount.value = 0
      state.running.value = true
      state.phase.value = ResponsePhase.Sweeping

      // Mirrors SteppedResponseRequest.PhaseKind: arg(L/R) is the same computation either way, and
      // what the reference switch changes is what the number means. Set locally for the same reason
      // as the chirp's above.
      state.phaseKind.value = !req.generateRight
        ? 'None'
        : req.rightChannelIsReference
          ? 'DutResponse'
          : 'ChannelDifference'
      state.rightChannelIsReference.value = req.rightChannelIsReference

      try {
        const started = await api.startSteppedResponse(req)
        state.sampleRateHz.value = started.sampleRateHz
        state.averageCount.value = started.averageCount
        state.pointCount.value = started.pointCount
        return started
      } catch (e) {
        state.running.value = false
        state.phase.value = ResponsePhase.Failed
        state.error.value = e instanceof Error ? e.message : String(e)
        throw e
      }
    },

    async cancel() {
      applyStatus(
        active.value,
        target.value === 'Stepped'
          ? await api.cancelSteppedResponse()
          : await api.cancelFrequencyResponse(),
      )
    },

    async clear() {
      applyStatus(
        active.value,
        target.value === 'Stepped'
          ? await api.clearSteppedResponse()
          : await api.clearFrequencyResponse(),
      )
    },
  }
}
