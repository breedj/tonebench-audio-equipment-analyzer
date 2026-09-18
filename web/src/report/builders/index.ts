import type { ReportSection } from '../../api/client'
import { isFrequencyResponseKind } from '../../measurements/kinds'
import type { ReportElementId } from '../elements'
import type { ReportSubject } from '../types'
import { frequencyResponseSections } from './frequencyResponse'
import { descriptionSections, genericSettingsSection } from './generic'

/**
 * The chapter's sections for a subject of any kind, chart excluded — the chart is an image the
 * orchestrator renders and inserts. A kind with a builder of its own gets it; every other kind gets
 * the generic description and settings, which is what lets a project report include a measurement
 * whose chart and readings have not been given a builder yet.
 */
export function chapterSections(subject: ReportSubject, elements: readonly ReportElementId[]): ReportSection[] {
  if (isFrequencyResponseKind(subject.kind)) return frequencyResponseSections(subject, elements)

  const sections: ReportSection[] = []
  if (elements.includes('description')) sections.push(...descriptionSections(subject))
  if (elements.includes('settings')) sections.push(genericSettingsSection(subject.paramsJson))
  return sections
}
