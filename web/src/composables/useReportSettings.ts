import { ref } from 'vue'
import { api, type ReportKindSettings } from '../api/client'
import { reportElementsForKind, type ReportElementId } from '../report/elements'
import type { ReportMode } from '../report/types'
import { defaultReportViews, reportViewsForKind } from '../report/views'

/**
 * How a report of each measurement kind is put together — which elements, which chart views, and
 * whether several measurements share one chart — remembered so the next report of the same kind
 * comes out the same way without ticking the boxes again.
 *
 * Module-level singleton like useUiLayout, and stored the same way (`report.settings` in the
 * server's configuration database): a preference about how this user likes a report, which belongs
 * with the layout and the capture delay rather than in a project file. Nothing stored means every
 * element the kind offers, its default view and combined charts; a stored id the kind no longer
 * offers is dropped on the way out, so a build that renames one cannot leave a report silently
 * missing it.
 */
const kinds = ref<Record<string, ReportKindSettings>>({})
let loadPromise: Promise<void> | null = null

export interface ReportKindChoice {
  elements: ReportElementId[]
  views: string[]
  mode: ReportMode
}

export function useReportSettings() {
  async function load() {
    if (loadPromise) return loadPromise
    loadPromise = (async () => {
      try {
        const settings = await api.getReportSettings()
        kinds.value = settings.kinds ?? {}
      } catch {
        // Nothing stored is the default anyway; a document that could not be read reads as that.
      }
    })()
    return loadPromise
  }

  /** The elements a report of this kind includes: the stored choice, or everything the kind offers. */
  function elementsFor(kind: string): ReportElementId[] {
    const offered = reportElementsForKind(kind).map((element) => element.id)
    const stored = kinds.value[kind]?.elements
    if (!stored) return offered
    return offered.filter((id) => stored.includes(id))
  }

  /** The chart views a report of this kind includes: the stored choice, or the kind's default view. */
  function viewsFor(kind: string): string[] {
    const offered = reportViewsForKind(kind).map((view) => view.id)
    const stored = kinds.value[kind]?.views
    if (!stored) return defaultReportViews(kind)
    const kept = offered.filter((id) => stored.includes(id))
    return kept.length > 0 ? kept : defaultReportViews(kind)
  }

  function modeFor(kind: string): ReportMode {
    return kinds.value[kind]?.mode === 'individual' ? 'individual' : 'combined'
  }

  function choiceFor(kind: string): ReportKindChoice {
    return { elements: elementsFor(kind), views: viewsFor(kind), mode: modeFor(kind) }
  }

  /** Remembers the choice for this kind. Applied locally first, so a failed write still shapes this session's reports. */
  async function setChoiceFor(kind: string, choice: ReportKindChoice) {
    kinds.value = {
      ...kinds.value,
      [kind]: { elements: [...choice.elements], views: [...choice.views], mode: choice.mode },
    }
    try {
      await api.setReportSettings({ kinds: kinds.value })
    } catch {
      // See above — the choice holds for this session either way.
    }
  }

  return { load, elementsFor, viewsFor, modeFor, choiceFor, setChoiceFor }
}
