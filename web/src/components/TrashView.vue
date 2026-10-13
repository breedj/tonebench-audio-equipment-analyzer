<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useTrash } from '../composables/useTrash'
import { useConfirm } from '../composables/useConfirm'
import { useDateTimeFormat } from '../composables/useDateTimeFormat'
import { deletePermanentlyConfirmation, emptyTrashConfirmation, type TrashGroup, type TrashItem } from '../measurements/trash'
import {
  commitRetentionDays,
  TRASH_RETENTION_MAX_DAYS,
  TRASH_RETENTION_MIN_DAYS,
} from '../measurements/trashRetention'
import MeasurementKindIcon from './MeasurementKindIcon.vue'

// Restoring a measurement puts it back in the sidebar, and restoring a result puts it back in the
// history list — neither of which this component owns. App.vue listens for this and reloads them.
const emit = defineEmits<{ close: []; restored: [] }>()

const {
  listing,
  groups,
  loading,
  error,
  retentionDays,
  load,
  loadRetention,
  setRetentionDays,
  restoreType,
  restoreResult,
  purgeType,
  purgeResult,
  empty,
} = useTrash()

const { format: formatDate, load: loadDateFormat } = useDateTimeFormat()

onMounted(() => {
  void load()
  void loadRetention()
  void loadDateFormat()
})

// A draft string rather than :value on the stored ref, and @change rather than @input. Both are the
// lesson of ConfigDialog.vue's capture-delay field — read its comment before touching this: Vue
// patches `value` on every re-render whether it changed or not, so a bound stored ref overwrites
// what is being typed mid-entry, and a string draft is what lets the box be briefly empty.
const retentionDraft = ref('')
watch(retentionDays, (days) => (retentionDraft.value = String(days)), { immediate: true })

async function onRetentionChange() {
  await setRetentionDays(commitRetentionDays(retentionDraft.value))
  // A draft that parses back to the stored value gives the watcher nothing to react to, so the
  // rejected text would otherwise stay in the box.
  retentionDraft.value = String(retentionDays.value)
}

async function onRestoreType(group: TrashGroup) {
  if (await restoreType(group.measurementTypeId)) emit('restored')
}

async function onRestoreResult(item: TrashItem) {
  if (await restoreResult(item.resultId)) emit('restored')
}

async function onPurgeType(group: TrashGroup) {
  if (!(await useConfirm().confirm(deletePermanentlyConfirmation(group.name, 'measurement')))) return
  await purgeType(group.measurementTypeId)
}

async function onPurgeResult(item: TrashItem) {
  if (!(await useConfirm().confirm(deletePermanentlyConfirmation(item.name, 'result')))) return
  await purgeResult(item.resultId)
}

async function onEmpty() {
  if (listing.value === null) return
  if (!(await useConfirm().confirm(emptyTrashConfirmation(listing.value)))) return
  await empty()
}

function when(iso: string): string {
  return formatDate(new Date(iso))
}
</script>

<template>
  <section class="flex flex-col gap-4">
    <header class="flex flex-wrap items-center gap-3">
      <h2 class="text-lg font-medium">Trash</h2>
      <span class="text-sm text-neutral-500 dark:text-neutral-400">
        Deleted measurements and results, kept so they can be put back.
      </span>
      <div class="ml-auto flex items-center gap-2">
        <button
          type="button"
          class="rounded border px-3 py-1 text-sm btn-action"
          title="Back to the measurement"
          @click="emit('close')"
        >
          Close
        </button>
        <button
          type="button"
          class="rounded border border-red-300 bg-red-50 px-3 py-1 text-sm text-red-700 hover:bg-red-100 disabled:opacity-40 dark:border-red-800 dark:bg-red-950 dark:text-red-400 dark:hover:bg-red-900"
          :disabled="(listing?.totalCount ?? 0) === 0"
          title="Permanently delete everything in the trash."
          @click="onEmpty"
        >
          Empty trash
        </button>
      </div>
    </header>

    <p v-if="error" class="text-sm text-red-600 dark:text-red-400">{{ error }}</p>

    <div class="panel-surface flex flex-wrap items-end gap-3 rounded-lg p-4">
      <label class="flex flex-col gap-1 text-sm">
        Keep deleted items for (days)
        <input
          v-model="retentionDraft"
          type="number"
          :min="TRASH_RETENTION_MIN_DAYS"
          :max="TRASH_RETENTION_MAX_DAYS"
          step="1"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @change="onRetentionChange"
        />
      </label>
      <p class="pb-1 text-xs text-neutral-500 dark:text-neutral-400">
        Anything deleted longer ago than this is destroyed when the project is next opened. The
        setting is this bench's, not the project's.
      </p>
    </div>

    <p v-if="loading && groups.length === 0" class="text-sm text-neutral-400 dark:text-neutral-600">Reading the trash…</p>

    <p v-else-if="groups.length === 0" class="text-sm text-neutral-400 dark:text-neutral-600">
      The trash is empty. Deleting a measurement or a saved result puts it here.
    </p>

    <div v-else class="flex flex-col gap-3">
      <!-- One block per measurement. A block whose own measurement is deleted can be restored as a
           whole; one that is only the parent of deleted results is a header, which is what
           `canRestore` false with a reason means. -->
      <article
        v-for="group in groups"
        :key="group.measurementTypeId"
        class="panel-surface rounded-xl p-3"
      >
        <div class="flex flex-wrap items-center gap-2">
          <MeasurementKindIcon :kind="group.kind" class="shrink-0" />
          <span class="font-medium" :class="group.deletedUtc === null ? 'text-neutral-500 dark:text-neutral-400' : ''">
            {{ group.name }}
          </span>

          <span v-if="group.deletedUtc !== null" class="text-xs text-neutral-400">
            deleted {{ when(group.deletedUtc) }}
            <template v-if="group.restorableResultCount > 0">
              — {{ group.restorableResultCount }}
              {{ group.restorableResultCount === 1 ? 'result comes' : 'results come' }} back with it
            </template>
          </span>
          <span v-else class="text-xs text-neutral-400">not deleted — only the results below are</span>

          <div class="ml-auto flex gap-1.5 text-xs whitespace-nowrap">
            <button
              v-if="group.deletedUtc !== null"
              type="button"
              class="rounded border border-emerald-300 bg-emerald-50 px-2 py-1 text-emerald-700 hover:bg-emerald-100 disabled:opacity-40 dark:border-emerald-700 dark:bg-emerald-950/40 dark:text-emerald-300 dark:hover:bg-emerald-900/40"
              :disabled="!group.canRestore"
              :title="group.restoreBlockedReason ?? 'Put this measurement and its results back.'"
              @click="onRestoreType(group)"
            >
              Restore
            </button>
            <button
              v-if="group.deletedUtc !== null"
              type="button"
              class="rounded border border-red-300 bg-red-50 px-2 py-1 text-red-700 hover:bg-red-100 dark:border-red-800 dark:bg-red-950 dark:text-red-400 dark:hover:bg-red-900"
              title="Delete this measurement and its results permanently."
              @click="onPurgeType(group)"
            >
              Delete permanently
            </button>
          </div>
        </div>

        <table v-if="group.results.length > 0" class="mt-2 w-full text-sm">
          <tbody>
            <tr
              v-for="item in group.results"
              :key="item.resultId"
              class="border-t border-neutral-100 dark:border-neutral-800"
            >
              <!-- The indent is what makes a result read as a child of the measurement above it. -->
              <td class="py-1 pl-6 pr-2">
                {{ item.name }}
                <span v-if="item.notes" class="text-neutral-400"> — {{ item.notes }}</span>
              </td>
              <td class="py-1 pr-2 whitespace-nowrap text-neutral-400">measured {{ when(item.timestampUtc) }}</td>
              <td class="py-1 pr-2 whitespace-nowrap text-neutral-400">deleted {{ when(item.deletedUtc) }}</td>
              <td class="py-1 text-right">
                <div class="flex justify-end gap-1.5 text-xs whitespace-nowrap">
                  <button
                    type="button"
                    class="rounded border border-emerald-300 bg-emerald-50 px-2 py-1 text-emerald-700 hover:bg-emerald-100 disabled:opacity-40 dark:border-emerald-700 dark:bg-emerald-950/40 dark:text-emerald-300 dark:hover:bg-emerald-900/40"
                    :disabled="!item.canRestore"
                    :title="item.restoreBlockedReason ?? 'Put this result back in the measurement\'s history.'"
                    @click="onRestoreResult(item)"
                  >
                    Restore
                  </button>
                  <button
                    type="button"
                    class="rounded border border-red-300 bg-red-50 px-2 py-1 text-red-700 hover:bg-red-100 dark:border-red-800 dark:bg-red-950 dark:text-red-400 dark:hover:bg-red-900"
                    title="Delete this result permanently."
                    @click="onPurgeResult(item)"
                  >
                    Delete permanently
                  </button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </article>
    </div>
  </section>
</template>
