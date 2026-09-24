import {
  api,
  type MeasurementDetail,
  type MeasurementType,
  type Project,
  type ReportChapter,
  type ReportDocument,
  type ReportRow,
  type ReportSection,
} from '../api/client'
import { createSpectrumDisplaySource, type StoredSpectrumDisplay } from '../composables/useChartDisplay'
import { formatWithPreference } from '../composables/useDateTimeFormat'
import { useLoadOhms } from '../composables/useLoadOhms'
import { toImpedanceTraces, toResponseTraces, toSweepPoints } from '../composables/useMeasurements'
import { toImdSweepPoints } from '../imd/points'
import { DEFAULT_SELECTED_METRICS } from '../composables/useMetricSelection'
import { isDutType, type DutType } from '../impedance/component'
import { impedanceDisplayFromParams } from '../impedance/display'
import { crosstalkDisplayFromParams } from '../crosstalk/display'
import { outputImpedanceDisplayFromParams } from '../outputImpedance/display'
import { defaultImpulseDisplay, impulseDisplayFromParams } from '../measurements/impulseDisplay'
import {
  FFT_SPECTRUM_KIND,
  isCrosstalkKind,
  isOutputImpedanceKind,
  isFrequencyResponseKind,
  isImpedanceKind,
  isImpulseResponseKind,
  isSweepKind,
  MEASUREMENT_KINDS,
  isImdSweepKind,
} from '../measurements/kinds'
import { responseDisplayFromParams, responsePhaseKindFromParams, responseRightChannelIsReferenceFromParams } from '../response/display'
import { sweepDisplayFromParams } from '../sweep/display'
import { sweepMethodFromParams } from '../sweep/points'
import { chapterSections } from './builders'
import { blobToBase64 } from './download'
import type { ReportElementId } from './elements'
import { renderExtraCharts, renderView, viewIsCombinable, type RenderedChart } from './render'
import { storedGeneratorTones, storedIntermodulationFrames, storedMetersFrames, storedNoiseFrames, storedSpectrumFrames, storedMultitoneFrames, storedStimulus } from './spectrumFrames'
import type { WeightingSpec } from '../streaming/protocol'
import { NO_WEIGHTING, weightingFromStored, weightingLabel } from '../weighting'
import { subjectLabel, type ReportPlan, type ReportSubject, type ReportSubjectData } from './types'
import { reportViewsForKind } from './views'

/**
 * Assembles report documents. A *plan* — the subjects (from the screen or from History), the chosen
 * elements, the chart views and whether the subjects share charts — becomes a title page and its
 * chapters; a project report is one chapter per stored result. The server lays the document out
 * (POST /api/reports/pdf) and knows nothing of measurements — see ReportsEndpoints.cs for why the
 * split is this way round.
 */

export function kindLabel(kind: string): string {
  return MEASUREMENT_KINDS.find((k) => k.kind === kind)?.label ?? kind
}

function formatTimestamp(subject: ReportSubject): string {
  if (!subject.timestampUtc) return subject.data.kind === 'none' ? 'Not measured — settings and notes only' : 'On screen — not saved'
  // Configuration ▸ General ▸ Date and time format — the same choice the Screenshot's date line follows.
  const date = new Date(subject.timestampUtc)
  return Number.isNaN(date.getTime()) ? subject.timestampUtc : formatWithPreference(date)
}

export function formatRangeDbv(dbv: number | null | undefined): string | null {
  return typeof dbv === 'number' ? `${dbv} dBV` : null
}

/** The IMD convention a stored FFT result was measured under, from its own params_json (`imdMethod`/`imdMaxOrder`); SMPTE/5 when it says nothing, which only matters for a row that has IMD figures at all. */
function storedImdAnalysis(paramsJson: string | null | undefined): { method: 'Smpte' | 'Ccif'; maxOrder: number } {
  try {
    const parsed = paramsJson ? (JSON.parse(paramsJson) as { imdMethod?: unknown; imdMaxOrder?: unknown }) : null
    return {
      method: parsed?.imdMethod === 'Ccif' ? 'Ccif' : 'Smpte',
      maxOrder: typeof parsed?.imdMaxOrder === 'number' ? parsed.imdMaxOrder : 5,
    }
  } catch {
    return { method: 'Smpte', maxOrder: 5 }
  }
}

/** The weighting and the measurement band a stored FFT result was measured under, from its params_json; unweighted and 20 Hz-20 kHz when it says nothing. */
function storedNoiseContext(paramsJson: string | null | undefined): { weighting: WeightingSpec; band: { loHz: number; hiHz: number } } {
  try {
    const parsed = paramsJson
      ? (JSON.parse(paramsJson) as { weighting?: unknown; weightingUser1?: unknown; weightingUser2?: unknown; measurementLoFreqHz?: unknown; measurementHiFreqHz?: unknown })
      : null
    return {
      weighting: weightingFromStored(
        typeof parsed?.weighting === 'string' ? parsed.weighting : null,
        typeof parsed?.weightingUser1 === 'boolean' ? parsed.weightingUser1 : null,
        typeof parsed?.weightingUser2 === 'boolean' ? parsed.weightingUser2 : null,
      ),
      band: {
        loHz: typeof parsed?.measurementLoFreqHz === 'number' ? parsed.measurementLoFreqHz : 20,
        hiHz: typeof parsed?.measurementHiFreqHz === 'number' ? parsed.measurementHiFreqHz : 20000,
      },
    }
  } catch {
    return { weighting: NO_WEIGHTING, band: { loHz: 20, hiHz: 20000 } }
  }
}

/** The user tables' names a stored result records (`weightingUser1Name`/`weightingUser2Name`), for the annunciator. */
function storedUserTableNames(paramsJson: string | null | undefined): { user1: string | null; user2: string | null } {
  try {
    const parsed = paramsJson ? (JSON.parse(paramsJson) as { weightingUser1Name?: unknown; weightingUser2Name?: unknown }) : null
    return {
      user1: typeof parsed?.weightingUser1Name === 'string' ? parsed.weightingUser1Name : null,
      user2: typeof parsed?.weightingUser2Name === 'string' ? parsed.weightingUser2Name : null,
    }
  } catch {
    return { user1: null, user2: null }
  }
}

function parseStoredDisplay(paramsJson: string | null | undefined): StoredSpectrumDisplay | null {
  if (!paramsJson) return null
  try {
    const parsed = JSON.parse(paramsJson) as StoredSpectrumDisplay
    return parsed && typeof parsed === 'object' ? parsed : null
  } catch {
    return null
  }
}

/**
 * A stored result's curves and the display state to draw them with. The display is the owning
 * measurement type's, because a result's own `params_json` records the run and not the screen (see
 * responseDisplayFromParams); a type that no longer exists leaves each kind's defaults.
 */
async function loadStoredData(detail: MeasurementDetail, type: MeasurementType | null): Promise<ReportSubjectData> {
  const displayParams = type?.paramsJson

  if (isFrequencyResponseKind(detail.kind)) {
    return {
      kind: 'response',
      traces: toResponseTraces((await api.getStoredResponse(detail.resultId)).traces),
      display: responseDisplayFromParams(displayParams),
      phaseKind: responsePhaseKindFromParams(detail.kind, detail.paramsJson),
      rightChannelIsReference: responseRightChannelIsReferenceFromParams(detail.paramsJson),
      latencySamples: null,
    }
  }

  if (detail.kind === FFT_SPECTRUM_KIND) {
    const stored = parseStoredDisplay(displayParams)
    const selected = (stored as { selectedMetrics?: unknown } | null)?.selectedMetrics
    const noiseContext = storedNoiseContext(detail.paramsJson)
    const tableNames = storedUserTableNames(detail.paramsJson)
    return {
      kind: 'spectrum',
      spectrum: storedSpectrumFrames(detail),
      meters: storedMetersFrames(detail),
      intermodulation: storedIntermodulationFrames(detail, storedImdAnalysis(detail.paramsJson)),
      multitone: storedMultitoneFrames(detail),
      noise: storedNoiseFrames(detail, noiseContext.weighting, noiseContext.band),
      weightingLabel: weightingLabel(noiseContext.weighting, tableNames.user1, tableNames.user2),
      display: createSpectrumDisplaySource(
        stored,
        detail.channelsData.map((c) => c.channel),
      ),
      selectedMetrics:
        Array.isArray(selected) && selected.length > 0
          ? selected.filter((l): l is string => typeof l === 'string')
          : [...DEFAULT_SELECTED_METRICS],
      generatorTones: storedGeneratorTones(detail.genJson),
      stimulus: storedStimulus(detail.genJson),
      loadOhms: useLoadOhms().loadOhms.value,
      scope: {},
      status: null,
      waterfall: null,
    }
  }

  if (isSweepKind(detail.kind)) {
    const imd = isImdSweepKind(detail.kind)
    const stored = (await api.getStoredSweep(detail.resultId)).points
    return {
      kind: 'sweep',
      points: imd ? toImdSweepPoints(stored) : toSweepPoints(stored),
      method: sweepMethodFromParams(detail.paramsJson),
      family: imd ? 'imd' : 'thd',
      display: sweepDisplayFromParams(displayParams),
    }
  }

  if (isImpulseResponseKind(detail.kind)) {
    const [trace, stored, response] = await Promise.all([
      api.getStoredImpulseTrace(detail.resultId),
      api.getStoredImpulse(detail.resultId),
      api.getStoredResponse(detail.resultId),
    ])
    const run = impulseRunFromParams(detail.paramsJson)
    // The reference and the removed delay are the stored impulse's own; the rest is the as-run params.
    const storedImpulse = stored.impulses[0] ?? null
    return {
      kind: 'impulse',
      impulse: trace.impulse,
      traces: toResponseTraces(response.traces),
      harmonics: stored.harmonics,
      display: impulseDisplayFromParams(displayParams, defaultImpulseDisplay(detail.sampleRateHz)),
      run: {
        referenceMode: storedImpulse?.referenceMode ?? run.referenceMode,
        referenceName: storedImpulse?.referenceName ?? null,
        latencySamples: storedImpulse?.latencySamples ?? null,
        // A stored result records what it was asked for, never how many records it actually took.
        averagesTaken: null,
        averageCount: run.averageCount,
        generatorAmpDbv: run.generatorAmpDbv,
      },
    }
  }

  if (isImpedanceKind(detail.kind)) {
    const run = impedanceRunFromParams(detail.paramsJson)
    return {
      kind: 'impedance',
      traces: toImpedanceTraces((await api.getStoredResponse(detail.resultId)).traces),
      display: impedanceDisplayFromParams(displayParams),
      // The component and the equivalent circuit's frequency are screen settings, stored with the
      // display on the type — not with the run.
      dutType: impedanceDutTypeFromParams(displayParams),
      equivalentHz: impedanceEquivalentHzFromParams(displayParams),
      referenceOhms: run.referenceOhms,
      generatorAmpDbv: run.generatorAmpDbv,
    }
  }

  if (isCrosstalkKind(detail.kind)) {
    return {
      kind: 'crosstalk',
      traces: toResponseTraces((await api.getStoredResponse(detail.resultId)).traces),
      display: crosstalkDisplayFromParams(displayParams),
    }
  }

  if (isOutputImpedanceKind(detail.kind)) {
    // The nominal impedance and the method come from the result's own params_json — they describe
    // the run, not the screen — while the chart window is the type's, like every other kind's.
    const run = outputImpedanceRunFromParams(detail.paramsJson)
    return {
      kind: 'outputImpedance',
      trace: toImpedanceTraces((await api.getStoredResponse(detail.resultId)).traces).find((t) => t.kind === 'Impedance') ?? null,
      display: outputImpedanceDisplayFromParams(displayParams),
      nominalOhms: run.nominalOhms,
      rightChannelIsReference: run.rightChannelIsReference,
    }
  }

  return { kind: 'none' }
}

/** The run facts an impulse chapter needs that its curves do not carry, read off a result's `params_json`. */
function impulseRunFromParams(paramsJson: string): { referenceMode: string; averageCount: number; generatorAmpDbv: number | null } {
  try {
    const params = JSON.parse(paramsJson) as { referenceMode?: unknown; averageCount?: unknown; generatorAmpDbv?: unknown }
    return {
      referenceMode: typeof params.referenceMode === 'string' ? params.referenceMode : 'None',
      averageCount: typeof params.averageCount === 'number' ? params.averageCount : 0,
      generatorAmpDbv: typeof params.generatorAmpDbv === 'number' ? params.generatorAmpDbv : null,
    }
  } catch {
    return { referenceMode: 'None', averageCount: 0, generatorAmpDbv: null }
  }
}

/** The two run facts an impedance chapter needs beside its curve, read off a result's `params_json`. */
function impedanceRunFromParams(paramsJson: string): { referenceOhms: number; generatorAmpDbv: number | null } {
  try {
    const params = JSON.parse(paramsJson) as { referenceOhms?: unknown; generatorAmpDbv?: unknown }
    return {
      referenceOhms: typeof params.referenceOhms === 'number' && params.referenceOhms > 0 ? params.referenceOhms : 0,
      generatorAmpDbv: typeof params.generatorAmpDbv === 'number' ? params.generatorAmpDbv : null,
    }
  } catch {
    return { referenceOhms: 0, generatorAmpDbv: null }
  }
}

/** What the type's Component dropdown says is on the fixture; None for a type that never chose, or none at all. */
function impedanceDutTypeFromParams(paramsJson: string | null | undefined): DutType {
  const stored = parseStoredDisplay(paramsJson) as { chartDutType?: unknown } | null
  return isDutType(stored?.chartDutType) ? stored.chartDutType : 'None'
}

/** The frequency the readout's Equivalent circuit block is set to; 1 kHz for a type that never chose. */
function impedanceEquivalentHzFromParams(paramsJson: string | null | undefined): number {
  const stored = parseStoredDisplay(paramsJson) as { chartEquivalentHz?: unknown } | null
  return typeof stored?.chartEquivalentHz === 'number' && stored.chartEquivalentHz > 0 ? stored.chartEquivalentHz : 1000
}

/** The two run facts an output-impedance chapter needs beside its curve, read off a result's `params_json`. */
function outputImpedanceRunFromParams(paramsJson: string): { nominalOhms: number; rightChannelIsReference: boolean } {
  try {
    const params = JSON.parse(paramsJson) as { nominalOhms?: unknown; rightChannelIsReference?: unknown }
    return {
      nominalOhms: typeof params.nominalOhms === 'number' && params.nominalOhms > 0 ? params.nominalOhms : 8,
      rightChannelIsReference: params.rightChannelIsReference === true,
    }
  } catch {
    return { nominalOhms: 8, rightChannelIsReference: false }
  }
}

/**
 * A measurement that has no stored result as a report subject: its notes and its current settings,
 * nothing measured. What the project report offers for a measurement that was set up but never
 * saved a result — the chapter then says what the measurement is, so the project's report is
 * complete even where its history is not. `data` is `none`, which every chart renderer answers with
 * nothing, and `paramsJson` is the type's own.
 */
export function typeOnlySubject(type: MeasurementType, projectName: string): ReportSubject {
  return {
    id: `type-${type.id}`,
    resultId: null,
    typeId: type.id,
    kind: type.kind,
    typeName: type.name,
    resultName: null,
    projectName,
    typeNotes: type.notes,
    resultNotes: null,
    paramsJson: type.paramsJson,
    timestampUtc: null,
    facts: [],
    sampleRateHz: 0,
    detail: null,
    data: { kind: 'none' },
  }
}

/** A stored result as a report subject. */
export async function loadStoredSubject(resultId: number, types: readonly MeasurementType[]): Promise<ReportSubject> {
  const detail = await api.getMeasurement(resultId)
  const type = types.find((t) => t.id === detail.measurementTypeId) ?? null

  const facts: ReportRow[] = [
    { label: 'Sample rate', value: `${detail.sampleRateHz} Hz` },
    { label: 'FFT size', value: String(detail.fftSize) },
  ]
  const inRange = formatRangeDbv(detail.inRangeDbv)
  const outRange = formatRangeDbv(detail.outRangeDbv)
  if (inRange) facts.push({ label: 'Input range', value: inRange })
  if (outRange) facts.push({ label: 'Output range', value: outRange })

  return {
    id: `stored-${resultId}`,
    resultId,
    typeId: detail.measurementTypeId,
    kind: detail.kind,
    typeName: detail.measurementTypeName,
    resultName: detail.name,
    projectName: detail.projectName,
    typeNotes: type?.notes ?? null,
    resultNotes: detail.notes,
    paramsJson: detail.paramsJson,
    timestampUtc: detail.timestampUtc,
    facts,
    sampleRateHz: detail.sampleRateHz,
    detail,
    data: await loadStoredData(detail, type),
  }
}

async function imageSection(chart: RenderedChart, caption: string): Promise<ReportSection> {
  return { kind: 'image', png: await blobToBase64(chart.png), caption }
}

/** The chart sections of one subject on its own: one per chosen view it has data for. */
async function ownChartSections(subject: ReportSubject, views: readonly string[], monochrome: boolean): Promise<ReportSection[]> {
  const sections: ReportSection[] = []
  for (const view of views) {
    const chart = await renderView(view, subject, [], null, monochrome)
    if (chart) sections.push(await imageSection(chart, chart.caption))
  }
  return sections
}

/** The secondary charts a kind's elements ask for (the FFT's oscilloscope and waterfall), as image sections. */
async function extraChartSections(subject: ReportSubject, elements: readonly ReportElementId[], monochrome: boolean): Promise<ReportSection[]> {
  const sections: ReportSection[] = []
  for (const chart of await renderExtraCharts(subject, elements, monochrome)) {
    sections.push(await imageSection(chart, chart.caption))
  }
  return sections
}

/** The rows that head a subject's part of a chapter: when it was taken and what it was taken with. */
function subjectMeta(subject: ReportSubject): ReportRow[] {
  return [{ label: 'Taken', value: formatTimestamp(subject) }, ...subject.facts]
}

/** One chapter for one subject: its charts first (when chosen and available), then the builder's sections. */
export async function buildChapter(
  subject: ReportSubject,
  elements: readonly ReportElementId[],
  views: readonly string[],
  monochrome = false,
): Promise<ReportChapter> {
  const sections: ReportSection[] = []
  if (elements.includes('chart')) sections.push(...(await ownChartSections(subject, views, monochrome)))
  sections.push(...(await extraChartSections(subject, elements, monochrome)))
  sections.push(...chapterSections(subject, elements))

  return {
    title: subject.resultName ? `${subject.typeName} — ${subject.resultName}` : subject.typeName,
    meta: [{ label: 'Measurement', value: kindLabel(subject.kind) }, { label: 'Project', value: subject.projectName }, ...subjectMeta(subject)],
    notes: null,
    sections,
  }
}

/**
 * One chapter for several subjects of one measurement. A combinable view is drawn once, with the
 * first subject as the primary curve and the rest as history overlays; a view that draws a single
 * measurement is drawn once per subject, captioned with its name. The subjects' own sections — their
 * notes, settings and readings — follow, each under a heading naming it, so the notes sit beneath the
 * combined chart in the order the curves are listed.
 */
async function buildCombinedChapter(plan: ReportPlan, onProgress?: (message: string) => void): Promise<ReportChapter> {
  const [primary, ...rest] = plan.subjects
  const sections: ReportSection[] = []

  if (plan.elements.includes('chart')) {
    for (const view of plan.views) {
      if (viewIsCombinable(primary.kind, view)) {
        onProgress?.(`Rendering ${view}…`)
        const chart = await renderView(view, primary, rest, rest.length > 0 ? subjectLabel(primary) : null, plan.monochrome)
        if (chart) {
          const names = plan.subjects.map(subjectLabel).join(', ')
          sections.push(await imageSection(chart, `${chart.caption} — ${names}`))
        }
      } else {
        for (const subject of plan.subjects) {
          onProgress?.(`Rendering ${view} — ${subjectLabel(subject)}…`)
          const chart = await renderView(view, subject, [], null, plan.monochrome)
          if (chart) sections.push(await imageSection(chart, `${chart.caption} — ${subjectLabel(subject)}`))
        }
      }
    }
  }

  // The secondary charts are per subject even here: a waterfall or a waveform is one measurement's.
  const textElements = plan.elements.filter((e) => e !== 'chart')
  for (const subject of plan.subjects) {
    sections.push({ kind: 'keyValues', title: subjectLabel(subject), rows: subjectMeta(subject) })
    for (const chart of await renderExtraCharts(subject, plan.elements, plan.monochrome)) {
      sections.push(await imageSection(chart, `${chart.caption} — ${subjectLabel(subject)}`))
    }
    sections.push(...chapterSections(subject, textElements))
  }

  return {
    title: primary.typeName,
    meta: [
      { label: 'Measurement', value: kindLabel(primary.kind) },
      { label: 'Project', value: primary.projectName },
      { label: 'Measurements', value: plan.subjects.map(subjectLabel).join(', ') },
    ],
    notes: null,
    sections,
  }
}

/** A report of one measurement: its selected subjects in the plan's mode, behind a title page. */
export async function buildMeasurementReport(plan: ReportPlan, onProgress?: (message: string) => void): Promise<ReportDocument> {
  if (plan.subjects.length === 0) throw new Error('Nothing selected to report.')
  const first = plan.subjects[0]

  let chapters: ReportChapter[]
  if (plan.mode === 'combined' && plan.subjects.length > 1) {
    chapters = [await buildCombinedChapter(plan, onProgress)]
  } else {
    chapters = []
    for (const subject of plan.subjects) {
      onProgress?.(`Rendering ${subjectLabel(subject)}…`)
      chapters.push(await buildChapter(subject, plan.elements, plan.views, plan.monochrome))
    }
  }

  return {
    title: plan.subjects.length === 1 ? chapters[0].title : first.typeName,
    subtitle: first.projectName,
    generated: formatWithPreference(new Date()),
    notes: null,
    chapters,
  }
}

export interface ProjectReportItem {
  subject: ReportSubject
  elements: readonly ReportElementId[]
  views: readonly string[]
}

export async function buildProjectReport(
  project: Project,
  items: readonly ProjectReportItem[],
  includeProjectNotes: boolean,
  monochrome: boolean,
  onProgress?: (done: number, total: number, title: string) => void,
): Promise<ReportDocument> {
  const chapters: ReportChapter[] = []
  for (let i = 0; i < items.length; i++) {
    const { subject, elements, views } = items[i]
    onProgress?.(i, items.length, subjectLabel(subject))
    const chapterViews = views.length > 0 ? views : reportViewsForKind(subject.kind).slice(0, 1).map((v) => v.id)
    chapters.push(await buildChapter(subject, elements, chapterViews, monochrome))
  }
  onProgress?.(items.length, items.length, '')

  return {
    title: project.name,
    subtitle: 'Project report',
    generated: formatWithPreference(new Date()),
    notes: includeProjectNotes ? project.notes : null,
    chapters,
  }
}
