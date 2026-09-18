<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, type MeasurementSummary, type MeasurementType, type Project } from '../api/client'
import { useReportSettings } from '../composables/useReportSettings'
import { buildProjectReport, kindLabel, loadStoredSubject, type ProjectReportItem } from '../report/buildReport'
import { downloadBlob, safeFileName } from '../report/download'
import { subjectHasChartData } from '../report/render'
import type { ReportSubject } from '../report/types'
import Modal from './Modal.vue'

/**
 * "Project report…": every stored result in the project, grouped by measurement in the sidebar's
 * order, each checked by default. Which elements each chapter carries is the kind's remembered
 * choice (useReportSettings) — the per-measurement Report dialog is where that is set — so this
 * dialog only asks which results to include. Queried per type rather than for the project as a
 * whole, because a measurement summary does not say which type it belongs to.
 */
const props = defineProps<{
  open: boolean
  project: Project | null
  types: readonly MeasurementType[]
  /** The measurement on screen, listed under its own type when it has been measured; null otherwise. */
  liveSubject: ReportSubject | null
}>()
const emit = defineEmits<{ close: [] }>()

const { load, elementsFor, viewsFor } = useReportSettings()

interface Group {
  type: MeasurementType
  items: MeasurementSummary[]
}

const groups = ref<Group[]>([])
const selected = ref<Set<number>>(new Set())
const includeLive = ref(false)
const includeNotes = ref(true)

/** The type the live measurement belongs to, when it has anything to show. */
const liveTypeId = computed(() =>
  props.liveSubject && subjectHasChartData(props.liveSubject) ? props.liveSubject.typeId : null,
)
const loading = ref(false)
const busy = ref(false)
const progress = ref<string | null>(null)
const error = ref<string | null>(null)

watch(
  () => props.open,
  async (open) => {
    if (!open || !props.project) return
    error.value = null
    progress.value = null
    busy.value = false
    loading.value = true
    try {
      await load()
      const loaded: Group[] = []
      for (const type of props.types) {
        const response = await api.listMeasurements({ measurementTypeId: type.id, take: 100 })
        // Newest first is how the History list reads; oldest first is how a report reads.
        loaded.push({ type, items: [...response.items].reverse() })
      }
      groups.value = loaded
      selected.value = new Set(loaded.flatMap((g) => g.items.map((i) => i.resultId)))
      includeLive.value = liveTypeId.value !== null
    } catch (e) {
      error.value = (e as Error).message
    } finally {
      loading.value = false
    }
  },
)

const resultCount = computed(() => groups.value.reduce((n, g) => n + g.items.length, 0) + (liveTypeId.value !== null ? 1 : 0))
const canGenerate = computed(() => (selected.value.size > 0 || includeLive.value) && !busy.value && !loading.value)

function isSelected(id: number): boolean {
  return selected.value.has(id)
}

function setSelected(id: number, on: boolean) {
  const next = new Set(selected.value)
  if (on) next.add(id)
  else next.delete(id)
  selected.value = next
}

function setGroup(group: Group, on: boolean) {
  const next = new Set(selected.value)
  for (const item of group.items) {
    if (on) next.add(item.resultId)
    else next.delete(item.resultId)
  }
  selected.value = next
  if (group.type.id === liveTypeId.value) includeLive.value = on
}

function groupState(group: Group): 'all' | 'none' | 'some' {
  const live = group.type.id === liveTypeId.value
  const total = group.items.length + (live ? 1 : 0)
  const count = group.items.filter((i) => selected.value.has(i.resultId)).length + (live && includeLive.value ? 1 : 0)
  return count === 0 ? 'none' : count === total ? 'all' : 'some'
}

async function generate() {
  const project = props.project
  if (!project || !canGenerate.value) return

  busy.value = true
  error.value = null
  try {
    const items: ProjectReportItem[] = []
    for (const group of groups.value) {
      for (const item of group.items) {
        if (!selected.value.has(item.resultId)) continue
        progress.value = `Loading ${item.name}…`
        const subject = await loadStoredSubject(item.resultId, props.types)
        items.push({ subject, elements: elementsFor(group.type.kind), views: viewsFor(group.type.kind) })
      }
      // The measurement on screen comes last in its type's group: it is the newest.
      if (includeLive.value && props.liveSubject && group.type.id === liveTypeId.value) {
        items.push({ subject: props.liveSubject, elements: elementsFor(group.type.kind), views: viewsFor(group.type.kind) })
      }
    }

    const document = await buildProjectReport(project, items, includeNotes.value, (done, total, title) => {
      progress.value = done < total ? `Rendering ${done + 1} of ${total} — ${title}` : 'Laying out the PDF…'
    })
    const pdf = await api.renderReportPdf(document)
    downloadBlob(pdf, `${safeFileName(project.name)}.pdf`)
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
  <Modal :open="props.open" title="Project report" @close="emit('close')">
    <div v-if="props.project" class="flex flex-col gap-3 text-sm">
      <p class="text-xs text-neutral-500 dark:text-neutral-400">
        <span class="font-medium text-neutral-800 dark:text-neutral-100">{{ props.project.name }}</span>
        — one chapter per stored result, in the sidebar's order. What each chapter includes is the
        choice remembered by that measurement's own Report dialog.
      </p>

      <p v-if="loading" class="text-xs text-neutral-500 dark:text-neutral-400">Loading the stored results…</p>

      <p v-else-if="resultCount === 0" class="text-xs text-neutral-500 dark:text-neutral-400">
        This project has no stored results yet. Save a measurement result first.
      </p>

      <div v-else class="flex max-h-80 flex-col gap-3 overflow-y-auto pr-1">
        <div v-for="group in groups" :key="group.type.id" class="flex flex-col gap-1">
          <label v-if="group.items.length > 0 || group.type.id === liveTypeId" class="flex items-center gap-2 font-medium">
            <input
              type="checkbox"
              :checked="groupState(group) === 'all'"
              :indeterminate="groupState(group) === 'some'"
              :disabled="busy"
              @change="setGroup(group, ($event.target as HTMLInputElement).checked)"
            />
            <span>{{ group.type.name }}</span>
            <span class="text-xs font-normal text-neutral-500 dark:text-neutral-400">{{ kindLabel(group.type.kind) }}</span>
          </label>
          <label
            v-for="item in group.items"
            :key="item.resultId"
            class="ml-6 flex items-center gap-2"
          >
            <input
              type="checkbox"
              :checked="isSelected(item.resultId)"
              :disabled="busy"
              @change="setSelected(item.resultId, ($event.target as HTMLInputElement).checked)"
            />
            <span class="truncate">{{ item.name }}</span>
            <span class="ml-auto shrink-0 text-xs text-neutral-500 dark:text-neutral-400">
              {{ new Date(item.timestampUtc).toLocaleString() }}
            </span>
          </label>
          <label v-if="group.type.id === liveTypeId" class="ml-6 flex items-center gap-2">
            <input v-model="includeLive" type="checkbox" :disabled="busy" />
            <span>Current measurement</span>
            <span class="ml-auto shrink-0 text-xs text-neutral-500 dark:text-neutral-400">on screen, not saved</span>
          </label>
        </div>
      </div>

      <label class="flex items-center gap-2">
        <input v-model="includeNotes" type="checkbox" :disabled="busy" />
        Project notes on the title page
      </label>

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
