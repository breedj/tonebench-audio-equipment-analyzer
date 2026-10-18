import { ref } from 'vue'
import type uPlot from 'uplot'
import { useZoomHistory, type ZoomWindow } from '../charts/zoomHistory'

/** Tolerant equality: a zoom is compared against bounds that may have been through a log/linear round trip. */
function sameBound(a: number | null | undefined, b: number): boolean {
  return a != null && Math.abs(a - b) <= 1e-9 + Math.abs(b) * 1e-6
}

/**
 * Tracks whether a chart's x/y scales still match the window its display-panel props (or, for
 * SpectrumChart, the data's own full span) ask for, and resets them back to it on request.
 *
 * Shared by every chart that offers cursor.drag zoom (FrequencyResponseChart, ThdSweepChart,
 * SpectrumChart) — each supplies its own notion of "the window that counts as not zoomed" via
 * `bounds()`, called fresh on every check since it typically reads from props/refs that change
 * independently of any actual zoom. `getPlot` is a getter rather than a Ref because two of the
 * three charts keep their uPlot instance in a plain module-scope `let`, not a ref.
 */
export function useZoomBounds(
  getPlot: () => uPlot | null,
  bounds: () => { xMin: number; xMax: number; yMin: number; yMax: number },
) {
  const zoomed = ref(false)
  const history = useZoomHistory()

  function isZoomedNow(u: uPlot): boolean {
    const b = bounds()
    return !(
      sameBound(u.scales.x.min, b.xMin) &&
      sameBound(u.scales.x.max, b.xMax) &&
      sameBound(u.scales.y?.min, b.yMin) &&
      sameBound(u.scales.y?.max, b.yMax)
    )
  }

  /** Call from a chart's `hooks.setScale` — change-gated so a no-op setData doesn't thrash the ref. */
  function noteScale(u: uPlot) {
    const next = isZoomedNow(u)
    if (next !== zoomed.value) zoomed.value = next
  }

  function resetZoom() {
    const u = getPlot()
    if (!u) return
    // All the way out, so the steps on the way in are no longer anywhere to go back to.
    history.clear()
    apply(u, bounds())
  }

  /**
   * Remembers the window the chart is about to leave — call from `hooks.setSelect`, which uPlot
   * fires *before* it applies the drag's own scales, so what the plot still holds is exactly the
   * window Back has to return to.
   */
  function noteZoomGesture(u: uPlot) {
    if (u.select.width <= 0 && u.select.height <= 0) return
    history.push(windowOf(u))
  }

  /** One step out: back to the window before the last drag, or to the panel's own at the bottom of the stack. */
  function zoomBack() {
    const u = getPlot()
    if (!u) return
    const previous = history.back()
    apply(u, previous ?? bounds())
  }

  /** The window on screen now, for the "Set axis" button to write into the display panel's boxes. */
  function currentWindow(): ZoomWindow | null {
    const u = getPlot()
    if (!u) return null
    const w = windowOf(u)
    return Number.isFinite(w.xMin) && Number.isFinite(w.yMin) ? w : null
  }

  return {
    zoomed,
    isZoomedNow,
    noteScale,
    resetZoom,
    noteZoomGesture,
    zoomBack,
    currentWindow,
    canGoBack: history.canGoBack,
    clearZoomHistory: history.clear,
  }
}

function windowOf(u: uPlot): ZoomWindow {
  return {
    xMin: u.scales.x.min ?? Number.NaN,
    xMax: u.scales.x.max ?? Number.NaN,
    yMin: u.scales.y?.min ?? Number.NaN,
    yMax: u.scales.y?.max ?? Number.NaN,
  }
}

function apply(u: uPlot, w: ZoomWindow) {
  u.setScale('x', { min: w.xMin, max: w.xMax })
  u.setScale('y', { min: w.yMin, max: w.yMax })
}
