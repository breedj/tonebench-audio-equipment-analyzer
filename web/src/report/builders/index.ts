import type { ReportSection } from '../../api/client'
import {
  FFT_SPECTRUM_KIND,
  isCrosstalkKind,
  isFrequencyResponseKind,
  isImpedanceKind,
  isImpulseResponseKind,
  isOutputImpedanceKind,
  isSweepKind,
} from '../../measurements/kinds'
import type { ReportElementId } from '../elements'
import type { ReportSubject } from '../types'
import { annotationsSections } from './annotations'
import { crosstalkSections } from './crosstalk'
import { outputImpedanceSections } from './outputImpedance'
import { frequencyResponseSections } from './frequencyResponse'
import { descriptionSections, genericSettingsSection } from './generic'
import { impedanceSections } from './impedance'
import { impulseResponseSections } from './impulseResponse'
import { spectrumSections } from './spectrum'
import { sweepSections } from './sweep'

/**
 * The chapter's sections for a subject of any kind, chart excluded — the chart is an image the
 * orchestrator renders and inserts. A kind with a builder of its own gets it; every other kind gets
 * the generic description and settings, which is what lets a project report include a measurement
 * whose chart and readings have not been given a builder yet.
 *
 * <p>The <b>annotations</b> are prepended here rather than left to each builder, which is what makes
 * them work on every kind at once and stops the eighth builder from silently forgetting them — a
 * missing section prints nothing and says nothing. First, because the numbered table is a legend for
 * the picture the orchestrator has just inserted, and a legend belongs under its figure rather than
 * after the settings. (Each kind's *settings* stay in its own builder: those tables are
 * kind-specific, and only the no-builder arm falls back to the generic one.)</p>
 *
 * @param views Which of the kind's charts this chapter prints, so a note on a picture nobody
 *   printed is not tabulated beside one that was.
 */
export function chapterSections(
  subject: ReportSubject,
  elements: readonly ReportElementId[],
  views?: readonly string[],
): ReportSection[] {
  const notes = elements.includes('annotations') ? annotationsSections(subject, views) : []
  return [...notes, ...kindSections(subject, elements)]
}

function kindSections(subject: ReportSubject, elements: readonly ReportElementId[]): ReportSection[] {
  if (isFrequencyResponseKind(subject.kind)) return frequencyResponseSections(subject, elements)
  if (isCrosstalkKind(subject.kind)) return crosstalkSections(subject, elements)
  if (isOutputImpedanceKind(subject.kind)) return outputImpedanceSections(subject, elements)
  if (isSweepKind(subject.kind)) return sweepSections(subject, elements)
  if (isImpedanceKind(subject.kind)) return impedanceSections(subject, elements)
  if (isImpulseResponseKind(subject.kind)) return impulseResponseSections(subject, elements)
  if (subject.kind === FFT_SPECTRUM_KIND) return spectrumSections(subject, elements)

  const sections: ReportSection[] = []
  if (elements.includes('description')) sections.push(...descriptionSections(subject))
  if (elements.includes('settings')) sections.push(genericSettingsSection(subject.paramsJson))
  return sections
}
