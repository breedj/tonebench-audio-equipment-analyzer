import { ref } from 'vue'
import {
  api,
  type HarmonicCurveJson,
  type ImpulseTraceJson,
  type MeasurementDetail,
  type MeasurementFilter,
  type MeasurementSummary,
  type SaveMeasurementRequest,
  type StoredResponseTrace,
  type StoredSweepPoint,
  type ThdMethodName,
  type ThdSweepPoint,
  type UpdateMeasurementRequest,
} from '../api/client'
import {
  isCrosstalkKind,
  isFrequencyResponseKind,
  isOutputImpedanceKind,
  isImdSweepKind,
  isImpedanceKind,
  isImpulseResponseKind,
  isSteppedResponseKind,
  isSweepKind,
} from '../measurements/kinds'
import { toImdSweepPoints } from '../imd/points'
import { sweepEndFreqHzFromParams, sweepMethodFromParams, type SweepTraceFamily } from '../sweep/points'
import type { ResponseTrace } from './useFrequencyResponse'
import type { ImpedanceTrace } from './useImpedance'
import { useTrash } from './useTrash'

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
   * How a stored sweep's harmonics were referenced, off its as-run params_json — which of a point's
   * two sets of figures the chart draws for this run. Conventional for a row written before the
   * method existed. Only meaningful alongside `sweepPoints`.
   */
  sweepMethod?: ThdMethodName

  /** Which sweep a stored run is — decides its trace labels on the chart (THD/D2.. or IMD/IM2..). See imd/points.ts. */
  sweepFamily?: SweepTraceFamily

  /** The end frequency a stored sweep was asked for, or null when its params_json does not say. A normalized sweep measured past it. */
  sweepEndFreqHz?: number | null

  /**
   * A frequency-response result's stored curves, in the same shape the live measurement produces —
   * so FrequencyResponseChart draws a stored response through exactly the code path it draws the
   * running one with. Undefined for every other kind.
   */
  responseTraces?: ResponseTrace[]

  /**
   * An impulse-response result's waveform, gated and decimated server-side into exactly the shape
   * the live measurement produces — so ImpulseResponseChart draws a stored impulse through the same
   * code path it draws the running one with. Undefined for every other kind, and null for an impulse
   * result that stored no trace.
   *
   * <p>Its own fetch, unlike the gated magnitude beside it: the impulse is not a response_trace row,
   * it comes from `/measurements/{id}/impulse-trace`.</p>
   */
  impulseTrace?: ImpulseTraceJson | null

  /**
   * An impulse-response result's harmonic distortion curves, so the Harmonics view can draw a stored
   * run beside the live one. Undefined for every other kind.
   *
   * <p>They come from `/measurements/{id}/impulse`, which also carries the full-resolution impulse —
   * about 1.2 MB for a 131072-sample record. That is the only endpoint holding the curves, and this
   * server is bound to loopback by design, so the cost is a local copy rather than a download.</p>
   */
  harmonicCurves?: HarmonicCurveJson[]

  /**
   * An impedance result's stored curve. Read through the same endpoint as a response — impedance
   * lives in the same `response_trace` rows — but kept in its own field because the units differ:
   * these magnitudes are dB relative to 1 ohm, not a gain, and putting them on a dB axis would draw
   * a plausible curve that means nothing.
   */
  impedanceTraces?: ImpedanceTrace[]
}

/**
 * A saved response's rows back into the shape the live measurement produces. The stored kind is
 * lower-case (`response_trace.kind`, mirroring the SQL) while the wire and the UI use the enum
 * names, so this is where the two conventions meet — one place rather than at every read site.
 */
export function toResponseTraces(stored: StoredResponseTrace[]): ResponseTrace[] {
  function kindOf(stored: string): ResponseTrace['kind'] {
    const kind = kinds[stored]
    if (!kind) throw new Error(`Unknown response trace kind '${stored}'.`)
    return kind
  }
  const kinds: Record<string, ResponseTrace['kind']> = {
    left: 'Left',
    right: 'Right',
    gain: 'Gain',
    xtalk_l2r: 'CrosstalkLeftToRight',
    xtalk_r2l: 'CrosstalkRightToLeft',
  }

  return stored.map((trace) => ({
    // No fallback: a row of a kind this table does not know would otherwise be drawn as gain —
    // which is exactly what a crosstalk row would have become before its kinds were added here.
    kind: kindOf(trace.kind),
    startFreqHz: trace.startHz,
    endFreqHz: trace.endHz,
    freqHz: trace.freqHz,
    magnitudeMinDb: trace.magnitudeMinDb,
    magnitudeMaxDb: trace.magnitudeMaxDb,
    phaseDeg: trace.phaseDeg,
    coherence: trace.coherence,
    // Null on a result saved before the delays were recorded (migration 012), and kept null: a
    // derivative of the decimated phase is not the curve the live screen showed.
    groupDelayS: trace.groupDelayS ?? null,
    phaseDelayS: trace.phaseDelayS ?? null,
    // The stored rows keep the two corner frequencies and their clamped flags but not the width,
    // which is exactly their difference — recomputed here rather than stored twice and able to
    // disagree.
    // The drop and its symmetry are the row's own convention rather than stored columns: bw1_* is
    // always the ±1 dB reading and bw3_* the −3 dB one. See ResponseBandwidth server-side, which
    // explains why that is safe for rows written before symmetric searching existed.
    oneDb: {
      dropDb: 1,
      symmetric: true,
      lowerHz: trace.oneDb.lowerHz,
      upperHz: trace.oneDb.upperHz,
      bandwidthHz: Math.max(0, trace.oneDb.upperHz - trace.oneDb.lowerHz),
      lowerClamped: trace.oneDb.lowerClamped,
      upperClamped: trace.oneDb.upperClamped,
    },
    threeDb: {
      dropDb: 3,
      symmetric: false,
      lowerHz: trace.threeDb.lowerHz,
      upperHz: trace.threeDb.upperHz,
      bandwidthHz: Math.max(0, trace.threeDb.upperHz - trace.threeDb.lowerHz),
      lowerClamped: trace.threeDb.lowerClamped,
      upperClamped: trace.threeDb.upperClamped,
    },
    peakDb: trace.peakDb,
    peakFreqHz: trace.peakHz,
    // Null on a result saved before the reference was recorded. It is left null rather than
    // back-filled with peakDb: those rows were peak-referenced, but a build that did not record it
    // may also have searched differently, and quoting a reference on that claim would be
    // asserting something the row does not say.
    referenceDb: trace.referenceDb,
    referenceMode: trace.referenceMode,
    // A stored trace has no fallback flag: what it was actually referenced to is in referenceMode,
    // which is the only thing the reader can act on afterwards.
    referenceFellBackToPeak: false,
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
        // The frequency-normalized figures. Written only where they existed (a conventional
        // sweep has none; a normalized one has none near the top of its extended grid), so
        // absent reads back as null and the chart draws a gap, exactly as it does live.
        normalizedThdPercent: 'nthd_percent' in metrics ? metrics.nthd_percent : null,
        normalizedHarmonicPercent: [2, 3, 4, 5].map((order) =>
          `nh${order}_percent` in metrics ? metrics[`nh${order}_percent`] : null,
        ),
        normalizedD6PlusPercent: 'nd6plus_percent' in metrics ? metrics.nd6plus_percent : null,
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

/**
 * Stored impedance rows as the UI's own curve shape. The `kind` strings are the storage layer's
 * ('z', 'z_open', 'z_short'); anything else in the result belongs to another measurement and is
 * dropped rather than drawn on an ohms axis.
 */
export function toImpedanceTraces(traces: StoredResponseTrace[]): ImpedanceTrace[] {
  const kinds: Record<string, ImpedanceTrace['kind']> = {
    z: 'Impedance',
    z_open: 'OpenCal',
    z_short: 'ShortCal',
    // An output-impedance result's Zout row: an impedance curve to draw and read (ohms in dB); the
    // result's own kind says what it is of. Its gain rows are response rows and are not overlaid.
    zout: 'Impedance',
  }

  return traces
    .filter((t) => kinds[t.kind] !== undefined)
    .map((t) => ({
      kind: kinds[t.kind],
      startFreqHz: t.startHz,
      endFreqHz: t.endHz,
      freqHz: t.freqHz,
      magnitudeMinDbOhms: t.magnitudeMinDb,
      magnitudeMaxDbOhms: t.magnitudeMaxDb,
      phaseDeg: t.phaseDeg,
      coherence: t.coherence,
      peakDbOhms: t.peakDb,
      peakFreqHz: t.peakHz,
    }))
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

  /** The IMD sweep's counterpart of saveSweep — its own runner, so its own endpoint; the same result list. */
  async function saveImdSweep(req: { measurementTypeId: number; name: string; notes: string | null }) {
    try {
      const result = await api.saveImdSweep(req)
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

  /**
   * Saves the impulse response on screen. Its own verb rather than a third arm of
   * `saveResponse`: an impulse result writes the same `response_trace` rows plus two tables of its
   * own, so it goes through its own endpoint even though the curves are identical.
   */
  /** Saves the output-impedance run on screen — its Zout row and the two gain rows, through its own endpoint. */
  async function saveOutputImpedance(req: { measurementTypeId: number; name: string; notes: string | null }) {
    try {
      const result = await api.saveOutputImpedance(req)
      await load()
      error.value = null
      return result
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  /** Saves the crosstalk curves on screen — the crosstalk runner's own rows, through its own endpoint. */
  async function saveCrosstalk(req: { measurementTypeId: number; name: string; notes: string | null }) {
    try {
      const result = await api.saveCrosstalk(req)
      await load()
      error.value = null
      return result
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  async function saveImpedance(req: { measurementTypeId: number; name: string; notes: string | null }) {
    try {
      const result = await api.saveImpedance(req)
      await load()
      error.value = null
      return result
    } catch (e) {
      error.value = (e as Error).message
      throw e
    }
  }

  async function saveImpulse(req: { measurementTypeId: number; name: string; notes: string | null }) {
    try {
      const result = await api.saveImpulseResponse(req)
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

  /**
   * Moves a stored result to the trash. Nothing is destroyed — useTrash restores it, or the
   * retention purge destroys it in a fortnight. The overlay goes either way: a result that is not
   * in the history list has no business still being drawn on the chart.
   */
  async function remove(id: number) {
    try {
      await api.deleteMeasurement(id)
      removeOverlay(id)
      await load()
      // The sidebar's badge is the only thing that says the trash is not empty.
      void useTrash().refreshCount()
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
      // The IMD sweep's rows carry imd_percent/im{k}_percent rather than thd_percent/h{k}_percent
      // and are mapped onto the same point shape by imd/points.ts.
      const sweepPoints = isImdSweepKind(detail.kind)
        ? toImdSweepPoints((await api.getStoredSweep(resultId)).points)
        : isSweepKind(detail.kind)
          ? toSweepPoints((await api.getStoredSweep(resultId)).points)
          : undefined
      const sweepFamily: SweepTraceFamily | undefined = sweepPoints ? (isImdSweepKind(detail.kind) ? 'imd' : 'thd') : undefined
      // Which figures that sweep is about, and how far it was asked to go: both live only in the
      // as-run params_json the save wrote, so they are read once here rather than parsed by every
      // consumer.
      const sweepMethod = sweepPoints ? sweepMethodFromParams(detail.paramsJson) : undefined
      const sweepEndFreqHz = sweepPoints ? sweepEndFreqHzFromParams(detail.paramsJson) : undefined
      // Likewise a response result: its curves live in response_trace, not in spectrum rows, and
      // likewise an impulse result: its gated magnitude and phase are response_trace rows, so they
      // come back through the same reader and overlay onto the same chart. The impulse itself and
      // the harmonic curves are not overlaid — they are read through their own endpoint by the
      // screen that draws them.
      // A crosstalk result's rows are response traces too (under their own kinds), and its chart
      // is the response chart, so they overlay through the same field.
      const responseTraces =
        isFrequencyResponseKind(detail.kind) || isImpulseResponseKind(detail.kind) || isCrosstalkKind(detail.kind)
          ? toResponseTraces((await api.getStoredResponse(resultId)).traces)
          : undefined
      // The impulse waveform itself, which is not a response_trace row and so needs its own call.
      // Gated and windowed by the runner's own code server-side, so an overlay and the live trace
      // are the same kind of object rather than two decimations that disagree.
      const impulseTrace = isImpulseResponseKind(detail.kind)
        ? (await api.getStoredImpulseTrace(resultId)).impulse
        : undefined
      const harmonicCurves = isImpulseResponseKind(detail.kind)
        ? (await api.getStoredImpulse(resultId)).harmonics
        : undefined
      // Impedance comes back through the same reader, since it shares the rows — but into its own
      // field, because its magnitudes are ohms in dB rather than a gain.
      const impedanceTraces = isImpedanceKind(detail.kind) || isOutputImpedanceKind(detail.kind)
        ? toImpedanceTraces((await api.getStoredResponse(resultId)).traces)
        : undefined
      overlays.value = [
        ...overlays.value,
        { id: resultId, detail, visible: true, sweepPoints, sweepMethod, sweepFamily, sweepEndFreqHz, responseTraces, impulseTrace, harmonicCurves, impedanceTraces },
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

  // No eager load: the list belongs to the open project, and the first use of this composable is
  // App.vue's, at startup, when there may be none (the welcome screen) — a request then is a 409
  // with nothing to show it on. MeasurementHistoryPanel loads on mount and on every type change,
  // which covers every screen the list is shown on.

  return { items, filter, overlays, error, load, save, saveSweep, saveImdSweep, saveResponse, saveImpulse, saveImpedance, saveCrosstalk, saveOutputImpedance,
    update, remove,
    addOverlay, removeOverlay, setOverlayVisible, visibleOverlayCount, canShowMoreOverlays }
}
