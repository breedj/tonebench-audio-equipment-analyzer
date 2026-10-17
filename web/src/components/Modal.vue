<script setup lang="ts">
import PanelIcon from './PanelIcon.vue'
import type { PanelIconName } from '../panels/panelIcons'
import type { MeasurementKindTone } from '../measurements/kinds'
import { computed, ref, watch } from 'vue'

// This codebase's first modal primitive — native <dialog> rather than a hand-rolled overlay div,
// since showModal() already gives Escape-to-close, a backdrop, and focus containment for free in
// every evergreen browser.

const props = withDefaults(
  defineProps<{
    open: boolean
    title: string
    /**
     * A line under the title. Only the picker carries one today; a dialog without it keeps exactly
     * the header it had.
     */
    subtitle?: string
    /**
     * How wide. `md` is the default (a form), `lg` what `wide` used to mean (a list, a settings
     * page), `xl` the measurement picker's two columns of cards at the width its design is drawn
     * at. `xl` also takes the larger header the same design specifies — a 18px title over its
     * subtitle, in a deeper bar — rather than the 13px one that suits a form; that is the one place
     * size drives type here, and it is why the picker is a size of its own rather than a wider `lg`.
     */
    size?: 'md' | 'lg' | 'xl' | 'full'
    /**
     * An optional tinted round glyph before the title — the panel header's own chrome, for the
     * dialogs that are a stack of panels rather than one form (the sound card's, above all).
     */
    icon?: PanelIconName
    tone?: MeasurementKindTone
    /**
     * Paints the body in the page's ground rather than the panel, so the sections inside it read as
     * cards on a surface. Goes with `flush`, which is what lets the body carry its own padding.
     */
    ground?: boolean
    /**
     * Drop the body's own padding and let the slot run to the dialog's edges, so it can paint its
     * own background. The picker does: its cards are the panel tone and the sheet behind them is the
     * ground tone, which is what makes a card read as a card.
     */
    flush?: boolean
  }>(),
  { subtitle: undefined, size: 'md', flush: false, icon: undefined, tone: 'blue', ground: false },
)
const emit = defineEmits<{ close: [] }>()

const dialogRef = ref<HTMLDialogElement | null>(null)

/**
 * The 2026-10-13 header — 17 px title, 40 px close — is used by every dialog that carries a glyph,
 * and the glyph is what says the dialog was drawn to those mockups. The `xl` picker predates them
 * and had the same header already, so it opts in by size.
 */
const titled = computed(() => props.icon !== undefined || props.size === 'xl')

const WIDTHS = { md: 'max-w-lg', lg: 'max-w-2xl', xl: 'max-w-[800px]', full: 'max-w-[1040px]' } as const

watch(
  () => props.open,
  (open) => {
    const dialog = dialogRef.value
    if (!dialog) return
    if (open && !dialog.open) dialog.showModal()
    else if (!open && dialog.open) dialog.close()
  },
  { immediate: true },
)

function onCancel(e: Event) {
  // Fires on Escape (native <dialog> behaviour), before the element actually closes — prevented
  // so dismissal always routes through the one @close handler, same as the X button, rather than
  // the parent's `open` state and the dialog's own state disagreeing.
  e.preventDefault()
  emit('close')
}

// There is deliberately no backdrop-click handler (Joost, 2026-09-30): a dialog here is closed by
// its own X, its Cancel button or Escape, and nothing else. Half of them already opted out through a
// `closeOnBackdrop` prop — a report with its elements ticked, a configuration page, a save dialog
// with a name typed into it — and the ones that had not were the same kind of thing, so the prop is
// gone rather than defaulted. A stray click beside a dialog is far more often a miss than a
// dismissal, and there is nothing to undo the discard it used to cause.
</script>

<template>
  <dialog
    ref="dialogRef"
    class="m-auto w-full overflow-hidden rounded-2xl border border-neutral-200 bg-[var(--surface-panel)] p-0 text-neutral-900 backdrop:bg-black/40 open:flex open:flex-col dark:border-neutral-800 dark:text-neutral-100"
    :class="WIDTHS[props.size]"
    @cancel="onCancel"
  >
    <div
      class="flex shrink-0 items-center justify-between border-b border-neutral-200 bg-[var(--surface-panel)] dark:border-neutral-800"
      :class="titled ? 'py-2.5 pl-5 pr-3' : 'px-4 py-3'"
    >
      <PanelIcon v-if="props.icon" :icon="props.icon" :tone="props.tone" :size="32" class="mr-3" />
      <div class="flex min-w-0 flex-1 flex-col gap-0.5">
        <!-- `title-after` is for what sits *beside* the name — the project a report is of, the file
             a dialog is about. `subtitle` is what sits under it. -->
        <div class="flex min-w-0 items-center gap-2.5">
          <h2 :class="titled ? 'text-[17px] font-semibold' : 'text-sm font-semibold'">{{ props.title }}</h2>
          <slot name="title-after" />
        </div>
        <slot name="subtitle">
          <span v-if="props.subtitle" class="text-[13px] text-neutral-500 dark:text-neutral-400">{{ props.subtitle }}</span>
        </slot>
      </div>
      <slot name="header-right" />
      <button
        type="button"
        class="shrink-0 rounded-lg text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
        :class="titled ? 'flex size-10 items-center justify-center' : ''"
        aria-label="Close"
        @click="emit('close')"
      >
        <svg
          v-if="titled"
          width="18"
          height="18"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="1.8"
          stroke-linecap="round"
          aria-hidden="true"
        >
          <path d="M6 6l12 12M18 6L6 18" />
        </svg>
        <template v-else>✕</template>
      </button>
    </div>
    <!-- Between the header and the body: the Configuration dialog's tab strip, which belongs to
         neither. -->
    <div class="shrink-0 empty:hidden"><slot name="under-header" /></div>
    <!--
      The one scrolling part. The dialog is a flex column (only while open, or an author
      `display: flex` would beat the user agent's `display: none` and show a closed dialog), so a
      body taller than the viewport scrolls *inside* the chrome instead of taking the header and the
      footer with it. `min-h-0` is what lets it shrink below its content: a flex item's automatic
      minimum size is its content height, so without it the body refuses to shrink, the dialog grows
      past its own max-height and the footer lands below the fold -- which is exactly what the
      Export dialog did at 768 px, with its Export button off-screen at y=745 and no way to reach
      it (2026-10-17). A body with its own `max-h` scroller still works; it simply stops being the
      one that moves.
    -->
    <div
      class="min-h-0 flex-1 overflow-y-auto"
      :class="props.flush ? '' : 'p-4'"
      :style="props.ground ? { backgroundColor: 'var(--surface-ground)' } : undefined"
    >
      <slot />
    </div>
    <!-- The actions, on their own strip under a rule: in a dialog this long they would otherwise be
         wherever the scroll happened to leave them. -->
    <div
      v-if="$slots.footer"
      class="flex shrink-0 items-center gap-2 border-t border-neutral-200 px-4 py-3 dark:border-neutral-800"
      :style="{ backgroundColor: 'var(--surface-panel)' }"
    >
      <slot name="footer" />
    </div>
  </dialog>
</template>
