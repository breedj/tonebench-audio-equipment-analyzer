<script setup lang="ts">
import { computed } from 'vue'
import { useUpdateCheck } from '../composables/useUpdateCheck'

const { updateAvailable, latestVersion, latestUrl, dismissedVersion, dismiss } = useUpdateCheck()

// Stays dismissed for this version once closed, but reappears if a newer one shows up later —
// same "dismissedVersion" persisted in localStorage that dismiss() writes.
const visible = computed(() => updateAvailable.value && latestVersion.value !== dismissedVersion.value)
</script>

<template>
  <div
    v-if="visible"
    class="flex items-center gap-3 border-b border-blue-200 bg-blue-50 px-4 py-2 text-sm text-blue-900 dark:border-blue-900 dark:bg-blue-950 dark:text-blue-100"
  >
    <span>
      QA403 Audio Analyzer {{ latestVersion }} is available.
      <a v-if="latestUrl" :href="latestUrl" target="_blank" rel="noopener" class="underline hover:no-underline">
        View release
      </a>
    </span>
    <span class="flex-1"></span>
    <button
      type="button"
      class="text-blue-700 hover:text-blue-900 dark:text-blue-300 dark:hover:text-blue-100"
      aria-label="Dismiss"
      title="Dismiss"
      @click="dismiss"
    >
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
        <path d="M18 6 6 18M6 6l12 12" />
      </svg>
    </button>
  </div>
</template>
