import { ref } from 'vue'
import { useProjects } from './useProjects'

/** What a brand-new measurement type (or one saved before this feature existed) shows. */
export const DEFAULT_SELECTED_METRICS = ['THD %', 'Pk dBV']

// Module-level singleton, same pattern as useChartDisplay.ts / useSpectrumConfig.ts — MetricsPanel
// (both channel instances), MetricPickerDialog and App.vue's save/load flow all need to see the
// same selection. Shared across L/R: a metric label picked once shows as "L: <label>" and
// "R: <label>" in each channel's own panel, not chosen independently per channel.
const selectedMetrics = ref<string[]>([...DEFAULT_SELECTED_METRICS])

export function useMetricSelection() {
  /** User edit (via MetricPickerDialog's Apply, or a tile's own remove button) — persisted on the next measurement-type save, like any other staged setting. */
  function setSelectedMetrics(labels: string[]) {
    selectedMetrics.value = labels
    useProjects().markDirty()
  }

  function removeMetric(label: string) {
    setSelectedMetrics(selectedMetrics.value.filter((l) => l !== label))
  }

  /**
   * Syncs from a loaded measurement type's stored selection (see App.vue's watch on loadedParams).
   * Not a user edit, so it never marks dirty. Null/undefined — a type saved before this feature
   * existed, or a freshly created one — falls back to the THD %/Pk dBV default per spec.
   */
  function applyLoaded(labels: string[] | null | undefined) {
    selectedMetrics.value = labels && labels.length > 0 ? [...labels] : [...DEFAULT_SELECTED_METRICS]
  }

  return { selectedMetrics, setSelectedMetrics, removeMetric, applyLoaded }
}
