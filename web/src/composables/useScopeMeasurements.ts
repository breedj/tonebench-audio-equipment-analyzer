import { computed, ref, shallowRef } from 'vue'
import { useProjects } from './useProjects'
import {
  DEFAULT_SCOPE_MEASUREMENTS,
  parseScopeMeasurementIds,
  scopeMeasurementsNeedPair,
} from '../scope/measurementCatalogue'
import {
  addStatSample,
  newStatAccumulator,
  statOf,
  type ScopeMeasurementStat,
  type ScopeStatAccumulator,
} from '../scope/measurementStats'
import type { ScopeMeasurementRow } from '../scope/measurementRows'

/**
 * Which measurements the oscilloscope screen is watching, and their statistics.
 *
 * <p>A module-level singleton, like <code>useMetricSelection</code> and for the same reasons: the
 * table under the chart, the picker dialog, the Measurements panel's own controls and App.vue's
 * save and load flow all have to see one selection, and a second copy of it is how two of them come
 * to disagree.</p>
 *
 * <p><b>The selection is stored with the measurement; the statistics never are.</b> Which figures a
 * reader is watching is a setting like any other display preference. What those figures have
 * averaged to over the last ninety records is a history of a run — it cannot be re-derived from
 * anything stored, it belongs to the records that were looked at rather than to the one that was
 * saved, and it is thrown away the moment a capture setting makes the records stop being
 * comparable. The persistence picture is kept and discarded on exactly the same grounds.</p>
 */

const selected = ref<string[]>([...DEFAULT_SCOPE_MEASUREMENTS])
const statistics = ref(false)

/**
 * One accumulator per row key, outside Vue's reactivity.
 *
 * <p>A plain Map rather than a reactive one: it is written on every arriving record, for every row,
 * and making each accumulator reactive would re-run every computed that touches any of them several
 * times a second to no purpose. <code>version</code> below is the one reactive handle, bumped once
 * per record after the whole set has been folded in — so a table reads a consistent snapshot rather
 * than one row updated at a time.</p>
 */
const accumulators = new Map<string, ScopeStatAccumulator>()
const version = ref(0)

/**
 * What the accumulators are of.
 *
 * <p>Held beside them rather than compared by the caller, so the reset cannot be forgotten at a new
 * call site: handing <code>accumulate</code> a signature that is not the one on file starts the
 * count again, which is the only way a stale figure can be shown.</p>
 */
const signature = shallowRef('')

export function useScopeMeasurements() {
  /** A user edit, so it marks the measurement dirty — persisted on the next save like any setting. */
  function setSelected(ids: string[]) {
    selected.value = parseScopeMeasurementIds(ids) ?? []
    useProjects().markDirty()
  }

  function remove(id: string) {
    setSelected(selected.value.filter((other) => other !== id))
  }

  function setStatistics(on: boolean) {
    statistics.value = on
    useProjects().markDirty()
  }

  /**
   * Syncs from a loaded measurement's stored selection. Not a user edit, so it never marks dirty.
   *
   * <p>An absent list is this build's default rather than an empty table — a measurement saved
   * before this feature existed had no selection to express, and a screen that shows nothing under
   * its chart reads as a feature that failed rather than as one nobody has set up. An explicitly
   * empty list is kept empty, which is a reader who cleared it.</p>
   */
  function applyLoaded(ids: string[] | null | undefined, statsOn: boolean | null | undefined) {
    const parsed = ids === null || ids === undefined ? null : parseScopeMeasurementIds(ids)
    selected.value = parsed ?? [...DEFAULT_SCOPE_MEASUREMENTS]
    statistics.value = statsOn === true
    // Deliberately NOT resetStats(). This runs on every re-apply of the stored display, and a save
    // is one of them: the autosave rewrites params_json, which the screen's own watcher reads back,
    // so resetting here threw the count away every time a display control was touched -- measured,
    // 29 records to 0 on one tick of the Residual box. The statistics belong to the measurement, so
    // the switch between measurements is what clears them; App.vue watches the open one.
  }

  /** Throws the accumulated statistics away and starts counting again from the next record. */
  function resetStats() {
    accumulators.clear()
    version.value += 1
  }

  /**
   * Folds one record's rows in.
   *
   * <p>Called once per arriving record, with the rows exactly as the table draws them — so the
   * Value column and the statistics beside it can never come from two different captures, and a
   * reading the record did not have is simply not counted rather than entering the mean as a
   * zero.</p>
   *
   * <p>A row whose key has gone (a measurement unticked, a channel taken off the chart) keeps its
   * accumulator, so putting it back resumes the count instead of starting a new one. They are all
   * cleared together whenever the signature moves, which is the only thing that makes an old count
   * wrong rather than merely paused.</p>
   */
  function accumulate(rows: readonly ScopeMeasurementRow[], forSignature: string) {
    if (forSignature !== signature.value) {
      signature.value = forSignature
      accumulators.clear()
    }

    for (const row of rows) {
      let acc = accumulators.get(row.key)
      if (!acc) {
        acc = newStatAccumulator()
        accumulators.set(row.key, acc)
      }
      addStatSample(acc, row.value)
    }

    version.value += 1
  }

  /** One row's statistics, or null before it has read anything. */
  function stat(key: string): ScopeMeasurementStat | null {
    // Read so this is re-evaluated when a record lands; the Map itself is not reactive.
    void version.value
    return statOf(accumulators.get(key))
  }

  /** How many records the statistics have counted — the largest count any row has reached. */
  const statCount = computed(() => {
    void version.value
    let most = 0
    for (const acc of accumulators.values()) if (acc.count > most) most = acc.count
    return most
  })

  /** Whether anything chosen needs the X–Y figure fetched: the Channel delay group reads it. */
  const needsPair = computed(() => scopeMeasurementsNeedPair(selected.value))

  return {
    selected,
    statistics,
    statsVersion: version,
    setSelected,
    remove,
    setStatistics,
    applyLoaded,
    resetStats,
    accumulate,
    stat,
    statCount,
    needsPair,
  }
}
