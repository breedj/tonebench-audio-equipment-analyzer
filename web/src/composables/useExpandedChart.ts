import { computed, onBeforeUnmount, onMounted, ref, watch, type Ref } from 'vue'

/**
 * The attribute an expanded chart's root carries. The `[data-chart-expanded]` rule in style.css
 * is what pins it over the viewport — an attribute rather than a class so Vue's own class
 * patching on that element never removes it.
 */
export const EXPANDED_ATTRIBUTE = 'data-chart-expanded'

/**
 * The one chart root currently expanded over the whole browser viewport, or null. Module-level:
 * only one chart can be expanded at a time, and useResultChartHeight reads it to know that a
 * root is pinned to the viewport rather than laid out in the page.
 */
export const expandedChart = ref<HTMLElement | null>(null)

/**
 * Expanding a chart in the page rather than through the Fullscreen API. The API was the first
 * implementation and was dropped: it is refused outright in embedded views (the desktop app's own
 * preview pane answers "Permissions check failed"), and a chart that cannot expand where the app
 * is being looked at is worse than one that expands to the window. So the root is pinned with
 * `position: fixed; inset: 0` over everything, which needs no permission anywhere, and Escape
 * brings it back the way it would from fullscreen.
 */
export function useExpandedChart(target: Ref<HTMLElement | null | undefined>) {
  const active = computed(() => !!target.value && expandedChart.value === target.value)

  function set(expanded: boolean) {
    const el = target.value
    if (!el) return
    if (expanded) {
      expandedChart.value?.removeAttribute(EXPANDED_ATTRIBUTE)
      el.setAttribute(EXPANDED_ATTRIBUTE, '')
      expandedChart.value = el
    } else if (expandedChart.value === el) {
      el.removeAttribute(EXPANDED_ATTRIBUTE)
      expandedChart.value = null
    }
  }

  function toggle() {
    set(!active.value)
  }

  function onKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape' && active.value) {
      event.preventDefault()
      set(false)
    }
  }

  onMounted(() => document.addEventListener('keydown', onKeydown))
  onBeforeUnmount(() => {
    document.removeEventListener('keydown', onKeydown)
    // A chart unmounted while expanded (the measurement was switched) must not leave the page
    // thinking something is still over it.
    set(false)
  })
  // The element itself may be replaced (a v-if around the chart), in which case the old one is
  // gone and the flag with it.
  watch(target, (el, old) => {
    if (old && expandedChart.value === old) expandedChart.value = null
    void el
  })

  return { active, toggle }
}
