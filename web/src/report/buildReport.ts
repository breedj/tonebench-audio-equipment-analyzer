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
import {
  LEGACY_SPECTRUM_FRAME,
  parseAnnotations,
  readStoredAnnotations,
  type ChartAnnotation,
} from '../measurements/chartAnnotations'
import { storedAnnotationFrame } from '../charts/annotationViews'
import { formatWithPreference } from '../composables/useDateTimeFormat'
import { splitVersion } from '../about/version'
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
  measurementKindLabel,
  isImdSweepKind,
} from '../measurements/kinds'
import { responseDisplayFromParams, responsePhaseKindFromParams, responseRightChannelIsReferenceFromParams } from '../response/display'
import { sweepDisplayFromParams } from '../sweep/display'
import { sweepMethodFromParams } from '../sweep/points'
import { chapterSections } from './builders'
import { reportAccent } from './palette'
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

/** Kept as this module's name for it; the table it reads lives in `measurements/kinds.ts`. */
export function kindLabel(kind: string): string {
  return measurementKindLabel(kind)
}

/** When a stored result was measured, in the configured format; null for anything not saved. */
function formatTimestamp(subject: ReportSubject): string | null {
  if (!subject.timestampUtc) return null
  // Configuration ▸ General ▸ Date and time format — the same choice the Screenshot's date line follows.
  const date = new Date(subject.timestampUtc)
  return Number.isNaN(date.getTime()) ? subject.timestampUtc : formatWithPreference(date)
}

/** The chapter's own heading: what this chapter is *of*, under the measurement's name. */
function chapterTitle(subject: ReportSubject): string {
  if (subject.resultName) return subject.resultName
  return subject.data.kind === 'none' ? 'Settings and notes' : 'Current measurement'
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
 *
 * <p><b>Its annotations are the one exception</b>, and that exception is the point of them: they are
 * notes about <i>this capture</i> — "this hum is the transformer", "the corner is here" — not a
 * preference about how the screen is arranged. A result saved last month must print the notes it was
 * saved with however the measurement's chart is set up today, which is the opposite of the rule for
 * everything else in this function. The copy is made by MeasurementSaveService.SaveAsync and
 * re-edited through PUT /api/measurements/{id} (the Edit measurement dialog's list).</p>
 */
/**
 * A display with the result's <b>own</b> annotations where it has any, and the measurement type's
 * otherwise.
 *
 * <p>The asymmetry with the FFT arm below is deliberate and is the price of adding no save plumbing.
 * The FFT's save <i>copies</i> the list onto the result, so its annotations are always the result's
 * own and a fallback there would make a result saved before the feature existed inherit the
 * measurement's current notes — exactly what a frozen copy exists to prevent. No other kind's save
 * copies anything, so until a stored result is given its own set in the Edit dialog, its report
 * prints the measurement's. `readStoredAnnotations` answering <b>null</b> for a document with no
 * list is what tells the two apart: an empty list is a result somebody cleared, and stays empty.</p>
 */
function withResultAnnotations<T extends { annotations: ChartAnnotation[] }>(
  display: T,
  detail: MeasurementDetail,
): T {
  const own = readStoredAnnotations(
    parseStoredDisplay(detail.paramsJson)?.chartAnnotations,
    storedAnnotationFrame(detail.kind),
  )
  return own === null ? display : { ...display, annotations: own }
}

async function loadStoredData(detail: MeasurementDetail, type: MeasurementType | null): Promise<ReportSubjectData> {
  const displayParams = type?.paramsJson

  if (isFrequencyResponseKind(detail.kind)) {
    return {
      kind: 'response',
      traces: toResponseTraces((await api.getStoredResponse(detail.resultId)).traces),
      display: withResultAnnotations(responseDisplayFromParams(displayParams), detail),
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
        parseAnnotations(parseStoredDisplay(detail.paramsJson)?.chartAnnotations, LEGACY_SPECTRUM_FRAME),
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
      spectrogram: null,
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
      display: withResultAnnotations(sweepDisplayFromParams(displayParams), detail),
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
      display: withResultAnnotations(
        impulseDisplayFromParams(displayParams, defaultImpulseDisplay(detail.sampleRateHz)),
        detail,
      ),
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
      display: withResultAnnotations(impedanceDisplayFromParams(displayParams), detail),
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
      display: withResultAnnotations(crosstalkDisplayFromParams(displayParams), detail),
    }
  }

  if (isOutputImpedanceKind(detail.kind)) {
    // The nominal impedance and the method come from the result's own params_json — they describe
    // the run, not the screen — while the chart window is the type's, like every other kind's.
    const run = outputImpedanceRunFromParams(detail.paramsJson)
    return {
      kind: 'outputImpedance',
      trace: toImpedanceTraces((await api.getStoredResponse(detail.resultId)).traces).find((t) => t.kind === 'Impedance') ?? null,
      display: withResultAnnotations(outputImpedanceDisplayFromParams(displayParams), detail),
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

/**
 * The ordinal badges on the chart's labels are drawn only when the Annotations table is included:
 * unticked, a number on the picture would tie a label to a row nobody printed. Set on the subject's
 * own display source, which is what the headless render draws through, right before it renders.
 */
function applyAnnotationNumbers(subject: ReportSubject, elements: readonly ReportElementId[]) {
  if (subject.data.kind !== 'spectrum') return
  subject.data.display.annotationNumbers.value = elements.includes('annotations')
}

async function imageSection(chart: RenderedChart, caption: string): Promise<ReportSection> {
  return { kind: 'image', png: await blobToBase64(chart.png), caption }
}

/** The chart sections of one subject on its own: one per chosen view it has data for. */
async function ownChartSections(
  subject: ReportSubject,
  views: readonly string[],
  monochrome: boolean,
  annotationNumbers: boolean,
): Promise<ReportSection[]> {
  const sections: ReportSection[] = []
  for (const view of views) {
    const chart = await renderView(view, subject, [], null, monochrome, annotationNumbers)
    if (chart) sections.push(await imageSection(chart, chart.caption))
  }
  return sections
}

/** The secondary charts a kind's elements ask for (the FFT's oscilloscope and spectrogram), as image sections. */
async function extraChartSections(subject: ReportSubject, elements: readonly ReportElementId[], monochrome: boolean): Promise<ReportSection[]> {
  const sections: ReportSection[] = []
  for (const chart of await renderExtraCharts(subject, elements, monochrome)) {
    sections.push(await imageSection(chart, chart.caption))
  }
  return sections
}

/**
 * The rows that head a subject's part of a chapter: when it was taken and what it was taken with.
 *
 * <p><b>Only a stored result has a "Taken" row.</b> A measurement read off the screen has no
 * timestamp to print, and the row said so — "Taken: On screen — not saved" — which told a reader
 * nothing they could use and read as a fault in the report. A chapter with nothing measured already
 * opens with a callout saying so.</p>
 */
function subjectMeta(subject: ReportSubject): ReportRow[] {
  const taken = formatTimestamp(subject)
  return taken === null ? [...subject.facts] : [{ label: 'Taken', value: taken }, ...subject.facts]
}

/**
 * The headline figures go under the chapter's head, above its chart — where the design puts them and
 * where a reader looks first. Done here rather than in the builders, so a builder stays free to emit
 * its figures beside the readings they summarise.
 *
 * **Only when there is one of them.** Several are a per-channel series (the sweeps emit one row of
 * cards per channel, each above its own table), and hoisting those would separate every one of them
 * from the table it belongs to.
 */
function hoistFigures(sections: ReportSection[]) {
  const figures = sections.filter((s) => s.kind === 'figures')
  if (figures.length !== 1) return
  sections.splice(sections.indexOf(figures[0]), 1)
  // After a callout, which says whether anything was measured at all.
  const callouts = sections.filter((s) => s.kind === 'callout').length
  sections.splice(callouts, 0, figures[0])
}

/** One chapter for one subject: its charts first (when chosen and available), then the builder's sections. */
export async function buildChapter(
  subject: ReportSubject,
  elements: readonly ReportElementId[],
  views: readonly string[],
  monochrome = false,
): Promise<ReportChapter> {
  const sections: ReportSection[] = []
  // Said once, at the top, rather than left to be worked out from a chapter with no chart in it.
  if (subject.data.kind === 'none') {
    sections.push({ kind: 'callout', title: 'Not measured.', text: 'This chapter carries the settings and notes only.' })
  }
  applyAnnotationNumbers(subject, elements)
  if (elements.includes('chart'))
    sections.push(...(await ownChartSections(subject, views, monochrome, elements.includes('annotations'))))
  sections.push(...(await extraChartSections(subject, elements, monochrome)))
  sections.push(...chapterSections(subject, elements, elements.includes('chart') ? views : []))
  hoistFigures(sections)

  return {
    title: chapterTitle(subject),
    // The measurement's own name is the eyebrow over it; the *kind* stays in the meta grid, where a
    // reader looking for "what measurement is this" finds it beside the rest of the facts.
    eyebrow: subject.typeName,
    accent: reportAccent(subject.kind),
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

  // In a combined chapter the chart draws the *primary* subject's annotations — an overlay is
  // plotted from its curves and carries no display source of its own — while each subject's own
  // table follows under its own heading below.
  for (const subject of plan.subjects) applyAnnotationNumbers(subject, plan.elements)

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

  // The secondary charts are per subject even here: a spectrogram or a waveform is one measurement's.
  const textElements = plan.elements.filter((e) => e !== 'chart')
  for (const subject of plan.subjects) {
    sections.push({ kind: 'keyValues', title: subjectLabel(subject), rows: subjectMeta(subject) })
    for (const chart of await renderExtraCharts(subject, plan.elements, plan.monochrome)) {
      sections.push(await imageSection(chart, `${chart.caption} — ${subjectLabel(subject)}`))
    }
    sections.push(...chapterSections(subject, textElements, plan.views))
  }

  return {
    // One chapter of several measurements, so its heading names them rather than one of them.
    title: plan.subjects.map(subjectLabel).join(', '),
    eyebrow: primary.typeName,
    accent: reportAccent(primary.kind),
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
    title: first.typeName,
    subtitle: first.projectName,
    eyebrow: 'Measurement report',
    generated: formatWithPreference(new Date()),
    version: await appVersion(),
    monochrome: plan.monochrome,
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
    subtitle: null,
    eyebrow: 'Project report',
    generated: formatWithPreference(new Date()),
    version: await appVersion(),
    monochrome,
    // Chapters and how many of them carry a measurement; the renderer adds the page count, which is
    // the one figure of the three that cannot be known before the document is laid out.
    stats: [
      { label: chapters.length === 1 ? 'Chapter' : 'Chapters', value: String(chapters.length) },
      { label: 'With stored results', value: String(items.filter((i) => i.subject.resultId !== null).length) },
    ],
    notes: includeProjectNotes ? project.notes : null,
    chapters,
  }
}

/**
 * "Tonebench v0.2.0" for the cover's footer. A report is a document that outlives the app that wrote
 * it, so which build produced it belongs on it; a failure is not worth taking the report down for,
 * and the renderer prints the bare name instead.
 */
async function appVersion(): Promise<string | null> {
  try {
    return `Tonebench ${splitVersion((await api.getAppInfo()).version).release}`
  } catch {
    return null
  }
}
