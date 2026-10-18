import { computed, ref } from 'vue'

/** One chart's axis window: the x pair and the main y pair, in the axes' own units. */
export interface ZoomWindow {
  xMin: number
  xMax: number
  yMin: number
  yMax: number
}

/**
 * How many steps back a chart remembers. Deep enough that nobody reaches the end of it by hand —
 * a zoom is three or four drags at most — and bounded so a chart left open all day cannot grow a
 * list of windows nobody will ever walk back through.
 */
export const ZOOM_HISTORY_LIMIT = 24

/**
 * The windows a chart was looking at before each drag-zoom, newest first.
 *
 * <p>Asked for 2026-10-18: "when zooming-in show a back button which zooms-out to the previous
 * zoomed-in area". Reset zoom already went all the way back to the display panel's window; this is
 * the step-by-step way out, so a reader who drags twice can undo one of them.</p>
 *
 * <p>A chart pushes the window it is <i>leaving</i>, from its `setSelect` hook — uPlot fires that
 * before it applies the drag's own scales, so what the plot still holds at that moment is exactly
 * the window to come back to. The bottom of the stack is therefore the panel's own window, which is
 * why walking all the way back lands where Reset zoom would have.</p>
 */
export function useZoomHistory(limit: number = ZOOM_HISTORY_LIMIT) {
  const windows = ref<ZoomWindow[]>([])

  /** Whether there is a window to step back to — what the Back button is shown by. */
  const canGoBack = computed(() => windows.value.length > 0)

  /** Remembers the window a chart is leaving. A window identical to the last one remembered is not pushed twice. */
  function push(window: ZoomWindow) {
    if (!isFinite(window)) return
    const top = windows.value[windows.value.length - 1]
    if (top && same(top, window)) return

    const next = [...windows.value, window]
    windows.value = next.length > limit ? next.slice(next.length - limit) : next
  }

  /** The window to go back to, removed from the history; null when there is none. */
  function back(): ZoomWindow | null {
    const next = [...windows.value]
    const window = next.pop() ?? null
    windows.value = next
    return window
  }

  /** Forgets every step — a Reset zoom, or a rebuild that makes the old windows meaningless. */
  function clear() {
    if (windows.value.length > 0) windows.value = []
  }

  return { canGoBack, depth: computed(() => windows.value.length), push, back, clear }
}

/** A window with a real number on every edge: a scale that has not been laid out yet has nulls, and they are not a place to return to. */
function isFinite(w: ZoomWindow): boolean {
  return Number.isFinite(w.xMin) && Number.isFinite(w.xMax) && Number.isFinite(w.yMin) && Number.isFinite(w.yMax)
}

function same(a: ZoomWindow, b: ZoomWindow): boolean {
  return close(a.xMin, b.xMin) && close(a.xMax, b.xMax) && close(a.yMin, b.yMin) && close(a.yMax, b.yMax)
}

/** The same tolerant comparison `useZoomBounds` makes, for the same reason: a window may have been through a log/linear round trip. */
function close(a: number, b: number): boolean {
  return Math.abs(a - b) <= 1e-9 + Math.abs(b) * 1e-6
}
