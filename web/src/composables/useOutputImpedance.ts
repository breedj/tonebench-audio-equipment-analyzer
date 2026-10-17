import { computed, ref, shallowRef } from 'vue'
import { api, type ImpedanceTraceJson, type OutputImpedanceStartRequest, type OutputImpedanceStatus } from '../api/client'
import { ResponsePhase, type ImpedanceFrame, type ImpedanceProgressFrame } from '../streaming/protocol'
import type { ImpedanceTrace } from './useImpedance'

/**
 * The output-impedance (damping factor) measurement's client-side state — the same stand-in for a
 * store `useImpedance` is, for the same reason, and a store of its own rather than a second target
 * inside that one: the runner, the status shape, the pause and the readings are all its own, and
 * only the curve's shape is shared. Frames reach it by the origin bit on the impedance frames
 * (see useSpectrumStream); everything else comes from `refresh()` against the status endpoint.
 *
 * The one thing no other store has: `waiting`, the pause between the two loads' passes,
 * ended by `continueRun()` (the CONTINUE button) or a cancel.
 */

const running = ref(false)
const phase = ref<ResponsePhase>(ResponsePhase.Idle)
const passesCompleted = ref(0)
const pointsMeasured = ref(0)
/** Over both passes. */
const pointCount = ref(0)
const generatorAmpDbv = ref(0)
const inputRangeDbv = ref<number | null>(null)
const binHz = ref(0)
const sampleRateHz = ref(0)
const fftSize = ref(0)
const loadOhms = ref(8)
/** Null when the second pass is taken open. */
const secondLoadOhms = ref<number | null>(4)
const nominalOhms = ref(8)
const rightChannelIsReference = ref(false)
const error = ref<string | null>(null)
const warning = ref<string | null>(null)
const medianOhms = ref<number | null>(null)
const zoutAt1kOhms = ref<number | null>(null)
const dampingFactorAt1k = ref<number | null>(null)
const firstGainAt1kDb = ref<number | null>(null)
const secondGainAt1kDb = ref<number | null>(null)

// shallowRef + whole-object replacement: a curve is immutable once measured.
const trace = shallowRef<ImpedanceTrace | null>(null)

/** The Zout curve as the impedance chart wants it — a one-element list, or none. */
const traces = computed<ImpedanceTrace[]>(() => (trace.value ? [trace.value] : []))

/** True while the run is paused for the load to be changed — the CONTINUE button's moment. */
const waiting = computed(() => phase.value === ResponsePhase.WaitingForLoadChange)

const phaseNames: Record<string, ResponsePhase> = {
  Idle: ResponsePhase.Idle,
  DeterminingLevel: ResponsePhase.DeterminingLevel,
  Measuring: ResponsePhase.Measuring,
  Finished: ResponsePhase.Finished,
  Cancelled: ResponsePhase.Cancelled,
  Failed: ResponsePhase.Failed,
  DeterminingRange: ResponsePhase.DeterminingRange,
  Sweeping: ResponsePhase.Sweeping,
  Analyzing: ResponsePhase.Analyzing,
  Calibrating: ResponsePhase.Calibrating,
  WaitingForLoadChange: ResponsePhase.WaitingForLoadChange,
}

const RUNNING_PHASES: ReadonlySet<ResponsePhase> = new Set([
  ResponsePhase.DeterminingLevel,
  ResponsePhase.DeterminingRange,
  ResponsePhase.Sweeping,
  ResponsePhase.WaitingForLoadChange,
])

/** Which pass a cumulative point index falls in, and where in it — for the label. */
function passOf(points: number, total: number): { pass: 1 | 2; index: number; perPass: number } {
  const perPass = Math.max(1, Math.floor(total / 2))
  return points < perPass ? { pass: 1, index: points, perPass } : { pass: 2, index: points - perPass, perPass }
}

const progressLabel = computed(() => {
  if (phase.value === ResponsePhase.DeterminingLevel) return 'Determining generator level'
  if (phase.value === ResponsePhase.DeterminingRange) return 'Determining input range'
  if (phase.value === ResponsePhase.WaitingForLoadChange) return 'First pass done · change the load, then press CONTINUE'

  if (phase.value === ResponsePhase.Sweeping) {
    if (pointCount.value <= 0) return 'Sweeping'
    const { pass, index, perPass } = passOf(pointsMeasured.value, pointCount.value)
    return `${pass === 1 ? 'First' : 'Second'} load · point ${Math.min(index + 1, perPass)} of ${perPass}`
  }

  if (phase.value === ResponsePhase.Finished && pointCount.value > 0) {
    return `Finished · ${pointCount.value / 2} points, both passes`
  }

  if (phase.value === ResponsePhase.Cancelled) {
    if (passesCompleted.value === 0 && pointsMeasured.value === 0) return 'Cancelled'
    const { pass, index, perPass } = passOf(pointsMeasured.value, pointCount.value)
    return pass === 1
      ? `Stopped in the first pass after ${index} of ${perPass} points`
      : `Stopped in the second pass after ${index} of ${perPass} points`
  }

  if (phase.value === ResponsePhase.Failed) return 'Failed'
  return 'Idle'
})

function fromJson(json: ImpedanceTraceJson): ImpedanceTrace {
  return {
    kind: 'Impedance',
    startFreqHz: json.startFreqHz,
    endFreqHz: json.endFreqHz,
    freqHz: json.freqHz,
    magnitudeMinDbOhms: json.magnitudeMinDbOhms,
    magnitudeMaxDbOhms: json.magnitudeMaxDbOhms,
    phaseDeg: json.phaseDeg,
    coherence: json.coherence,
    peakDbOhms: json.peakDbOhms,
    peakFreqHz: json.peakFreqHz,
  }
}

function fromFrame(frame: ImpedanceFrame): ImpedanceTrace {
  return {
    kind: 'Impedance',
    startFreqHz: frame.startFreqHz,
    endFreqHz: frame.endFreqHz,
    freqHz: Array.from(frame.freqHz),
    magnitudeMinDbOhms: Array.from(frame.magnitudeMinDbOhms),
    magnitudeMaxDbOhms: Array.from(frame.magnitudeMaxDbOhms),
    phaseDeg: Array.from(frame.phaseDeg),
    coherence: Array.from(frame.coherence),
    peakDbOhms: frame.peakDbOhms,
    peakFreqHz: frame.peakFreqHz,
  }
}

function applyStatus(status: OutputImpedanceStatus) {
  running.value = status.running
  phase.value = phaseNames[status.phase] ?? ResponsePhase.Idle
  passesCompleted.value = status.passesCompleted
  pointsMeasured.value = status.pointsMeasured
  pointCount.value = status.pointCount
  generatorAmpDbv.value = status.generatorAmpDbv
  inputRangeDbv.value = status.inputRangeDbv
  binHz.value = status.binHz
  sampleRateHz.value = status.sampleRateHz
  fftSize.value = status.fftSize
  loadOhms.value = status.loadOhms
  secondLoadOhms.value = status.secondLoadOhms
  nominalOhms.value = status.nominalOhms
  rightChannelIsReference.value = status.rightChannelIsReference
  error.value = status.error
  warning.value = status.warning
  medianOhms.value = status.medianOhms
  zoutAt1kOhms.value = status.zoutAt1kOhms
  dampingFactorAt1k.value = status.dampingFactorAt1k
  firstGainAt1kDb.value = status.firstGainAt1kDb
  secondGainAt1kDb.value = status.secondGainAt1kDb
  trace.value = status.trace ? fromJson(status.trace) : null
}

function ingestTrace(frame: ImpedanceFrame) {
  trace.value = fromFrame(frame)
}

async function refresh() {
  applyStatus(await api.getOutputImpedanceStatus())
}

function ingestProgress(frame: ImpedanceProgressFrame) {
  const wasRunning = running.value

  phase.value = frame.phase
  generatorAmpDbv.value = frame.generatorAmpDbv
  running.value = RUNNING_PHASES.has(frame.phase)

  // The readings, the warning and the two gain passes live on the status alone, so a run that has
  // just ended — or just paused, since the range it chose is a status field too — is read back.
  if ((wasRunning && !running.value) || frame.phase === ResponsePhase.WaitingForLoadChange) {
    void refresh().catch(() => undefined)
  }

  // The level and range probes reuse this frame with counts of their own.
  if (frame.phase === ResponsePhase.DeterminingLevel || frame.phase === ResponsePhase.DeterminingRange) return

  pointsMeasured.value = frame.taken
  pointCount.value = frame.total
  if (frame.phase === ResponsePhase.WaitingForLoadChange) passesCompleted.value = Math.max(passesCompleted.value, 1)
}

export function useOutputImpedance() {
  return {
    running,
    waiting,
    phase,
    passesCompleted,
    pointsMeasured,
    pointCount,
    generatorAmpDbv,
    inputRangeDbv,
    binHz,
    sampleRateHz,
    fftSize,
    loadOhms,
    secondLoadOhms,
    nominalOhms,
    rightChannelIsReference,
    error,
    warning,
    trace,
    traces,
    medianOhms,
    zoutAt1kOhms,
    dampingFactorAt1k,
    firstGainAt1kDb,
    secondGainAt1kDb,
    progressLabel,
    ingestTrace,
    ingestProgress,
    refresh,

    async start(req: OutputImpedanceStartRequest) {
      error.value = null
      warning.value = null
      trace.value = null
      passesCompleted.value = 0
      pointsMeasured.value = 0
      pointCount.value = 0
      medianOhms.value = null
      zoutAt1kOhms.value = null
      dampingFactorAt1k.value = null
      firstGainAt1kDb.value = null
      secondGainAt1kDb.value = null
      inputRangeDbv.value = null
      loadOhms.value = req.loadOhms
      secondLoadOhms.value = req.secondLoadOhms
      nominalOhms.value = req.nominalOhms
      rightChannelIsReference.value = req.rightChannelIsReference
      running.value = true
      phase.value = req.levelMode === 'GeneratorVoltage' ? ResponsePhase.Sweeping : ResponsePhase.DeterminingLevel

      try {
        const started = await api.startOutputImpedance(req)
        sampleRateHz.value = started.sampleRateHz
        pointCount.value = started.pointCount * started.passCount
        return started
      } catch (e) {
        running.value = false
        phase.value = ResponsePhase.Failed
        error.value = e instanceof Error ? e.message : String(e)
        throw e
      }
    },

    /** The CONTINUE button: resumes a run waiting for the load to be changed. */
    async continueRun() {
      applyStatus(await api.continueOutputImpedance())
    },

    async cancel() {
      applyStatus(await api.cancelOutputImpedance())
    },

    async clear() {
      applyStatus(await api.clearOutputImpedance())
    },

    /**
     * Hands this runner the measurement now on screen and applies the status it answers with: the
     * run it was holding is parked under the measurement that made it, and whatever the arriving
     * one had parked comes back. Posted on arrival at this screen only — see
     * OutputImpedanceRunner.SwitchOwner and RunParking.
     */
    async switchOwner(owner: string | null) {
      applyStatus(await api.setOutputImpedanceOwner(owner))
    },
  }
}
