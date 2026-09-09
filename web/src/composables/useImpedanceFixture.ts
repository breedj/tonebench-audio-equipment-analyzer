import { computed, ref } from 'vue'
import { api, type ImpedanceFixtureConfig } from '../api/client'

/**
 * The globally configured impedance fixture: reference resistor, calibration drive level, and the
 * two captured standards.
 *
 * <p>Module-level, like the other shared stores here, because two components read it — the
 * measurement's calibration panel, which offers to use it and to store into it, and the
 * Configuration dialog, which shows and clears it.</p>
 *
 * <p>Server-side rather than `localStorage`, unlike theme or capture delay. This is measurement data
 * of a few kilobytes describing physical hardware; it has to read the same in every tab, survive a
 * browser cache clear, and live on the machine the QA403 is plugged into. It is kept in its own
 * database file — see the server's `Qa403ConfigDatabaseOptions` for why it must not sit in the
 * measurement history.</p>
 */
const fixture = ref<ImpedanceFixtureConfig | null>(null)
const error = ref<string | null>(null)
let loadPromise: Promise<void> | null = null

/** True once a usable open standard is stored — the only condition under which a measurement may adopt the fixture. */
const hasCalibration = computed(() => fixture.value?.hasCalibration === true)

export function useImpedanceFixture() {
  async function load(force = false) {
    // Coalesce the several components that mount in one tick and all want it, the same way
    // useSpectrumConfig does.
    if (loadPromise && !force) return loadPromise

    loadPromise = (async () => {
      try {
        fixture.value = await api.getImpedanceFixture()
        error.value = null
      } catch (e) {
        error.value = (e as Error).message
      } finally {
        loadPromise = null
      }
    })()

    return loadPromise
  }

  async function save(next: ImpedanceFixtureConfig) {
    fixture.value = await api.setImpedanceFixture(next)
    error.value = null
    return fixture.value
  }

  async function clear() {
    fixture.value = await api.clearImpedanceFixture()
    error.value = null
    return fixture.value
  }

  return { fixture, hasCalibration, error, load, save, clear }
}
