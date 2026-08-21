import { ref } from 'vue'

// Module-level singleton, same pattern as useMeasurements.ts / useSpectrumConfig.ts — the chart
// itself and the display-options column (a separate sibling in the layout, not nested inside the
// chart's own card) both need to see the same state.
const DEFAULT_Y_TOP = 20
const DEFAULT_Y_BOTTOM = -180

const yTop = ref(DEFAULT_Y_TOP)
const yBottom = ref(DEFAULT_Y_BOTTOM)
const xFrom = ref(20)
const xTo = ref(20000)
const thickLines = ref(false)
const showDataPoints = ref(false)
const visibleL = ref(true)
const visibleR = ref(true)
const showMinMax = ref(true)
const showPeakHold = ref(false)

// The frequency span the spectrum chart is actually showing right now — the net result of the
// From/To dropdowns above, a drag-zoom on the chart, and the double-click reset, all of which end
// up in the same setScale('x'). Published by SpectrumChart.vue rather than derived here, because
// only the chart knows the full span (it comes off the streamed frames) and therefore what a zoom
// is relative to. Read by WaterfallChart.vue, which is stacked directly beneath it and has to
// share the exact same axis for the two to be readable as one picture. Null until the first frame
// establishes a span.
const activeXRange = ref<{ min: number; max: number } | null>(null)

// SpectrumChart.vue owns the actual per-bin peak data (module-level there, same reasoning as
// autofit below); this is just the button's "clear it" signal.
const resetPeakHoldTrigger = ref(0)

// "Autofit" needs the currently-plotted data (highest/lowest dBV point, current sample rate) to
// compute a real answer — data this composable doesn't have. The buttons just bump these counters;
// SpectrumChart.vue (which owns the data) watches them and does the actual computation, falling
// back to a plain default reset when nothing's plotted yet.
const autofitYTrigger = ref(0)
const autofitXTrigger = ref(0)

export function useChartDisplay() {
  function resetYDefaults() {
    yTop.value = DEFAULT_Y_TOP
    yBottom.value = DEFAULT_Y_BOTTOM
  }

  function resetXDefaults() {
    xFrom.value = 20
    xTo.value = 20000
  }

  function requestAutofitY() {
    autofitYTrigger.value++
  }

  function requestAutofitX() {
    autofitXTrigger.value++
  }

  function requestResetPeakHold() {
    resetPeakHoldTrigger.value++
  }

  return {
    yTop,
    yBottom,
    xFrom,
    xTo,
    thickLines,
    showDataPoints,
    visibleL,
    visibleR,
    showMinMax,
    showPeakHold,
    activeXRange,
    resetPeakHoldTrigger,
    autofitYTrigger,
    autofitXTrigger,
    requestAutofitY,
    requestAutofitX,
    requestResetPeakHold,
    resetYDefaults,
    resetXDefaults,
  }
}
