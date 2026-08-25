import { computed, ref, shallowRef } from 'vue'
import { api, type ThdSweepPoint, type ThdSweepStartRequest, type ThdSweepStatus } from '../api/client'
import { SweepPhase, type SweepPointFrame, type SweepProgressFrame } from '../streaming/protocol'

/**
 * The THD-vs-frequency sweep's client-side state.
 *
 * Module-level singleton refs, the same stand-in for a store the rest of this app uses (see
 * useSpectrumConfig.ts) — the chart, the config panel and the progress readout are separate
 * components all looking at one sweep.
 *
 * The sweep itself runs on the server, so this is a mirror, never the source of truth. It is fed
 * two ways, and both are needed: live SweepPoint/SweepProgress frames off the existing
 * /ws/stream socket while a sweep is running, and `refresh()` against GET /api/sweep/status for
 * everything that happened before this browser was looking — a reload mid-sweep, or a second tab.
 */

const running = ref(false)
const phase = ref<SweepPhase>(SweepPhase.Idle)
const index = ref(0)
const total = ref(0)
const currentFreqHz = ref(0)
const generatorAmpDbv = ref(0)
const error = ref<string | null>(null)

// shallowRef + whole-array replacement: a sweep point is immutable once measured, and replacing
// the array is what makes the chart's watcher fire without deep-watching hundreds of objects.
const points = shallowRef<ThdSweepPoint[]>([])

const phaseLabel: Record<SweepPhase, string> = {
  [SweepPhase.Idle]: 'Idle',
  [SweepPhase.DeterminingLevel]: 'Determining level',
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

function applyStatus(status: ThdSweepStatus) {
  running.value = status.running
  phase.value = PHASE_BY_NAME[status.phase] ?? SweepPhase.Idle
  index.value = status.index
  total.value = status.total
  currentFreqHz.value = status.currentFreqHz
  generatorAmpDbv.value = status.generatorAmpDbv
  error.value = status.error
  points.value = status.points
}

/**
 * Folds one live sweep-point frame in. Keyed by (channel, frequency) rather than appended blindly:
 * a reload that calls refresh() and then starts receiving frames again would otherwise duplicate
 * every point it had already loaded.
 */
function ingestPoint(frame: SweepPointFrame) {
  const next = points.value.filter((p) => !(p.channel === frame.channel && p.freqHz === frame.freqHz))
  next.push({
    freqHz: frame.freqHz,
    channel: frame.channel,
    fundamentalDbv: frame.fundamentalDbv,
    gainDb: frame.gainDb,
    powerWatt: frame.powerWatt,
    thdPercent: frame.thdPercent,
    thdNPercent: frame.thdNPercent,
    snrDb: frame.snrDb,
    harmonicPercent: frame.harmonicPercent,
    d6PlusPercent: frame.d6PlusPercent,
    noisePercent: frame.noisePercent,
    previewFft: frame.previewFft,
    previewScope: frame.previewScope,
  })
  next.sort((a, b) => a.freqHz - b.freqHz || a.channel - b.channel)
  points.value = next
}

function ingestProgress(frame: SweepProgressFrame) {
  phase.value = frame.phase
  index.value = frame.index
  currentFreqHz.value = frame.currentFreqHz
  if (frame.total > 0) total.value = frame.total
  running.value =
    frame.phase === SweepPhase.DeterminingLevel ||
    frame.phase === SweepPhase.NoiseFloor ||
    frame.phase === SweepPhase.Sweeping

  // The frame carries no message; the reason lives on the status endpoint.
  if (frame.phase === SweepPhase.Failed) void refresh()
}

async function refresh() {
  applyStatus(await api.getSweepStatus())
}

async function start(request: ThdSweepStartRequest) {
  points.value = []
  error.value = null
  index.value = 0
  currentFreqHz.value = 0

  const started = await api.startThdSweep(request)

  // Optimistic, so the progress line appears on the click rather than on the first frame.
  running.value = true
  phase.value = SweepPhase.DeterminingLevel
  total.value = started.pointCount
  return started
}

async function cancel() {
  applyStatus(await api.cancelSweep())
}

/**
 * Throws away the sweep on screen. Goes through the server rather than just emptying `points`
 * here: the runner owns them (that is what `refresh()` reads back), so a local clear would return
 * on the next status poll — a reload, a second tab, or simply leaving the sweep screen and coming
 * back. The response is the emptied status, so the progress line resets with the chart instead of
 * still reading "Finished · 50 points" over nothing.
 */
async function clear() {
  applyStatus(await api.clearSweep())
}

export function useSweep() {
  return {
    running,
    phase,
    index,
    total,
    currentFreqHz,
    generatorAmpDbv,
    error,
    points,

    /** "Point 7/31 · 1.26 kHz" — the progress readout, in the style of metrics.ts's formatAveraging. */
    progressLabel: computed(() => {
      if (phase.value === SweepPhase.Idle) return ''
      if (phase.value === SweepPhase.Sweeping && total.value > 0) {
        return `Point ${Math.min(index.value + 1, total.value)}/${total.value} · ${formatHz(currentFreqHz.value)}`
      }
      if (phase.value === SweepPhase.Finished && total.value > 0) {
        return `Finished · ${total.value} points`
      }
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

/** "1.26 kHz" / "250 Hz" — shared with the small preview charts' own frequency readout. */
export function formatHz(hz: number): string {
  if (!(hz > 0)) return '—'
  return hz >= 1000 ? `${(hz / 1000).toFixed(hz >= 10000 ? 1 : 2)} kHz` : `${hz.toFixed(hz >= 100 ? 0 : 1)} Hz`
}
