<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, type MeasurementSummary } from '../api/client'
import { useMeasurements } from '../composables/useMeasurements'
import { useProjects } from '../composables/useProjects'
import { useReportSettings } from '../composables/useReportSettings'
import { buildMeasurementReport, kindLabel, loadStoredSubject } from '../report/buildReport'
import { downloadBlob, safeFileName } from '../report/download'
import { reportElementsForKind, type ReportElementId } from '../report/elements'
import { subjectHasChartData } from '../report/render'
import type { ReportMode, ReportSubject } from '../report/types'
import { reportViewsForKind } from '../report/views'
import Modal from './Modal.vue'

/**
 * "Report…" for the measurement that is open: which of its stored results to include, the current
 * measurement on screen among them when there is one, which chart views, whether the measurements
 * share one chart or get a chart each, and which elements. The element, view and mode choices are
 * remembered per kind (useReportSettings) and written *before* building, so a report that fails half
 * way still leaves the next dialog opening the same. Which results are ticked is not remembered — it
 * is a choice about this report.
 *
 * Opened from the toolbar the current measurement is preselected, together with the stored results
 * ticked on in the History checklist — the chart on screen — and from a History row that row is.
 */
const props = defineProps<{
  open: boolean
  /** What is on screen, or null when nothing has been measured (the option is then not offered). */
  liveSubject: ReportSubject | null
  /** A History row's result to start with ticked; null when opened from the toolbar. */
  preselectResultId: number | null
}>()
const emit = defineEmits<{ close: [] }>()

const { currentTypeId, types } = useProjects()
const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)
const { overlays } = useMeasurements()
const { load, choiceFor, setChoiceFor } = useReportSettings()

const kind = computed(() => currentType.value?.kind ?? '')
const elements = computed(() => reportElementsForKind(kind.value))
const views = computed(() => reportViewsForKind(kind.value))
const kindLabelLower = computed(() => (kind.value ? kindLabel(kind.value).toLowerCase() : 'measurement'))

const items = ref<MeasurementSummary[]>([])
const loading = ref(false)
const includeLive = ref(false)
const selected = ref<Set<number>>(new Set())
const chosenElements = ref<Set<ReportElementId>>(new Set())
const chosenViews = ref<Set<string>>(new Set())
const mode = ref<ReportMode>('combined')
const busy = ref(false)
const progress = ref<string | null>(null)
const error = ref<string | null>(null)

const liveAvailable = computed(() => props.liveSubject !== null && subjectHasChartData(props.liveSubject))

watch(
  () => props.open,
  async (open) => {
    if (!open || !currentType.value) return
    error.value = null
    progress.value = null
    busy.value = false
    loading.value = true
    try {
      await load()
      const choice = choiceFor(kind.value)
      chosenElements.value = new Set(choice.elements)
      chosenViews.value = new Set(choice.views)
      mode.value = choice.mode

      const response = await api.listMeasurements({ measurementTypeId: currentType.value.id, take: 100 })
      // Newest first is how the History list reads; oldest first is how a report reads.
      items.value = [...response.items].reverse()

      includeLive.value = liveAvailable.value && props.preselectResultId === null
      const ticked = new Set<number>()
      if (props.preselectResultId !== null) ticked.add(props.preselectResultId)
      else for (const overlay of overlays.value) if (overlay.visible) ticked.add(overlay.id)
      selected.value = new Set(items.value.filter((i) => ticked.has(i.resultId)).map((i) => i.resultId))
    } catch (e) {
      error.value = (e as Error).message
    } finally {
      loading.value = false
    }
  },
)

const selectionCount = computed(() => selected.value.size + (includeLive.value ? 1 : 0))
const canGenerate = computed(
  () => selectionCount.value > 0 && chosenElements.value.size > 0 && !busy.value && !loading.value,
)

function toggleSet<T>(set: Set<T>, id: T, on: boolean): Set<T> {
  const next = new Set(set)
  if (on) next.add(id)
  else next.delete(id)
  return next
}

async function generate() {
  const type = currentType.value
  if (!type || !canGenerate.value) return

  busy.value = true
  error.value = null
  const elementIds = elements.value.map((e) => e.id).filter((id) => chosenElements.value.has(id))
  const viewIds = views.value.map((v) => v.id).filter((id) => chosenViews.value.has(id))
  try {
    await setChoiceFor(kind.value, { elements: elementIds, views: viewIds, mode: mode.value })

    const subjects: ReportSubject[] = []
    if (includeLive.value && props.liveSubject) subjects.push(props.liveSubject)
    for (const item of items.value) {
      if (!selected.value.has(item.resultId)) continue
      progress.value = `Loading ${item.name}…`
      subjects.push(await loadStoredSubject(item.resultId, types.value))
    }

    const document = await buildMeasurementReport(
      { subjects, elements: elementIds, views: viewIds, mode: mode.value },
      (message) => {
        progress.value = message
      },
    )
    progress.value = 'Laying out the PDF…'
    const pdf = await api.renderReportPdf(document)
    const name = subjects.length === 1 ? (subjects[0].resultName ?? type.name) : type.name
    downloadBlob(pdf, `${safeFileName(name)}.pdf`)
    emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
    progress.value = null
  }
}
</script>

<template>
  <Modal :open="props.open" title="Report" @close="emit('close')">
    <div v-if="currentType" class="flex flex-col gap-3 text-sm">
      <p class="text-xs text-neutral-500 dark:text-neutral-400">
        <span class="font-medium text-neutral-800 dark:text-neutral-100">{{ currentType.name }}</span>
        — {{ kindLabel(kind) }}
      </p>

      <fieldset class="flex flex-col gap-1">
        <legend class="mb-1 text-xs text-neutral-500 dark:text-neutral-400">Measurements</legend>
        <p v-if="loading" class="text-xs text-neutral-500 dark:text-neutral-400">Loading the stored results…</p>
        <template v-else>
          <label v-if="liveAvailable" class="flex items-center gap-2">
            <input v-model="includeLive" type="checkbox" :disabled="busy" />
            <span>Current measurement</span>
            <span class="text-xs text-neutral-500 dark:text-neutral-400">on screen, not saved</span>
          </label>
          <div class="flex max-h-48 flex-col gap-1 overflow-y-auto pr-1">
            <label v-for="item in items" :key="item.resultId" class="flex items-center gap-2">
              <input
                type="checkbox"
                :checked="selected.has(item.resultId)"
                :disabled="busy"
                @change="selected = toggleSet(selected, item.resultId, ($event.target as HTMLInputElement).checked)"
              />
              <span class="truncate">{{ item.name }}</span>
              <span class="ml-auto shrink-0 text-xs text-neutral-500 dark:text-neutral-400">
                {{ new Date(item.timestampUtc).toLocaleString() }}
              </span>
            </label>
          </div>
          <p v-if="!liveAvailable && items.length === 0" class="text-xs text-neutral-500 dark:text-neutral-400">
            Nothing measured and nothing stored yet.
          </p>
        </template>
      </fieldset>

      <fieldset v-if="views.length > 1" class="flex flex-col gap-1">
        <legend class="mb-1 text-xs text-neutral-500 dark:text-neutral-400">Charts</legend>
        <div class="flex flex-wrap gap-x-4 gap-y-1">
          <label v-for="view in views" :key="view.id" class="flex items-center gap-2">
            <input
              type="checkbox"
              :checked="chosenViews.has(view.id)"
              :disabled="busy || !chosenElements.has('chart')"
              @change="chosenViews = toggleSet(chosenViews, view.id, ($event.target as HTMLInputElement).checked)"
            />
            <span>{{ view.label }}</span>
          </label>
        </div>
      </fieldset>

      <fieldset class="flex flex-col gap-1">
        <legend class="mb-1 text-xs text-neutral-500 dark:text-neutral-400">Several measurements</legend>
        <label class="flex items-center gap-2" :class="{ 'opacity-50': selectionCount < 2 }">
          <input v-model="mode" type="radio" value="combined" :disabled="busy || selectionCount < 2" />
          <span>One chart with every measurement, their notes beneath it</span>
        </label>
        <label class="flex items-center gap-2" :class="{ 'opacity-50': selectionCount < 2 }">
          <input v-model="mode" type="radio" value="individual" :disabled="busy || selectionCount < 2" />
          <span>A chart per measurement, each with its own notes</span>
        </label>
      </fieldset>

      <fieldset class="flex flex-col gap-2">
        <legend class="mb-1 text-xs text-neutral-500 dark:text-neutral-400">Include</legend>
        <label v-for="element in elements" :key="element.id" class="flex items-start gap-2" :title="element.hint">
          <input
            type="checkbox"
            class="mt-0.5"
            :checked="chosenElements.has(element.id)"
            :disabled="busy"
            @change="chosenElements = toggleSet(chosenElements, element.id, ($event.target as HTMLInputElement).checked)"
          />
          <span class="flex flex-col">
            <span>{{ element.label }}</span>
            <span class="text-xs text-neutral-500 dark:text-neutral-400">{{ element.hint }}</span>
          </span>
        </label>
      </fieldset>

      <p class="text-xs text-neutral-500 dark:text-neutral-400">
        The charts, the layout and the elements are remembered for every {{ kindLabelLower }} report.
      </p>

      <p v-if="error" class="text-xs text-red-600 dark:text-red-400">{{ error }}</p>

      <div class="flex items-center justify-end gap-2 pt-2">
        <span v-if="progress" class="mr-auto truncate text-xs text-neutral-500 dark:text-neutral-400">{{ progress }}</span>
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          :disabled="busy"
          @click="emit('close')"
        >
          Cancel
        </button>
        <button
          type="button"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          :disabled="!canGenerate"
          @click="generate"
        >
          Generate PDF
        </button>
      </div>
    </div>
  </Modal>
</template>
