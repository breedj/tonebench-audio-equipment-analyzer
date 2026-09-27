import { computed, type ComputedRef } from 'vue'
import type { OverlayEntry } from './useMeasurements'
import type { ResponseTrace } from './useFrequencyResponse'
import { toComplexCurve, type ComplexPoint } from '../response/complexPlane'
import { measuredBand } from '../response/poleZeroMap'
import type { MeasuredBand } from '../response/rationalFit'

/**
 * The complex curve the s-plane views read, derived from the response traces once.
 *
 * Extracted from `ResponsePolarChart` when the |H(s)| surface arrived, and **forced rather than
 * tidy**: `ResponsePoleZeroPanel` does not read a store, it receives its curve from whichever chart
 * is mounted (`App.vue` passes the chart's exposed `curve`), so the panel's table, its fit and the
 * chart's own picture are guaranteed to describe the same points. A second chart in that position
 * therefore has to produce the identical curve, and the only way to guarantee "identical" is for
 * there to be one derivation.
 *
 * Everything here is a `computed`, so both charts pay for it once per change rather than per paint.
 */

export interface ComplexCurveInputs {
  traces: ResponseTrace[]
  overlays: OverlayEntry[]
  /** The plotted frequency window — the display panel's From/To. */
  xFromHz: number
  xToHz: number
  invertSign: boolean
  restoreDelay: boolean
  latencySamples: number | null
  sampleRateHz: number | null
}

export interface ResponseComplexCurve {
  /** Whether the live measurement occupies run 0 — a stored result opened from History has none. */
  liveDrawn: ComputedRef<boolean>
  /** The live run's curve, empty when there is no live Gain trace. */
  curve: ComputedRef<ComplexPoint[]>
  overlayCurves: ComputedRef<{ id: number; name: string; points: ComplexPoint[] }[]>
  /** The curve everything derived reads: the live run, or the first checked history item. */
  readCurve: ComputedRef<ComplexPoint[]>
  band: ComputedRef<MeasuredBand | null>
}

/**
 * A chirp's low-coherence bins whip around the origin and manufacture phantom crossings; a stepped
 * run's coherence is a constant 1.
 */
export const MIN_COHERENCE = 0.5

export function useResponseComplexCurve(inputs: () => ComplexCurveInputs): ResponseComplexCurve {
  const liveDrawn = computed(() => inputs().traces.length > 0)

  /**
   * Only the Gain curve carries a relationship between two channels, and only that relationship is a
   * transfer function. A stepped run's Left and Right traces hold an all-zero phase array by
   * construction — the same trap `FrequencyResponseChart`'s `phaseTrace` documents, which there drew
   * a flat 0° line and here would draw a tidy straight line along the positive real axis.
   */
  const gainTrace = computed(() => inputs().traces.find((t) => t.kind === 'Gain') ?? null)

  const curveOptions = computed(() => {
    const i = inputs()
    return {
      fromHz: i.xFromHz,
      toHz: i.xToHz,
      invertSign: i.invertSign,
      minCoherence: MIN_COHERENCE,
      latencySamples: i.restoreDelay ? i.latencySamples : null,
      sampleRateHz: i.sampleRateHz,
    }
  })

  const curve = computed<ComplexPoint[]>(() =>
    gainTrace.value ? toComplexCurve(gainTrace.value, curveOptions.value) : [],
  )

  /**
   * Stored runs, restricted twice over: to the Gain curve, for the reason above, and to runs whose
   * own phase kind is one this plot can represent. An overlay is drawn without the live run's delay
   * correction — `StoredResponseTrace` carries neither a latency nor a sample rate — which is why the
   * header says so when the live curve is being corrected.
   */
  const overlayCurves = computed(() =>
    inputs()
      .overlays.filter((o) => o.visible && o.responseTraces?.length)
      .map((overlay) => {
        const trace = overlay.responseTraces?.find((t) => t.kind === 'Gain')
        if (!trace) return null
        const points = toComplexCurve(trace, { ...curveOptions.value, latencySamples: null })
        if (points.length < 2) return null
        return { id: overlay.id, name: overlay.detail.name, points }
      })
      .filter((entry): entry is { id: number; name: string; points: ComplexPoint[] } => entry !== null),
  )

  /**
   * The curve everything *derived* reads — the fit, the stability margins, the measured-band marker
   * and the cursor — as opposed to the contour the plot strokes for the live run.
   *
   * The live measurement when there is one, and otherwise the first checked history item: the same
   * "run 0 is the curve you are meant to read" rule the dash allocation follows, so the solid line
   * and the fitted model are always the same run. Without it a stored result opened from History drew
   * its contour and then reported no poles, no zeros, no margins and no band — every derived number
   * came from a live curve that was not there, which is exactly what a saved pole-zero measurement
   * looks like when it is reopened.
   *
   * Note an overlay carries no latency or sample rate, so a promoted curve is fitted without the
   * delay correction the live one would get; the header already says so whenever an overlay is drawn
   * beside a corrected live run.
   */
  const readCurve = computed<ComplexPoint[]>(() =>
    curve.value.length > 0 ? curve.value : (overlayCurves.value[0]?.points ?? []),
  )

  const band = computed(() => measuredBand(readCurve.value))

  return { liveDrawn, curve, overlayCurves, readCurve, band }
}
