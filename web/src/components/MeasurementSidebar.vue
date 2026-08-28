<script setup lang="ts">
import { ref } from 'vue'
import { useProjects } from '../composables/useProjects'
import MeasurementKindIcon from './MeasurementKindIcon.vue'
import NewMeasurementDialog from './NewMeasurementDialog.vue'

const { types, currentTypeId, currentProjectId, dirtyTypeIds, selectType, deleteMeasurementType, error } = useProjects()

const dialogOpen = ref(false)

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
        v-for="t in types"
        :key="t.id"
        class="group flex items-center rounded"
        :class="
          t.id === currentTypeId
            ? 'bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300'
            : 'text-neutral-600 hover:bg-neutral-100 dark:text-neutral-300 dark:hover:bg-neutral-800'
        "
      >
        <button type="button" class="flex min-w-0 flex-1 items-center gap-2 px-2 py-1.5 text-left text-sm" @click="selectType(t.id)">
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
