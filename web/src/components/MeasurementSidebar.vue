<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useProjects } from '../composables/useProjects'
import { dropIndex, moveItem } from '../measurements/reorder'
import MeasurementKindIcon from './MeasurementKindIcon.vue'
import NewMeasurementDialog from './NewMeasurementDialog.vue'

const { types, currentTypeId, currentProjectId, dirtyTypeIds, selectType, deleteMeasurementType, reorderTypes, error } =
  useProjects()

const dialogOpen = ref(false)

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
  <aside class="flex w-[15.4rem] shrink-0 flex-col gap-2 border-r border-neutral-200 p-2 dark:border-neutral-800">
    <button
      type="button"
      class="flex items-center justify-center gap-1.5 rounded bg-neutral-900 px-2 py-1.5 text-xs font-medium text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
      @click="dialogOpen = true"
    >
      <span aria-hidden="true">+</span> New measurement
    </button>

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

    <NewMeasurementDialog :open="dialogOpen" @close="dialogOpen = false" />
  </aside>
</template>
