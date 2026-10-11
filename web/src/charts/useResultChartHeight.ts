import { onBeforeUnmount, onMounted, ref, watch, type Ref } from 'vue'
import { expandedChart } from '../composables/useExpandedChart'
import { MIN_RESULT_CHART_HEIGHT_PX, RESULT_CHART_HEIGHT_PX } from './dimensions'

export interface ResultChartHeightOptions {
  /**
   * An element above the chart in the same column whose height the fit should reserve only part
   * of — the FFT screen's metrics row, so the chart and its readings share the view. Everything
   * above the chart is reserved in full by default; this names the one block that is not.
   * Observed for size changes of its own, since a metric added or removed changes it without the
   * window moving.
   */
  above?: Ref<HTMLElement | null | undefined>
  /**
   * How much of `above` to reserve. The default reserves all of it plus its bottom margin, i.e.
   * changes nothing; the FFT screen passes a function that reserves one line of tiles however
   * many lines there are, because a wall of metrics must not shrink the chart into a strip.
   */
  aboveHeight?: (el: HTMLElement) => number
  /**
   * A fixed drawing height to keep **in the page**, for a chart that is not the view's main one.
   *
   * <p>The oscilloscope and the spectrogram sit under the spectrum chart, and the two sweep previews
   * stack in the rail: fitting any of them to the view is exactly wrong, because several of them
   * share it. They keep the height they have always had, and this composable is used only for the
   * other half of its job - knowing that the root has been pinned over the viewport and giving it
   * the window. Without that an expanded preview is a 150 px plot adrift in a full-screen panel,
   * which is how the button would look broken rather than absent.</p>
   *
   * <p>The furniture is still measured in the page, because the expanded branch has no way to
   * measure it (a root at `inset: 0` is the window's height whatever it contains) and reads what
   * the page last saw.</p>
   */
  pageHeightPx?: number
}

/** The whole element plus its bottom margin — what a block above the chart costs the column. */
export function fullHeightWithMargin(el: HTMLElement): number {
  return el.getBoundingClientRect().height + parseFloat(getComputedStyle(el).marginBottom)
}

/**
 * Everything above the chart's root in the view: the measurement's toolbar row (its name, Save,
 * Save as…, Clear, the ? button) that `<main>` lays out above the layout, and whatever the screen
 * puts above the chart inside its column — the sweep and response screens' run-status line, the
 * FFT screen's metrics. Measured as the root's offset from main's content top rather than listed,
 * so it is whatever precedes the chart and not a list of what does today; `scrollTop` is added
 * back because the rectangle is where the root is drawn, not where it sits in the content. The
 * toolbar wraps onto two lines on a narrow window, which the ResizeObserver on main already sees.
 * Without this the fit was 26 px too tall on every screen (a 42 px toolbar row less main's 16 px
 * padding), 54 px on the screens with a status line, and the x axis's title sat below the fold
 * until the view was scrolled.
 */
function offsetAbove(main: HTMLElement, rootEl: HTMLElement): number {
  const contentTop = main.getBoundingClientRect().top + parseFloat(getComputedStyle(main).paddingTop)
  return rootEl.getBoundingClientRect().top - contentTop + main.scrollTop
}

/**
 * The plot height that lets a whole result chart fit the measurement view: the window, less the
 * app header and the status bar, less the view's own padding, less whatever sits above the chart
 * in the view (`offsetAbove`), less the chart's own furniture — its readout row, the axis
 * labels it draws outside the plot, whatever else the component puts around the plot box.
 * Scrolled to the top of the view, the chart is then in view completely, and on any screen tall
 * enough the chart simply takes the height it is given.
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
  // A secondary chart starts at its own height rather than the full-width fallback, so a plot
  // built on mount is built at the size it will keep instead of being resized a frame later.
  const height = ref(options.pageHeightPx ?? RESULT_CHART_HEIGHT_PX)
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

    if (expandedChart.value === rootEl) {
      const visible = window.innerHeight - (verticalPadding(rootEl) - rootPadding)
      height.value = Math.max(MIN_RESULT_CHART_HEIGHT_PX, Math.round(visible - furniture))
      return
    }

    furniture = rootEl.getBoundingClientRect().height - plotEl.getBoundingClientRect().height
    rootPadding = verticalPadding(rootEl)

    // Kept, not fitted, and deliberately not clamped to MIN_RESULT_CHART_HEIGHT_PX: that floor is
    // what stops a *result* chart squashing on a short window, and every secondary height here is
    // below it on purpose.
    if (options.pageHeightPx !== undefined) {
      height.value = options.pageHeightPx
      return
    }

    let visible = main.clientHeight - verticalPadding(main) - offsetAbove(main, rootEl)
    // The offset already holds the whole of `above`; the caller decides how much of it to keep.
    const above = options.above?.value
    if (above) visible += fullHeightWithMargin(above) - aboveHeight(above)
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
