import { beforeEach, describe, expect, it } from 'vitest'
import {
  closeSimulationImport,
  openSimulationImport,
  simulationImportOpen,
} from './simulationImport'

/**
 * The store itself is three lines; what it is worth testing for is that it is *shared*. The bug it
 * replaces was a per-mount `@import-simulation` handler, forgotten on five of the seven screens
 * that mount a `MeasurementChecklist` — so the button rendered, the tooltip was right and the click
 * did nothing. Anything reading this module sees the same flag, whichever screen opened it.
 */
describe('simulationImport', () => {
  beforeEach(() => closeSimulationImport())

  it('is closed until something opens it', () => {
    expect(simulationImportOpen.value).toBe(false)
  })

  it('is one flag, whoever opens and whoever closes it', () => {
    openSimulationImport()
    expect(simulationImportOpen.value).toBe(true)

    // Opening twice is not a toggle: two screens' checklists are mounted at once in a stacked
    // layout, and a second press must not close the dialog the first one opened.
    openSimulationImport()
    expect(simulationImportOpen.value).toBe(true)

    closeSimulationImport()
    expect(simulationImportOpen.value).toBe(false)
  })
})
