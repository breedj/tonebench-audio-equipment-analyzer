<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RUN_ACTIVE_MESSAGE, useProjects } from '../composables/useProjects'
import { SIDEBAR_PIN_MIN_WIDTH_PX, useMediaQuery } from '../composables/useMediaQuery'
import { useUiLayout } from '../composables/useUiLayout'
import { useTrash } from '../composables/useTrash'
import { dropIndex, moveItem } from '../measurements/reorder'
import MeasurementKindChip from './MeasurementKindChip.vue'
import NewMeasurementDialog from './NewMeasurementDialog.vue'

const { types, currentTypeId, currentProjectId, dirtyTypeIds, selectType, deleteMeasurementType, reorderTypes, error, runActive } =
  useProjects()

/**
 * Why every row that would open another measurement is disabled, or null while nothing is running.
 *
 * <p>Selecting a measurement stops whatever is running (deviceControl's `stopRun`), so until
 * 2026-10-17 a stray click threw away a sweep minutes into its grid without asking. `selectType`
 * refuses it now; this is what says so before the click rather than after it, and the row that is
 * already open stays live, since selecting it changes nothing.</p>
 */
const lockedReason = computed(() => (runActive.value ? RUN_ACTIVE_MESSAGE : null))
function locked(id: number) {
  return runActive.value && id !== currentTypeId.value
}

// The badge is the only thing on screen that says the trash holds anything, so it is refreshed
// whenever the project changes as well as after each delete (useProjects/useMeasurements do that
// half). A project switch is what the watcher catches; `immediate` covers the first paint.
const { open: trashOpen, count: trashCount, openTrash, closeTrash, refreshCount } = useTrash()
watch(currentProjectId, () => void refreshCount(), { immediate: true })

// Clicking a row while the trash is showing means "take me back to that measurement", and that has
// to be handled here rather than left to App.vue's watcher on currentTypeId: the row most likely to
// be clicked is the highlighted one — the measurement that was open when the trashcan was pressed —
// and selecting it sets currentTypeId to the value it already holds, so the watcher never fires and
// the trash stays on screen. Every other row worked, which is how it was reported. The watcher is
// still what covers the paths that do not come through here, NewMeasurementDialog above all.
function onSelect(id: number) {
  closeTrash()
  void selectType(id)
}

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

// The ✕ deletes the measurement *and* every result ever saved under it, and the server never
// refuses — there is no 409 for "it still has saved results", which an earlier comment here claimed
// and which cost a project file's "Amp 1 kHz at 1 W" and its five results on 2026-10-02. So the
// question is asked first, by deleteMeasurementType itself (ConfirmDialog, mounted in App.vue): a
// confirmation inside the composable is one no caller can skip, and the sidebar row is far too
// narrow for the click-twice Confirm/Cancel pair MeasurementHistoryPanel uses for a single result.
async function onDelete(id: number) {
  if (currentProjectId.value === null) return
  try {
    await deleteMeasurementType(id, currentProjectId.value)
  } catch {
    // Surfaced via the error ref below. A declined confirmation is not an error — it resolves false.
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
        class="flex h-8 w-8 items-center justify-center rounded-[10px] bg-neutral-900 text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
        :title="lockedReason ?? 'New measurement'"
        aria-label="New measurement"
        :disabled="runActive"
        @click="dialogOpen = true"
      >
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" aria-hidden="true">
          <path d="M12 5v14M5 12h14" />
        </svg>
      </button>
      <nav class="flex flex-col gap-0.5">
        <button
          v-for="t in types"
          :key="t.id"
          type="button"
          class="relative flex h-9 w-9 items-center justify-center rounded-[10px]"
          :class="[
            t.id === currentTypeId
              ? 'bg-blue-50 dark:bg-blue-900/30'
              : 'hover:bg-neutral-100 dark:hover:bg-neutral-800',
            locked(t.id) ? 'opacity-40' : '',
          ]"
          :title="locked(t.id) ? `${t.name} — ${lockedReason}` : t.name"
          :aria-label="t.name"
          :disabled="locked(t.id)"
          @click="onSelect(t.id)"
        >
          <MeasurementKindChip :kind="t.kind" :size="26" />
          <span
            v-if="dirtyTypeIds.has(t.id)"
            class="absolute right-0.5 top-0.5 h-1.5 w-1.5 rounded-full bg-red-500"
            title="Unsaved changes"
          ></span>
        </button>
      </nav>

      <!-- The rail's trashcan. Same action as the footer button below; the count is a dot here,
           because a number does not fit in 2rem and the tooltip carries it. -->
      <button
        type="button"
        class="relative flex h-9 w-9 items-center justify-center rounded-[10px]"
        :class="
          trashOpen
            ? 'bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300'
            : 'text-neutral-500 hover:bg-neutral-100 dark:text-neutral-400 dark:hover:bg-neutral-800'
        "
        :title="trashCount === 0 ? 'Trash (empty)' : `Trash (${trashCount})`"
        :aria-label="trashCount === 0 ? 'Trash, empty' : `Trash, ${trashCount} items`"
        @click="openTrash()"
      >
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3" />
        </svg>
        <span
          v-if="trashCount > 0"
          class="absolute right-0.5 top-0.5 h-1.5 w-1.5 rounded-full bg-amber-500"
          aria-hidden="true"
        ></span>
      </button>
    </div>

    <!-- The full menu: in flow at the sidebar's width when pinned, floating over the content with
         a shadow when an unpinned rail is hovered. The same markup either way, so drag-to-reorder
         and every other behaviour is identical in both. -->
    <div
      v-if="expanded"
      class="flex flex-col gap-2 p-2"
      :class="
        pinned
          ? 'h-full bg-[var(--surface-panel)]'
          : 'absolute inset-y-0 left-0 z-30 w-[15.4rem] overflow-y-auto border-r border-neutral-200 bg-[var(--surface-panel)] shadow-xl dark:border-neutral-800'
      "
    >
      <div class="flex items-center gap-1">
        <button
          type="button"
          class="flex h-10 min-w-0 flex-1 items-center justify-center gap-1.5 rounded-[10px] bg-neutral-900 px-2 text-[13px] font-semibold text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          :title="lockedReason ?? undefined"
          :disabled="runActive"
          @click="dialogOpen = true"
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" aria-hidden="true">
            <path d="M12 5v14M5 12h14" />
          </svg>
          New measurement
        </button>
        <button
          type="button"
          class="flex h-10 w-10 shrink-0 items-center justify-center rounded-[10px] hover:bg-neutral-200 dark:hover:bg-neutral-800"
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

      <!-- Amber, not red: a locked sidebar is the run doing its job, where `error` is something
           that went wrong. Shown without waiting for a click, which is the whole point of it. -->
      <p v-if="lockedReason" class="px-2 text-xs text-amber-600 dark:text-amber-400">{{ lockedReason }}</p>
      <p v-else-if="error" class="px-2 text-xs text-red-600 dark:text-red-400">{{ error }}</p>

      <nav class="flex flex-col gap-0.5">
        <div
          v-for="(t, index) in types"
          :key="t.id"
          class="group relative flex items-center rounded-[10px] border-y border-transparent"
          :class="[
            t.id === currentTypeId
              ? 'bg-blue-50 font-semibold text-blue-700 dark:bg-blue-900/30 dark:text-blue-300'
              : 'text-neutral-700 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800',
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
          <button
            type="button"
            class="flex min-w-0 flex-1 items-center gap-2.5 py-1 pr-2 text-left text-[13px]"
            :class="locked(t.id) ? 'opacity-40' : ''"
            :disabled="locked(t.id)"
            :title="locked(t.id) ? lockedReason ?? undefined : undefined"
            @click="onSelect(t.id)"
          >
            <MeasurementKindChip :kind="t.kind" />
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

      <div class="mt-auto h-px shrink-0 bg-neutral-200 dark:bg-neutral-800"></div>
      <!-- The footer. The rule above it is the mockup's separator between what is open and what has
           been thrown away; `mt-auto` on *it* is what puts the pair at the bottom of the pinned column, which is
           `h-full flex flex-col`; in the floating menu the container is `inset-y-0` and already
           scrolls, so the same rule applies there. -->
      <button
        type="button"
        class="flex shrink-0 items-center gap-2.5 rounded-[10px] px-2 py-2 text-left text-[13px]"
        :class="
          trashOpen
            ? 'bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300'
            : 'text-neutral-500 hover:bg-neutral-100 dark:text-neutral-400 dark:hover:bg-neutral-800'
        "
        title="Measurements and results you have deleted, and how long they are kept."
        @click="openTrash()"
      >
        <span class="flex h-[30px] w-[30px] items-center justify-center" aria-hidden="true">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3" />
          </svg>
        </span>
        <span class="min-w-0 flex-1 truncate">Trash</span>
        <span
          v-if="trashCount > 0"
          class="shrink-0 rounded-full bg-amber-100 px-1.5 text-xs text-amber-800 dark:bg-amber-900/50 dark:text-amber-200"
          >{{ trashCount }}</span
        >
      </button>
    </div>

    <NewMeasurementDialog :open="dialogOpen" @close="dialogOpen = false" />
  </aside>
</template>
