import { computed, ref, shallowRef } from 'vue'
import { api, type ImdMethodName, type ImdSweepStartRequest, type ImdSweepStatus, type ThdSweepPoint } from '../api/client'
import { imdFrameToDistortionPoint, imdStatusPointToDistortionPoint } from '../imd/points'
import { SweepPhase, type ImdSweepPointFrame, type SweepProgressFrame } from '../streaming/protocol'
import { formatVolts } from './useSweep'

/**
 * The IMD sweep's client-side state — useSweep's shape for its own runner. Its own store rather
 * than a third axis on useSweep: the two runners are independent server-side (one can run while the
 * other holds a finished result), so the two screens must not share a point list or a phase.
 *
 * Points are kept in the chart's own distortion-point shape (see imd/points.ts) so ThdSweepChart,
 * the alignment, the previews and the report draw them unchanged; only the trace labels differ.
 * Like useSweep this is a mirror of the runner, fed by live frames and by `refresh()` against
 * GET /api/imd-sweep/status for whatever happened before this browser was looking.
 */

const running = ref(false)
const phase = ref<SweepPhase>(SweepPhase.Idle)
const index = ref(0)
const total = ref(0)
/** The sine-equivalent generator level of the point being measured, in volts. */
const currentLevelVolt = ref(0)
const error = ref<string | null>(null)
const method = ref<ImdMethodName>('Smpte')
const maxOrder = ref(5)

const measured = shallowRef<ThdSweepPoint[]>([])
const points = computed(() => measured.value)

const phaseLabel: Record<SweepPhase, string> = {
  [SweepPhase.Idle]: 'Idle',
  [SweepPhase.DeterminingLevel]: 'Starting',
  [SweepPhase.NoiseFloor]: 'Measuring noise floor',
  [SweepPhase.Sweeping]: 'Sweeping',
  [SweepPhase.Finished]: 'Finished',
  [SweepPhase.Cancelled]: 'Cancelled',
  [SweepPhase.Failed]: 'Failed',
}

const PHASE_BY_NAME: Record<string, SweepPhase> = {
  Idle: SweepPhase.Idle,
  DeterminingLevel: SweepPhase.DeterminingLevel,
  NoiseFloor: SweepPhase.NoiseFloor,
  Sweeping: SweepPhase.Sweeping,
  Finished: SweepPhase.Finished,
  Cancelled: SweepPhase.Cancelled,
  Failed: SweepPhase.Failed,
}

function applyStatus(status: ImdSweepStatus) {
  running.value = status.running
  phase.value = PHASE_BY_NAME[status.phase] ?? SweepPhase.Idle
  index.value = status.index
  total.value = status.total
  currentLevelVolt.value = status.currentLevelVolt
  error.value = status.error
  method.value = status.method
  maxOrder.value = status.maxOrder
  measured.value = status.points.map(imdStatusPointToDistortionPoint)
}

/** Keyed by (channel, level), which is what identifies a point on an amplitude axis — every point shares the tones. */
function ingestPoint(frame: ImdSweepPointFrame) {
  const point = imdFrameToDistortionPoint(frame)
  const next = measured.value.filter((p) => !(p.channel === point.channel && p.generatorDbv === point.generatorDbv))
  next.push(point)
  next.sort((a, b) => a.generatorDbv - b.generatorDbv || a.channel - b.channel)
  measured.value = next
}

function ingestProgress(frame: SweepProgressFrame) {
  const wasRunning = running.value
  phase.value = frame.phase
  index.value = frame.index
  currentLevelVolt.value = frame.currentX
  if (frame.total > 0) total.value = frame.total
  running.value =
    frame.phase === SweepPhase.DeterminingLevel ||
    frame.phase === SweepPhase.NoiseFloor ||
    frame.phase === SweepPhase.Sweeping

  // The frame carries no message; the reason lives on the status endpoint.
  if (frame.phase === SweepPhase.Failed) void refresh()
  // A sweep another tab started announces itself through its progress frames only.
  if (!wasRunning && running.value) void refresh()
}

async function refresh() {
  applyStatus(await api.getImdSweepStatus())
}

async function start(request: ImdSweepStartRequest) {
  measured.value = []
  error.value = null
  index.value = 0
  currentLevelVolt.value = 0
  const started = await api.startImdSweep(request)
  // Optimistic, so the progress line appears on the click rather than on the first frame.
  running.value = true
  phase.value = SweepPhase.DeterminingLevel
  total.value = started.pointCount
  method.value = request.method
  maxOrder.value = request.maxOrder
  return started
}

async function cancel() {
  applyStatus(await api.cancelImdSweep())
}

/** Server-side for the reason useSweep's is: the runner owns the points, and a local clear would return on the next status read. */
async function clear() {
  applyStatus(await api.clearImdSweep())
}

export function useImdSweep() {
  return {
    running,
    phase,
    index,
    total,
    currentLevelVolt,
    error,
    method,
    maxOrder,
    points,

    /** "Point 7/31 · 250 mV" — the progress readout, useSweep's style. */
    progressLabel: computed(() => {
      if (phase.value === SweepPhase.Idle) return ''
      if (phase.value === SweepPhase.Sweeping && total.value > 0)
        return `Point ${Math.min(index.value + 1, total.value)}/${total.value} · ${formatVolts(currentLevelVolt.value)}`
      if (phase.value === SweepPhase.Finished && total.value > 0) return `Finished · ${total.value} points`
      return phaseLabel[phase.value]
    }),

    start,
    cancel,
    clear,
    refresh,
    ingestPoint,
    ingestProgress,
  }
}
