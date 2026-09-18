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
import { toImpedanceTraces, toResponseTraces, toSweepPoints } from '../composables/useMeasurements'
import { impedanceDisplayFromParams } from '../impedance/display'
import { defaultImpulseDisplay, impulseDisplayFromParams } from '../measurements/impulseDisplay'
import {
  FFT_SPECTRUM_KIND,
  isFrequencyResponseKind,
  isImpedanceKind,
  isImpulseResponseKind,
  isSweepKind,
  MEASUREMENT_KINDS,
} from '../measurements/kinds'
import { responseDisplayFromParams, responsePhaseKindFromParams, responseRightChannelIsReferenceFromParams } from '../response/display'
import { sweepDisplayFromParams } from '../sweep/display'
import { sweepMethodFromParams } from '../sweep/points'
import { chapterSections } from './builders'
import { blobToBase64 } from './download'
import type { ReportElementId } from './elements'
import { renderView, viewIsCombinable, type RenderedChart } from './render'
import { storedSpectrumFrames } from './spectrumFrames'
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

function formatTimestamp(iso: string | null): string {
  if (!iso) return 'On screen — not saved'
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString()
}

export function formatRangeDbv(dbv: number | null | undefined): string | null {
  return typeof dbv === 'number' ? `${dbv} dBV` : null
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
    return {
      kind: 'spectrum',
      spectrum: storedSpectrumFrames(detail),
      meters: {},
      display: createSpectrumDisplaySource(
        parseStoredDisplay(displayParams),
        detail.channelsData.map((c) => c.channel),
      ),
    }
  }

  if (isSweepKind(detail.kind)) {
    return {
      kind: 'sweep',
      points: toSweepPoints((await api.getStoredSweep(detail.resultId)).points),
      method: sweepMethodFromParams(detail.paramsJson),
      display: sweepDisplayFromParams(displayParams),
    }
  }

  if (isImpulseResponseKind(detail.kind)) {
    const [trace, stored, response] = await Promise.all([
      api.getStoredImpulseTrace(detail.resultId),
      api.getStoredImpulse(detail.resultId),
      api.getStoredResponse(detail.resultId),
    ])
    return {
      kind: 'impulse',
      impulse: trace.impulse,
      traces: toResponseTraces(response.traces),
      harmonics: stored.harmonics,
      display: impulseDisplayFromParams(displayParams, defaultImpulseDisplay(detail.sampleRateHz)),
    }
  }

  if (isImpedanceKind(detail.kind)) {
    return {
      kind: 'impedance',
      traces: toImpedanceTraces((await api.getStoredResponse(detail.resultId)).traces),
      display: impedanceDisplayFromParams(displayParams),
    }
  }

  return { kind: 'none' }
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
async function ownChartSections(subject: ReportSubject, views: readonly string[]): Promise<ReportSection[]> {
  const sections: ReportSection[] = []
  for (const view of views) {
    const chart = await renderView(view, subject, [], null)
    if (chart) sections.push(await imageSection(chart, chart.caption))
  }
  return sections
}

/** The rows that head a subject's part of a chapter: when it was taken and what it was taken with. */
function subjectMeta(subject: ReportSubject): ReportRow[] {
  return [{ label: 'Taken', value: formatTimestamp(subject.timestampUtc) }, ...subject.facts]
}

/** One chapter for one subject: its charts first (when chosen and available), then the builder's sections. */
export async function buildChapter(
  subject: ReportSubject,
  elements: readonly ReportElementId[],
  views: readonly string[],
): Promise<ReportChapter> {
  const sections: ReportSection[] = []
  if (elements.includes('chart')) sections.push(...(await ownChartSections(subject, views)))
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
        const chart = await renderView(view, primary, rest, rest.length > 0 ? subjectLabel(primary) : null)
        if (chart) {
          const names = plan.subjects.map(subjectLabel).join(', ')
          sections.push(await imageSection(chart, `${chart.caption} — ${names}`))
        }
      } else {
        for (const subject of plan.subjects) {
          onProgress?.(`Rendering ${view} — ${subjectLabel(subject)}…`)
          const chart = await renderView(view, subject, [], null)
          if (chart) sections.push(await imageSection(chart, `${chart.caption} — ${subjectLabel(subject)}`))
        }
      }
    }
  }

  const textElements = plan.elements.filter((e) => e !== 'chart')
  for (const subject of plan.subjects) {
    sections.push({ kind: 'keyValues', title: subjectLabel(subject), rows: subjectMeta(subject) })
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
      chapters.push(await buildChapter(subject, plan.elements, plan.views))
    }
  }

  return {
    title: plan.subjects.length === 1 ? chapters[0].title : first.typeName,
    subtitle: first.projectName,
    generated: new Date().toLocaleString(),
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
  onProgress?: (done: number, total: number, title: string) => void,
): Promise<ReportDocument> {
  const chapters: ReportChapter[] = []
  for (let i = 0; i < items.length; i++) {
    const { subject, elements, views } = items[i]
    onProgress?.(i, items.length, subjectLabel(subject))
    chapters.push(await buildChapter(subject, elements, views.length > 0 ? views : reportViewsForKind(subject.kind).slice(0, 1).map((v) => v.id)))
  }
  onProgress?.(items.length, items.length, '')

  return {
    title: project.name,
    subtitle: 'Project report',
    generated: new Date().toLocaleString(),
    notes: includeProjectNotes ? project.notes : null,
    chapters,
  }
}
