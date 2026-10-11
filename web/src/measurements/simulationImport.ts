import { ref } from 'vue'

/**
 * Whether the simulation import dialog is open — a module store rather than an event, because the
 * event has been forgotten twice.
 *
 * <p><b>Every measurement screen mounts its own `MeasurementChecklist`</b>, so the Import button is
 * rendered in seven places and an `@import-simulation` handler has to be written on each one that
 * can take a file. Wiring is invisible when it is missing: the button renders, the tooltip is
 * right, the click does nothing at all. It cost a bug when the `.ac` import landed on the frequency
 * response screen (only the FFT mount was wired) and the identical bug the moment the impedance
 * screen was added. A module-level ref removes the wiring entirely — the checklist opens the dialog
 * itself, and a new screen inherits a working button by mounting the component.</p>
 *
 * <p>Which dialog opens is still decided in `App.vue` from the measurement's kind, as it must be:
 * what a file means depends on the screen it is imported onto.</p>
 */
export const simulationImportOpen = ref(false)

export function openSimulationImport(): void {
  simulationImportOpen.value = true
}

export function closeSimulationImport(): void {
  simulationImportOpen.value = false
}
