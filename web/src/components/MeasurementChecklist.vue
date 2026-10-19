<script setup lang="ts">
import { computed } from 'vue'
import type { MeasurementSummary } from '../api/client'
import { useProjects } from '../composables/useProjects'
import { MAX_VISIBLE_OVERLAYS, useMeasurements } from '../composables/useMeasurements'
import { useSweep } from '../composables/useSweep'
import { dashAttr, sweepOverlayDash } from '../measurements/sweepOverlayStyles'
import { sweepOverlayScreen } from '../measurements/overlayScope'
import {
  isSweepKind,
  isFrequencyResponseKind,
  isImpedanceKind,
  FFT_SPECTRUM_KIND,
  THD_VS_AMPLITUDE_KIND,
  THD_VS_FREQUENCY_KIND,
} from '../measurements/kinds'
import { groupHistory, isSimulation, showsGroupHeaders } from '../measurements/historyGroups'
import { useAnnotations } from '../composables/useAnnotations'
// A module store rather than an emit, because every screen mounts its own checklist and the
// handler was forgotten twice -- see simulationImport.ts.
import { openSimulationImport } from '../measurements/simulationImport'
import AnnotationToggle from './AnnotationToggle.vue'
import CollapsibleSection from './CollapsibleSection.vue'

const emit = defineEmits<{ error: [message: string] }>()

const { currentTypeId, types } = useProjects()
const { items, overlays, addOverlay, setOverlayVisible, visibleOverlayCount, canShowMoreOverlays } =
  useMeasurements()

// The chart gives run 0 — the solid line — to the live sweep when there is one, and to the first
// checked history item when there is not (see runDash). The swatch has to make the same call, or a
// row promises a pattern the chart is not drawing.
const { points: sweepPoints } = useSweep()

// Whose annotations are on the charts. A row's icon is one member of that group -- see
// useAnnotations -- so lighting one here darkens this measurement's own and every other row's.
const { source: annotationSource, show: showAnnotations } = useAnnotations()

// The open type's kind — every item in `items` is server-filtered to currentTypeId, so they all
// share this same kind. Needed to scope the MAX_VISIBLE_OVERLAYS cap per kind (see the constant's
// own doc): this checklist is reused on both the sweep and FFT screens, and a result loaded on one
// screen stays loaded, just hidden, when the other is open.
const currentKind = computed(() => types.value.find((t) => t.id === currentTypeId.value)?.kind ?? null)

// Measured runs and imported simulations are both results of this same measurement (migration
// 014), so they arrive in one list and are split for display only. The arithmetic is in
// historyGroups.ts, where it can be tested — nothing in a .vue is.
const groups = computed(() => groupHistory(items.value))
const withHeaders = computed(() => showsGroupHeaders(groups.value))

// Five screens can import one, and what each takes of the file is the whole distinction: an FFT
// measurement takes a transient (.tran) run, because a spectrum is what a waveform becomes; a
// frequency response takes an AC (.ac) sweep, because that already is one; an impedance
// measurement takes the same AC sweep but reads a voltage over a current, which is ohms; and the
// two THD sweeps take a `.step`ped transient, because a tone stepped across the band *is* a
// distortion-against-frequency curve written in another form, and a drive level stepped up is a
// distortion-against-amplitude one.
//
// The dialog that opens follows from this too, and a file of the wrong kind is refused by name,
// pointing at the screen it does belong on.
const canImport = computed(
  () =>
    currentKind.value === FFT_SPECTRUM_KIND ||
    isFrequencyResponseKind(currentKind.value) ||
    isImpedanceKind(currentKind.value) ||
    currentKind.value === THD_VS_FREQUENCY_KIND ||
    currentKind.value === THD_VS_AMPLITUDE_KIND,
)

/** What the button says it will read, which is the thing most often got wrong. */
const importsWhat = computed(() => {
  if (isImpedanceKind(currentKind.value)) return 'a voltage and a current from a simulated .ac sweep'
  if (isFrequencyResponseKind(currentKind.value)) return 'a simulated .ac sweep'
  if (currentKind.value === THD_VS_FREQUENCY_KIND) return 'a tone-stepped (.step) simulated .tran run as a sweep'
  if (currentKind.value === THD_VS_AMPLITUDE_KIND) return 'a level-stepped (.step) simulated .tran run as a sweep'
  return 'a simulated .tran run'
})

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
  // Scoped to the open kind, like the chart's own list: a stored run of another sweep kind is not
  // drawn, so promising it a pattern would point at a line that is not there.
  return dashAttr(
    sweepOverlayDash(overlays.value, resultId, sweepPoints.value.length > 0, sweepOverlayScreen(currentKind.value)),
  )
}

// At most MAX_VISIBLE_OVERLAYS of the open type's kind may be plotted at once (see the constant for
// why). The remaining boxes are disabled rather than left clickable-and-rejected, so the ceiling is
// visible before the click instead of arriving as an error afterwards; the checked ones stay live
// so a slot can always be freed.
//
// Simulations share that one budget with measured runs, deliberately: the cap is about how many
// traces a chart can carry legibly, which does not depend on where a trace came from — and
// splitting it would stop someone comparing three simulations against the measurement itself.
function isDisabled(resultId: number): boolean {
  return currentKind.value !== null && !canShowMoreOverlays(currentKind.value) && !isChecked(resultId)
}

/**
 * What the notes icon says it will do, which has to name the result: this is the one control in the
 * rail that can point the Annotations panel at something other than the open measurement.
 */
function annotationTitle(item: MeasurementSummary): string {
  if (annotationSource.value === item.resultId) return "This result's annotations are on the charts. Click to hide them."
  const count = item.annotationCount
  return count === 0
    ? `"${item.name}" has no annotations. Showing it lets you add some, which are saved to that result.`
    : `Show the ${count} annotation${count === 1 ? '' : 's'} saved with "${item.name}".`
}

async function onAnnotationToggle(resultId: number) {
  try {
    await showAnnotations(resultId)
  } catch (e) {
    emit('error', (e as Error).message)
  }
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
      <div class="flex items-center gap-2">
        <button
          v-if="canImport && currentTypeId !== null"
          type="button"
          class="rounded border px-1.5 py-0.5 text-[10px] btn-action"
          :title="`Import ${importsWhat} (.raw or .qraw) as a trace on this chart.`"
          @click="openSimulationImport()"
        >
          <!-- Abbreviated: at the display rail's width the full label pushed the panel's own title
               down to "Hi…" (Joost, 2026-10-12). The tooltip carries what it imports. -->
          Import sim…
        </button>
        <span
          v-if="currentKind !== null && items.length > 0"
          class="font-mono text-[10px] text-neutral-400 dark:text-neutral-600"
          :title="`At most ${MAX_VISIBLE_OVERLAYS} history items can be plotted at once.`"
        >
          {{ visibleOverlayCount(currentKind) }}/{{ MAX_VISIBLE_OVERLAYS }}
        </span>
      </div>
    </template>
    <p v-if="currentTypeId === null" class="text-xs text-neutral-400 dark:text-neutral-600">No measurement selected.</p>
    <template v-else>
      <div v-for="group in groups" :key="group.id" class="flex flex-col gap-1.5">
        <!-- Only when there is more than one section: a lone "Measurements" header above a list
             that is obviously measurements is noise, and that is the ordinary case. -->
        <p
          v-if="withHeaders"
          class="mt-1.5 text-[10px] font-semibold tracking-wide text-neutral-400 uppercase first:mt-0 dark:text-neutral-500"
        >
          {{ group.label }}
        </p>
        <ul class="flex flex-col gap-1.5 text-sm">
          <li v-for="item in group.items" :key="item.resultId" class="flex items-center gap-2">
            <input
              type="checkbox"
              :checked="isChecked(item.resultId)"
              :disabled="isDisabled(item.resultId)"
              class="h-4 w-4 shrink-0 rounded border-neutral-400 disabled:cursor-not-allowed disabled:opacity-40 dark:border-neutral-600"
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
            <span
              class="min-w-0 truncate"
              :class="{ 'text-neutral-400 dark:text-neutral-600': isDisabled(item.resultId) }"
            >
              {{ item.name }}
            </span>
            <!-- Colour identifies the run and dash identifies the channel, both already spoken
                 for, so provenance is a chip rather than a line style. -->
            <span
              v-if="isSimulation(item)"
              class="shrink-0 rounded px-1 text-[9px] font-semibold tracking-wide uppercase"
              :class="'bg-violet-100 text-violet-700 dark:bg-violet-950 dark:text-violet-300'"
              title="Imported from an LTspice simulation, not measured."
            >
              sim
            </span>
            <!-- Its own notes, on the charts. Independent of the checkbox on purpose: an annotation
                 is stored in data coordinates and draws perfectly well without its curve, and
                 coupling the two would spend one of the three plotting slots just to read a note
                 (Joost, 2026-10-18). Last in the row and pushed right, so the icons line up down
                 the list however long the names are. -->
            <AnnotationToggle
              class="ml-auto"
              :active="annotationSource === item.resultId"
              :empty="item.annotationCount === 0"
              :title="annotationTitle(item)"
              @click="onAnnotationToggle(item.resultId)"
            />
          </li>
        </ul>
      </div>
      <p v-if="items.length === 0" class="text-xs text-neutral-400 dark:text-neutral-600">No measurements saved yet.</p>
    </template>
  </CollapsibleSection>
</template>
