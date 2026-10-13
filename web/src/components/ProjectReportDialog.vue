<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api, type MeasurementSummary, type MeasurementType, type Project } from '../api/client'
import { useReportSettings } from '../composables/useReportSettings'
import { buildProjectReport, kindLabel, loadStoredSubject, typeOnlySubject, type ProjectReportItem } from '../report/buildReport'
import { downloadBlob, safeFileName } from '../report/download'
import { subjectHasChartData } from '../report/render'
import type { ReportSubject } from '../report/types'
import Modal from './Modal.vue'
import MeasurementKindChip from './MeasurementKindChip.vue'

/**
 * "Project report…": every measurement in the project, grouped in the sidebar's order, each
 * checked by default — its stored results one chapter each, the measurement on screen when it has
 * been measured, and for a measurement with no stored result at all a chapter of its notes and
 * settings (typeOnlySubject), so the report describes the whole project and not just the part of
 * it that was saved. Which elements each chapter carries is the kind's remembered choice
 * (useReportSettings), or the kind's defaults where no Report dialog has been used yet — the
 * per-measurement Report dialog is where that is set — so this dialog only asks which chapters to
 * include. Queried per type rather than for the project as a whole, because a measurement summary
 * does not say which type it belongs to.
 */
const props = defineProps<{
  open: boolean
  project: Project | null
  types: readonly MeasurementType[]
  /** The measurement on screen, listed under its own type when it has been measured; null otherwise. */
  liveSubject: ReportSubject | null
}>()
const emit = defineEmits<{ close: [] }>()

const { load, elementsFor, viewsFor, chartsMonochrome, setChartsMonochrome } = useReportSettings()

interface Group {
  type: MeasurementType
  items: MeasurementSummary[]
}

const groups = ref<Group[]>([])
const selected = ref<Set<number>>(new Set())
const includeLive = ref(false)
const includeNotes = ref(true)
const monochrome = ref(false)
/** The measurements with nothing measured whose notes-and-settings chapter is ticked, by type id. */
const selectedTypeOnly = ref<Set<number>>(new Set())

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
      selectedTypeOnly.value = new Set(loaded.filter((g) => isTypeOnly(g)).map((g) => g.type.id))
      monochrome.value = chartsMonochrome.value
    } catch (e) {
      error.value = (e as Error).message
    } finally {
      loading.value = false
    }
  },
)

const canGenerate = computed(
  () => (selected.value.size > 0 || includeLive.value || selectedTypeOnly.value.size > 0) && !busy.value && !loading.value,
)

/** A measurement with no stored result and nothing on screen: its chapter can only be its notes and settings. */
function isTypeOnly(group: Group): boolean {
  return group.items.length === 0 && group.type.id !== liveTypeId.value
}

function isSelected(id: number): boolean {
  return selected.value.has(id)
}

function setTypeOnly(typeId: number, on: boolean) {
  const next = new Set(selectedTypeOnly.value)
  if (on) next.add(typeId)
  else next.delete(typeId)
  selectedTypeOnly.value = next
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
  if (isTypeOnly(group)) setTypeOnly(group.type.id, on)
}

function groupState(group: Group): 'all' | 'none' | 'some' {
  if (isTypeOnly(group)) return selectedTypeOnly.value.has(group.type.id) ? 'all' : 'none'
  const live = group.type.id === liveTypeId.value
  const total = group.items.length + (live ? 1 : 0)
  const count = group.items.filter((i) => selected.value.has(i.resultId)).length + (live && includeLive.value ? 1 : 0)
  return count === 0 ? 'none' : count === total ? 'all' : 'some'
}

/**
 * Select all / None over the whole list, from the 2026-10-13 mockup's chapter header. A project with
 * twenty measurements is the case it exists for: everything is ticked when the dialog opens, so the
 * one thing anyone wants is to clear it and tick two.
 */
function setEverything(on: boolean) {
  for (const group of groups.value) setGroup(group, on)
}

/** How many chapters a group contributes, which is what its row's count says. */
function groupChapterCount(group: Group): number {
  if (isTypeOnly(group)) return 1
  return group.items.length + (group.type.id === liveTypeId.value ? 1 : 0)
}

async function generate() {
  const project = props.project
  if (!project || !canGenerate.value) return

  busy.value = true
  error.value = null
  try {
    if (monochrome.value !== chartsMonochrome.value) await setChartsMonochrome(monochrome.value)

    const items: ProjectReportItem[] = []
    for (const group of groups.value) {
      if (isTypeOnly(group)) {
        if (selectedTypeOnly.value.has(group.type.id)) {
          // The row says "Notes and settings", and with nothing measured the settings are most of
          // what there is to say — so they go in whatever the kind's remembered choice.
          const elements = elementsFor(group.type.kind)
          items.push({
            subject: typeOnlySubject(group.type, project.name),
            elements: elements.includes('settings') ? elements : [...elements, 'settings'],
            views: viewsFor(group.type.kind),
          })
        }
        continue
      }
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

    const document = await buildProjectReport(project, items, includeNotes.value, monochrome.value, (done, total, title) => {
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
  <!-- The 2026-10-13 chrome: the project's name as a chip beside the title rather than as the first
       words of the paragraph, the chapter list as a card that takes the height the dialog has left,
       and the two options as a card of their own. -->
  <Modal
    :open="props.open"
    title="Project report"
    icon="document"
    tone="amber"
    size="lg"
    ground
    flush
    @close="emit('close')"
  >
    <template v-if="props.project" #title-after>
      <span
        class="inline-flex items-center gap-1.5 rounded-full border border-neutral-200 bg-neutral-100 px-2.5 py-0.5 text-[12px] text-neutral-700 dark:border-neutral-700 dark:bg-neutral-800 dark:text-neutral-200"
      >
        <svg
          width="12"
          height="12"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
        </svg>
        {{ props.project.name }}
      </span>
    </template>

    <div v-if="props.project" class="flex flex-col gap-3 p-4 text-[13px]">
      <p class="px-0.5 text-[12.5px] leading-[1.5] text-neutral-600 dark:text-neutral-300">
        Every measurement in the sidebar's order: one chapter per stored result, the measurement on
        screen, and the notes and settings of a measurement that has no result yet. What each chapter
        includes is the choice remembered by that measurement's own Report dialog, or the defaults
        where none has been made.
      </p>

      <p v-if="loading" class="px-0.5 text-[12px] text-neutral-500 dark:text-neutral-400">
        Loading the stored results…
      </p>

      <p v-else-if="groups.length === 0" class="px-0.5 text-[12px] text-neutral-500 dark:text-neutral-400">
        This project has no measurements yet.
      </p>

      <section v-else class="dialog-card flex min-h-0 flex-col">
        <div class="flex items-center gap-2 border-b border-neutral-200 px-3.5 py-2 dark:border-neutral-800">
          <span class="flex-1 text-[11px] font-semibold tracking-[0.06em] text-neutral-500 uppercase dark:text-neutral-400">
            Chapters
          </span>
          <button type="button" class="btn-chip-link" :disabled="busy" @click="setEverything(true)">Select all</button>
          <button type="button" class="btn-chip-link" :disabled="busy" @click="setEverything(false)">None</button>
        </div>

        <div class="max-h-[46vh] overflow-y-auto px-3.5">
          <div
            v-for="group in groups"
            :key="group.type.id"
            class="border-b border-neutral-100 pt-2.5 pb-1.5 last:border-b-0 dark:border-neutral-800"
          >
            <label class="flex cursor-pointer items-center gap-2.5">
              <input
                type="checkbox"
                class="tb-check"
                :checked="groupState(group) === 'all'"
                :indeterminate="groupState(group) === 'some'"
                :disabled="busy"
                @change="setGroup(group, ($event.target as HTMLInputElement).checked)"
              />
              <MeasurementKindChip :kind="group.type.kind" :size="24" />
              <span class="truncate text-[13.5px] font-semibold">{{ group.type.name }}</span>
              <span class="shrink-0 text-[12px] text-neutral-500 dark:text-neutral-400">
                {{ kindLabel(group.type.kind) }}
              </span>
              <span class="flex-1"></span>
              <span class="shrink-0 font-mono text-[11.5px] text-neutral-500 dark:text-neutral-400">
                {{ groupChapterCount(group) }}
              </span>
            </label>

            <!-- Indented past the group's own checkbox *and* its kind chip, so a chapter reads as
                 belonging to the measurement above it. -->
            <div class="flex flex-col pt-1 pl-[59px]">
              <label
                v-for="item in group.items"
                :key="item.resultId"
                class="flex cursor-pointer items-center gap-2.5 py-[5px]"
              >
                <input
                  type="checkbox"
                  class="tb-check"
                  :checked="isSelected(item.resultId)"
                  :disabled="busy"
                  @change="setSelected(item.resultId, ($event.target as HTMLInputElement).checked)"
                />
                <span class="min-w-0 flex-1 truncate">{{ item.name }}</span>
                <span class="shrink-0 font-mono text-[11.5px] whitespace-nowrap text-neutral-500 dark:text-neutral-400">
                  {{ new Date(item.timestampUtc).toLocaleString() }}
                </span>
              </label>

              <label v-if="group.type.id === liveTypeId" class="flex cursor-pointer items-center gap-2.5 py-[5px]">
                <input v-model="includeLive" type="checkbox" class="tb-check" :disabled="busy" />
                <span class="min-w-0 flex-1 truncate">Current measurement</span>
                <span class="shrink-0 text-[11.5px] whitespace-nowrap text-neutral-500 dark:text-neutral-400">
                  on screen, not saved
                </span>
              </label>

              <label v-if="isTypeOnly(group)" class="flex cursor-pointer items-center gap-2.5 py-[5px]">
                <input
                  type="checkbox"
                  class="tb-check"
                  :checked="selectedTypeOnly.has(group.type.id)"
                  :disabled="busy"
                  @change="setTypeOnly(group.type.id, ($event.target as HTMLInputElement).checked)"
                />
                <span class="min-w-0 flex-1 truncate">Notes and settings</span>
                <span class="shrink-0 text-[11.5px] whitespace-nowrap text-neutral-500 dark:text-neutral-400">
                  no stored result
                </span>
              </label>
            </div>
          </div>
        </div>
      </section>

      <section class="dialog-card">
        <label class="flex cursor-pointer items-start gap-2.5 px-3.5 py-3">
          <input v-model="monochrome" type="checkbox" class="tb-check mt-0.5" :disabled="busy" />
          <span class="flex flex-col gap-0.5">
            <span class="font-semibold">Charts in black and white</span>
            <span class="text-[12px] text-neutral-500 dark:text-neutral-400">
              For a monochrome printer; one setting for every kind.
            </span>
          </span>
        </label>
        <label
          class="flex cursor-pointer items-start gap-2.5 border-t border-neutral-100 px-3.5 py-3 dark:border-neutral-800"
        >
          <input v-model="includeNotes" type="checkbox" class="tb-check mt-0.5" :disabled="busy" />
          <span class="font-semibold">Project notes on the title page</span>
        </label>
      </section>

      <p v-if="error" class="px-0.5 text-[12px] text-red-600 dark:text-red-400">{{ error }}</p>
    </div>

    <template v-if="props.project" #footer>
      <span v-if="progress" class="flex-1 truncate text-[12px] text-neutral-500 dark:text-neutral-400">
        {{ progress }}
      </span>
      <span v-else class="flex-1"></span>
      <button type="button" class="btn-secondary" :disabled="busy" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" :disabled="!canGenerate" @click="generate">
        <svg
          width="15"
          height="15"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M6 2h9l5 5v15H6zM14 2v6h6M12 11v6M9 14l3 3 3-3" />
        </svg>
        Generate PDF
      </button>
    </template>
  </Modal>
</template>
