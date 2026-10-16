import { ref } from 'vue'
import { api, type ReportKindSettings } from '../api/client'
import { defaultReportElements, reportElementsForKind, storedChoiceElements, type ReportElementId } from '../report/elements'
import type { ReportMode } from '../report/types'
import { defaultReportViews, reportViewsForKind } from '../report/views'

/**
 * How a report of each measurement kind is put together — which elements, which chart views, and
 * whether several measurements share one chart — remembered so the next report of the same kind
 * comes out the same way without ticking the boxes again.
 *
 * Module-level singleton like useUiLayout, and stored the same way (`report.settings` in the
 * server's configuration database): a preference about how this user likes a report, which belongs
 * with the layout and the capture delay rather than in a project file. Nothing stored means the
 * kind's default elements (everything it offers but the settings — see defaultReportElements), its
 * default view and a chart per measurement; a stored id the kind no longer offers is dropped on the
 * way out, so a build that renames one cannot leave a report silently missing it. The project
 * report reads the same choices, so a kind that has never had its own Report dialog opened still
 * gets a full chapter there.
 *
 * Black and white is one flag for every kind rather than a per-kind choice: a project report spans
 * kinds and prints as one document, and nobody wants half of it grey.
 */
const kinds = ref<Record<string, ReportKindSettings>>({})
const chartsMonochrome = ref(false)
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
        chartsMonochrome.value = settings.chartsMonochrome === true
      } catch {
        // Nothing stored is the default anyway; a document that could not be read reads as that.
      }
    })()
    return loadPromise
  }

  /** The elements a report of this kind includes: the stored choice, or the kind's defaults. */
  function elementsFor(kind: string): ReportElementId[] {
    const stored = kinds.value[kind]?.elements
    if (!stored) return defaultReportElements(kind)
    return storedChoiceElements(
      reportElementsForKind(kind).map((element) => element.id),
      stored,
      kinds.value[kind]?.offered,
    )
  }

  /** The chart views a report of this kind includes: the stored choice, or the kind's default view. */
  function viewsFor(kind: string): string[] {
    const offered = reportViewsForKind(kind).map((view) => view.id)
    const stored = kinds.value[kind]?.views
    if (!stored) return defaultReportViews(kind)
    const kept = offered.filter((id) => stored.includes(id))
    return kept.length > 0 ? kept : defaultReportViews(kind)
  }

  /** A chart per measurement unless this kind was told otherwise. */
  function modeFor(kind: string): ReportMode {
    return kinds.value[kind]?.mode === 'combined' ? 'combined' : 'individual'
  }

  function choiceFor(kind: string): ReportKindChoice {
    return { elements: elementsFor(kind), views: viewsFor(kind), mode: modeFor(kind) }
  }

  async function persist() {
    try {
      await api.setReportSettings({ kinds: kinds.value, chartsMonochrome: chartsMonochrome.value })
    } catch {
      // The choice holds for this session either way — see setChoiceFor.
    }
  }

  /** Remembers the choice for this kind. Applied locally first, so a failed write still shapes this session's reports. */
  async function setChoiceFor(kind: string, choice: ReportKindChoice) {
    kinds.value = {
      ...kinds.value,
      [kind]: {
        elements: [...choice.elements],
        views: [...choice.views],
        mode: choice.mode,
        // What this choice was offered, so a later build can tell an unticked element from one that
        // did not exist when the choice was made — see elementsFor.
        offered: reportElementsForKind(kind).map((element) => element.id),
      },
    }
    await persist()
  }

  /** Remembers whether report charts are black and white — for every kind at once. */
  async function setChartsMonochrome(on: boolean) {
    chartsMonochrome.value = on
    await persist()
  }

  return { load, elementsFor, viewsFor, modeFor, choiceFor, setChoiceFor, chartsMonochrome, setChartsMonochrome }
}
