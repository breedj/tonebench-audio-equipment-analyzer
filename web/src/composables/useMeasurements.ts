import { ref } from 'vue'
import {
  api,
  type MeasurementDetail,
  type MeasurementFilter,
  type MeasurementSummary,
  type SaveMeasurementRequest,
  type StoredSweepPoint,
  type ThdSweepPoint,
  type UpdateMeasurementRequest,
} from '../api/client'
import { THD_VS_FREQUENCY_KIND } from '../measurements/kinds'

// A distinct hue per overlay, cycling once more than 8 are on screen at once — unlikely, and a
// repeat is harmless since the list rows still disambiguate by name. Used where colour has to
// identify *which stored measurement* this is (ThdSweepChart, where L/R is already carried by line
// style) — see colorL/colorR below for the FFT Spectrum chart's own convention.
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

// The live L/R channel colors on SpectrumChart.vue — kept as a second copy rather than an import
// because that component's own CHANNEL_COLORS is private to its chart-drawing concerns, but the
// hues themselves have to match exactly for an overlay's tint to read as "a lighter version of the
// live trace" rather than an unrelated blue/orange.
const CHANNEL_BASE_COLORS: [string, string] = ['#3b82f6', '#f97316']

// How far each successive overlay's channel colors are lightened toward white, cycling — distinct
// enough that overlays on screen at once don't collapse into the same look, without ever reaching
// pure white (which would stop reading as "blue" or "orange" at all).
const OVERLAY_TINTS = [0.15, 0.3, 0.45, 0.6, 0.22, 0.37, 0.52, 0.67]

/** Blends a `#rrggbb` colour toward white by `amount` (0 = unchanged, 1 = white). */
function tint(hex: string, amount: number): string {
  const n = parseInt(hex.slice(1), 16)
  const mix = (c: number) => Math.round(c + (255 - c) * amount)
  const r = mix((n >> 16) & 0xff)
  const g = mix((n >> 8) & 0xff)
  const b = mix(n & 0xff)
  return `#${((r << 16) | (g << 8) | b).toString(16).padStart(6, '0')}`
}

export interface OverlayEntry {
  /** Same as the underlying measurement_result id — re-adding an already-loaded result is a no-op, not a duplicate. */
  id: number
  detail: MeasurementDetail
  /** Identifies this overlay on ThdSweepChart, where hue means "which run" and dash means channel. */
  color: string
  /**
   * This overlay's L/R traces on SpectrumChart.vue — a tint of the live channel colors rather than
   * an arbitrary hue, so a stored trace reads as "an earlier version of this same channel" instead
   * of two unrelated lines sharing one dash-differentiated color (the old behavior).
   */
  colorL: string
  colorR: string
  visible: boolean
  /**
   * A sweep result's stored points, in the same shape the live sweep produces — so ThdSweepChart
   * plots a stored sweep through exactly the code path it plots the running one with. Undefined
   * for an FFTSpectrum result, whose trace lives in `detail.channelsData` instead.
   */
  sweepPoints?: ThdSweepPoint[]
}

/**
 * A saved sweep's metric rows back into sweep points. The metric names are the ones
 * MeasurementSaveService.BuildSweepMetrics writes; a name the save didn't record (a harmonic above
 * Nyquist at that frequency) becomes NaN, which ThdSweepChart already renders as a gap rather than
 * a plunge to the axis floor.
 */
export function toSweepPoints(stored: StoredSweepPoint[]): ThdSweepPoint[] {
  const points: ThdSweepPoint[] = []
  for (const point of stored) {
    for (const [channel, metrics] of Object.entries(point.metricsByChannel)) {
      const value = (name: string) => (name in metrics ? metrics[name] : Number.NaN)

      // D2..D5, stopping at the first one this frequency has no row for — the save writes them in
      // order and stops at the highest harmonic actually found, so a gap can only be the end.
      const harmonicPercent: number[] = []
      for (let order = 2; order <= 5; order++) {
        if (!(`h${order}_percent` in metrics)) break
        harmonicPercent.push(metrics[`h${order}_percent`])
      }

      const previews = point.previewsByChannel?.[Number(channel)]

      points.push({
        freqHz: point.freqHz,
        channel: Number(channel),
        fundamentalDbv: value('fundamental_dbv'),
        gainDb: value('gain_db'),
        powerWatt: value('power_watt'),
        thdPercent: value('thd_percent'),
        thdNPercent: value('thd_n_percent'),
        snrDb: value('snr_db'),
        harmonicPercent,
        d6PlusPercent: value('d6plus_percent'),
        noisePercent: value('noise_percent'),
        previewFft: previews?.fft ?? null,
        previewScope: previews?.scope ?? null,
      })
    }
  }
  return points
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

  /**
   * Saves the last completed sweep. A separate call from save() because the server reads a
   * different source (the sweep runner's points, not the spectrum pipeline's retained trace) and
   * writes a different row shape — but it lands in the same result list, so it reloads it too.
   */
  async function saveSweep(req: { measurementTypeId: number; name: string; notes: string | null }) {
    try {
      const result = await api.saveSweep(req)
      await load()
      error.value = null
      return result
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  async function update(id: number, req: UpdateMeasurementRequest) {
    try {
      await api.updateMeasurement(id, req)
      await load()
      error.value = null
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
      // A sweep result carries no spectra, so its trace has to be fetched separately — see
      // SweepSaveRequest on why only the scalars are stored.
      const sweepPoints =
        detail.kind === THD_VS_FREQUENCY_KIND
          ? toSweepPoints((await api.getStoredSweep(resultId)).points)
          : undefined
      const color = OVERLAY_COLORS[nextColorIndex % OVERLAY_COLORS.length]
      const tintAmount = OVERLAY_TINTS[nextColorIndex % OVERLAY_TINTS.length]
      nextColorIndex++
      const colorL = tint(CHANNEL_BASE_COLORS[0], tintAmount)
      const colorR = tint(CHANNEL_BASE_COLORS[1], tintAmount)
      overlays.value = [...overlays.value, { id: resultId, detail, color, colorL, colorR, visible: true, sweepPoints }]
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

  return { items, filter, overlays, error, load, save,
    saveSweep, update, remove, addOverlay, removeOverlay, setOverlayVisible }
}
