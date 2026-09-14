<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useProjects } from '../composables/useProjects'
import { SIDEBAR_PIN_MIN_WIDTH_PX, useMediaQuery } from '../composables/useMediaQuery'
import { useUiLayout } from '../composables/useUiLayout'
import { dropIndex, moveItem } from '../measurements/reorder'
import MeasurementKindIcon from './MeasurementKindIcon.vue'
import NewMeasurementDialog from './NewMeasurementDialog.vue'

const { types, currentTypeId, currentProjectId, dirtyTypeIds, selectType, deleteMeasurementType, reorderTypes, error } =
  useProjects()

const dialogOpen = ref(false)

// Pinned, the sidebar is a fixed-width column as it always was. Unpinned, it collapses to a rail of
// kind icons — the extra width a chart on a small screen needs — and the full menu floats over the
// content while the pointer (or keyboard focus) is on it. Full width is the same 15.4rem either
// way, so pinning and unpinning never reflows the list itself.
//
// The pin is honoured only from SIDEBAR_PIN_MIN_WIDTH_PX up: narrower, a pinned sidebar behaves as
// the rail while the stored preference stays pinned, so the column comes back with the width.
// `pinned` is that effective state and is what the template reads; `sidebarPinned` is the
// preference, read only by the pin button.
const { sidebarPinned, load: loadLayout, setPinned } = useUiLayout()
void loadLayout()
const wideEnoughToPin = useMediaQuery(`(min-width: ${SIDEBAR_PIN_MIN_WIDTH_PX}px)`)
const pinned = computed(() => sidebarPinned.value && wideEnoughToPin.value)
const hovered = ref(false)
const expanded = computed(() => pinned.value || hovered.value)

// Keyboard users reach the rail by Tab, so focus entering it expands the menu too; focus leaving
// for somewhere outside the sidebar collapses it again, while focus moving between its own rows
// keeps it open.
function onFocusOut(event: FocusEvent) {
  const next = event.relatedTarget as Node | null
  if (!next || !(event.currentTarget as HTMLElement).contains(next)) hovered.value = false
}

// Rows are draggable only while their own grip is held: with `draggable` left on permanently,
// selecting the name text or dragging from the ✕ starts a drag instead, and the whole row stops
// behaving like the button it is.
const gripHeldIndex = ref<number | null>(null)
const dragFromIndex = ref<number | null>(null)
// Which row the pointer is over and which half of it — the insertion line is drawn from these, and
// dropIndex turns them into the destination index. Kept as the raw "row + half" rather than the
// computed index so the line can be drawn on the row actually under the pointer.
const overIndex = ref<number | null>(null)
const overAfter = ref(false)

// A grip pressed but released without a drag (or released outside the sidebar) would otherwise
// leave its row armed, and the next plain drag from anywhere on that row — selecting its name —
// would start a reorder nobody asked for.
function releaseGrip() {
  gripHeldIndex.value = null
}
onMounted(() => window.addEventListener('mouseup', releaseGrip))
onBeforeUnmount(() => window.removeEventListener('mouseup', releaseGrip))

function onDragStart(index: number, event: DragEvent) {
  dragFromIndex.value = index
  overIndex.value = index
  overAfter.value = false
  if (!event.dataTransfer) return
  event.dataTransfer.effectAllowed = 'move'
  // Firefox starts no drag at all unless some data is set.
  event.dataTransfer.setData('text/plain', String(types.value[index]?.id ?? ''))
}

function onDragOver(index: number, event: DragEvent) {
  if (dragFromIndex.value === null) return
  event.preventDefault() // Without this the row is not a drop target and no drop event ever fires.
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
  overIndex.value = index
  overAfter.value = event.clientY - rect.top > rect.height / 2
}

async function onDrop() {
  const from = dragFromIndex.value
  const over = overIndex.value
  const after = overAfter.value
  const projectId = currentProjectId.value
  endDrag()
  if (from === null || over === null || projectId === null) return

  const to = dropIndex(from, over, after)
  if (to === from) return
  try {
    await reorderTypes(projectId, moveItem(types.value.map((t) => t.id), from, to))
  } catch {
    // Surfaced via the error ref below; reorderTypes has already put the old order back.
  }
}

function endDrag() {
  gripHeldIndex.value = null
  dragFromIndex.value = null
  overIndex.value = null
  overAfter.value = false
}

/** True when the insertion line belongs above (`after` false) or below this row right now. */
function showsLine(index: number, after: boolean): boolean {
  if (dragFromIndex.value === null || overIndex.value !== index || overAfter.value !== after) return false
  return dropIndex(dragFromIndex.value, index, after) !== dragFromIndex.value
}

async function onDelete(id: number) {
  if (currentProjectId.value === null) return
  try {
    await deleteMeasurementType(id, currentProjectId.value)
  } catch {
    // Surfaced via the error ref below (e.g. 409 when the type still has saved results).
  }
}
</script>

<template>
  <aside
    class="relative shrink-0 border-r border-neutral-200 dark:border-neutral-800"
    :class="pinned ? 'w-[15.4rem]' : 'w-11'"
    @mouseenter="hovered = true"
    @mouseleave="hovered = false"
    @focusin="hovered = true"
    @focusout="onFocusOut"
  >
    <!-- The rail: what an unpinned sidebar shows at rest. One icon per measurement so the list is
         still recognisable and selectable without opening it; names are in the tooltip. -->
    <div v-if="!pinned" class="flex flex-col items-center gap-2 p-1.5">
      <button
        type="button"
        class="flex h-7 w-7 items-center justify-center rounded bg-neutral-900 text-sm font-medium text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
        title="New measurement"
        aria-label="New measurement"
        @click="dialogOpen = true"
      >
        +
      </button>
      <nav class="flex flex-col gap-0.5">
        <button
          v-for="t in types"
          :key="t.id"
          type="button"
          class="relative flex h-8 w-8 items-center justify-center rounded"
          :class="
            t.id === currentTypeId
              ? 'bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300'
              : 'text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800'
          "
          :title="t.name"
          :aria-label="t.name"
          @click="selectType(t.id)"
        >
          <MeasurementKindIcon :kind="t.kind" />
          <span
            v-if="dirtyTypeIds.has(t.id)"
            class="absolute right-0.5 top-0.5 h-1.5 w-1.5 rounded-full bg-red-500"
            title="Unsaved changes"
          ></span>
        </button>
      </nav>
    </div>

    <!-- The full menu: in flow at the sidebar's width when pinned, floating over the content with
         a shadow when an unpinned rail is hovered. The same markup either way, so drag-to-reorder
         and every other behaviour is identical in both. -->
    <div
      v-if="expanded"
      class="flex flex-col gap-2 p-2"
      :class="
        pinned
          ? 'h-full'
          : 'absolute inset-y-0 left-0 z-30 w-[15.4rem] overflow-y-auto border-r border-neutral-200 bg-neutral-50 shadow-xl dark:border-neutral-800 dark:bg-neutral-950'
      "
    >
      <div class="flex items-center gap-1">
        <button
          type="button"
          class="flex min-w-0 flex-1 items-center justify-center gap-1.5 rounded bg-neutral-900 px-2 py-1.5 text-xs font-medium text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          @click="dialogOpen = true"
        >
          <span aria-hidden="true">+</span> New measurement
        </button>
        <button
          type="button"
          class="flex h-7 w-7 shrink-0 items-center justify-center rounded hover:bg-neutral-200 dark:hover:bg-neutral-800"
          :class="sidebarPinned ? 'text-neutral-900 dark:text-neutral-100' : 'text-neutral-400 dark:text-neutral-500'"
          :title="sidebarPinned ? 'Unpin: collapse the menu to icons and open it on hover' : 'Pin the menu open'"
          :aria-label="sidebarPinned ? 'Unpin menu' : 'Pin menu'"
          :aria-pressed="sidebarPinned"
          @click="setPinned(!sidebarPinned)"
        >
          <!-- A pushpin, drawn upright when pinned and tilted when it is not. -->
          <svg
            width="16"
            height="16"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
            :class="sidebarPinned ? '' : 'rotate-45'"
          >
            <path d="M9 3h6l-1 6 3 3v2H7v-2l3-3-1-6Z" :fill="sidebarPinned ? 'currentColor' : 'none'" />
            <path d="M12 14v7" />
          </svg>
        </button>
      </div>

      <p v-if="error" class="px-2 text-xs text-red-600 dark:text-red-400">{{ error }}</p>

      <nav class="flex flex-col gap-0.5">
        <div
          v-for="(t, index) in types"
          :key="t.id"
          class="group relative flex items-center rounded border-y border-transparent"
          :class="[
            t.id === currentTypeId
              ? 'bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300'
              : 'text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800',
            dragFromIndex === index ? 'opacity-40' : '',
            showsLine(index, false) ? '!border-t-blue-500' : '',
            showsLine(index, true) ? '!border-b-blue-500' : '',
          ]"
          :draggable="gripHeldIndex === index"
          @dragstart="onDragStart(index, $event)"
          @dragover="onDragOver(index, $event)"
          @drop.prevent="onDrop"
          @dragend="endDrag"
        >
          <span
            class="shrink-0 cursor-grab select-none px-1 text-neutral-300 opacity-60 group-hover:opacity-100 dark:text-neutral-600"
            title="Drag to reorder"
            aria-label="Drag to reorder"
            @mousedown="gripHeldIndex = index"
            >⠿</span
          >
          <button type="button" class="flex min-w-0 flex-1 items-center gap-2 py-1.5 pr-2 text-left text-sm" @click="selectType(t.id)">
            <MeasurementKindIcon :kind="t.kind" class="shrink-0" />
            <span class="min-w-0 flex-1 truncate" :title="t.name">{{ t.name }}</span>
            <span
              v-if="dirtyTypeIds.has(t.id)"
              class="h-1.5 w-1.5 shrink-0 rounded-full bg-red-500"
              title="Unsaved changes"
            ></span>
          </button>
          <button
            type="button"
            class="shrink-0 px-1.5 text-neutral-400 opacity-0 hover:text-red-600 group-hover:opacity-100"
            title="Delete measurement"
            aria-label="Delete measurement"
            @click="onDelete(t.id)"
          >
            ✕
          </button>
        </div>
        <p v-if="currentProjectId !== null && types.length === 0" class="px-2 text-xs text-neutral-400 dark:text-neutral-600">
          No measurements yet.
        </p>
      </nav>
    </div>

    <NewMeasurementDialog :open="dialogOpen" @close="dialogOpen = false" />
  </aside>
</template>
