<script setup lang="ts">
/**
 * Changes an imported `.ac` sweep's analysis settings and recomputes its curves from the rows
 * stored with it — no file, no re-import.
 *
 * <p>A sweep is stored at full resolution (nothing smooths it and nothing decimates it), so the
 * rows <em>are</em> the curve and a re-analysis is a lossless re-read of them. That is why the band
 * here is a <strong>search window rather than a slice</strong>: narrowing it and widening it again
 * returns the corners it started with.</p>
 *
 * <p>Two things it deliberately cannot change. <strong>Which traces were imported</strong> — the
 * other columns of the file were discarded. And there is <strong>no smoothing</strong>: a simulated
 * curve is noiseless, so there is nothing to smooth, and not smoothing is exactly what keeps it
 * re-analysable.</p>
 */
import { computed, reactive, ref, watch } from 'vue'
import { api } from '../api/client'
import {
  acSimulationFit,
  hasReferenceCurve,
  MAX_REFERENCE_HZ,
  parseStoredAcSimulation,
  referenceBandError,
  type StoredAcSimulation,
} from '../measurements/acSimulationAnalysis'
import { useManual } from '../manual/useManual'
import BusyNote from './BusyNote.vue'
import InlineCodeText from './InlineCodeText.vue'

const props = defineProps<{ open: boolean; resultId: number | null }>()
const emit = defineEmits<{ close: []; reanalyzed: [resultId: number] }>()

const { openManual } = useManual()

const stored = ref<StoredAcSimulation | null>(null)
const resultName = ref('')
const error = ref<string | null>(null)
const busy = ref(false)

const form = reactive({
  levelOffsetDb: 0,
  analysisLowHz: 20,
  analysisHighHz: 20000,
  bandwidthReference: 'BandMedian',
  bandwidthReferenceLowHz: 200,
  bandwidthReferenceHighHz: 2000,
  rightChannelIsReference: true,
})

watch(
  () => [props.open, props.resultId] as const,
  async ([open, resultId]) => {
    if (!open || resultId === null) return
    stored.value = null
    error.value = null
    busy.value = true
    try {
      const detail = await api.getMeasurement(resultId)
      const found = parseStoredAcSimulation(detail.paramsJson)
      if (!found) {
        error.value =
          'This result did not come from an imported .ac sweep, so it cannot be re-analysed. ' +
          'Only imported simulations can.'
        return
      }
      resultName.value = detail.name
      stored.value = found
      reset()
    } catch (e) {
      error.value = (e as Error).message
    } finally {
      busy.value = false
    }
  },
  { immediate: true },
)

/** Back to what the result is stored at, which is also what the form opens on. */
function reset() {
  const s = stored.value
  if (!s) return
  form.levelOffsetDb = s.levelOffsetDb
  form.analysisLowHz = s.analysisLowHz
  form.analysisHighHz = s.analysisHighHz
  form.bandwidthReference = s.bandwidthReference
  form.bandwidthReferenceLowHz = s.bandwidthReferenceLowHz
  form.bandwidthReferenceHighHz = s.bandwidthReferenceHighHz
  form.rightChannelIsReference = s.rightChannelIsReference
}

const canSwapReference = computed(() => stored.value !== null && hasReferenceCurve(stored.value))

const fit = computed(() =>
  acSimulationFit({
    startFreqHz: stored.value?.source.startFreqHz ?? 0,
    endFreqHz: stored.value?.source.endFreqHz ?? 0,
    pointCount: stored.value?.source.pointCount ?? 0,
    pointsPerDecade: stored.value?.source.pointsPerDecade ?? 0,
    isLogSpaced: true,
    analysisLowHz: form.analysisLowHz,
    analysisHighHz: form.analysisHighHz,
  }),
)

const bandError = computed(() =>
  referenceBandError(
    form.bandwidthReference,
    form.bandwidthReferenceLowHz,
    form.bandwidthReferenceHighHz,
  ),
)

const changed = computed(() => {
  const s = stored.value
  if (!s) return false
  return (
    form.levelOffsetDb !== s.levelOffsetDb ||
    form.analysisLowHz !== s.analysisLowHz ||
    form.analysisHighHz !== s.analysisHighHz ||
    form.bandwidthReference !== s.bandwidthReference ||
    form.bandwidthReferenceLowHz !== s.bandwidthReferenceLowHz ||
    form.bandwidthReferenceHighHz !== s.bandwidthReferenceHighHz ||
    form.rightChannelIsReference !== s.rightChannelIsReference
  )
})

const canApply = computed(
  () => stored.value !== null && !busy.value && !fit.value.bandInvalid && bandError.value === null,
)

function fmtHz(hz: number): string {
  if (hz >= 1e6) return `${(hz / 1e6).toFixed(2)} MHz`
  if (hz >= 1e3) return `${(hz / 1e3).toFixed(2)} kHz`
  return `${hz.toPrecision(3)} Hz`
}

async function submit() {
  if (!canApply.value || props.resultId === null) return
  busy.value = true
  error.value = null
  try {
    await api.reanalyzeAcSimulation(props.resultId, {
      levelOffsetDb: form.levelOffsetDb,
      analysisLowHz: form.analysisLowHz,
      analysisHighHz: form.analysisHighHz,
      bandwidthReference: form.bandwidthReference,
      bandwidthReferenceLowHz: form.bandwidthReferenceLowHz,
      bandwidthReferenceHighHz: form.bandwidthReferenceHighHz,
      rightChannelIsReference: form.rightChannelIsReference,
    })
    emit('reanalyzed', props.resultId)
    emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div
    v-if="open"
    class="fixed inset-0 z-40 flex items-start justify-center overflow-y-auto bg-black/40 p-6"
    @click.self="emit('close')"
  >
    <div class="w-full max-w-3xl rounded-lg border border-neutral-200 bg-[var(--surface-panel)] p-5 shadow-xl dark:border-neutral-800">
      <h2 class="mb-1 text-base font-semibold">Re-analyse simulated response</h2>
      <p class="mb-4 text-xs text-neutral-500 dark:text-neutral-400">
        Recomputes this result's corners and readings from the curve stored with it. The file is
        not needed and is not read — only the settings below change.
      </p>

      <p v-if="error" class="mb-3 rounded border border-red-300 bg-red-50 p-2 text-xs text-red-700 dark:border-red-800 dark:bg-red-950 dark:text-red-300">
        <InlineCodeText :text="error" />
      </p>

      <template v-if="stored">
        <!-- What is fixed: everything that lives in the stored curve rather than in the settings. -->
        <div class="mb-4 rounded border border-neutral-200 p-3 text-xs dark:border-neutral-800">
          <p class="truncate font-medium" :title="resultName">{{ resultName }}</p>
          <dl class="mt-2 grid grid-cols-2 gap-x-6 gap-y-1 sm:grid-cols-3">
            <div>
              <dt class="mr-1 inline text-neutral-500">Output</dt>
              <dd class="inline">{{ stored.source.outputTrace }} ({{ stored.source.outputUnit }})</dd>
            </div>
            <div>
              <dt class="mr-1 inline text-neutral-500">Reference</dt>
              <dd class="inline">
                {{ stored.source.referenceTrace ? `${stored.source.referenceTrace} (${stored.source.referenceUnit})` : '— none —' }}
              </dd>
            </div>
            <div>
              <dt class="mr-1 inline text-neutral-500">Sweep</dt>
              <dd class="inline">
                {{ fmtHz(stored.source.startFreqHz) }}–{{ fmtHz(stored.source.endFreqHz) }}
              </dd>
            </div>
            <div>
              <dt class="mr-1 inline text-neutral-500">Points</dt>
              <dd class="inline">
                {{ stored.source.pointCount }} ({{ stored.source.pointsPerDecade.toFixed(0) }}/decade)
              </dd>
            </div>
          </dl>
          <p class="mt-2 text-[11px] text-neutral-500 dark:text-neutral-400">
            The traces cannot be changed here — the other columns of the file were discarded at
            import. Import the file again to look at a different node.
          </p>
        </div>

        <div class="grid gap-4 sm:grid-cols-2">
          <label class="flex flex-col gap-1 text-sm">
            Analysed band (Hz)
            <div class="flex items-center gap-2">
              <input v-model.number="form.analysisLowHz" type="number" class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
              <span class="text-neutral-400">–</span>
              <input v-model.number="form.analysisHighHz" type="number" class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
            </div>
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              Where the corners are searched for. A window, not a crop — the whole sweep stays
              stored, so widening this again brings the original corners back.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Level offset (dB)
            <input v-model.number="form.levelOffsetDb" type="number" step="0.1" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              If the bench sees this node through a divider or a pad. Applied to the gain curve, never
              baked into it.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Bandwidth reference
            <select v-model="form.bandwidthReference" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option value="BandMedian">Passband median</option>
              <option value="Peak">Trace peak</option>
            </select>
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              What the ±1 dB and −3 dB corners are measured down from.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Reference band (Hz)
            <div class="flex items-center gap-2">
              <input
                v-model.number="form.bandwidthReferenceLowHz"
                type="number"
                :disabled="form.bandwidthReference !== 'BandMedian'"
                class="w-full rounded border border-neutral-300 px-2 py-1 disabled:opacity-40 dark:border-neutral-700"
              />
              <span class="text-neutral-400">–</span>
              <input
                v-model.number="form.bandwidthReferenceHighHz"
                type="number"
                :disabled="form.bandwidthReference !== 'BandMedian'"
                class="w-full rounded border border-neutral-300 px-2 py-1 disabled:opacity-40 dark:border-neutral-700"
              />
            </div>
            <span class="text-[11px]" :class="bandError ? 'text-red-700 dark:text-red-400' : 'text-neutral-500 dark:text-neutral-400'">
              <template v-if="bandError">{{ bandError }}</template>
              <template v-else>
                Put it somewhere the response is flat. Capped at
                {{ MAX_REFERENCE_HZ }} Hz, which is the analyzer's own ceiling — the
                corners themselves are not.
              </template>
            </span>
          </label>
        </div>

        <label v-if="canSwapReference" class="mt-3 flex items-start gap-2 text-sm">
          <input v-model="form.rightChannelIsReference" type="checkbox" class="mt-0.5 h-4 w-4 rounded border-neutral-400 dark:border-neutral-600" />
          <span>
            Divide {{ stored.source.outputTrace }} by {{ stored.source.referenceTrace }}
            <span class="block text-[11px] text-neutral-500 dark:text-neutral-400">
              Unticked, the division runs the other way and the curve is its own reciprocal. Only
              offered because this result imported both curves; one on its own has nothing to
              divide by.
            </span>
          </span>
        </label>

        <div class="mt-4 rounded border border-neutral-200 bg-[var(--surface-control)] p-3 text-xs dark:border-neutral-800">
          <p class="text-neutral-500 dark:text-neutral-400">
            {{ stored.source.pointCount }} points over
            {{ fit.decades.toFixed(2) }} decades — {{ fit.stepPercent.toFixed(2) }} % between them,
            which is as closely as a corner can be located. There is no smoothing setting: a
            simulated curve has no noise to smooth, and leaving it alone is what lets this be
            re-analysed at all.
          </p>
          <p v-if="fit.bandInvalid" class="mt-1 text-red-700 dark:text-red-400">
            The analysed band must end above where it starts.
          </p>
          <p v-else-if="fit.bandNotCovered" class="mt-1 text-amber-700 dark:text-amber-400">
            The sweep covers {{ fmtHz(stored.source.startFreqHz) }}–{{ fmtHz(stored.source.endFreqHz) }},
            which does not reach across this band. Corners outside the sweep read as clamped.
          </p>
        </div>
      </template>

      <p v-else-if="!error" class="text-sm text-neutral-500 dark:text-neutral-400">Reading the stored settings…</p>

      <BusyNote v-if="busy" label="Re-analysing…" class="mt-4" />

      <div class="mt-5 flex justify-between gap-2">
        <div class="flex gap-2">
          <button
            v-if="stored"
            type="button"
            class="rounded border px-3 py-1.5 text-sm btn-action disabled:cursor-not-allowed disabled:opacity-40"
            :disabled="!changed"
            @click="reset"
          >
            Back to stored
          </button>
          <button type="button" class="rounded border px-3 py-1.5 text-sm btn-action" @click="openManual('simulation-import')">
            Manual
          </button>
        </div>
        <div class="flex gap-2">
          <button type="button" class="rounded border px-3 py-1.5 text-sm btn-action" @click="emit('close')">Cancel</button>
          <button
            type="button"
            class="rounded border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm text-emerald-700 hover:bg-emerald-100 disabled:cursor-not-allowed disabled:opacity-40 dark:border-emerald-700 dark:bg-emerald-950/40 dark:text-emerald-300"
            :disabled="!canApply"
            @click="submit"
          >
            Re-analyse
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
