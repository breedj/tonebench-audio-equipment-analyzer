<script setup lang="ts">
/**
 * The Notes box directly above every screen's History details, at the same width: free text about the measurement
 * itself — what is under test, how it is wired, what to look for — kept with the measurement type
 * and shown again whenever it is opened. Staged through useProjects like the settings: typing
 * marks the measurement dirty and Save / Save as… write it. The Save measurement dialog copies the
 * text into a new result's own notes, so a result carries the description its measurement had
 * when it was taken, and a report's Description element prints both.
 *
 * Each screen in App.vue places it immediately before its MeasurementHistoryPanel, so the two
 * panels always share a column and a width — a new screen adds both together.
 */
import { useProjects } from '../composables/useProjects'
import CollapsibleSection from './CollapsibleSection.vue'

const { currentTypeId, typeNotes, setTypeNotes } = useProjects()
</script>

<template>
  <CollapsibleSection v-if="currentTypeId !== null" title="Notes">
    <textarea
      :value="typeNotes"
      rows="3"
      placeholder="About this measurement — the DUT, the wiring, what to look for. Saved with the measurement, and copied onto every result saved from it."
      class="w-full resize-y rounded border border-neutral-300 px-2 py-1 text-sm dark:border-neutral-700"
      @input="setTypeNotes(($event.target as HTMLTextAreaElement).value)"
    />
  </CollapsibleSection>
</template>
