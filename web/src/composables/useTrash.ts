import { computed, ref } from 'vue'
import { api } from '../api/client'
import type { TrashListing } from '../api/client'
import { toTrashGroups, trashCount } from '../measurements/trash'
import { TRASH_RETENTION_DEFAULT_DAYS } from '../measurements/trashRetention'

/**
 * The trash: whether its view is showing, what is in it, and how long things stay.
 *
 * <p>Module-level singletons like the other shared stores here, because three unrelated components
 * read them — the sidebar's footer button and its badge, the view itself, and App.vue's choice of
 * what to put in the main panel.</p>
 *
 * <p><b>This store deliberately does not import `useProjects` or `useMeasurements`.</b> Both of
 * them call into it (to refresh the badge after a delete), so a reference back would be a cycle;
 * the project id arrives as a parameter instead, and refreshing the sidebar list after a restore is
 * the caller's job. That is also why `restoreType` and the rest only report what happened and leave
 * reloading to the view.</p>
 */
const open = ref(false)
const listing = ref<TrashListing | null>(null)
const retentionDays = ref(TRASH_RETENTION_DEFAULT_DAYS)
const loading = ref(false)
const error = ref<string | null>(null)
let retentionPromise: Promise<void> | null = null

const count = computed(() => trashCount(listing.value))
const groups = computed(() => (listing.value === null ? [] : toTrashGroups(listing.value)))

export function useTrash() {
  function openTrash() {
    open.value = true
  }

  function closeTrash() {
    open.value = false
  }

  /**
   * Reloads the listing. Swallows nothing — a trash that cannot be read is worth saying so, since
   * the user is looking at an empty list and would otherwise read it as "nothing in here".
   *
   * <p>A 409 is the ordinary no-project case (the badge is loaded before the welcome screen has
   * been left), so it clears the listing instead of complaining.</p>
   */
  async function load() {
    loading.value = true
    try {
      listing.value = await api.listTrash()
      error.value = null
    } catch (e) {
      const message = (e as Error).message
      if (/\b409\b/.test(message)) {
        listing.value = null
        error.value = null
      } else {
        error.value = message
      }
    } finally {
      loading.value = false
    }
  }

  /** Quietly keeps the badge current after a delete elsewhere. Never surfaces an error. */
  async function refreshCount() {
    try {
      listing.value = await api.listTrash()
    } catch {
      // The badge is not worth an error banner; the view's own load reports properly.
    }
  }

  async function loadRetention() {
    if (retentionPromise) return retentionPromise
    retentionPromise = (async () => {
      try {
        retentionDays.value = (await api.getTrashRetention()).days
      } catch {
        // A setting that could not be read is not worth an error on screen; the default is what a
        // fresh install has, and the next edit writes a fresh document.
      }
    })()
    return retentionPromise
  }

  async function setRetentionDays(days: number) {
    const previous = retentionDays.value
    retentionDays.value = days
    try {
      retentionDays.value = (await api.setTrashRetention({ days })).days
      error.value = null
    } catch (e) {
      retentionDays.value = previous
      error.value = (e as Error).message
    }
  }

  /**
   * The four actions, each reloading the listing afterwards and reporting whether it landed. They
   * return false rather than throwing: every caller's response to a failure is the same — leave the
   * row where it is and show `error` — and a 409 here is an ordinary answer (a name already taken,
   * a measurement still in the trash), not an exception.
   */
  async function restoreType(id: number): Promise<boolean> {
    return run(() => api.restoreTrashedMeasurementType(id))
  }

  async function restoreResult(id: number): Promise<boolean> {
    return run(() => api.restoreTrashedMeasurement(id))
  }

  async function purgeType(id: number): Promise<boolean> {
    return run(() => api.purgeTrashedMeasurementType(id))
  }

  async function purgeResult(id: number): Promise<boolean> {
    return run(() => api.purgeTrashedMeasurement(id))
  }

  async function empty(): Promise<boolean> {
    return run(() => api.emptyTrash().then(() => undefined))
  }

  async function run(action: () => Promise<void>): Promise<boolean> {
    let failure: string | null = null
    try {
      await action()
    } catch (e) {
      failure = (e as Error).message
    }

    // Reloaded even on failure: a refusal is often about state that has moved on — someone renamed
    // the measurement in another tab — and a stale list would keep offering the same refusal.
    await load()

    // After the reload, not before it: `load` clears `error` on success, so setting the message
    // first would have the refusal wiped by the very reload meant to explain it. The 409 then
    // vanished and the row simply sat there, which is what useTrash.test.ts pins.
    error.value = failure
    return failure === null
  }

  return {
    open,
    listing,
    groups,
    count,
    loading,
    error,
    retentionDays,
    openTrash,
    closeTrash,
    load,
    refreshCount,
    loadRetention,
    setRetentionDays,
    restoreType,
    restoreResult,
    purgeType,
    purgeResult,
    empty,
  }
}
