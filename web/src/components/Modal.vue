<script setup lang="ts">
import { ref, watch } from 'vue'

// This codebase's first modal primitive — native <dialog> rather than a hand-rolled overlay div,
// since showModal() already gives Escape-to-close, a backdrop, and focus containment for free in
// every evergreen browser.

const props = withDefaults(defineProps<{ open: boolean; title: string; wide?: boolean }>(), {
  wide: false,
})
const emit = defineEmits<{ close: [] }>()

const dialogRef = ref<HTMLDialogElement | null>(null)

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
    :class="props.wide ? 'max-w-2xl' : 'max-w-lg'"
    @cancel="onCancel"
  >
    <div class="flex items-center justify-between border-b border-neutral-200 px-4 py-3 dark:border-neutral-800">
      <h2 class="text-sm font-semibold">{{ props.title }}</h2>
      <button
        type="button"
        class="text-neutral-500 hover:text-neutral-900 dark:hover:text-neutral-100"
        aria-label="Close"
        @click="emit('close')"
      >
        ✕
      </button>
    </div>
    <div class="p-4">
      <slot />
    </div>
  </dialog>
</template>
