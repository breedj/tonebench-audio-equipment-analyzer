<script setup lang="ts">
/**
 * Which stimulus the generator plays, picked from a figure of each.
 *
 * <p>The same shape as <code>ScopeMeasurementPickerDialog</code>, and for the same reason: ten
 * names in a dropdown are a list a reader parses, where ten pictures are scanned — and a
 * <code>&lt;select&gt;</code> cannot carry a picture or the line of prose that says what each one
 * is <em>for</em>, which is the part that actually decides the choice.</p>
 *
 * <p><b>Four sections</b> (Joost's design, 2026-11-20), by what a stimulus is for rather than by
 * what it looks like — see <code>STIMULUS_SECTIONS</code>. Ten tiles in one grid is a list again,
 * which is the thing the figures exist to prevent; two to four under a heading is not.</p>
 *
 * <p><b>It stages and commits on Apply</b>, so Cancel really discards — unlike the dropdown it
 * replaces, where every arrow key reconfigured the generator on the way past.</p>
 */
import { computed, ref, watch } from 'vue'
import Modal from './Modal.vue'
import PickerFigureWell from './PickerFigureWell.vue'
import PickerSectionHeading from './PickerSectionHeading.vue'
import PickerTileCheck from './PickerTileCheck.vue'
import ScopeToggleIcon from './ScopeToggleIcon.vue'
import { sectionMarkColor } from '../panels/pickerSections'
import { stimulusKind, stimulusSections } from '../generator/stimulusKinds'
import type { GeneratorKind } from '../api/client'

const props = defineProps<{ open: boolean; kind: GeneratorKind }>()
const emit = defineEmits<{ close: []; pick: [kind: GeneratorKind] }>()

const staged = ref<GeneratorKind>(props.kind)

// Re-seeded on every open, never watched continuously: a dialog that opened holding what was
// staged last time reads as having lost its place.
watch(
  () => props.open,
  (open) => {
    if (open) staged.value = props.kind
  },
)

const sections = stimulusSections()

/** The chosen kind's own entry, for the footer — or null for one this picker does not offer. */
const chosen = computed(() => stimulusKind(staged.value))

/** The section the chosen kind sits in, so the footer's pill is in that section's own colours. */
const chosenSection = computed(
  () => sections.find((entry) => entry.items.some((item) => item.value === staged.value))?.section ?? null,
)

function apply() {
  if (staged.value !== props.kind) emit('pick', staged.value)
  emit('close')
}
</script>

<template>
  <Modal :open="props.open" title="Stimulus" icon="sine" tone="orange" size="lg" @close="emit('close')">
    <div class="flex flex-col gap-4 text-sm">
      <section v-for="entry in sections" :key="entry.section.id" class="flex flex-col gap-2">
        <PickerSectionHeading :section="entry.section" />
        <div class="grid grid-cols-1 gap-2 sm:grid-cols-2">
          <button
            v-for="option in entry.items"
            :key="option.value"
            type="button"
            class="relative flex items-start gap-3 rounded-[10px] border py-3 pl-2.5 pr-3 text-left transition-colors"
            :class="
              staged === option.value
                ? 'border-blue-500 bg-blue-500/10 ring-[3px] ring-blue-500/20 dark:border-blue-400'
                : 'border-neutral-200 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800'
            "
            :aria-pressed="staged === option.value"
            @click="staged = option.value"
          >
            <PickerFigureWell :tone="entry.section.tone">
              <ScopeToggleIcon
                :name="option.icon"
                :size="26"
                :mark="sectionMarkColor(entry.section.mark)"
              />
            </PickerFigureWell>
            <span class="min-w-0 flex flex-col gap-1">
              <span
                class="text-sm font-semibold"
                :class="staged === option.value ? 'text-blue-700 dark:text-blue-300' : ''"
                >{{ option.label }}</span
              >
              <span class="text-[12px] leading-[1.45] text-neutral-600 dark:text-neutral-400">
                {{ option.title }}
              </span>
            </span>
            <PickerTileCheck v-if="staged === option.value" />
          </button>
        </div>
      </section>

      <!-- The one caveat that belongs to the dialog rather than to any tile in it. -->
      <div
        class="flex items-start gap-2.5 rounded-[10px] border border-neutral-200 p-3 text-[12px] leading-relaxed text-neutral-600 dark:border-neutral-800 dark:text-neutral-400"
        :style="{ backgroundColor: 'var(--surface-control)' }"
      >
        <svg
          width="16"
          height="16"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          class="mt-px shrink-0 text-blue-600 dark:text-blue-400"
          aria-hidden="true"
        >
          <circle cx="12" cy="12" r="9" />
          <path d="M12 11v5M12 8h.01" />
        </svg>
        <span>
          Whether a channel plays at all is the
          <strong class="font-semibold text-neutral-900 dark:text-neutral-100">Play on</strong> pair
          above, not a stimulus of its own — so picking one here never starts the generator by
          itself.
        </span>
      </div>
    </div>

    <template #footer>
      <span class="flex flex-1 items-center gap-2 text-xs text-neutral-500 dark:text-neutral-400">
        Selected
        <span
          v-if="chosen && chosenSection"
          class="inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-semibold"
          :style="{
            color: `var(--kind-${chosenSection.tone}-badge-fg)`,
            backgroundColor: `var(--kind-${chosenSection.tone}-badge-bg)`,
            borderColor: `var(--kind-${chosenSection.tone}-badge-border)`,
          }"
        >
          <ScopeToggleIcon :name="chosen.icon" :size="15" />
          {{ chosen.label }}
        </span>
      </span>
      <button type="button" class="btn-secondary" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" @click="apply">Apply</button>
    </template>
  </Modal>
</template>
