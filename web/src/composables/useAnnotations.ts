/**
 * Whose annotations are on the charts, and the one list that is drawn.
 *
 * <p>A module-level singleton, the pattern useChartDisplay and useMeasurements already use, and for
 * a reason that is specific to this feature: exactly one measurement is on screen at a time, and a
 * measurement keeps one flat list across all of its charts. So the FFT screen's rail, a sweep
 * screen's rail and the impulse screen's five charts can all read and write the same thing with no
 * props threaded through anything, which is what lets {@link AnnotationsPanel} be dropped into seven
 * display panels as one tag.</p>
 *
 * <p><b>There is more than one set to choose from</b>, which is the whole of what this module adds
 * beyond a ref. The open measurement has its working set, and every saved result has its own frozen
 * one; a chart can carry one of them legibly and no more. So {@link AnnotationSource} is a radio
 * selection — the open measurement, one result, or nothing — and {@link useAnnotations.shown} is the
 * set it names. Everything that <i>draws</i> reads `shown`; everything that <i>saves the
 * measurement</i> reads {@link useAnnotations.live}, and keeping those two apart is what stops an
 * edit to a stored result's notes marking the measurement dirty (Joost, 2026-10-18).</p>
 *
 * <p>It is <b>not</b> what a headless report render reads. `renderChartPng` shares module singletons
 * with the live app, so a stored result rendered through this one would draw whatever is on screen;
 * every chart therefore takes its list as a prop, and only the live screens pass this. That is the
 * same reason the FFT's report render injects its display source rather than importing it.</p>
 *
 * <p>It may import `api` and nothing else of ours. `useMeasurements` and `useProjects` are imported
 * by the screens that call in here, and `useProjects -> useMeasurements` is already the one allowed
 * direction between the stores; a third edge from here would make a cycle out of it.</p>
 */

import { computed, ref } from 'vue'
import { api } from '../api/client'
import { storedAnnotationFrame } from '../charts/annotationViews'
import { parseAnnotations, type AnnotationStyle, type ChartAnnotation } from '../measurements/chartAnnotations'

/**
 * Which set of annotations is drawn: `'live'` for the open measurement's own, a result id for one
 * saved result's, `null` for none.
 *
 * <p>A result id rather than an object, so the panel's and every row's "is this the one" test is
 * `source === item.resultId` — a comparison a template can make without a helper.</p>
 */
export type AnnotationSource = 'live' | number | null

/** The open measurement's working set: what a save writes, and what the dirty watcher watches. */
const live = ref<ChartAnnotation[]>([])

/**
 * The shown result's set, as stored.
 *
 * <p>One ref rather than a map keyed by result, because only one result's notes are ever drawn:
 * a map would be state nothing reads, and it would go stale against a database this app is not the
 * only writer of (the Edit dialog writes one too).</p>
 */
const resultAnnotations = ref<ChartAnnotation[]>([])

/**
 * Which one is drawn. <b>The open measurement by default</b> (Joost, 2026-10-18): a measurement's
 * own notes are on the chart the moment it is opened, exactly as they were before this was a
 * choice, so nothing already saved becomes invisible. Only the history rows start dark.
 */
const source = ref<AnnotationSource>('live')

/** The shown result's name, for the panel's caption. Null while the open measurement's are shown. */
const sourceName = ref<string | null>(null)

/** What went wrong reading or writing a result's notes, for the panel to show. */
const error = ref<string | null>(null)

/**
 * Which style the next click places, or null when no chart is armed — which is how a plain click
 * keeps doing nothing and every chart's drag-zoom keeps working. Transient: never saved, and a
 * report render has none of it.
 */
const armed = ref<AnnotationStyle | null>(null)

/**
 * Whether the ordinal badges are drawn on the labels. On everywhere except a report whose
 * Annotations table was not included, where a number would tie a label to a row nobody printed.
 */
const numbers = ref(true)

/**
 * How long a run of edits is collected before a shown result's notes are written back.
 *
 * <p>A stored result has no Save button anywhere on screen, so an edit made from the rail has to
 * persist itself; typing a label is a keystroke per character, and each one replaces the list. Long
 * enough to swallow a word, short enough that nobody switches measurement inside it — and
 * {@link flushPendingWrite} covers the case where somebody does.</p>
 */
const WRITE_DELAY_MS = 400

let writeTimer: ReturnType<typeof setTimeout> | null = null
let writeForResultId: number | null = null

const EMPTY: readonly ChartAnnotation[] = Object.freeze([])

export function useAnnotations() {
  /**
   * The set on the charts: the open measurement's, one result's, or none.
   *
   * <p>Writable, because this is what every chart binds `v-model:annotations` to — a placement, a
   * label drag and a box resize all come back through here. The setter puts them wherever `source`
   * points, and schedules the write when that is a result. A write while nothing is shown is
   * dropped rather than guessed at; it cannot arrive, because nothing can be placed on a chart that
   * is drawing nothing.</p>
   */
  const shown = computed<ChartAnnotation[]>({
    get() {
      if (source.value === 'live') return live.value
      if (typeof source.value === 'number') return resultAnnotations.value
      return EMPTY as ChartAnnotation[]
    },
    set(next) {
      if (source.value === 'live') {
        live.value = next
        return
      }
      if (typeof source.value === 'number') {
        resultAnnotations.value = next
        scheduleWrite(source.value)
      }
    },
  })

  /**
   * Arms a chart for the next click, or disarms it.
   *
   * <p>Arming <b>turns visibility on</b> when nothing is shown, and on the open measurement's own
   * set: a user who presses Add is asking for a note, and placing one onto a list the chart is not
   * drawing would look like the click having been missed (Joost, 2026-10-18). The open measurement
   * is the right target because this button lives in its own rail — reaching a result's notes is an
   * explicit click on that result's row.</p>
   */
  function arm(style: AnnotationStyle | null) {
    if (style !== null && source.value === null) {
      source.value = 'live'
      sourceName.value = null
    }
    armed.value = style
  }

  /**
   * Shows one set and hides the rest, or hides everything.
   *
   * <p>Passing the source that is already shown turns it <b>off</b>, which is what makes each icon a
   * toggle as well as a member of one group. Selecting a result reads that result's own stored
   * notes; a failure leaves nothing shown and puts the reason on {@link error}, rather than leaving
   * a lit icon over the previous result's list.</p>
   */
  async function show(next: AnnotationSource) {
    // Whatever is owed for the set being left has to go out first: the debounce is 400 ms and a
    // click is faster than that, so without this an edit made just before switching away is lost.
    await flushPendingWrite()

    const target: AnnotationSource = next !== null && next === source.value ? null : next
    armed.value = null
    error.value = null

    if (target === null) {
      source.value = null
      sourceName.value = null
      return
    }
    if (target === 'live') {
      source.value = 'live'
      sourceName.value = null
      return
    }

    // Always fetched, never read off a cached overlay `detail`: it is one request on an explicit
    // click, it answers for a row whose overlay is not loaded at all, and it guarantees the panel
    // is showing what is *stored* rather than what was stored when the overlay was plotted -- the
    // Edit dialog writes the same field.
    try {
      const detail = await api.getMeasurement(target)
      const stored = JSON.parse(detail.paramsJson) as { chartAnnotations?: unknown }
      resultAnnotations.value = parseAnnotations(stored.chartAnnotations, storedAnnotationFrame(detail.kind))
      source.value = target
      sourceName.value = detail.name
    } catch (e) {
      source.value = null
      sourceName.value = null
      error.value = `This result's annotations could not be read. ${(e as Error).message}`
      throw e
    }
  }

  /**
   * Replaces the open measurement's list from its stored params, without marking it dirty.
   *
   * <p>Called with an empty list for a measurement that has none, which is the part that matters:
   * every per-kind restore watcher only fires when its type has stored params, so a measurement that
   * has never been saved would otherwise keep the <i>previous</i> measurement's notes on screen.
   * That is deliberate for display preferences and wrong for notes, which belong to one
   * measurement.</p>
   *
   * <p>It also <b>puts the selection back on the open measurement</b>. A result of the measurement
   * being left behind must not stay lit — its notes would be drawn over a different measurement's
   * curves, and the panel would be editing a row the History list beside it no longer shows.</p>
   *
   * <p>It carries no `restoring` flag of its own: every caller is inside its kind's own
   * `applyStoredXDisplay`, which has already set that kind's flag, so the dirty watcher is suppressed
   * by the time this writes. A second flag here would be one more thing to keep in step for no
   * benefit.</p>
   */
  function applyLoadedLive(loaded: readonly ChartAnnotation[]) {
    // Fire-and-forget rather than awaited: this is called from synchronous restore paths, and what
    // is owed belongs to the result being left, not to the measurement arriving.
    void flushPendingWrite()
    live.value = [...loaded]
    resultAnnotations.value = []
    source.value = 'live'
    sourceName.value = null
    armed.value = null
    error.value = null
  }

  return {
    /** The set on the charts. Bind this; see {@link shown}. */
    shown,
    /** The open measurement's own set. Save this, and watch this for dirtiness. */
    live,
    source,
    sourceName,
    error,
    armed,
    numbers,
    arm,
    show,
    applyLoadedLive,
    flushPendingWrite,
  }
}

function scheduleWrite(resultId: number) {
  if (writeTimer !== null && writeForResultId !== resultId) {
    // Two results cannot be owed at once -- `show` flushes before it switches -- but if that ever
    // changes, losing the older edit silently is the one outcome worth ruling out here.
    void flushPendingWrite()
  }
  writeForResultId = resultId
  if (writeTimer !== null) clearTimeout(writeTimer)
  writeTimer = setTimeout(() => void flushPendingWrite(), WRITE_DELAY_MS)
}

/**
 * Writes a shown result's notes back now, if any are owed.
 *
 * <p>Exported because two callers have to be able to wait for it: {@link useAnnotations.show} before
 * it switches away, and a test that wants the write without waiting out the debounce.</p>
 */
async function flushPendingWrite() {
  if (writeTimer !== null) {
    clearTimeout(writeTimer)
    writeTimer = null
  }
  const id = writeForResultId
  writeForResultId = null
  if (id === null) return

  try {
    await api.setMeasurementAnnotations(id, resultAnnotations.value)
    error.value = null
  } catch (e) {
    // Said rather than thrown: this runs from a timer and from a switch, neither of which has a
    // caller to catch it, and a note that did not persist is exactly the thing the user has to be
    // told about before they close the app.
    error.value = `These annotations could not be saved. ${(e as Error).message}`
  }
}
