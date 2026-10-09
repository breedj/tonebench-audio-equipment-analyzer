<script setup lang="ts">
import { ref, watch } from 'vue'
import Modal from './Modal.vue'
import MeasurementKindIcon from './MeasurementKindIcon.vue'
import { useProjects } from '../composables/useProjects'
import { groupedMeasurementKinds, MEASUREMENT_KINDS } from '../measurements/kinds'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const { currentProjectId, createMeasurementType, selectType, error } = useProjects()

const selectedKind = ref<string | null>(null)
const name = ref('')

// Computed once: the grouping is static, and a `computed` here would only re-run on nothing.
const sections = groupedMeasurementKinds()

watch(
  () => props.open,
  (open) => {
    if (!open) return
    selectedKind.value = null
    name.value = ''
  },
)

function pickKind(kind: string) {
  selectedKind.value = kind
  name.value = MEASUREMENT_KINDS.find((k) => k.kind === kind)?.label ?? ''
}

async function onCreate() {
  if (currentProjectId.value === null || selectedKind.value === null || !name.value.trim()) return
  const id = await createMeasurementType(currentProjectId.value, { kind: selectedKind.value, name: name.value.trim(), notes: null })
  await selectType(id)
  emit('close')
}
</script>

<template>
  <!-- Wide, and the kinds in two columns: ten of them in one column ran past the bottom of an
       ordinary window, so the last few were only found by scrolling a list that gave no sign it
       scrolled. Grouped into four sections as well (Joost's design, 2026-10-09) — the badge and the
       count on each heading are what say how much list there is, which a flat grid could not, and a
       section cut off at the bottom edge is itself the sign that it scrolls.

       The second step, naming the measurement, is an ordinary form: `md`, padded, no sheet. -->
  <Modal
    :open="props.open"
    title="New measurement"
    :subtitle="selectedKind === null ? 'Pick what you want to measure' : undefined"
    :size="selectedKind === null ? 'xl' : 'md'"
    :flush="selectedKind === null"
    @close="emit('close')"
  >
    <!-- The sheet is the *ground* tone and every card the *panel* tone, which is what makes a card
         read as raised rather than as a patch — the design draws the same two steps in its own
         greys, and these are the app's.

         It rounds its own bottom corners rather than the dialog clipping them: a `<dialog>` is
         `overflow: auto` in the UA stylesheet, which is what lets the long dialogs (Configuration,
         the library list) scroll at all, and an `overflow-hidden` on the element to clip this one
         sheet would take that away from every one of them. -->
    <div
      v-if="selectedKind === null"
      class="flex max-h-[72vh] flex-col gap-5 overflow-y-auto rounded-b-lg bg-[var(--surface-ground)] px-5 pb-5 pt-[18px] text-sm"
    >
      <p v-if="currentProjectId === null" class="text-xs text-neutral-500 dark:text-neutral-400">
        Select or create a project first.
      </p>

      <section v-for="section in sections" :key="section.group.id" class="flex flex-col gap-2.5">
        <div class="flex items-center gap-2">
          <h3
            class="rounded-full border px-2.5 py-[3px] text-[11px] font-semibold uppercase tracking-[0.06em]"
            :style="{
              color: `var(--kind-${section.group.tone}-badge-fg)`,
              backgroundColor: `var(--kind-${section.group.tone}-badge-bg)`,
              borderColor: `var(--kind-${section.group.tone}-badge-border)`,
            }"
          >
            {{ section.group.label }}
          </h3>
          <span class="text-xs text-neutral-500 dark:text-neutral-400">{{ section.kinds.length }}</span>
          <span class="h-px flex-1 bg-neutral-200 dark:bg-neutral-800" aria-hidden="true"></span>
        </div>

        <div class="grid grid-cols-1 gap-3.5 sm:grid-cols-2">
          <button
            v-for="k in section.kinds"
            :key="k.kind"
            type="button"
            class="kind-card flex items-start gap-3.5 rounded-xl border border-neutral-200 p-4 text-left disabled:opacity-40 dark:border-neutral-800"
            :disabled="currentProjectId === null"
            @click="pickKind(k.kind)"
          >
            <!-- The app's own icon for the kind, not the mockup's: this is the same glyph the
                 sidebar will show beside the measurement once it exists, and the picker is where
                 that association is learnt. Sized up to the design's 22px; the 20px the component
                 declares is a presentation attribute, which the class beats. -->
            <span
              class="flex size-11 shrink-0 items-center justify-center rounded-full"
              :style="{ backgroundColor: `var(--kind-${k.tone}-chip-bg)`, color: `var(--kind-${k.tone}-chip-fg)` }"
            >
              <MeasurementKindIcon :kind="k.kind" class="size-[22px]" />
            </span>
            <span class="flex min-w-0 flex-col gap-1">
              <span class="text-[15px] font-semibold text-neutral-900 dark:text-neutral-100">{{ k.label }}</span>
              <span class="text-[13px] leading-[1.45] text-neutral-600 dark:text-neutral-400">{{ k.description }}</span>
            </span>
          </button>
        </div>
      </section>
    </div>

    <div v-else class="flex flex-col gap-3 text-sm">
      <label class="flex flex-col gap-1">
        Name
        <input
          v-model="name"
          type="text"
          placeholder="e.g. FFT analysis"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
        />
      </label>

      <p v-if="error" class="text-xs text-red-600 dark:text-red-400">{{ error }}</p>

      <div class="flex justify-end gap-2 pt-2">
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="selectedKind = null"
        >
          Back
        </button>
        <button
          type="button"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          :disabled="!name.trim()"
          @click="onCreate"
        >
          Create
        </button>
      </div>
    </div>
  </Modal>
</template>
