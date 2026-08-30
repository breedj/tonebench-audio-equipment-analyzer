<script setup lang="ts">
import { computed } from 'vue'
import { useProjects } from '../composables/useProjects'
import { MAX_VISIBLE_OVERLAYS, useMeasurements } from '../composables/useMeasurements'
import { dashAttr, sweepOverlayDash } from '../measurements/sweepOverlayStyles'
import { isSweepKind } from '../measurements/kinds'
import CollapsibleSection from './CollapsibleSection.vue'

const emit = defineEmits<{ error: [message: string] }>()

const { currentTypeId, types } = useProjects()
const { items, overlays, addOverlay, setOverlayVisible, visibleOverlayCount, canShowMoreOverlays } =
  useMeasurements()

// The open type's kind — every item in `items` is server-filtered to currentTypeId, so they all
// share this same kind. Needed to scope the MAX_VISIBLE_OVERLAYS cap per kind (see the constant's
// own doc): this checklist is reused on both the sweep and FFT screens, and a result loaded on one
// screen stays loaded, just hidden, when the other is open.
const currentKind = computed(() => types.value.find((t) => t.id === currentTypeId.value)?.kind ?? null)

// Checkbox state mirrors the overlay's visibility (checked = plotted on the chart), matching the
// design's "check it to see it" model. Unchecking hides rather than removes the overlay, so its
// color/series slot stays stable if it's re-checked. The full history detail (timestamps, THD
// headline, CSV export, delete, date filter) lives in MeasurementHistoryPanel.vue further down the
// page, not here — this is deliberately just the compact checklist from the design.
function isChecked(resultId: number): boolean {
  return overlays.value.find((o) => o.id === resultId)?.visible ?? false
}

/**
 * On the sweep screen a checked run is identified on the chart purely by its dash pattern, so the
 * row shows that pattern too — otherwise the only way to map a line back to a name is to read the
 * legend. Derived through sweepOverlayDash, i.e. from the same slot order ThdSweepChart assigns
 * from, so the two cannot disagree.
 *
 * Undefined on the FFT screen: those overlays are told apart by colour instead, and OverlayList
 * already renders their swatches.
 */
const showsSweepDash = computed(() => isSweepKind(currentKind.value))

function sweepDashFor(resultId: number): string | undefined {
  return dashAttr(sweepOverlayDash(overlays.value, resultId))
}

// At most MAX_VISIBLE_OVERLAYS of the open type's kind may be plotted at once (see the constant for
// why). The remaining boxes are disabled rather than left clickable-and-rejected, so the ceiling is
// visible before the click instead of arriving as an error afterwards; the checked ones stay live
// so a slot can always be freed.
function isDisabled(resultId: number): boolean {
  return currentKind.value !== null && !canShowMoreOverlays(currentKind.value) && !isChecked(resultId)
}

async function onToggle(resultId: number, event: Event) {
  const input = event.target as HTMLInputElement
  const checked = input.checked
  try {
    if (checked && currentKind.value !== null) {
      await addOverlay(resultId, currentKind.value)
    } else if (!checked) {
      setOverlayVisible(resultId, false)
    }
  } catch (e) {
    emit('error', (e as Error).message)
  }
  // The DOM checkbox flipped itself on click; if the toggle was refused (or the fetch failed) the
  // overlay state did not change, so Vue re-renders nothing and the box would be left lying about
  // what is on the chart. Put it back where the state says it belongs.
  input.checked = isChecked(resultId)
}
</script>

<template>
  <CollapsibleSection title="History" size="xs">
    <template #header-right>
      <span
        v-if="currentKind !== null && items.length > 0"
        class="font-mono text-[10px] text-neutral-400 dark:text-neutral-600"
        :title="`At most ${MAX_VISIBLE_OVERLAYS} history items can be plotted at once.`"
      >
        {{ visibleOverlayCount(currentKind) }}/{{ MAX_VISIBLE_OVERLAYS }}
      </span>
    </template>
    <p v-if="currentTypeId === null" class="text-xs text-neutral-400 dark:text-neutral-600">No measurement selected.</p>
    <ul v-else class="flex flex-col gap-1.5 text-sm">
      <li v-for="item in items" :key="item.resultId" class="flex items-center gap-2">
        <input
          type="checkbox"
          :checked="isChecked(item.resultId)"
          :disabled="isDisabled(item.resultId)"
          class="h-4 w-4 rounded border-neutral-400 disabled:cursor-not-allowed disabled:opacity-40 dark:border-neutral-600"
          :title="isDisabled(item.resultId) ? `Uncheck one of the ${MAX_VISIBLE_OVERLAYS} plotted items first.` : undefined"
          @change="onToggle(item.resultId, $event)"
        />
        <!-- Reserved even when unchecked (the <line> is simply omitted), so ticking a box doesn't
             shift every name in the list sideways. -->
        <svg
          v-if="showsSweepDash"
          width="22"
          height="6"
          viewBox="0 0 22 6"
          aria-hidden="true"
          class="shrink-0 overflow-visible text-neutral-500 dark:text-neutral-400"
        >
          <line
            v-if="isChecked(item.resultId)"
            x1="0"
            y1="3"
            x2="22"
            y2="3"
            stroke="currentColor"
            stroke-width="1.5"
            :stroke-dasharray="sweepDashFor(item.resultId)"
          />
        </svg>
        <span class="truncate" :class="{ 'text-neutral-400 dark:text-neutral-600': isDisabled(item.resultId) }">
          {{ item.name }}
        </span>
      </li>
      <li v-if="items.length === 0" class="text-xs text-neutral-400 dark:text-neutral-600">No measurements saved yet.</li>
    </ul>
  </CollapsibleSection>
</template>
