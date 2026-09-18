import { computed, ref, shallowRef, type InjectionKey, type Ref } from 'vue'
import {
  api,
  type ThdAmplitudeSweepStartRequest,
  type ThdMethodName,
  type ThdSweepPoint,
  type ThdSweepStartRequest,
  type ThdSweepStatus,
} from '../api/client'
import { SweepPhase, type SweepPointFrame, type SweepProgressFrame } from '../streaming/protocol'
import { siPrefixed } from '../sweep/xAxis'

/**
 * The sweep's client-side state — one store for both axes, because there is one runner and so only
 * ever one sweep in flight.
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
/** Which quantity the running (or last) sweep steps. Mirrors ThdSweepStatus.axis. */
const axis = ref<'Frequency' | 'Amplitude'>('Frequency')

/** The point being measured, in the running sweep's own unit — hertz, or generator volts. */
const currentX = ref(0)

/**
 * How the running (or last) sweep's harmonics are referenced — which of a point's two sets of
 * figures the chart draws. Mirrors ThdSweepStatus.thdMethod; set optimistically from the start
 * request so the first point is drawn the right way rather than a status poll later.
 */
const thdMethod = ref<ThdMethodName>('Conventional')

/**
 * The end frequency the running (or last) frequency sweep was asked for. A normalized sweep
 * measures past it (reference points for the harmonics at the top of the band), and Autofit fits
 * the band that was asked for, not the reference points. Null when unknown — a sweep found already
 * running by a reload, whose request this browser never saw.
 */
const requestedEndFreqHz = ref<number | null>(null)
const generatorAmpDbv = ref(0)
const error = ref<string | null>(null)

// shallowRef + whole-array replacement: a sweep point is immutable once measured, and replacing
// the array is what makes the chart's watcher fire without deep-watching hundreds of objects.
const measured = shallowRef<ThdSweepPoint[]>([])

/**
 * Which sweep screen is open, set by App.vue. There is one runner and so one set of points, but two
 * screens that can display them, and a set measured on one axis is meaningless on the other: an
 * amplitude sweep's fifty points all share one frequency, so a frequency chart would stack the
 * whole run into a single vertical line.
 */
const viewAxis = ref<'Frequency' | 'Amplitude'>('Frequency')

/**
 * The points the open screen should draw: the ones measured, or nothing at all when they came from
 * the other axis. Deliberately not a server-side clear — the sweep is still there, and switching
 * back to the screen that ran it shows it again.
 */
const points = computed(() => (axis.value === viewAxis.value ? measured.value : []))

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
  axis.value = status.axis ?? 'Frequency'
  currentX.value = axis.value === 'Amplitude' ? status.currentAmplitudeVolt : status.currentFreqHz
  generatorAmpDbv.value = status.generatorAmpDbv
  error.value = status.error
  thdMethod.value = status.thdMethod ?? 'Conventional'
  measured.value = status.points
}

/**
 * Folds one live sweep-point frame in. Keyed by (channel, frequency, level) rather than appended
 * blindly: a reload that calls refresh() and then starts receiving frames again would otherwise
 * duplicate every point it had already loaded. The same key is what lets a frequency-normalized
 * sweep re-send a point as its normalized figures fill in — the later frame replaces the earlier.
 *
 * Both coordinates are in the key, and neither alone would do — a frequency sweep repeats its level
 * at every point and an amplitude sweep repeats its frequency at every point, so keying on either
 * one on its own collapses the other axis' entire sweep into a single point.
 */
function ingestPoint(frame: SweepPointFrame) {
  const next = measured.value.filter(
    (p) => !(p.channel === frame.channel && p.freqHz === frame.freqHz && p.generatorDbv === frame.generatorDbv),
  )
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
    generatorDbv: frame.generatorDbv,
    previewFft: frame.previewFft,
    previewScope: frame.previewScope,
    normalizedThdPercent: frame.normalizedThdPercent,
    normalizedHarmonicPercent: frame.normalizedHarmonicPercent,
    normalizedD6PlusPercent: frame.normalizedD6PlusPercent,
  })
  next.sort((a, b) => a.freqHz - b.freqHz || a.generatorDbv - b.generatorDbv || a.channel - b.channel)
  measured.value = next
}

function ingestProgress(frame: SweepProgressFrame) {
  const wasRunning = running.value
  phase.value = frame.phase
  index.value = frame.index
  currentX.value = frame.currentX
  if (frame.total > 0) total.value = frame.total
  running.value =
    frame.phase === SweepPhase.DeterminingLevel ||
    frame.phase === SweepPhase.NoiseFloor ||
    frame.phase === SweepPhase.Sweeping

  // The frame carries no message; the reason lives on the status endpoint.
  if (frame.phase === SweepPhase.Failed) void refresh()

  // A sweep this browser did not start -- another tab, or a script against the API -- announces
  // itself only through its progress frames, which carry neither the method nor the axis, and
  // the points that follow would merge into whatever sweep was on screen before. The status has
  // all of it, so a sweep that starts from elsewhere is read from there.
  if (!wasRunning && running.value) void refresh()
}

async function refresh() {
  applyStatus(await api.getSweepStatus())
}

async function start(request: ThdSweepStartRequest) {
  const started = began('Frequency', await beginWith(() => api.startThdSweep(request)))
  thdMethod.value = request.thdMethod
  requestedEndFreqHz.value = request.endFreqHz
  return started
}

async function startAmplitude(request: ThdAmplitudeSweepStartRequest) {
  const started = began('Amplitude', await beginWith(() => api.startThdAmplitudeSweep(request)))
  thdMethod.value = 'Conventional'
  requestedEndFreqHz.value = null
  return started
}

async function beginWith<T>(call: () => Promise<T>): Promise<T> {
  measured.value = []
  error.value = null
  index.value = 0
  currentX.value = 0
  return call()
}

/** Optimistic, so the progress line appears on the click rather than on the first frame. */
function began(startedAxis: 'Frequency' | 'Amplitude', started: { pointCount: number }) {
  axis.value = startedAxis
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

/**
 * What ThdSweepChart needs to draw a run: the points and the method their figures are read under.
 * The screen's chart takes them from this composable; a headless render for a report provides a
 * stored result's instead, under this key — see report/renderChart.ts. An injection rather than
 * props so the screen's own wiring stays as it is.
 */
export interface SweepPointsSource {
  points: Readonly<Ref<ThdSweepPoint[]>>
  thdMethod: Readonly<Ref<ThdMethodName>>
}

export const SWEEP_POINTS_KEY: InjectionKey<SweepPointsSource> = Symbol('sweepPoints')

export function useSweep() {
  return {
    running,
    phase,
    index,
    total,
    axis,
    viewAxis,
    currentX,
    generatorAmpDbv,
    error,
    points,
    thdMethod,
    requestedEndFreqHz,

    /** "Point 7/31 · 1.26 kHz" (or "· 250 mV") — the progress readout, in the style of metrics.ts's formatAveraging. */
    progressLabel: computed(() => {
      if (phase.value === SweepPhase.Idle) return ''
      if (phase.value === SweepPhase.Sweeping && total.value > 0) {
        const where = axis.value === 'Amplitude' ? formatVolts(currentX.value) : formatHz(currentX.value)
        return `Point ${Math.min(index.value + 1, total.value)}/${total.value} · ${where}`
      }
      if (phase.value === SweepPhase.Finished && total.value > 0) {
        return `Finished · ${total.value} points`
      }
      return phaseLabel[phase.value]
    }),

    start,
    startAmplitude,
    cancel,
    clear,
    refresh,
    ingestPoint,
    ingestProgress,
  }
}

/** "250 mV" / "1.5 V" — the amplitude axis' counterpart of formatHz. */
export function formatVolts(volt: number): string {
  if (!(volt > 0)) return '—'
  return `${siPrefixed(volt)} V`
}

/** "1.26 kHz" / "250 Hz" — shared with the small preview charts' own frequency readout. */
export function formatHz(hz: number): string {
  if (!(hz > 0)) return '—'
  return hz >= 1000 ? `${(hz / 1000).toFixed(hz >= 10000 ? 1 : 2)} kHz` : `${hz.toFixed(hz >= 100 ? 0 : 1)} Hz`
}
