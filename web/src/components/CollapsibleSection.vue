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
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import PanelIcon from './PanelIcon.vue'
import { panelIconFor, type PanelIconName } from '../panels/panelIcons'
import { panelSlug } from '../panels/panelOrder'
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
    /**
     * What the panel is, in a sentence, shown on hovering its icon.
     *
     * <p><b>Where a panel’s explanatory caption goes.</b> A rail whose sections each carry a
     * paragraph explaining how their controls work spends a great deal of height on text a reader
     * needs once — so the explanation moves here and the icon becomes the place to hover for it
     * (asked for 2026-11-19). What does <em>not</em> move is anything the panel is saying about the
     * present: a warning, a count, a state line. The rule is <b>state stays on screen, explanation
     * moves to the hint</b>, and it decides each case on its own.</p>
     *
     * <p>On a wrapper around the icon rather than on the header button, because that is what was
     * asked for and because <code>PanelIcon</code> is <code>aria-hidden</code> with no
     * <code>title</code> of its own — a title written at its call site would land on a hidden
     * element. The same sentence is repeated into a visually hidden span beside the title, so the
     * explanation is still in the accessibility tree rather than only in a tooltip.</p>
     */
    hint?: string
    /**
     * What this panel is called in the stored rail order, for the panels whose heading is
     * <em>computed</em>.
     *
     * <p>The default is a slug of the title, which is stable for the thirty-odd panels whose
     * heading is a constant. It is not stable for the handful that spell a setting into their
     * name — "dB range" against "Distortion range", "Time window (ms)" against "(µs)" — and
     * those must declare one here, or their place in the rail is lost the moment that setting
     * is touched. See panels/panelOrder.ts.</p>
     */
    panelId?: string
  }>(),
  { size: 'sm', padding: 'p-3', defaultOpen: true },
)

const open = ref(props.defaultOpen)

const derived = computed(() => panelIconFor(props.title))
const icon = computed(() => props.icon ?? derived.value.icon)
const tone = computed(() => props.tone ?? derived.value.tone)

const bodyPadding = computed(() => (props.padding === 'p-4' ? 'px-4 pb-4' : 'px-3.5 pb-3.5'))

const id = computed(() => props.panelId ?? panelSlug(props.title))

const root = ref<HTMLElement | null>(null)

/**
 * Whether this panel can be dragged into a different place, which only the panels of a
 * measurement screen's two rails can.
 *
 * <p>Decided from the DOM at mount rather than from a prop or an injection. A prop would have to
 * be threaded through every one of the thirty-odd call sites; an injection would not arrive at
 * all, because slot content belongs to the component that <em>wrote</em> it — App.vue — rather
 * than to the rail it is rendered into, so <code>provide</code> from the rail never reaches it.
 * The rail marks itself, and a panel asks whether it is in one. The second test is what keeps a
 * section nested inside another panel's body out of it.</p>
 */
const inRail = ref(false)

/**
 * True only while the grip is held, which is what makes the <em>grip</em> the handle rather
 * than the whole panel — the measurement sidebar's own rule, and for its reason: a draggable
 * card cannot have its text selected, and a press anywhere in a settings panel would start a
 * drag instead of working the control under it.
 */
const armed = ref(false)

function disarm() {
  armed.value = false
}

onMounted(() => {
  const el = root.value
  inRail.value =
    el?.closest('[data-panel-rail]') != null && el?.parentElement?.closest('[data-panel-id]') == null
  // On the window, because a press released outside the panel would otherwise leave it armed
  // for the next unrelated drag.
  window.addEventListener('mouseup', disarm)
})

onBeforeUnmount(() => window.removeEventListener('mouseup', disarm))
</script>

<template>
  <section
    ref="root"
    class="panel-surface group/panel relative overflow-hidden rounded-xl border border-neutral-200 dark:border-neutral-800"
    :data-panel-id="id"
    :draggable="armed ? 'true' : undefined"
  >
    <!-- The grip, and only in a rail. Two decisions. It appears on hovering the panel rather
         than standing there always, because thirty-odd panels each carrying a visible handle is
         a column of furniture — the measurement sidebar's own grip already works this way. And
         it is laid *over* the header's left padding rather than put in the row: a 20 px handle
         in the flow pushes the icon and the title of every rail panel across, which is a change
         to the look of every screen bought for a control that is not even visible most of the
         time. Absolutely positioned, nothing moves. -->
    <button
      v-if="inRail"
      type="button"
      class="absolute left-0 top-0 z-10 flex w-3 cursor-grab items-center justify-center text-neutral-300 opacity-0 transition-opacity hover:text-neutral-500 focus-visible:opacity-100 group-hover/panel:opacity-100 dark:text-neutral-600 dark:hover:text-neutral-300"
      :class="size === 'xs' ? 'h-10' : 'h-11'"
      title="Drag to move this panel up or down the rail. The order is kept for every measurement of this kind."
      aria-label="Reorder this panel"
      @mousedown="armed = true"
    >
      <svg width="8" height="14" viewBox="0 0 8 14" fill="currentColor" aria-hidden="true">
        <circle cx="1.6" cy="2" r="1.1" /><circle cx="6.4" cy="2" r="1.1" />
        <circle cx="1.6" cy="7" r="1.1" /><circle cx="6.4" cy="7" r="1.1" />
        <circle cx="1.6" cy="12" r="1.1" /><circle cx="6.4" cy="12" r="1.1" />
      </svg>
    </button>
    <div class="flex items-center gap-2.5 pl-3 pr-1.5" :class="size === 'xs' ? 'h-10' : 'h-11'">
      <!-- The title and its icon are one button: the whole left of the header toggles the body,
           which is a far easier target than the chevron and is what the mockup implies by putting
           no affordance on the title itself. -->
      <button type="button" class="flex min-w-0 flex-1 items-center gap-2.5 text-left" @click="open = !open">
        <!-- The hint hangs on a wrapper, not on PanelIcon: that component is aria-hidden and takes
             no title, so one written at its call site would land on a hidden element. The sr-only
             copy keeps the sentence in the accessibility tree, where a tooltip never is. -->
        <span v-if="hint" :title="hint" class="flex shrink-0 cursor-help">
          <PanelIcon :icon="icon" :tone="tone" :size="size === 'xs' ? 24 : 28" />
        </span>
        <PanelIcon v-else :icon="icon" :tone="tone" :size="size === 'xs' ? 24 : 28" />
        <h2
          class="min-w-0 truncate font-semibold text-neutral-900 dark:text-neutral-100"
          :class="size === 'xs' ? 'text-[13px]' : 'text-sm'"
        >
          {{ title }}
          <span v-if="suffix" class="font-normal text-neutral-500 dark:text-neutral-400">{{ suffix }}</span>
        </h2>
      </button>
      <!-- Outside the button on purpose: inside it, the sentence joins the toggle's accessible
           name, so a screen-reader user tabbing to a collapse control hears a whole paragraph. Out
           here it is read as the header's own text, where the explanation belongs. -->
      <span v-if="hint" class="sr-only">{{ hint }}</span>
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
