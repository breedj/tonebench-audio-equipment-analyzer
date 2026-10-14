<script setup lang="ts">
/**
 * The Notes box in every screen's chart column, at History details' own width: free text about the measurement
 * itself — what is under test, how it is wired, what to look for — kept with the measurement type
 * and shown again whenever it is opened. Staged through useProjects like the settings: typing
 * marks the measurement dirty and Save / Save as… write it. The Save measurement dialog copies the
 * text into a new result's own notes, so a result carries the description its measurement had
 * when it was taken, and a report's Description element prints both.
 *
 * Six of the seven screens in App.vue place it immediately before their MeasurementHistoryPanel, so
 * the two panels share a column and a width — a new screen adds both together. The FFT screen is
 * the exception and has to be: its History spans the whole measurement view at the foot of the page,
 * under the overlay list and the harmonics/other-peaks grids, so a Notes box kept above it was far
 * enough down to read as absent (Joost, 2026-10-14). It sits under that screen's charts instead,
 * which is where the other six have it relative to their own chart — the parity that was asked for.
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
