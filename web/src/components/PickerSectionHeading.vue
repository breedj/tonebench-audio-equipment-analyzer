<script setup lang="ts">
/**
 * The heading over one section of a picker: a tinted pill carrying a square dot and the section's
 * name, the line that says what the section has in common, and a hairline out to the right edge.
 *
 * <p>One component for the three oscilloscope pickers, because the heading is the whole of what
 * they share — the tiles under it differ (a mono operator, a title and a sentence, a short label)
 * and should. Three copies of a heading is how two of them come to sit a pixel apart.</p>
 *
 * <p>The square dot inside the pill is the section's <em>accent</em> rather than its badge
 * foreground: it is the colour the figures below it are drawn in, so the heading says which tiles
 * belong to it even where the badge text itself is read past.</p>
 */
import { sectionBadgeStyle, type PickerSection } from '../panels/pickerSections'

const props = defineProps<{ section: PickerSection }>()
</script>

<template>
  <div class="flex items-center gap-2">
    <h3
      class="inline-flex items-center gap-1.5 rounded-full border py-[3px] pl-2 pr-2.5 text-[11px] font-semibold uppercase tracking-[0.06em]"
      :style="sectionBadgeStyle(props.section.tone)"
    >
      <span
        class="size-[7px] rounded-[2px]"
        :style="{ backgroundColor: `var(--kind-${props.section.tone}-chip-fg)` }"
        aria-hidden="true"
      ></span>
      {{ props.section.label }}
    </h3>
    <span class="text-xs text-neutral-500 dark:text-neutral-400">{{ props.section.note }}</span>
    <span class="h-px flex-1 bg-neutral-200 dark:bg-neutral-800" aria-hidden="true"></span>
  </div>
</template>
