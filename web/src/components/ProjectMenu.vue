<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { firstEnabledIndex, lastEnabledIndex, nextEnabledIndex } from '../menu/menuNavigation'

// The header's Project menu: the app's first dropdown. A button that toggles a popover list,
// closing on a click anywhere else, on Escape, or when focus leaves it for somewhere outside —
// the same relatedTarget test MeasurementSidebar uses for its floating rail. Arrow keys move
// through the enabled items with wrap-around (menuNavigation.ts), Enter/Space activates.

export type ProjectMenuAction = 'new' | 'open' | 'save' | 'saveAs' | 'properties' | 'report' | 'delete' | 'close'

const props = defineProps<{
  /** The open project's name, or null on the welcome screen. */
  projectName: string | null
  /** Whether Save has anything to save. */
  dirty: boolean
  /** While a session request is in flight every item is disabled — two switches racing is the one thing the server cannot order. */
  busy: boolean
}>()
const emit = defineEmits<{ action: [action: ProjectMenuAction] }>()

interface Item {
  action: ProjectMenuAction
  label: string
  title: string
  disabled: boolean
  /** A separator is drawn above this item. */
  divider?: boolean
}

const items = computed<Item[]>(() => {
  const open = props.projectName !== null
  return [
    { action: 'new', label: 'New…', title: 'Create a new project file.', disabled: props.busy },
    { action: 'open', label: 'Open…', title: 'Open a project file.', disabled: props.busy },
    { action: 'save', label: 'Save', title: 'Save every changed measurement in this project (Ctrl+S).', disabled: props.busy || !open || !props.dirty },
    { action: 'saveAs', label: 'Save As…', title: 'Save this project as a new file and continue in the copy.', disabled: props.busy || !open },
    { action: 'properties', label: 'Properties…', title: 'Rename the project or edit its description.', disabled: props.busy || !open },
    { action: 'report', label: 'Report…', title: 'A PDF of the stored results in this project, one chapter each, grouped by measurement.', disabled: props.busy || !open },
    { action: 'delete', label: 'Delete', title: 'Close this project and delete its file.', disabled: props.busy || !open, divider: true },
    { action: 'close', label: 'Close', title: 'Close this project and return to the welcome screen.', disabled: props.busy || !open },
  ]
})

const isOpen = ref(false)
const activeIndex = ref<number | null>(null)
const rootRef = ref<HTMLElement | null>(null)
const buttonRef = ref<HTMLButtonElement | null>(null)
const itemRefs = ref<HTMLButtonElement[]>([])

function toggle() {
  if (isOpen.value) closeMenu()
  else openMenu(null)
}

async function openMenu(focusIndex: number | null) {
  isOpen.value = true
  activeIndex.value = focusIndex
  await nextTick()
  if (focusIndex !== null) itemRefs.value[focusIndex]?.focus()
}

function closeMenu(returnFocus = false) {
  if (!isOpen.value) return
  isOpen.value = false
  activeIndex.value = null
  if (returnFocus) buttonRef.value?.focus()
}

function activate(item: Item) {
  if (item.disabled) return
  closeMenu(true)
  emit('action', item.action)
}

function focusIndex(index: number | null) {
  if (index === null) return
  activeIndex.value = index
  itemRefs.value[index]?.focus()
}

function onButtonKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    void openMenu(event.key === 'ArrowDown' ? firstEnabledIndex(items.value) : lastEnabledIndex(items.value))
  }
}

function onMenuKeydown(event: KeyboardEvent) {
  switch (event.key) {
    case 'ArrowDown':
      event.preventDefault()
      focusIndex(nextEnabledIndex(items.value, activeIndex.value, 1))
      break
    case 'ArrowUp':
      event.preventDefault()
      focusIndex(nextEnabledIndex(items.value, activeIndex.value, -1))
      break
    case 'Home':
      event.preventDefault()
      focusIndex(firstEnabledIndex(items.value))
      break
    case 'End':
      event.preventDefault()
      focusIndex(lastEnabledIndex(items.value))
      break
    case 'Escape':
      event.preventDefault()
      closeMenu(true)
      break
    case 'Tab':
      closeMenu()
      break
  }
}

function onFocusOut(event: FocusEvent) {
  const next = event.relatedTarget as Node | null
  if (!next || !rootRef.value?.contains(next)) closeMenu()
}

function onDocumentPointerDown(event: PointerEvent) {
  if (!rootRef.value?.contains(event.target as Node)) closeMenu()
}

onMounted(() => document.addEventListener('pointerdown', onDocumentPointerDown))
onBeforeUnmount(() => document.removeEventListener('pointerdown', onDocumentPointerDown))
</script>

<template>
  <div ref="rootRef" class="relative" @focusout="onFocusOut">
    <button
      ref="buttonRef"
      type="button"
      class="flex items-center gap-1.5 rounded border border-neutral-300 px-2 py-1 text-xs text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-800"
      :class="isOpen ? 'bg-neutral-100 dark:bg-neutral-800' : ''"
      aria-haspopup="menu"
      :aria-expanded="isOpen"
      @click="toggle"
      @keydown="onButtonKeydown"
    >
      <span class="font-medium">Project</span>
      <span v-if="projectName" class="max-w-xs truncate text-neutral-500 dark:text-neutral-400" :title="projectName">
        — {{ projectName }}
      </span>
      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
        <path d="m6 9 6 6 6-6" />
      </svg>
    </button>

    <ul
      v-if="isOpen"
      role="menu"
      class="absolute left-0 top-full z-30 mt-1 min-w-[12rem] rounded border border-neutral-200 bg-[var(--surface-panel)] py-1 text-sm shadow-lg dark:border-neutral-700 dark:bg-neutral-900"
      @keydown="onMenuKeydown"
    >
      <template v-for="(item, index) in items" :key="item.action">
        <li v-if="item.divider" role="separator" class="my-1 border-t border-neutral-200 dark:border-neutral-700"></li>
        <li role="none">
          <button
            :ref="(el) => { if (el) itemRefs[index] = el as HTMLButtonElement }"
            type="button"
            role="menuitem"
            class="flex w-full items-center px-3 py-1.5 text-left hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:hover:bg-neutral-800"
            :class="activeIndex === index ? 'bg-neutral-100 dark:bg-neutral-800' : ''"
            :disabled="item.disabled"
            :title="item.title"
            :tabindex="activeIndex === index ? 0 : -1"
            @click="activate(item)"
            @mouseenter="activeIndex = index"
          >
            {{ item.label }}
          </button>
        </li>
      </template>
    </ul>
  </div>
</template>
