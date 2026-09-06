import { ref } from 'vue'
import type uPlot from 'uplot'

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
    const b = bounds()
    u.setScale('x', { min: b.xMin, max: b.xMax })
    u.setScale('y', { min: b.yMin, max: b.yMax })
  }

  return { zoomed, isZoomedNow, noteScale, resetZoom }
}
