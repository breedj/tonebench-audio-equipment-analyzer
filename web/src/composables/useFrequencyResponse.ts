import { computed, ref, shallowRef } from 'vue'
import {
  api,
  type FrequencyResponseStartRequest,
  type FrequencyResponseStatus,
  type FrequencyResponseTraceJson,
  type ResponseTraceKindName,
} from '../api/client'
import {
  ResponsePhase,
  ResponseTraceKind,
  type FrequencyResponseFrame,
  type FrequencyResponseProgressFrame,
} from '../streaming/protocol'

/**
 * The frequency-response measurement's client-side state.
 *
 * Module-level singleton refs, the same stand-in for a store the rest of this app uses (see
 * useSweep.ts): the chart, the config panel, the readout table and the progress line are separate
 * components all looking at one measurement.
 *
 * The measurement runs on the server, so this is a mirror and never the source of truth. It is fed
 * two ways and needs both: live FrequencyResponse/Progress frames off the existing /ws/stream
 * socket while a run is going, and `refresh()` against GET /api/frequency-response/status for
 * anything that happened before this browser was looking — a reload mid-run, or a second tab.
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

const running = ref(false)
const phase = ref<ResponsePhase>(ResponsePhase.Idle)
const continuous = ref(false)
const passesCompleted = ref(0)
const averagesTaken = ref(0)
const averageCount = ref(0)
const generatorAmpDbv = ref(0)
const binHz = ref(0)
const sampleRateHz = ref(0)
const fftSize = ref(0)
const latencySamples = ref<number | null>(null)
const error = ref<string | null>(null)

// shallowRef + whole-array replacement: a curve is immutable once measured, and replacing the array
// is what makes the chart's watcher fire without deep-watching thousands of numbers.
const traces = shallowRef<ResponseTrace[]>([])

const phaseLabel: Record<ResponsePhase, string> = {
  [ResponsePhase.Idle]: 'Idle',
  [ResponsePhase.DeterminingLevel]: 'Determining level',
  [ResponsePhase.Measuring]: 'Measuring',
  [ResponsePhase.Finished]: 'Finished',
  [ResponsePhase.Cancelled]: 'Cancelled',
  [ResponsePhase.Failed]: 'Failed',
  [ResponsePhase.DeterminingRange]: 'Determining input range',
}

const phaseNames: Record<string, ResponsePhase> = {
  Idle: ResponsePhase.Idle,
  DeterminingLevel: ResponsePhase.DeterminingLevel,
  Measuring: ResponsePhase.Measuring,
  Finished: ResponsePhase.Finished,
  Cancelled: ResponsePhase.Cancelled,
  Failed: ResponsePhase.Failed,
  DeterminingRange: ResponsePhase.DeterminingRange,
}

const traceKindNames: Record<ResponseTraceKind, ResponseTraceKindName> = {
  [ResponseTraceKind.Left]: 'Left',
  [ResponseTraceKind.Right]: 'Right',
  [ResponseTraceKind.Gain]: 'Gain',
}

const progressLabel = computed(() => {
  if (phase.value === ResponsePhase.DeterminingLevel) return 'Determining generator level'
  if (phase.value === ResponsePhase.DeterminingRange) return 'Determining input range'

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

function applyStatus(status: FrequencyResponseStatus) {
  running.value = status.running
  phase.value = phaseNames[status.phase] ?? ResponsePhase.Idle
  continuous.value = status.continuous
  passesCompleted.value = status.passesCompleted
  averagesTaken.value = status.averagesTaken
  averageCount.value = status.averageCount
  generatorAmpDbv.value = status.generatorAmpDbv
  binHz.value = status.binHz
  sampleRateHz.value = status.sampleRateHz
  fftSize.value = status.fftSize
  latencySamples.value = status.latencySamples
  error.value = status.error
  traces.value = status.traces.map(fromJson)
}

/**
 * Folds one live curve in, replacing whatever was there for the same kind. Keyed by kind rather
 * than appended, so a rerun replaces the previous curves instead of stacking three more on top.
 */
function ingestTrace(frame: FrequencyResponseFrame) {
  const trace = fromFrame(frame)
  const rest = traces.value.filter((t) => t.kind !== trace.kind)
  traces.value = [...rest, trace]

  // A continuous run sends a whole set of curves per pass. The Left frame is the first of each set,
  // so counting those is what advances the pass number without needing a frame of its own.
  if (continuous.value && trace.kind === 'Left') passesCompleted.value += 1
}

function ingestProgress(frame: FrequencyResponseProgressFrame) {
  phase.value = frame.phase
  averagesTaken.value = frame.taken
  generatorAmpDbv.value = frame.generatorAmpDbv
  running.value =
    frame.phase === ResponsePhase.Measuring ||
    frame.phase === ResponsePhase.DeterminingLevel ||
    frame.phase === ResponsePhase.DeterminingRange

  // Both probes reuse this frame with their own pass counts, which are not averages — taking their
  // total as the average count would rewrite the "record n of m" readout with a probe's numbers and
  // leave it wrong for the rest of the run.
  if (
    frame.phase !== ResponsePhase.DeterminingLevel &&
    frame.phase !== ResponsePhase.DeterminingRange
  ) {
    averageCount.value = frame.total
  }
}

export function useFrequencyResponse() {
  return {
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

    trace: (kind: ResponseTraceKindName) => traces.value.find((t) => t.kind === kind) ?? null,

    ingestTrace,
    ingestProgress,

    async refresh() {
      applyStatus(await api.getFrequencyResponseStatus())
    },

    /**
     * Clears the previous curves locally the moment a run starts, rather than waiting for the new
     * ones: a chart still showing the last measurement while a new one is under way reads as though
     * nothing is happening.
     */
    async start(req: FrequencyResponseStartRequest) {
      error.value = null
      traces.value = []
      averagesTaken.value = 0
      passesCompleted.value = 0
      continuous.value = req.continuous
      running.value = true
      phase.value = ResponsePhase.Measuring

      try {
        const started = await api.startFrequencyResponse(req)
        binHz.value = started.binHz
        sampleRateHz.value = started.sampleRateHz
        fftSize.value = started.fftSize
        averageCount.value = started.averageCount
        return started
      } catch (e) {
        running.value = false
        phase.value = ResponsePhase.Failed
        error.value = e instanceof Error ? e.message : String(e)
        throw e
      }
    },

    async cancel() {
      applyStatus(await api.cancelFrequencyResponse())
    },

    async clear() {
      applyStatus(await api.clearFrequencyResponse())
    },
  }
}
