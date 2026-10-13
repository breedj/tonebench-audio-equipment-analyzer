<script setup lang="ts">
/**
 * Shared panel chrome: a rounded card with a tinted round icon, its title, a disclosure chevron,
 * and the body underneath. Thirty-odd components render through this, so the panel language of
 * Joost's 2026-10-12 mockups is applied here once rather than in each of them.
 *
 * The icon and its accent come from the *title* by default (panels/panelIcons.ts), which is what
 * gave every panel in the app the new header without thirty-four template edits; a panel that wants
 * something else passes `icon` and `tone`, and a new panel should do that rather than add a row to
 * that table.
 *
 * Two layout rules the old version did not have, both from the mockup. The header carries its own
 * padding and the body carries its own, instead of one padding around the whole card — the header's
 * hover target then runs the full width of the card, and a collapsed panel is exactly its header
 * tall. And the title row is `h-11`, so a column of collapsed panels is a regular stack.
 *
 * `header-right` is still for controls that must stay clickable without toggling the body (date
 * pickers, a count badge, a numeric input) — see MeasurementHistoryPanel / OtherPeaksTable /
 * MeasurementChecklist.
 */
import { computed, ref } from 'vue'
import PanelIcon from './PanelIcon.vue'
import { panelIconFor, type PanelIconName } from '../panels/panelIcons'
import type { MeasurementKindTone } from '../measurements/kinds'

const props = withDefaults(
  defineProps<{
    title: string
    /**
     * A lighter-weight continuation of the title, set in grey beside it: the unit a range panel is
     * expressed in. "Gain range" + "(dB)" rather than one string, so the unit does not compete with
     * the name — the mockup's own treatment.
     */
    suffix?: string
    /** `xs` is the compact panel used inside a chart column: 24 px icon, 13 px title. */
    size?: 'xs' | 'sm'
    /** Kept for the four callers that asked for a roomier body; `p-4` widens the body's padding. */
    padding?: 'p-3' | 'p-4'
    defaultOpen?: boolean
    /** Override the title-derived icon. */
    icon?: PanelIconName
    /** Override the title-derived accent. */
    tone?: MeasurementKindTone
  }>(),
  { size: 'sm', padding: 'p-3', defaultOpen: true },
)

const open = ref(props.defaultOpen)

const derived = computed(() => panelIconFor(props.title))
const icon = computed(() => props.icon ?? derived.value.icon)
const tone = computed(() => props.tone ?? derived.value.tone)

const bodyPadding = computed(() => (props.padding === 'p-4' ? 'px-4 pb-4' : 'px-3.5 pb-3.5'))
</script>

<template>
  <section class="panel-surface overflow-hidden rounded-xl border border-neutral-200 dark:border-neutral-800">
    <div class="flex items-center gap-2.5 pl-3 pr-1.5" :class="size === 'xs' ? 'h-10' : 'h-11'">
      <!-- The title and its icon are one button: the whole left of the header toggles the body,
           which is a far easier target than the chevron and is what the mockup implies by putting
           no affordance on the title itself. -->
      <button type="button" class="flex min-w-0 flex-1 items-center gap-2.5 text-left" @click="open = !open">
        <PanelIcon :icon="icon" :tone="tone" :size="size === 'xs' ? 24 : 28" />
        <h2
          class="min-w-0 truncate font-semibold text-neutral-900 dark:text-neutral-100"
          :class="size === 'xs' ? 'text-[13px]' : 'text-sm'"
        >
          {{ title }}
          <span v-if="suffix" class="font-normal text-neutral-500 dark:text-neutral-400">{{ suffix }}</span>
        </h2>
      </button>
      <div class="flex shrink-0 items-center gap-2">
        <slot name="header-right" />
        <button
          type="button"
          class="flex h-8 w-8 items-center justify-center rounded-lg text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
          :aria-expanded="open"
          :title="open ? 'Collapse' : 'Expand'"
          @click="open = !open"
        >
          <svg
            class="h-4 w-4 transition-transform duration-150"
            :class="{ '-rotate-90': !open }"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <path d="M6 9l6 6 6-6" />
          </svg>
        </button>
      </div>
    </div>
    <div v-show="open" class="flex flex-col gap-3 pt-0.5" :class="bodyPadding">
      <slot />
    </div>
    <!-- The footer strip of the mockup's settings panels: what the form adds up to — the analysis
         the sweep will run at, and how long it will take — on the page's own ground, bled out to
         the card's edges under a rule. It is deliberately outside the padded body, because a
         summary indented like a field reads as one more setting. -->
    <div
      v-if="$slots.footer"
      v-show="open"
      class="flex flex-col gap-1.5 border-t border-neutral-200 px-3.5 py-2.5 text-[12px] text-neutral-500 dark:border-neutral-800 dark:text-neutral-400"
      :style="{ backgroundColor: 'var(--surface-ground)' }"
    >
      <slot name="footer" />
    </div>
  </section>
</template>
