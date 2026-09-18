import type {
  HarmonicCurveJson,
  ImpulseTraceJson,
  MeasurementDetail,
  ReportRow,
  ResponsePhaseKindName,
  ThdMethodName,
  ThdSweepPoint,
} from '../api/client'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import type { ImpedanceTrace } from '../composables/useImpedance'
import type { ImpedanceDisplay } from '../impedance/display'
import type { ImpulseDisplay } from '../measurements/impulseDisplay'
import type { ResponseDisplay } from '../response/display'
import type { SweepDisplay } from '../sweep/display'
import type { MetersFrame, SpectrumFrame } from '../streaming/protocol'
import type { SpectrumDisplaySource } from '../composables/useChartDisplay'
import type { ReportElementId } from './elements'

/**
 * What a report chapter is about, in one shape whether it came from the screen or from History.
 *
 * The builders (report/builders) and the chart renderers (report/render.ts) read this and nothing
 * else, which is what lets one path serve a live measurement and a stored one: the orchestrator
 * (buildReport.ts) fills it from App.vue's refs in the one case and from the measurement endpoints
 * in the other. `data` carries the kind-specific curves and the display state the charts are drawn
 * with.
 */
export interface ReportSubject {
  /** `live`, or `stored-<resultId>` — what the dialogs select by. */
  id: string
  /** The stored result's id; null for what is on screen. */
  resultId: number | null
  /** The measurement type the subject belongs to. */
  typeId: number
  kind: string
  /** The measurement (type) name — the chapter's title. */
  typeName: string
  /** The stored result's own name, or null for what is on screen. */
  resultName: string | null
  projectName: string
  typeNotes: string | null
  resultNotes: string | null
  /** The settings the measurement ran with: a stored result's own, or the type's current ones on screen. */
  paramsJson: string
  /** ISO-8601 UTC, or null for a live measurement. */
  timestampUtc: string | null
  /** Key facts for the chapter head: sample rate, ranges, device — whatever the source knows. */
  facts: ReportRow[]
  sampleRateHz: number
  /** The stored result's detail — what a history overlay entry is built from; null for the live one. */
  detail: MeasurementDetail | null
  data: ReportSubjectData
}

export type ReportSubjectData =
  | {
      kind: 'response'
      traces: ResponseTrace[]
      display: ResponseDisplay
      phaseKind: ResponsePhaseKindName
      rightChannelIsReference: boolean
      /** The delay the estimator removed, which the polar views can put back; null on a stored result. */
      latencySamples: number | null
    }
  | {
      kind: 'spectrum'
      spectrum: Record<number, SpectrumFrame>
      meters: Record<number, MetersFrame>
      /** The chart's display state — the screen's singleton, or a stored type's own (see SPECTRUM_DISPLAY_KEY). */
      display: SpectrumDisplaySource
    }
  | {
      kind: 'sweep'
      points: ThdSweepPoint[]
      method: ThdMethodName
      display: SweepDisplay
    }
  | {
      kind: 'impulse'
      impulse: ImpulseTraceJson | null
      /** The gated response — the impulse screen's Response view. */
      traces: ResponseTrace[]
      harmonics: HarmonicCurveJson[]
      display: ImpulseDisplay
    }
  | {
      kind: 'impedance'
      traces: ImpedanceTrace[]
      display: ImpedanceDisplay
    }
  | { kind: 'none' }

/** How several measurements share one report. */
export type ReportMode = 'combined' | 'individual'

/** Everything a report of one measurement type is built from. */
export interface ReportPlan {
  /** In report order; in a combined chart the first is the primary curve and the rest are overlays. */
  subjects: ReportSubject[]
  elements: readonly ReportElementId[]
  views: readonly string[]
  mode: ReportMode
}

/** A name for a subject wherever the report has to tell several apart. */
export function subjectLabel(subject: ReportSubject): string {
  return subject.resultName ?? 'Current measurement'
}
