import { ref } from 'vue'
import { useProjects } from './useProjects'

/** What a brand-new measurement type (or one saved before this feature existed) shows. */
export const DEFAULT_SCOPE_PERIODS = 4

/**
 * Hard ceiling on the field, not a promise: a scope frame is a fixed 2048 samples
 * (SpectrumPipeline.ScopeSampleCount), so how many periods actually fit depends on the tone
 * frequency and sample rate — 64 periods of 1 kHz at 192 kHz would need 12288 samples. The chart
 * steps the count down to whatever whole periods the buffer really holds; see
 * OscilloscopeChart.vue's findTriggerWindow.
 */
export const MAX_SCOPE_PERIODS = 64

// Module-level singleton, same pattern as useMetricSelection.ts / useChartDisplay.ts — the field
// lives in the oscilloscope card's own header and the value is read by the chart's trigger-window
// search, and both must see the same number.
const scopePeriods = ref(DEFAULT_SCOPE_PERIODS)

export function useScopeDisplay() {
  /** User edit (the Periods field) — persisted on the next measurement-type save, like any other staged setting. */
  function setScopePeriods(periods: number) {
    const clamped = Math.min(MAX_SCOPE_PERIODS, Math.max(1, Math.round(periods)))
    if (clamped === scopePeriods.value) return
    scopePeriods.value = clamped
    useProjects().markDirty()
  }

  /**
   * Syncs from a loaded measurement type's stored value (see App.vue's watch on loadedParams).
   * Not a user edit, so it never marks dirty. Null/undefined — a type saved before this feature
   * existed, or a freshly created one — falls back to DEFAULT_SCOPE_PERIODS, which is the count
   * the chart used before the field existed.
   */
  function applyLoaded(periods: number | null | undefined) {
    scopePeriods.value = periods && periods > 0 ? Math.min(MAX_SCOPE_PERIODS, Math.round(periods)) : DEFAULT_SCOPE_PERIODS
  }

  return { scopePeriods, setScopePeriods, applyLoaded }
}
