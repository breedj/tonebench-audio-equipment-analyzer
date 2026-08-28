import { nextTick, ref, watch } from 'vue'
import { useProjects } from './useProjects'

// Module-level singleton, same pattern as useMeasurements.ts / useSpectrumConfig.ts — the chart
// itself and the display-options column (a separate sibling in the layout, not nested inside the
// chart's own card) both need to see the same state.
const DEFAULT_Y_TOP = 20
const DEFAULT_Y_BOTTOM = -150
const DEFAULT_X_FROM = 20
const DEFAULT_X_TO = 20000

const yTop = ref(DEFAULT_Y_TOP)
const yBottom = ref(DEFAULT_Y_BOTTOM)
const xFrom = ref(DEFAULT_X_FROM)
const xTo = ref(DEFAULT_X_TO)
const thickLines = ref(false)
const showDataPoints = ref(false)
const visibleL = ref(true)
const visibleR = ref(true)
const showMinMax = ref(true)
const showPeakHold = ref(false)

// Suppresses the dirty watcher below while applyLoaded writes these refs from a loaded type's
// stored params — loading is the opposite of an unsaved edit. Same pattern as
// SweepConfigPanel.vue's own `restoring` flag.
let restoring = false

// These eight are the ones persisted (see FftSpectrumParameters.ChartYTopDbv etc); activeXRange/
// the trigger counters are transient chart-owned state, never saved. No setter functions to route
// through (ChartDisplayPanel.vue binds straight to the refs via v-model), so this watches the
// values themselves instead — same effect as useSpectrumConfig.ts's apply() marking dirty, just
// triggered by the ref changing rather than a function call.
watch([yTop, yBottom, xFrom, xTo, thickLines, showDataPoints, showMinMax, showPeakHold], () => {
  if (!restoring) useProjects().markDirty()
})

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
    xFrom.value = DEFAULT_X_FROM
    xTo.value = DEFAULT_X_TO
  }

  /**
   * Syncs from a loaded measurement type's stored chart preferences (see App.vue's watch on
   * loadedParams). Not a user edit, so it never marks dirty. Null/undefined per field — a type
   * saved before this feature existed, or a freshly created one — falls back to that field's own
   * default, same as resetYDefaults/resetXDefaults.
   */
  function applyLoaded(loaded: {
    chartYTopDbv?: number | null
    chartYBottomDbv?: number | null
    chartXFromHz?: number | null
    chartXToHz?: number | null
    chartThickLines?: boolean | null
    chartShowDataPoints?: boolean | null
    chartShowMinMax?: boolean | null
    chartShowPeakHold?: boolean | null
  } | null | undefined) {
    restoring = true
    yTop.value = loaded?.chartYTopDbv ?? DEFAULT_Y_TOP
    yBottom.value = loaded?.chartYBottomDbv ?? DEFAULT_Y_BOTTOM
    xFrom.value = loaded?.chartXFromHz ?? DEFAULT_X_FROM
    xTo.value = loaded?.chartXToHz ?? DEFAULT_X_TO
    thickLines.value = loaded?.chartThickLines ?? false
    showDataPoints.value = loaded?.chartShowDataPoints ?? false
    showMinMax.value = loaded?.chartShowMinMax ?? true
    showPeakHold.value = loaded?.chartShowPeakHold ?? false
    void nextTick(() => {
      restoring = false
    })
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
    applyLoaded,
  }
}
