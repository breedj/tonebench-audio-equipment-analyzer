import { computed, ref, shallowRef } from 'vue'
import {
  api,
  type ImpedanceCalibrationJson,
  type ImpedanceCalTargetName,
  type ImpedanceStartRequest,
  type ImpedanceStatus,
  type ImpedanceStimulusName,
  type ImpedanceTraceJson,
  type ImpedanceTraceKindName,
} from '../api/client'
import {
  ImpedanceTraceKind,
  ResponsePhase,
  type ImpedanceFrame,
  type ImpedanceProgressFrame,
} from '../streaming/protocol'

/**
 * The impedance measurement's client-side state.
 *
 * Module-level, the same stand-in for a store the rest of this app uses: the chart, the config
 * panel, the readout and the progress line are separate components all looking at one measurement.
 *
 * The measurement runs on the server, so this is a mirror and never the source of truth. It is fed
 * two ways and needs both: live Impedance/Progress frames off the existing /ws/stream socket while a
 * run is going, and `refresh()` against the status endpoint for anything that happened before this
 * browser was looking — a reload mid-run, or a second tab.
 *
 * **One state, unlike useFrequencyResponse.** That store carries two because a chirp and a stepped
 * sine are two server-side runners producing the same curves, and sharing refs put one's results on
 * the other's screen. There is one impedance runner, and its stimulus is a field on the request
 * rather than a separate kind, so there is nothing here to keep apart.
 */

/** One curve as the UI holds it. Plain arrays rather than the wire's Float32Array, so a stored result and a live one are the same shape. */
export interface ImpedanceTrace {
  kind: ImpedanceTraceKindName
  startFreqHz: number
  endFreqHz: number
  freqHz: number[]
  /** dB relative to 1 ohm for an Impedance curve; a plain dB ratio for a calibration standard. */
  magnitudeMinDbOhms: number[]
  magnitudeMaxDbOhms: number[]
  phaseDeg: number[]
  coherence: number[]
  peakDbOhms: number
  peakFreqHz: number
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
const referenceOhms = ref(100)
const rightChannelIsReference = ref(false)
const calTarget = ref<ImpedanceCalTargetName>('None')
const error = ref<string | null>(null)
const warning = ref<string | null>(null)
const medianOhms = ref<number | null>(null)
const driveCurrentAmps = ref<number | null>(null)

/** What the run on screen played. The effective one, so a chirp calibration inside a stepped measurement says Chirp. */
const stimulus = ref<ImpedanceStimulusName>('Chirp')

/**
 * Points finished and points in total, for a stepped run.
 *
 * <p>Null — not zero — on a chirp run, and the progress line branches on that rather than on the
 * phase. A stepped <i>calibration</i> reports Calibrating and still counts points, so a
 * phase-driven label would call it records.</p>
 */
const pointsMeasured = ref<number | null>(null)
const pointCount = ref<number | null>(null)

/**
 * Which stimulus the config panel currently has selected, published here for App.vue.
 *
 * <p>Separate from {@link stimulus}, which describes the run that produced what is on screen. This
 * one describes what the next RUN would do, and is what hides the continuous-run button: a stepped
 * sweep is single-shot, and the server rejects a continuous one.</p>
 */
const selectedStimulus = ref<ImpedanceStimulusName>('Chirp')

/**
 * The standard the most recent calibration run captured. The config panel watches this and writes it
 * into the measurement type's settings — the server keeps no calibration, so this is the only route
 * from a cal run to somewhere it survives a restart.
 */
const capturedCalibration = shallowRef<ImpedanceCalibrationJson | null>(null)

/**
 * The calibration standards currently in force — the ones stored on the measurement type, which the
 * config panel owns and writes here whenever they change.
 *
 * <p>Separate from {@link capturedCalibration}, which is only ever the standard the <i>last run</i>
 * produced. The chart wants the one that is actually being applied, and that has to survive a
 * reload and a subsequent measurement; a captured curve is gone the moment anything else runs.</p>
 */
const storedOpenCal = shallowRef<ImpedanceCalibrationJson | null>(null)
const storedShortCal = shallowRef<ImpedanceCalibrationJson | null>(null)

// shallowRef + whole-array replacement: a curve is immutable once measured, and replacing the array
// is what makes the chart's watcher fire without deep-watching thousands of numbers.
const traces = shallowRef<ImpedanceTrace[]>([])

const phaseLabel: Record<ResponsePhase, string> = {
  [ResponsePhase.Idle]: 'Idle',
  [ResponsePhase.DeterminingLevel]: 'Determining level',
  [ResponsePhase.Measuring]: 'Measuring',
  [ResponsePhase.Finished]: 'Finished',
  [ResponsePhase.Cancelled]: 'Cancelled',
  [ResponsePhase.Failed]: 'Failed',
  [ResponsePhase.DeterminingRange]: 'Determining input range',
  [ResponsePhase.Sweeping]: 'Sweeping',
  [ResponsePhase.Analyzing]: 'Analyzing',
  [ResponsePhase.Calibrating]: 'Calibrating',
  [ResponsePhase.WaitingForLoadChange]: 'Waiting for the load to be changed',
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
  Analyzing: ResponsePhase.Analyzing,
  Calibrating: ResponsePhase.Calibrating,
  WaitingForLoadChange: ResponsePhase.WaitingForLoadChange,
}

const traceKindNames: Record<ImpedanceTraceKind, ImpedanceTraceKindName> = {
  [ImpedanceTraceKind.Impedance]: 'Impedance',
  [ImpedanceTraceKind.OpenCal]: 'OpenCal',
  [ImpedanceTraceKind.ShortCal]: 'ShortCal',
}

const progressLabel = computed(() => {
  if (phase.value === ResponsePhase.DeterminingRange) return 'Determining input range'

  // A stepped run counts points, whatever phase it is in — a stepped calibration reports
  // Calibrating and still walks a grid, so this cannot branch on the phase.
  const points = pointCount.value
  const stepped = points !== null && points > 0

  if (phase.value === ResponsePhase.Calibrating) {
    const what = calTarget.value === 'Short' ? 'short' : 'open'
    if (stepped) return `Capturing the ${what} standard · point ${(pointsMeasured.value ?? 0) + 1} of ${points}`

    const record =
      averageCount.value > 1 ? ` · record ${averagesTaken.value} of ${averageCount.value}` : ''
    return `Capturing the ${what} standard${record}`
  }

  if (phase.value === ResponsePhase.Sweeping) {
    const averages = averageCount.value > 1 ? ` · ${averageCount.value} averages` : ''
    return `Point ${(pointsMeasured.value ?? 0) + 1} of ${points ?? '?'}${averages}`
  }

  if (phase.value === ResponsePhase.Finished && stepped) {
    const measured = pointsMeasured.value ?? 0
    return measured < (points ?? 0) ? `Stopped after ${measured} of ${points} points` : `Finished · ${points} points`
  }

  if (phase.value === ResponsePhase.Cancelled && stepped) {
    return `Stopped after ${pointsMeasured.value ?? 0} of ${points} points`
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

  return phaseLabel[phase.value] ?? 'Idle'
})

/** The DUT curve, which is the one every chart and readout draws. Null while only a calibration has been captured. */
const impedanceTrace = computed(() => traces.value.find((t) => t.kind === 'Impedance') ?? null)

function fromJson(trace: ImpedanceTraceJson): ImpedanceTrace {
  return {
    kind: trace.kind,
    startFreqHz: trace.startFreqHz,
    endFreqHz: trace.endFreqHz,
    freqHz: trace.freqHz,
    magnitudeMinDbOhms: trace.magnitudeMinDbOhms,
    magnitudeMaxDbOhms: trace.magnitudeMaxDbOhms,
    phaseDeg: trace.phaseDeg,
    coherence: trace.coherence,
    peakDbOhms: trace.peakDbOhms,
    peakFreqHz: trace.peakFreqHz,
  }
}

function fromFrame(frame: ImpedanceFrame): ImpedanceTrace {
  return {
    kind: traceKindNames[frame.kind] ?? 'Impedance',
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

function applyStatus(status: ImpedanceStatus) {
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
  referenceOhms.value = status.referenceOhms
  rightChannelIsReference.value = status.rightChannelIsReference === true
  calTarget.value = status.calTarget ?? 'None'
  error.value = status.error
  warning.value = status.warning ?? null
  capturedCalibration.value = status.capturedCalibration
  medianOhms.value = status.medianOhms
  driveCurrentAmps.value = status.driveCurrentAmps
  stimulus.value = status.stimulus ?? 'Chirp'
  pointsMeasured.value = status.pointsMeasured ?? null
  pointCount.value = status.pointCount ?? null
  traces.value = status.traces.map(fromJson)
}

/**
 * Folds one live curve in, replacing whatever was there for the same kind. Keyed by kind rather than
 * appended, so a rerun replaces the previous curve instead of stacking another on top.
 */
function ingestTrace(frame: ImpedanceFrame) {
  const trace = fromFrame(frame)
  const rest = traces.value.filter((t) => t.kind !== trace.kind)
  traces.value = [...rest, trace]

  // A continuous run sends one curve per pass, so counting them is what advances the pass number
  // without needing a frame of its own.
  if (continuous.value && trace.kind === 'Impedance') passesCompleted.value += 1
}

async function refreshStatus() {
  applyStatus(await api.getImpedanceStatus())
}

function ingestProgress(frame: ImpedanceProgressFrame) {
  const wasRunning = running.value

  phase.value = frame.phase
  generatorAmpDbv.value = frame.generatorAmpDbv
  running.value =
    frame.phase === ResponsePhase.Measuring ||
    frame.phase === ResponsePhase.Sweeping ||
    frame.phase === ResponsePhase.Calibrating ||
    frame.phase === ResponsePhase.DeterminingRange

  // A run that has just ended has to be read back from the status, because the things it produced
  // are not in any frame: the captured calibration curve, the median impedance and the drive
  // current all live on the status alone. Curves arrive over the socket, so without this the screen
  // looks finished and right while the panel still says "Open: not captured" and the readout still
  // shows dashes — and the calibration is never written into the measurement type either, because
  // the config panel folds it in by watching that same value. It stayed hidden because switching
  // measurement kinds *does* refresh, so navigating away and back appeared to fix it.
  if (wasRunning && !running.value) {
    void refreshStatus().catch(() => undefined)
    return
  }

  // The range probe reuses this frame with a pass count of its own, which is not a record count —
  // taking its total would rewrite the readout and leave it wrong for the rest of the run.
  if (frame.phase === ResponsePhase.DeterminingRange) return

  // One frame, two meanings: points for a stepped run, records for a chirp. Which it is was settled
  // by the start reply, so this reads the mode rather than trying to infer it from the numbers.
  if (pointCount.value !== null) {
    pointsMeasured.value = frame.taken
    pointCount.value = frame.total
    return
  }

  averagesTaken.value = frame.taken
  averageCount.value = frame.total
}

export function useImpedance() {
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
    referenceOhms,
    rightChannelIsReference,
    calTarget,
    error,
    warning,
    traces,
    impedanceTrace,
    capturedCalibration,
    storedOpenCal,
    storedShortCal,
    medianOhms,
    driveCurrentAmps,
    stimulus,
    pointsMeasured,
    pointCount,
    selectedStimulus,
    progressLabel,

    ingestTrace,
    ingestProgress,

    refresh: refreshStatus,

    /**
     * Clears the previous curve locally the moment a run starts, rather than waiting for the new
     * one: a chart still showing the last measurement while a new one is under way reads as though
     * nothing is happening.
     */
    async start(req: ImpedanceStartRequest) {
      error.value = null
      traces.value = []
      capturedCalibration.value = null
      continuous.value = req.continuous
      passesCompleted.value = 0
      medianOhms.value = null
      driveCurrentAmps.value = null
      running.value = true
      calTarget.value = req.calTarget
      stimulus.value = req.calTarget === 'None' ? req.stimulus : req.calibrationStimulus

      // Cleared rather than left over: the previous run's point count decides how the progress line
      // reads, so a stale one would make a chirp run count points it does not have.
      pointsMeasured.value = null
      pointCount.value = null
      referenceOhms.value = req.referenceOhms
      rightChannelIsReference.value = req.rightChannelIsReference
      phase.value =
        req.calTarget === 'None' ? ResponsePhase.Measuring : ResponsePhase.Calibrating

      try {
        const started = await api.startImpedance(req)
        sampleRateHz.value = started.sampleRateHz
        fftSize.value = started.fftSize
        binHz.value = started.binHz
        averageCount.value = started.averageCount
        stimulus.value = started.stimulus ?? stimulus.value
        pointCount.value = started.pointCount ?? null
        pointsMeasured.value = started.pointCount === null ? null : 0

        // A stepped run reports Sweeping, not Measuring — set here so the progress line is right
        // before the first frame arrives rather than a record later.
        if (started.pointCount !== null && req.calTarget === 'None') phase.value = ResponsePhase.Sweeping

        return started
      } catch (e) {
        running.value = false
        phase.value = ResponsePhase.Failed
        error.value = e instanceof Error ? e.message : String(e)
        throw e
      }
    },

    async cancel() {
      applyStatus(await api.cancelImpedance())
    },

    async clear() {
      applyStatus(await api.clearImpedance())
    },
  }
}
