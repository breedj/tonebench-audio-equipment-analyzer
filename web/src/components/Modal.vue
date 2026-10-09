<script setup lang="ts">
import { ref, watch } from 'vue'

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
    size?: 'md' | 'lg' | 'xl'
    /**
     * Drop the body's own padding and let the slot run to the dialog's edges, so it can paint its
     * own background. The picker does: its cards are the panel tone and the sheet behind them is the
     * ground tone, which is what makes a card read as a card.
     */
    flush?: boolean
  }>(),
  { subtitle: undefined, size: 'md', flush: false },
)
const emit = defineEmits<{ close: [] }>()

const dialogRef = ref<HTMLDialogElement | null>(null)

const WIDTHS = { md: 'max-w-lg', lg: 'max-w-2xl', xl: 'max-w-[800px]' } as const

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
    class="m-auto w-full rounded-lg border border-neutral-200 bg-[var(--surface-panel)] p-0 text-neutral-900 backdrop:bg-black/40 dark:border-neutral-800 dark:text-neutral-100"
    :class="WIDTHS[props.size]"
    @cancel="onCancel"
  >
    <div
      class="flex items-center justify-between border-b border-neutral-200 bg-[var(--surface-panel)] dark:border-neutral-800"
      :class="props.size === 'xl' ? 'px-6 py-[18px]' : 'px-4 py-3'"
    >
      <div class="flex min-w-0 flex-col gap-0.5">
        <h2 :class="props.size === 'xl' ? 'text-[18px] font-semibold' : 'text-sm font-semibold'">{{ props.title }}</h2>
        <span v-if="props.subtitle" class="text-[13px] text-neutral-500 dark:text-neutral-400">{{ props.subtitle }}</span>
      </div>
      <button
        type="button"
        class="shrink-0 rounded-lg text-neutral-500 hover:text-neutral-900 dark:hover:text-neutral-100"
        :class="props.size === 'xl' ? 'flex size-11 items-center justify-center' : ''"
        aria-label="Close"
        @click="emit('close')"
      >
        <svg
          v-if="props.size === 'xl'"
          width="20"
          height="20"
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
    <div :class="props.flush ? '' : 'p-4'">
      <slot />
    </div>
  </dialog>
</template>
