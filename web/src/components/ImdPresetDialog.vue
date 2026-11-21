<script setup lang="ts">
/**
 * Which standard two-tone intermodulation test the generator sets up.
 *
 * <p>The Stimulus picker's shape, and for its reason: five names in a dropdown —
 * <em>SMPTE 60 Hz + 7 kHz (4:1)</em>, <em>CCIF/ITU-R 19 kHz + 20 kHz (1:1)</em> — are a list to be
 * parsed, where a picture of where each one puts its energy is scanned. What separates these is
 * not what they look like but <em>where in the band</em> they sit, which is exactly what a
 * footprint shows and a name cannot.</p>
 *
 * <p>Staged and committed on Apply, so Cancel really discards — unlike the dropdown it replaces,
 * where every arrow key reconfigured both tones on the way past.</p>
 */
import { computed, ref, watch } from 'vue'
import ImdFootprint from './ImdFootprint.vue'
import Modal from './Modal.vue'
import PickerFigureWell from './PickerFigureWell.vue'
import PickerSectionHeading from './PickerSectionHeading.vue'
import PickerTileCheck from './PickerTileCheck.vue'
import { imdPreset, imdPresetChips, imdSections, type ImdPresetId } from '../imd/presets'

const props = defineProps<{ open: boolean; preset: ImdPresetId }>()
const emit = defineEmits<{ close: []; pick: [preset: ImdPresetId] }>()

const staged = ref<ImdPresetId>(props.preset)

// Re-seeded on every open, never watched continuously: a dialog that opened holding what was
// staged last time reads as having lost its place.
watch(
  () => props.open,
  (open) => {
    if (open) staged.value = props.preset
  },
)

const sections = imdSections()

const chosen = computed(() => imdPreset(staged.value))
const chosenSection = computed(
  () => sections.find((entry) => entry.items.some((item) => item.value === staged.value))?.section ?? null,
)

/** The two tone inks, repeated from the footprint for the chips' own dots. */
const INK = { f1: '#3b82f6', f2: '#f97316' } as const

function apply() {
  if (staged.value !== props.preset) emit('pick', staged.value)
  emit('close')
}
</script>

<template>
  <Modal :open="props.open" title="IMD preset" icon="bars" tone="blue" size="lg" @close="emit('close')">
    <div class="flex flex-col gap-4 text-sm">
      <!-- What the pictures are, said once. Without it a reader has to work out what the red bars
           mean from five examples of them. -->
      <div class="flex flex-wrap items-center gap-x-4 gap-y-1.5 text-xs text-neutral-500 dark:text-neutral-400">
        <span>Icons show the FFT footprint, 20 Hz – 40 kHz, log axis:</span>
        <span class="inline-flex items-center gap-1.5">
          <span class="h-3 w-[3px] rounded-sm" :style="{ backgroundColor: INK.f1 }"></span>f1
        </span>
        <span class="inline-flex items-center gap-1.5">
          <span class="h-3 w-[3px] rounded-sm" :style="{ backgroundColor: INK.f2 }"></span>f2
        </span>
        <span class="inline-flex items-center gap-1.5">
          <span class="h-2 w-[2px] rounded-sm bg-rose-600 dark:bg-rose-400"></span>IMD products
        </span>
      </div>

      <section v-for="entry in sections" :key="entry.section.id" class="flex flex-col gap-2">
        <PickerSectionHeading :section="entry.section" />
        <div class="grid grid-cols-1 gap-2 sm:grid-cols-2">
          <button
            v-for="option in entry.items"
            :key="option.value"
            type="button"
            class="relative flex items-start gap-3 rounded-[10px] border py-3 pl-2.5 pr-3 text-left transition-colors"
            :class="[
              staged === option.value
                ? 'border-blue-500 bg-blue-500/10 ring-[3px] ring-blue-500/20 dark:border-blue-400'
                : 'border-neutral-200 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800',
              // Custom is alone in its section, so it takes the width rather than leaving a gap
              // beside it that reads as a tile that failed to render.
              entry.items.length === 1 ? 'sm:col-span-2' : '',
            ]"
            :aria-pressed="staged === option.value"
            @click="staged = option.value"
          >
            <!-- Wider than the other pickers' wells: a spectrum needs its x axis. -->
            <PickerFigureWell :tone="entry.section.tone" size="wide">
              <ImdFootprint :preset="option.value" />
            </PickerFigureWell>
            <span class="flex min-w-0 flex-col gap-1.5">
              <span
                class="text-sm font-semibold"
                :class="staged === option.value ? 'text-blue-700 dark:text-blue-300' : ''"
                >{{ option.name }}</span
              >
              <span class="flex flex-wrap gap-1.5">
                <span
                  v-for="chip in imdPresetChips(option)"
                  :key="chip.text"
                  class="inline-flex h-5 items-center gap-1.5 rounded-md border border-neutral-200 px-1.5 font-mono text-[11.5px] text-neutral-800 dark:border-neutral-700 dark:text-neutral-100"
                  :style="{ backgroundColor: 'var(--surface-control)' }"
                >
                  <span
                    v-if="chip.tone"
                    class="size-1.5 rounded-full"
                    :style="{ backgroundColor: INK[chip.tone] }"
                  ></span>
                  {{ chip.text }}
                </span>
              </span>
              <span class="text-[12px] leading-[1.45] text-neutral-600 dark:text-neutral-400">
                {{ option.description }}
              </span>
            </span>
            <PickerTileCheck v-if="staged === option.value" />
          </button>
        </div>
      </section>
    </div>

    <template #footer>
      <span class="flex flex-1 items-center gap-2 text-xs text-neutral-500 dark:text-neutral-400">
        Selected
        <span
          v-if="chosenSection"
          class="inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-semibold"
          :style="{
            color: `var(--kind-${chosenSection.tone}-badge-fg)`,
            backgroundColor: `var(--kind-${chosenSection.tone}-badge-bg)`,
            borderColor: `var(--kind-${chosenSection.tone}-badge-border)`,
          }"
        >
          <ImdFootprint :preset="staged" class="!h-3 !w-5" />
          {{ chosen.label }}
        </span>
      </span>
      <button type="button" class="btn-secondary" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" @click="apply">Apply</button>
    </template>
  </Modal>
</template>
