<script setup lang="ts">
/**
 * The measurements-and-results tree both transfer dialogs tick, lifted from
 * `ProjectReportDialog`'s chapter card so an export and a report read the same way.
 *
 * Two things it carries that the report's does not. A **size** per row, because the whole point of
 * a transfer file is that it gets attached to something. And, where a measurement's name is already
 * taken in the destination, the **Rename / Merge / Skip** choice for that one measurement — asked
 * on its own row, beside the name that clashes, because the answer genuinely differs per
 * measurement: the same test from two benches wants merging, and two unrelated measurements that
 * happen to share a name very much do not.
 *
 * All of the arithmetic is `projects/transferSelection.ts`, which is testable; this file is the
 * markup.
 */
import { computed } from 'vue'
import type { ProjectContentsType } from '../api/client'
import { isKnownMeasurementKind, measurementKindLabel } from '../measurements/kinds'
import {
  NAME_CLASH_POLICIES,
  formatBytes,
  groupState,
  setEverything,
  setGroup,
  setResult,
  type NameClashPolicy,
  type TransferSelectionState,
} from '../projects/transferSelection'
import { formatWithPreference } from '../composables/useDateTimeFormat'
import MeasurementKindChip from './MeasurementKindChip.vue'

const props = defineProps<{
  caption: string
  types: readonly ProjectContentsType[]
  selection: TransferSelectionState
  busy: boolean
  /** Incoming type id → the measurement of that name already in the destination. */
  clashes: ReadonlyMap<number, ProjectContentsType>
  /** Incoming type ids whose chosen policy cannot be applied (a merge across kinds). */
  blocked: readonly number[]
  policies: ReadonlyMap<number, NameClashPolicy>
  /** The default policy, shown as the selected one where a measurement has made no choice. */
  defaultPolicy: NameClashPolicy
  /** Kinds this build does not know; those rows cannot be ticked. */
  unknown: readonly string[]
}>()

const emit = defineEmits<{
  'update:selection': [value: TransferSelectionState]
  'update:policy': [typeId: number, policy: NameClashPolicy]
}>()

const blockedSet = computed(() => new Set(props.blocked))
const unknownSet = computed(() => new Set(props.unknown))

const isUnknown = (type: ProjectContentsType) => unknownSet.value.has(type.kind) || !isKnownMeasurementKind(type.kind)
const policyFor = (typeId: number) => props.policies.get(typeId) ?? props.defaultPolicy

/** A measurement nothing can draw must not be tickable, so Select all skips those rows too. */
const tickable = computed(() => props.types.filter((t) => !isUnknown(t)))

function toggleGroup(type: ProjectContentsType, on: boolean) {
  emit('update:selection', setGroup(props.selection, type, on))
}

function toggleResult(type: ProjectContentsType, resultId: number, on: boolean) {
  emit('update:selection', setResult(props.selection, type, resultId, on))
}

function toggleEverything(on: boolean) {
  emit('update:selection', setEverything(tickable.value, on))
}

function resultBytes(type: ProjectContentsType): number {
  return type.results
    .filter((r) => props.selection.results.has(r.resultId))
    .reduce((n, r) => n + r.approximateBytes, 0)
}

/** "3 of 5 · 1.4 MB", or just the count where nothing under it is ticked. */
function groupFigure(type: ProjectContentsType): string {
  const picked = type.results.filter((r) => props.selection.results.has(r.resultId)).length
  if (type.results.length === 0) return 'settings only'
  const counted = picked === type.results.length ? `${picked}` : `${picked} of ${type.results.length}`
  return picked === 0 ? `0 of ${type.results.length}` : `${counted} · ${formatBytes(resultBytes(type))}`
}

function timestamp(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : formatWithPreference(date)
}
</script>

<template>
  <section class="dialog-card flex min-h-0 shrink-0 flex-col">
    <div class="flex items-center gap-2 border-b border-neutral-200 px-3.5 py-2 dark:border-neutral-800">
      <span class="flex-1 text-[11px] font-semibold tracking-[0.06em] text-neutral-500 uppercase dark:text-neutral-400">
        {{ props.caption }}
      </span>
      <button type="button" class="btn-chip-link" :disabled="props.busy" @click="toggleEverything(true)">Select all</button>
      <button type="button" class="btn-chip-link" :disabled="props.busy" @click="toggleEverything(false)">None</button>
    </div>

    <p v-if="props.types.length === 0" class="px-3.5 py-3 text-[12px] text-neutral-500 dark:text-neutral-400">
      There are no measurements here.
    </p>

    <div v-else class="max-h-[46vh] overflow-y-auto px-3.5">
      <div
        v-for="type in props.types"
        :key="type.typeId"
        class="border-b border-neutral-100 pt-2.5 pb-1.5 last:border-b-0 dark:border-neutral-800"
      >
        <label class="flex items-center gap-2.5" :class="isUnknown(type) ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'">
          <input
            type="checkbox"
            class="tb-check"
            :checked="groupState(type, props.selection) === 'all'"
            :indeterminate="groupState(type, props.selection) === 'some'"
            :disabled="props.busy || isUnknown(type)"
            @change="toggleGroup(type, ($event.target as HTMLInputElement).checked)"
          />
          <MeasurementKindChip :kind="type.kind" :size="24" />
          <span class="truncate text-[13.5px] font-semibold">{{ type.name }}</span>
          <span class="shrink-0 text-[12px] text-neutral-500 dark:text-neutral-400">
            {{ measurementKindLabel(type.kind) }}
          </span>
          <span class="flex-1"></span>
          <span class="shrink-0 font-mono text-[11.5px] text-neutral-500 dark:text-neutral-400">
            {{ groupFigure(type) }}
          </span>
        </label>

        <!-- Indented past the group's own checkbox *and* its kind chip, so a result reads as
             belonging to the measurement above it. -->
        <div class="flex flex-col pt-1 pl-[59px]">
          <p v-if="isUnknown(type)" class="py-[5px] text-[11.5px] text-amber-700 dark:text-amber-400">
            Unknown measurement kind “{{ type.kind }}” — written by a newer version of Tonebench, so
            this one cannot be brought across.
          </p>

          <template v-else>
            <label
              v-for="result in type.results"
              :key="result.resultId"
              class="flex cursor-pointer items-center gap-2.5 py-[5px]"
            >
              <input
                type="checkbox"
                class="tb-check"
                :checked="props.selection.results.has(result.resultId)"
                :disabled="props.busy"
                @change="toggleResult(type, result.resultId, ($event.target as HTMLInputElement).checked)"
              />
              <span class="min-w-0 flex-1 truncate">{{ result.name }}</span>
              <span v-if="result.source === 'simulation'" class="shrink-0 text-[11px] text-neutral-500 dark:text-neutral-400">
                simulation
              </span>
              <span class="shrink-0 font-mono text-[11.5px] whitespace-nowrap text-neutral-500 dark:text-neutral-400">
                {{ formatBytes(result.approximateBytes) }}
              </span>
              <span class="shrink-0 font-mono text-[11.5px] whitespace-nowrap text-neutral-500 dark:text-neutral-400">
                {{ timestamp(result.timestampUtc) }}
              </span>
            </label>

            <p v-if="type.results.length === 0" class="py-[5px] text-[11.5px] text-neutral-500 dark:text-neutral-400">
              No saved results — its settings and notes travel on their own.
            </p>

            <!-- The clash, asked where it is visible: beside the name that is taken. -->
            <div
              v-if="props.clashes.has(type.typeId) && props.selection.types.has(type.typeId)"
              class="mt-1 flex flex-wrap items-center gap-x-2.5 gap-y-1 py-[5px]"
            >
              <span class="rounded bg-amber-100 px-1.5 py-0.5 text-[11px] font-medium text-amber-800 dark:bg-amber-950/60 dark:text-amber-300">
                name already here
              </span>
              <label v-for="policy in NAME_CLASH_POLICIES" :key="policy" class="flex cursor-pointer items-center gap-1.5 text-[12px]">
                <input
                  type="radio"
                  class="h-[13px] w-[13px] shrink-0 accent-blue-600"
                  :name="`clash-${type.typeId}`"
                  :checked="policyFor(type.typeId) === policy"
                  :disabled="props.busy"
                  @change="emit('update:policy', type.typeId, policy)"
                />
                <span>{{ policy === 'Merge' ? 'Merge into it' : policy === 'Skip' ? 'Skip' : 'Keep both' }}</span>
              </label>
              <!-- No article before the kind: "Impedance", "IMD vs amplitude" and "FFT analysis"
                   all want "an", and a rule on the first letter gets FFT wrong anyway. -->
              <span v-if="blockedSet.has(type.typeId)" class="text-[11.5px] text-red-600 dark:text-red-400">
                cannot merge — the measurement of that name here is a different kind
                ({{ measurementKindLabel(props.clashes.get(type.typeId)?.kind) }})
              </span>
            </div>
          </template>
        </div>
      </div>
    </div>
  </section>
</template>
