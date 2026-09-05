import { ref } from 'vue'
import {
  api,
  type MeasurementDetail,
  type MeasurementFilter,
  type MeasurementSummary,
  type SaveMeasurementRequest,
  type StoredResponseTrace,
  type StoredSweepPoint,
  type ThdSweepPoint,
  type UpdateMeasurementRequest,
} from '../api/client'
import { isFrequencyResponseKind, isSteppedResponseKind, isSweepKind } from '../measurements/kinds'
import type { ResponseTrace } from './useFrequencyResponse'

/**
 * How many overlays of one kind may be *shown* at once — the THD-vs-frequency and FFT-analysis
 * screens each get their own budget of this many, not a pool shared between the two unrelated
 * charts. Not a rendering budget so much as what keeps each visible overlay tellable from the
 * others: both charts identify a stored run by dash pattern or fixed color (sweepOverlayStyles'
 * SWEEP_OVERLAY_DASH, spectrumOverlayColors' FFT_OVERLAY_COLORS) and there are only so many that stay
 * distinguishable from each other and from the live trace. Overlays past the limit stay loaded and
 * simply hidden, so unchecking one frees a slot without a refetch.
 */
export const MAX_VISIBLE_OVERLAYS = 3

export interface OverlayEntry {
  /** Same as the underlying measurement_result id — re-adding an already-loaded result is a no-op, not a duplicate. */
  id: number
  detail: MeasurementDetail
  visible: boolean
  /**
   * A sweep result's stored points, in the same shape the live sweep produces — so ThdSweepChart
   * plots a stored sweep through exactly the code path it plots the running one with. Undefined
   * for an FFTSpectrum result, whose trace lives in `detail.channelsData` instead.
   */
  sweepPoints?: ThdSweepPoint[]

  /**
   * A frequency-response result's stored curves, in the same shape the live measurement produces —
   * so FrequencyResponseChart draws a stored response through exactly the code path it draws the
   * running one with. Undefined for every other kind.
   */
  responseTraces?: ResponseTrace[]
}

/**
 * A saved response's rows back into the shape the live measurement produces. The stored kind is
 * lower-case (`response_trace.kind`, mirroring the SQL) while the wire and the UI use the enum
 * names, so this is where the two conventions meet — one place rather than at every read site.
 */
export function toResponseTraces(stored: StoredResponseTrace[]): ResponseTrace[] {
  const kinds: Record<string, ResponseTrace['kind']> = { left: 'Left', right: 'Right', gain: 'Gain' }

  return stored.map((trace) => ({
    kind: kinds[trace.kind] ?? 'Gain',
    startFreqHz: trace.startHz,
    endFreqHz: trace.endHz,
    freqHz: trace.freqHz,
    magnitudeMinDb: trace.magnitudeMinDb,
    magnitudeMaxDb: trace.magnitudeMaxDb,
    phaseDeg: trace.phaseDeg,
    coherence: trace.coherence,
    // The stored rows keep the two corner frequencies and their clamped flags but not the width,
    // which is exactly their difference — recomputed here rather than stored twice and able to
    // disagree.
    oneDb: {
      lowerHz: trace.oneDb.lowerHz,
      upperHz: trace.oneDb.upperHz,
      bandwidthHz: Math.max(0, trace.oneDb.upperHz - trace.oneDb.lowerHz),
      lowerClamped: trace.oneDb.lowerClamped,
      upperClamped: trace.oneDb.upperClamped,
    },
    threeDb: {
      lowerHz: trace.threeDb.lowerHz,
      upperHz: trace.threeDb.upperHz,
      bandwidthHz: Math.max(0, trace.threeDb.upperHz - trace.threeDb.lowerHz),
      lowerClamped: trace.threeDb.lowerClamped,
      upperClamped: trace.threeDb.upperClamped,
    },
    peakDb: trace.peakDb,
    peakFreqHz: trace.peakHz,
  }))
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
        // Absent from sweeps saved before the amplitude axis existed. NaN rather than 0 there: 0 dBV
        // is a real level, and an overlay plotted at 1 V that was actually measured at 20 mV is
        // worse than one the chart drops.
        generatorDbv: value('generator_dbv'),
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

/**
 * How many overlays of `kind` are currently drawn — what MAX_VISIBLE_OVERLAYS is counted against.
 * Scoped per kind (rather than a single computed over the whole list) because `overlays` is one
 * shared singleton behind both the sweep and FFT screens: a result loaded on one screen stays
 * loaded, hidden and harmless, when you switch to the other, and a global cap would let 3 checked
 * THD history items silently block every FFT overlay from ever being shown at all.
 *
 * A plain function rather than a computed, since it needs a parameter — but it reads `overlays`
 * inside, so it stays reactive wherever it's called from a template or another computed.
 */
function visibleOverlayCount(kind: string): number {
  return overlays.value.filter((o) => o.visible && o.detail.kind === kind).length
}

/** False once MAX_VISIBLE_OVERLAYS of this kind are already on screen: nothing further of that kind may be checked on until one is unchecked. */
function canShowMoreOverlays(kind: string): boolean {
  return visibleOverlayCount(kind) < MAX_VISIBLE_OVERLAYS
}

function requireOverlaySlot(kind: string): void {
  if (!canShowMoreOverlays(kind))
    throw new Error(`At most ${MAX_VISIBLE_OVERLAYS} history items can be shown at once — uncheck one first.`)
}

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

  /**
   * A frequency-response result is built from the response runner's own curves, not from the
   * pipeline's retained spectrum, so it saves through its own endpoint — the same split as
   * `saveSweep`, and for the same reason.
   *
   * The two response kinds have a runner each, so the *save* has to name which one even though the
   * rows it writes are identical. Reading back does not: a stored response is a stored response,
   * which is why `isFrequencyResponseKind` covers both everywhere else.
   */
  async function saveResponse(
    req: { measurementTypeId: number; name: string; notes: string | null },
    kind?: string | null,
  ) {
    try {
      const result = isSteppedResponseKind(kind)
        ? await api.saveSteppedResponse(req)
        : await api.saveFrequencyResponse(req)
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

  /**
   * Adds a stored result as a chart overlay. Re-adding one already on the chart just re-shows it
   * (visible: true) rather than plotting a duplicate trace.
   *
   * `kind` is the caller's own measurement type's kind — MeasurementChecklist always knows it
   * (its items are already server-filtered to the open type), and passing it lets the cap be
   * checked *before* the fetch, so a rejected click costs nothing. Throws once MAX_VISIBLE_OVERLAYS
   * of that kind are already shown; the checklist disables its remaining checkboxes at that point,
   * so this is the backstop for the other entry points rather than the path a user normally hits.
   */
  async function addOverlay(resultId: number, kind: string) {
    try {
      const existing = overlays.value.find((o) => o.id === resultId)
      if (existing) {
        if (!existing.visible) requireOverlaySlot(kind)
        existing.visible = true
        return
      }
      requireOverlaySlot(kind)
      const detail = await api.getMeasurement(resultId)
      // A sweep result carries no spectra, so its trace has to be fetched separately — see
      // SweepSaveRequest on why only the scalars are stored.
      const sweepPoints = isSweepKind(detail.kind)
        ? toSweepPoints((await api.getStoredSweep(resultId)).points)
        : undefined
      // Likewise a response result: its curves live in response_trace, not in spectrum rows.
      const responseTraces = isFrequencyResponseKind(detail.kind)
        ? toResponseTraces((await api.getStoredResponse(resultId)).traces)
        : undefined
      overlays.value = [
        ...overlays.value,
        { id: resultId, detail, visible: true, sweepPoints, responseTraces },
      ]
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

  /**
   * Shows or hides an already-loaded overlay. Returns false — leaving the overlay hidden — when
   * showing it would exceed MAX_VISIBLE_OVERLAYS, rather than throwing: this one is called straight
   * from click handlers that have nowhere to put an exception. Hiding always succeeds.
   */
  function setOverlayVisible(id: number, visible: boolean): boolean {
    const entry = overlays.value.find((o) => o.id === id)
    if (!entry) return false
    if (visible && !entry.visible && !canShowMoreOverlays(entry.detail.kind)) return false
    entry.visible = visible
    return true
  }

  if (items.value.length === 0) load()

  return { items, filter, overlays, error, load, save, saveSweep, saveResponse, update, remove,
    addOverlay, removeOverlay, setOverlayVisible, visibleOverlayCount, canShowMoreOverlays }
}
