<script setup lang="ts">
/**
 * Changes an already-imported simulation's analysis settings and recomputes its spectrum from the
 * samples stored with it — no file, no re-import.
 *
 * <p>That is the point of storing the samples at all: an LTspice `.raw` is overwritten by the next
 * run and deleted without warning, so the window, the FFT size, the level offset, the band and the
 * start time are applied on the way to the stored spectrum rather than baked into the waveform.
 * This dialog is the one place that applies them again.</p>
 *
 * <p>Two things it deliberately cannot change, because neither is in the samples. <b>Which traces
 * were imported</b> — the unselected columns were discarded, so another node means importing the
 * file again. And <b>a start time earlier than the samples' own</b>: they begin where the import
 * left them, so the start can move forward into them and never back out. Both are stated on the
 * form rather than left to be discovered as a refusal.</p>
 */
import { computed, reactive, ref, watch } from 'vue'
import { api } from '../api/client'
import {
  parseStoredSimulation,
  samplesStartS,
  simulationFit,
  SIMULATION_FFT_SIZES,
  SIMULATION_WINDOWS,
  type StoredSimulation,
} from '../measurements/simulationAnalysis'
import { useManual } from '../manual/useManual'
import BusyNote from './BusyNote.vue'
import InlineCodeText from './InlineCodeText.vue'

const props = defineProps<{ open: boolean; resultId: number | null }>()
const emit = defineEmits<{ close: []; reanalyzed: [resultId: number] }>()

// Same page as the import dialog links to; "Changing the settings afterwards" on it is about
// exactly this form.
const { openManual } = useManual()

const stored = ref<StoredSimulation | null>(null)
const resultName = ref('')
const error = ref<string | null>(null)
const busy = ref(false)

const form = reactive({
  startTimeMs: 0,
  levelOffsetDb: 0,
  fftSize: 8192,
  window: 'Hann' as string,
  measurementLoFreqHz: 20,
  measurementHiFreqHz: 20000,
  periodicRecord: true,
})

// The settings come from the result's own as-run params_json rather than from the measurement
// type's current ones: a stored result has to say what produced it, and the type may well have
// been edited since.
watch(
  () => [props.open, props.resultId] as const,
  async ([open, resultId]) => {
    if (!open || resultId === null) return
    stored.value = null
    error.value = null
    busy.value = true
    try {
      const detail = await api.getMeasurement(resultId)
      const found = parseStoredSimulation(detail.paramsJson)
      if (!found) {
        error.value =
          'This result carries no stored simulation settings, so it cannot be re-analysed. ' +
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
  form.startTimeMs = s.startTimeS * 1e3
  form.levelOffsetDb = s.levelOffsetDb
  form.fftSize = s.fftSize
  form.window = s.window
  form.measurementLoFreqHz = s.measurementLoFreqHz
  form.measurementHiFreqHz = s.measurementHiFreqHz
  form.periodicRecord = s.periodicRecord
}

/** Where the stored samples begin — the earliest this result can be analysed from. */
const earliestMs = computed(() => (stored.value ? samplesStartS(stored.value) * 1e3 : 0))

const startTooEarly = computed(
  // Half a millisecond of slack so the prefilled figure, which is the samples' own start rounded
  // for display, is never read as asking for less than there is.
  () => stored.value !== null && form.startTimeMs < earliestMs.value - 0.0005,
)

const fit = computed(() =>
  simulationFit({
    endTimeS: stored.value?.source.sourceEndS ?? 0,
    startTimeS: form.startTimeMs / 1e3,
    nativeRateHz: stored.value?.source.nativeRateHz ?? 0,
    fftSize: form.fftSize,
    measurementLoFreqHz: form.measurementLoFreqHz,
    measurementHiFreqHz: form.measurementHiFreqHz,
  }),
)

const unit = computed(() => stored.value?.source.leftUnit ?? 'V')

/** Whether anything on the form differs from what the result already holds. */
const changed = computed(() => {
  const s = stored.value
  if (!s) return false
  return (
    Math.abs(form.startTimeMs - s.startTimeS * 1e3) > 1e-9 ||
    form.levelOffsetDb !== s.levelOffsetDb ||
    form.fftSize !== s.fftSize ||
    form.window !== s.window ||
    form.measurementLoFreqHz !== s.measurementLoFreqHz ||
    form.measurementHiFreqHz !== s.measurementHiFreqHz ||
    form.periodicRecord !== s.periodicRecord
  )
})

const canApply = computed(
  () =>
    stored.value !== null &&
    !busy.value &&
    !startTooEarly.value &&
    !fit.value.fftTooLarge &&
    fit.value.usableS > 0 &&
    form.measurementLoFreqHz > 0 &&
    form.measurementHiFreqHz > form.measurementLoFreqHz,
)

function fmtRate(hz: number): string {
  return hz >= 1e6 ? `${(hz / 1e6).toFixed(3)} MHz` : `${(hz / 1e3).toFixed(1)} kHz`
}

async function submit() {
  if (!canApply.value || props.resultId === null) return
  busy.value = true
  error.value = null
  try {
    await api.reanalyzeSimulation(props.resultId, {
      startTimeS: form.startTimeMs / 1e3,
      levelOffsetDb: form.levelOffsetDb,
      fftSize: form.fftSize,
      window: form.window,
      measurementLoFreqHz: form.measurementLoFreqHz,
      measurementHiFreqHz: form.measurementHiFreqHz,
      periodicRecord: form.periodicRecord,
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
      <h2 class="mb-1 text-base font-semibold">Re-analyse simulation</h2>
      <p class="mb-4 text-xs text-neutral-500 dark:text-neutral-400">
        Recomputes this result's spectrum from the samples stored with it. The file is not needed
        and is not read — only the settings below change.
      </p>

      <p v-if="error" class="mb-3 rounded border border-red-300 bg-red-50 p-2 text-xs text-red-700 dark:border-red-800 dark:bg-red-950 dark:text-red-300">
        <InlineCodeText :text="error" />
      </p>

      <template v-if="stored">
        <!-- What is fixed: everything that lives in the samples rather than in the settings. -->
        <div class="mb-4 rounded border border-neutral-200 p-3 text-xs dark:border-neutral-800">
          <p class="truncate font-medium" :title="resultName">{{ resultName }}</p>
          <dl class="mt-2 grid grid-cols-2 gap-x-6 gap-y-1 sm:grid-cols-3">
            <div>
              <dt class="mr-1 inline text-neutral-500">Traces</dt>
              <dd class="inline">
                {{ stored.source.leftTrace }} ({{ stored.source.leftUnit }})<template v-if="stored.source.rightTrace">
                  + {{ stored.source.rightTrace }} ({{ stored.source.rightUnit }})</template>
              </dd>
            </div>
            <div>
              <dt class="mr-1 inline text-neutral-500">Samples</dt>
              <dd class="inline">
                {{ earliestMs.toFixed(2) }}–{{ (stored.source.sourceEndS * 1e3).toFixed(2) }} ms
              </dd>
            </div>
            <div>
              <dt class="mr-1 inline text-neutral-500">Simulator rate</dt>
              <dd class="inline">{{ fmtRate(stored.source.nativeRateHz) }}</dd>
            </div>
            <div v-if="stored.removedDcV && stored.removedDcV.length > 0" class="sm:col-span-3">
              <dt class="mr-1 inline text-neutral-500">Operating point removed</dt>
              <dd class="inline">
                {{ stored.removedDcV.map((v) => `${v.toPrecision(6)} ${unit}`).join(' + ') }}
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
            Analyse from (ms)
            <input
              v-model.number="form.startTimeMs"
              type="number"
              step="0.1"
              :min="earliestMs"
              class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            />
            <span class="text-[11px]" :class="startTooEarly ? 'text-red-700 dark:text-red-400' : 'text-neutral-500 dark:text-neutral-400'">
              <template v-if="startTooEarly">
                The stored samples start at {{ earliestMs.toFixed(2) }} ms. Anything earlier was not
                imported — import the file again from an earlier start if you need it.
              </template>
              <template v-else>
                Moves the record's start forward into the stored samples, which begin at
                {{ earliestMs.toFixed(2) }} ms. It cannot go back past that.
              </template>
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Level offset (dB)
            <input
              v-model.number="form.levelOffsetDb"
              type="number"
              step="0.1"
              class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            />
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              If the bench sees this node through a divider or a pad. Never baked into the samples,
              which is why it can be corrected here.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            FFT size
            <select v-model.number="form.fftSize" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option v-for="n in SIMULATION_FFT_SIZES" :key="n" :value="n" :disabled="n > fit.maxFftSize">
                {{ n }}{{ n > fit.maxFftSize ? ' — too large for these samples' : '' }}
              </option>
            </select>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Window
            <select v-model="form.window" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option v-for="w in SIMULATION_WINDOWS" :key="w" :value="w">{{ w }}</option>
            </select>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Measurement band (Hz)
            <div class="flex items-center gap-2">
              <input v-model.number="form.measurementLoFreqHz" type="number" class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
              <span class="text-neutral-400">–</span>
              <input v-model.number="form.measurementHiFreqHz" type="number" class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
            </div>
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              What THD+N, SNR and the noise figures integrate over.
            </span>
          </label>
        </div>

        <label class="mt-3 flex items-start gap-2 text-sm">
          <input v-model="form.periodicRecord" type="checkbox" class="mt-0.5 h-4 w-4 rounded border-neutral-400 dark:border-neutral-600" />
          <span>
            Lock the record to whole cycles
            <span class="block text-[11px] text-neutral-500 dark:text-neutral-400">
              Shortens the record by up to one cycle of the detected tone so it holds a whole number
              of them, putting every harmonic exactly on a bin. Leave on unless the stimulus is
              noise or a multi-tone.
            </span>
          </span>
        </label>

        <!-- The arithmetic, which is the thing to understand about this measurement -->
        <div class="mt-4 rounded border border-neutral-200 bg-[var(--surface-control)] p-3 text-xs dark:border-neutral-800">
          <p>
            <span class="text-neutral-500">{{ (fit.usableS * 1e3).toFixed(2) }} ms analysed</span>
            →
            <span class="font-medium">{{ fit.binWidthHz.toFixed(2) }} Hz bins</span>
            <span class="text-neutral-500"> at {{ fmtRate(fit.analysisRateHz) }}</span>
          </p>
          <p class="mt-1 text-neutral-500 dark:text-neutral-400">
            Resolution is 1 ÷ the analysed length, so the FFT size cannot improve it — a larger one
            only raises the rate and computes more empty spectrum above the band. Only a longer
            simulation gives finer bins, and that needs a fresh import.
          </p>
          <p
            v-if="fit.resolution !== 'ok'"
            class="mt-1"
            :class="fit.resolution === 'tooCoarse' ? 'text-red-700 dark:text-red-400' : 'text-amber-700 dark:text-amber-400'"
          >
            <strong v-if="fit.resolution === 'tooCoarse'">Too coarse for this band.</strong>
            <strong v-else>Coarse at the bottom of the band.</strong>
            {{ form.measurementLoFreqHz }} Hz is only {{ fit.binsBelowBandEdge.toFixed(1) }} bins in at
            {{ fit.binWidthHz.toFixed(2) }} Hz resolution. Nothing here can improve that — it needs
            at least {{ fit.neededMs.toFixed(0) }} ms simulated.
          </p>
          <p v-if="fit.nyquistTooLow" class="mt-1 text-amber-700 dark:text-amber-400">
            At this FFT size the spectrum reaches only {{ (fit.nyquistHz / 1e3).toFixed(1) }} kHz,
            below the top of the measurement band. Raise the FFT size.
          </p>
          <p v-if="fit.fftTooLarge" class="mt-1 text-red-700 dark:text-red-400">
            {{ Math.floor(fit.usableS * stored.source.nativeRateHz) }} samples are
            available from here, so {{ fit.maxFftSize }} is the largest usable FFT size.
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
          <button
            type="button"
            class="flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm btn-action"
            title="Importing a simulation: what each setting does, and which of them can be changed after an import."
            @click="openManual('simulation-import')"
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
              <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
              <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2Z" />
            </svg>
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
