import { onBeforeUnmount, onMounted, ref, watch, type Ref } from 'vue'
import { expandedChart } from '../composables/useExpandedChart'
import { MIN_RESULT_CHART_HEIGHT_PX, RESULT_CHART_HEIGHT_PX } from './dimensions'

export interface ResultChartHeightOptions {
  /**
   * An element above the chart in the same column whose height the fit should also leave room
   * for — the FFT screen's metrics row, so the chart and its readings share the view. Observed
   * for size changes of its own, since a metric added or removed changes it without the window
   * moving.
   */
  above?: Ref<HTMLElement | null | undefined>
  /**
   * How much of `above` to reserve. The default reserves all of it plus its bottom margin; the
   * FFT screen passes a function that reserves one line of tiles however many lines there are,
   * because a wall of metrics must not shrink the chart into a strip.
   */
  aboveHeight?: (el: HTMLElement) => number
}

/** The whole element plus its bottom margin — what a block above the chart costs the column. */
export function fullHeightWithMargin(el: HTMLElement): number {
  return el.getBoundingClientRect().height + parseFloat(getComputedStyle(el).marginBottom)
}

/**
 * The plot height that lets a whole result chart fit the measurement view: the window, less the
 * app header and the status bar, less the view's own padding, less the chart's own furniture — its
 * readout row, the axis labels it draws outside the plot, whatever else the component puts around
 * the plot box. Scrolled to the top of the view, the chart is then in view completely, and on any
 * screen tall enough the chart simply takes the height it is given.
 *
 * Two arguments, both elements the component already has: `root` is the component's outermost
 * element and `plot` the box whose height this sets. The furniture is measured as the difference
 * between the two rather than listed, so a chart that gains a row of legend, or loses one, keeps
 * fitting without being told. The visible height is `<main>`'s — it is the scroll container, so
 * its client height already excludes the header, the status bar, the project panel and the update
 * banner, and its padding is read rather than assumed.
 *
 * An expanded chart (ExpandToggle pins `root` over the viewport) is the one case where the
 * furniture cannot be measured live: a root at `position: fixed; inset: 0` is the window's height
 * whatever its content. The last furniture measured in the page is used instead, corrected for
 * whatever padding the `[data-chart-expanded]` rule in style.css adds, and the visible height is
 * the window's. Anything reserved `above` is not reserved there — the chart is alone on the
 * screen.
 *
 * Only `<main>` (and `above`) is observed. Observing `root` as well would be natural and is
 * deliberately not done: a height written here resizes `root` in the same rendering step, which
 * Chrome reports as "ResizeObserver loop completed with undelivered notifications" — harmless,
 * and an error in the console on every window resize. The furniture only changes when the width
 * does, and a width change resizes `<main>` too.
 *
 * The value starts at the fixed fallback and is measured in `onMounted`, which runs before the
 * component's own `onMounted` provided this is called first in setup — so a plot built on mount is
 * built at the right height, not rebuilt to it. Note that the plot box may be empty at that moment
 * (a uPlot instance not yet constructed); the furniture is then root's whole height, which is the
 * right answer.
 */
export function useResultChartHeight(
  root: Ref<HTMLElement | null | undefined>,
  plot: Ref<HTMLElement | null | undefined>,
  options: ResultChartHeightOptions = {},
): Ref<number> {
  const height = ref(RESULT_CHART_HEIGHT_PX)
  const aboveHeight = options.aboveHeight ?? fullHeightWithMargin
  let mainObserver: ResizeObserver | null = null
  let aboveObserver: ResizeObserver | null = null
  let observedAbove: HTMLElement | null = null
  // The page's furniture and root padding, kept for the expanded state, where neither can be measured.
  let furniture = 0
  let rootPadding = 0

  function verticalPadding(el: Element): number {
    const style = getComputedStyle(el)
    return parseFloat(style.paddingTop) + parseFloat(style.paddingBottom)
  }

  function measure() {
    const rootEl = root.value
    const plotEl = plot.value
    const main = rootEl?.closest('main')
    if (!rootEl || !plotEl || !main) return

    let visible: number
    if (expandedChart.value === rootEl) {
      visible = window.innerHeight - (verticalPadding(rootEl) - rootPadding)
    } else {
      furniture = rootEl.getBoundingClientRect().height - plotEl.getBoundingClientRect().height
      rootPadding = verticalPadding(rootEl)
      visible = main.clientHeight - verticalPadding(main)
      const above = options.above?.value
      if (above) visible -= aboveHeight(above)
    }
    height.value = Math.max(MIN_RESULT_CHART_HEIGHT_PX, Math.round(visible - furniture))
  }

  function observeAbove(el: HTMLElement | null | undefined) {
    if (observedAbove) aboveObserver?.unobserve(observedAbove)
    observedAbove = el ?? null
    if (observedAbove) aboveObserver?.observe(observedAbove)
  }

  onMounted(() => {
    measure()
    if (typeof ResizeObserver === 'undefined') return
    const main = root.value?.closest('main')
    if (main) {
      mainObserver = new ResizeObserver(measure)
      mainObserver.observe(main)
    }
    if (options.above) {
      aboveObserver = new ResizeObserver(measure)
      observeAbove(options.above.value)
      watch(options.above, (el) => {
        observeAbove(el)
        measure()
      })
    }
    // The expanded flag flips before the attribute's rule has laid the root out, so the measure
    // waits a frame; the ResizeObserver on main is no help here, since main does not move.
    watch(expandedChart, () => requestAnimationFrame(measure))
  })
  onBeforeUnmount(() => {
    mainObserver?.disconnect()
    aboveObserver?.disconnect()
    mainObserver = aboveObserver = null
  })

  return height
}
