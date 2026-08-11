import { ref } from 'vue'
import { api, type MeasurementDetail, type MeasurementFilter, type MeasurementSummary, type SaveMeasurementRequest } from '../api/client'

// A distinct hue per overlay, cycling once more than 8 are on screen at once — unlikely, and a
// repeat is harmless since the list rows still disambiguate by name.
export const OVERLAY_COLORS = [
  '#a855f7', // violet
  '#14b8a6', // teal
  '#ec4899', // pink
  '#f59e0b', // amber
  '#84cc16', // lime
  '#06b6d4', // cyan
  '#ef4444', // red
  '#6366f1', // indigo
]

export interface OverlayEntry {
  /** Same as the underlying measurement_result id — re-adding an already-loaded result is a no-op, not a duplicate. */
  id: number
  detail: MeasurementDetail
  color: string
  visible: boolean
}

// Module-level singleton, same pattern as useSpectrumConfig.ts / useProjects.ts — the history
// panel, the overlay list, and the chart all need to see the same state.
const items = ref<MeasurementSummary[]>([])
const filter = ref<MeasurementFilter>({})
const overlays = ref<OverlayEntry[]>([])
const error = ref<string | null>(null)
let nextColorIndex = 0

export function useMeasurements() {
  async function load() {
    try {
      const response = await api.listMeasurements(filter.value)
      items.value = response.items
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  /** Saves the pipeline's currently retained (stopped) snapshot; throws on 409 (still acquiring, or nothing captured). */
  async function save(req: SaveMeasurementRequest) {
    try {
      const detail = await api.saveMeasurement(req)
      await load()
      error.value = null
      return detail
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  async function remove(id: number) {
    try {
      await api.deleteMeasurement(id)
      removeOverlay(id)
      await load()
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  /** Adds a stored result as a chart overlay. Re-adding one already on the chart just re-shows it (visible: true) rather than plotting a duplicate trace. */
  async function addOverlay(resultId: number) {
    try {
      const existing = overlays.value.find((o) => o.id === resultId)
      if (existing) {
        existing.visible = true
        return
      }
      const detail = await api.getMeasurement(resultId)
      const color = OVERLAY_COLORS[nextColorIndex % OVERLAY_COLORS.length]
      nextColorIndex++
      overlays.value = [...overlays.value, { id: resultId, detail, color, visible: true }]
      error.value = null
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  /** Detaches an overlay from the chart — does not delete the stored measurement (see remove() for that). */
  function removeOverlay(id: number) {
    overlays.value = overlays.value.filter((o) => o.id !== id)
  }

  function setOverlayVisible(id: number, visible: boolean) {
    const entry = overlays.value.find((o) => o.id === id)
    if (entry) entry.visible = visible
  }

  if (items.value.length === 0) load()

  return { items, filter, overlays, error, load, save, remove, addOverlay, removeOverlay, setOverlayVisible }
}
