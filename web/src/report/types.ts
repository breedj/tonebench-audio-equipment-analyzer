import type {
  DeviceStatus,
  GeneratorTone,
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
import type { DutType } from '../impedance/component'
import type { ImpedanceDisplay } from '../impedance/display'
import type { CrosstalkDisplay } from '../crosstalk/display'
import type { OutputImpedanceDisplay } from '../outputImpedance/display'
import type { ImpulseDisplay } from '../measurements/impulseDisplay'
import type { ImpulseRunFacts } from '../measurements/impulseReadings'
import type { ResponseDisplay } from '../response/display'
import type { SweepDisplay } from '../sweep/display'
import type { SweepTraceFamily } from '../sweep/points'
import type { IntermodulationFrame, MetersFrame, NoiseFiguresFrame, ScopeFrame, SpectrumFrame, MultitoneFrame } from '../streaming/protocol'
import type { SpectrumDisplaySource } from '../composables/useChartDisplay'
import type { StimulusSummary } from '../metrics'
import type { ReportElementId } from './elements'
import type { ExportableChart } from './renderChart'

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
      /** The meters per channel — live frames, or a stored result's metric rows repacked (storedMetersFrames). */
      meters: Record<number, MetersFrame>
      /** The two-tone IMD figure per channel, when one was measured — live frames, or a stored result's `imd_*` rows (storedIntermodulationFrames). */
      intermodulation: Record<number, IntermodulationFrame>
      /** The band RMS / noise density / weighting per channel — live frames, or a stored result's two noise rows (storedNoiseFrames). */
      noise: Record<number, NoiseFiguresFrame>
      /** The weighting annunciator's text, or null when unweighted — the chart's title carries it. */
      weightingLabel: string | null
      /** The chart's display state — the screen's singleton, or a stored type's own (see SPECTRUM_DISPLAY_KEY). */
      display: SpectrumDisplaySource
      /** The metric tiles' labels in their order: the screen's selection, or the stored type's. */
      selectedMetrics: string[]
      /** The generator's tones, for the Gain tile; empty when nothing is known. */
      generatorTones: GeneratorTone[]
      /**
       * What the generator was playing, when it is known — the screen's staged stimulus, or a
       * stored result's gen_json (storedStimulus). A stimulus with more than one tone makes the
       * noise-integrated metric rows read "n/a"; see metrics.ts's noiseFigureCaveat.
       */
      stimulus?: StimulusSummary | null
      loadOhms: number
      /** Each channel's multitone figure, while the generator plays a comb; rebuilt from the mt_* rows for a stored result. */
      multitone?: Record<number, MultitoneFrame>
      /** The oscilloscope frames — the screen's; empty for a stored result, which keeps no waveform. */
      scope: Record<number, ScopeFrame>
      /** The same tick's THD-residual frames, when the oscilloscope's Residual view is on; the chart draws these instead of `scope` then. */
      scopeResidual?: Record<number, ScopeFrame>
      /** What the oscilloscope triggers from; null for a stored result. */
      status: DeviceStatus | null
      /** The waterfall chart on screen, whose collected history only it can draw; null off screen or for a stored result. */
      waterfall: ExportableChart | null
    }
  | {
      kind: 'sweep'
      points: ThdSweepPoint[]
      method: ThdMethodName
      /** THD/D2.. or IMD/IM2.. — which measurement the points are, for the chart's labels (see imd/points.ts). */
      family: SweepTraceFamily
      display: SweepDisplay
    }
  | {
      kind: 'impulse'
      impulse: ImpulseTraceJson | null
      /** The gated response — the impulse screen's Response view. */
      traces: ResponseTrace[]
      harmonics: HarmonicCurveJson[]
      display: ImpulseDisplay
      /** What the run says about itself — the Display panel's Measurement block, which no curve carries. */
      run: ImpulseRunFacts
    }
  | {
      kind: 'impedance'
      traces: ImpedanceTrace[]
      display: ImpedanceDisplay
      /** What the type's Component dropdown says is on the fixture — which component fit the chapter prints. */
      dutType: DutType
      /** The frequency the readout panel's Equivalent circuit block is set to — what that block is printed at. */
      equivalentHz: number
      /** The series resistor the run was measured through. */
      referenceOhms: number
      /** The generator level the run used, for the drive current; null when the run did not record one. */
      generatorAmpDbv: number | null
    }
  | {
      kind: 'outputImpedance'
      /** The Zout curve — an impedance trace; null when nothing was measured. */
      trace: ImpedanceTrace | null
      display: OutputImpedanceDisplay
      /** What the damping factor is quoted against. */
      nominalOhms: number
      rightChannelIsReference: boolean
    }
  | {
      kind: 'crosstalk'
      /** The crosstalk trace(s), one per direction measured — response traces of the crosstalk kinds. */
      traces: ResponseTrace[]
      display: CrosstalkDisplay
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
  /** Charts in black and white. */
  monochrome: boolean
}

/** A name for a subject wherever the report has to tell several apart. */
export function subjectLabel(subject: ReportSubject): string {
  return subject.resultName ?? 'Current measurement'
}
