<script setup lang="ts">
/**
 * Imports a simulator's `.ac` sweep as a frequency-response result, so a simulated Bode plot can
 * be overlaid on a measured one.
 *
 * <p>Its own dialog rather than a mode on `SimulationImportDialog`, for the reason the stored
 * parameter records are separate: the two forms disagree about nearly every field. A transient run
 * is analysed with an FFT size, a window, a start time and whole-cycle locking, none of which exist
 * here; a sweep is analysed over a band with a bandwidth reference, which mean nothing there. What
 * they share is picking a file, which is twenty lines.</p>
 */
import { computed, reactive, ref, watch } from 'vue'
import { api, type AcSimulationFileInfo } from '../api/client'
import {
  acSimulationFit,
  MAX_REFERENCE_HZ,
  referenceBandError,
  sweepLabel,
} from '../measurements/acSimulationAnalysis'
import { useManual } from '../manual/useManual'
import { useErrorScroll } from '../composables/useErrorScroll'
import FileBrowserDialog from './FileBrowserDialog.vue'
import BusyNote from './BusyNote.vue'
import InlineCodeText from './InlineCodeText.vue'

const props = defineProps<{ open: boolean; measurementTypeId: number | null }>()
const emit = defineEmits<{ close: []; imported: [resultIds: number[]] }>()

const { openManual } = useManual()

const browserOpen = ref(false)
// Where the file browser reopens: simulations for one amplifier live together, and starting over
// in the default projects folder every time means retyping the path.
const lastPath = ref<string | null>(null)
const info = ref<AcSimulationFileInfo | null>(null)
const error = ref<string | null>(null)

// A refusal that renders above the fold of a long dialog is a refusal nobody sees.
useErrorScroll(error)
const busy = ref(false)

const form = reactive({
  outputTraceIndex: null as number | null,
  referenceTraceIndex: null as number | null,
  levelOffsetDb: 0,
  analysisLowHz: 20,
  analysisHighHz: 20000,
  bandwidthReference: 'BandMedian',
  bandwidthReferenceLowHz: 200,
  bandwidthReferenceHighHz: 2000,
  name: '',
  notes: '',
})

watch(
  () => props.open,
  (open) => {
    if (!open) return
    info.value = null
    error.value = null
    browserOpen.value = true
  },
)

function onBrowserClose() {
  browserOpen.value = false
  if (!info.value) emit('close')
}

async function inspect(path: string) {
  browserOpen.value = false
  busy.value = true
  error.value = null
  try {
    lastPath.value = path
    const found = await api.inspectAcSimulation(path)
    info.value = found

    // Default to something output-shaped if the circuit names one, then to the first voltage —
    // the first listed trace is whatever the simulator happened to put first, which on the
    // reference amplifier is an internal plate node.
    const voltages = found.traces.filter((t) => t.unit === 'V')
    const named = voltages.find((t) => /^v\((out|output|spk|speaker|load|vout)\d*\)$/i.test(t.name))
    form.outputTraceIndex = (named ?? voltages[0] ?? found.traces[0])?.index ?? null

    // And an input-shaped one as the reference, which is what makes the result the circuit's own
    // gain rather than a claim about the simulator's source amplitude.
    const input = voltages.find((t) => /^v\((in|input|src|source|vin)\d*\)$/i.test(t.name))
    form.referenceTraceIndex = input && input.index !== form.outputTraceIndex ? input.index : null
    form.name = ''

    // Every sweep of a stepped run, because the comparison is the whole reason for stepping one.
    chosenSweeps.value = found.sweeps.map((sw) => sw.index)
  } catch (e) {
    info.value = null
    chosenSweeps.value = []
    error.value = (e as Error).message
    // Deliberately keeps lastPath: a refused file is almost always in the folder the usable one is
    // in, and the first thing anyone does next is pick a different file from it.
  } finally {
    busy.value = false
  }
}

const outputTrace = computed(
  () => info.value?.traces.find((t) => t.index === form.outputTraceIndex) ?? null,
)
const referenceTrace = computed(
  () => info.value?.traces.find((t) => t.index === form.referenceTraceIndex) ?? null,
)

const unit = computed(() => outputTrace.value?.unit ?? 'V')

const fit = computed(() =>
  acSimulationFit({
    startFreqHz: info.value?.startFreqHz ?? 0,
    endFreqHz: info.value?.endFreqHz ?? 0,
    pointCount: info.value?.pointCount ?? 0,
    pointsPerDecade: info.value?.pointsPerDecade ?? 0,
    isLogSpaced: info.value?.isLogSpaced ?? true,
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

/**
 * Which sweeps of a `.step`ped file to import. Every one by default, which is what a user who
 * stepped a component wants to see — the point of stepping is the comparison. Empty means "all"
 * too, so a file with one sweep never touches this.
 */
const chosenSweeps = ref<number[]>([])

const sweeps = computed(() => info.value?.sweeps ?? [])
const isStepped = computed(() => sweeps.value.length > 1)

function toggleSweep(index: number, on: boolean) {
  chosenSweeps.value = on
    ? [...chosenSweeps.value, index].sort((a, b) => a - b)
    : chosenSweeps.value.filter((i) => i !== index)
}

const canImport = computed(
  () =>
    props.measurementTypeId !== null &&
    info.value !== null &&
    form.outputTraceIndex !== null &&
    (!isStepped.value || chosenSweeps.value.length > 0) &&
    !fit.value.bandInvalid &&
    bandError.value === null &&
    !busy.value,
)

function fmtHz(hz: number): string {
  if (hz >= 1e6) return `${(hz / 1e6).toFixed(2)} MHz`
  if (hz >= 1e3) return `${(hz / 1e3).toFixed(2)} kHz`
  return `${hz.toPrecision(3)} Hz`
}

/** See SimulationImportDialog's own: the wait is long enough to be worth naming. */
const busyLabel = computed(() => {
  if (info.value === null) return 'Reading the file…'
  const sweeps = chosenSweeps.value.length
  return isStepped.value ? `Importing ${sweeps} sweep${sweeps === 1 ? '' : 's'}…` : 'Importing…'
})

async function submit() {
  if (!canImport.value || props.measurementTypeId === null || !info.value) return
  busy.value = true
  error.value = null
  try {
    const result = await api.importAcSimulation(props.measurementTypeId, {
      path: info.value.path,
      outputTraceIndex: form.outputTraceIndex!,
      referenceTraceIndex: form.referenceTraceIndex,
      levelOffsetDb: form.levelOffsetDb,
      analysisLowHz: form.analysisLowHz,
      analysisHighHz: form.analysisHighHz,
      bandwidthReference: form.bandwidthReference,
      bandwidthReferenceLowHz: form.bandwidthReferenceLowHz,
      bandwidthReferenceHighHz: form.bandwidthReferenceHighHz,
      name: form.name.trim() || null,
      notes: form.notes.trim() || null,
      ...(isStepped.value ? { sweepIndices: chosenSweeps.value } : {}),
    })
    emit('imported', result.resultIds)
    emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <FileBrowserDialog
    :open="browserOpen"
    mode="open"
    extension=".raw,.qraw"
    :start-path="lastPath"
    title="Open a simulated sweep (LTspice .raw or QSpice .qraw)"
    remember-as="simulation"
    @close="onBrowserClose"
    @select="inspect"
  />

  <div
    v-if="open && !browserOpen"
    class="fixed inset-0 z-40 flex items-start justify-center overflow-y-auto bg-black/40 p-6"
    @click.self="emit('close')"
  >
    <div class="w-full max-w-3xl rounded-lg border border-neutral-200 bg-[var(--surface-panel)] p-5 shadow-xl dark:border-neutral-800">
      <h2 class="mb-1 text-base font-semibold">Import simulated response</h2>
      <p class="mb-4 text-xs text-neutral-500 dark:text-neutral-400">
        Reads an LTspice or QSpice <code>.ac</code> sweep and measures it exactly as this screen
        measures a chirp, so its corners and phase mean the same thing.
      </p>

      <p v-if="error" ref="errorBox" class="mb-3 rounded border border-red-300 bg-red-50 p-2 text-xs text-red-700 dark:border-red-800 dark:bg-red-950 dark:text-red-300">
        <InlineCodeText :text="error" />
      </p>

      <template v-if="info">
        <div class="mb-4 rounded border border-neutral-200 p-3 text-xs dark:border-neutral-800">
          <p class="truncate font-mono text-[11px] text-neutral-500 dark:text-neutral-400" :title="info.path">{{ info.path }}</p>
          <dl class="mt-2 grid grid-cols-2 gap-x-6 gap-y-1 sm:grid-cols-3">
            <div><dt class="mr-1 inline text-neutral-500">Run</dt><dd class="inline">{{ info.plotname }}</dd></div>
            <div><dt class="mr-1 inline text-neutral-500">Points</dt><dd class="inline">{{ info.pointCount }}</dd></div>
            <div>
              <dt class="mr-1 inline text-neutral-500">Sweep</dt>
              <dd class="inline">{{ fmtHz(info.startFreqHz) }}–{{ fmtHz(info.endFreqHz) }}</dd>
            </div>
            <div>
              <dt class="mr-1 inline text-neutral-500">Density</dt>
              <dd class="inline">{{ info.pointsPerDecade.toFixed(0) }}/decade</dd>
            </div>
            <div><dt class="mr-1 inline text-neutral-500">Traces</dt><dd class="inline">{{ info.traces.length }}</dd></div>
            <div v-if="isStepped">
              <dt class="mr-1 inline text-neutral-500">Sweeps</dt>
              <dd class="inline">{{ sweeps.length }} (stepped)</dd>
            </div>
          </dl>
        </div>

        <!--
          A stepped run's sweeps. Each becomes a result of its own, so this is a selection and not
          a setting: nothing here changes how a curve is analysed, only how many curves arrive.
        -->
        <fieldset v-if="isStepped" class="mb-3 rounded border border-neutral-200 p-2 dark:border-neutral-700">
          <legend class="px-1 text-xs text-neutral-500 dark:text-neutral-400">Sweeps to import</legend>
          <div class="flex flex-wrap gap-x-4 gap-y-1">
            <label v-for="sweep in sweeps" :key="sweep.index" class="flex items-center gap-1.5 text-sm">
              <input
                type="checkbox"
                :checked="chosenSweeps.includes(sweep.index)"
                @change="toggleSweep(sweep.index, ($event.target as HTMLInputElement).checked)"
              />
              <span class="font-mono text-xs">{{ sweepLabel(sweep) }}</span>
            </label>
          </div>
          <p class="mt-1 text-[11px] text-neutral-500 dark:text-neutral-400">
            Each one is stored as its own result, named after the step, so they can be shown over
            each other in the History list and deleted separately.
          </p>
        </fieldset>

        <p
          v-for="warning in info.warnings"
          :key="warning"
          class="mb-2 rounded border border-amber-300 bg-amber-50 p-2 text-xs text-amber-800 dark:border-amber-700 dark:bg-amber-950/50 dark:text-amber-300"
        >
          <InlineCodeText :text="warning" />
        </p>

        <div class="grid gap-4 sm:grid-cols-2">
          <label class="flex flex-col gap-1 text-sm">
            Output trace
            <select v-model.number="form.outputTraceIndex" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option v-for="t in info.traces" :key="t.index" :value="t.index">{{ t.name }} ({{ t.unit }})</option>
            </select>
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              The node whose response this is — usually the output.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Reference trace
            <select v-model="form.referenceTraceIndex" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option :value="null">— none —</option>
              <option v-for="t in info.traces" :key="t.index" :value="t.index">{{ t.name }} ({{ t.unit }})</option>
            </select>
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              Divide the output by this — normally the input node. That is what a bench measurement
              does, and the only way to be right if the AC source is not exactly 1 V.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Analysed band (Hz)
            <div class="flex items-center gap-2">
              <input v-model.number="form.analysisLowHz" type="number" class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
              <span class="text-neutral-400">–</span>
              <input v-model.number="form.analysisHighHz" type="number" class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
            </div>
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              Where the corners are searched for. Changeable afterwards — the whole sweep is stored.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Level offset (dB)
            <input v-model.number="form.levelOffsetDb" type="number" step="0.1" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              If the bench sees this node through a divider or a pad.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Bandwidth reference
            <select v-model="form.bandwidthReference" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option value="BandMedian">Passband median</option>
              <option value="Peak">Trace peak</option>
            </select>
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
              <template v-else>Capped at {{ MAX_REFERENCE_HZ }} Hz; the corners themselves are not.</template>
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm sm:col-span-2">
            Name
            <input
              v-model="form.name"
              type="text"
              :placeholder="outputTrace ? `${info.title.replace(/^\*?\s*/, '').split(/[\\/]/).pop()?.replace(/\.(raw|qraw|net|asc|qsch)$/i, '') ?? 'simulation'} - ${outputTrace.name}` : 'simulation'"
              class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            />
          </label>
        </div>

        <div class="mt-4 rounded border border-neutral-200 bg-[var(--surface-control)] p-3 text-xs dark:border-neutral-800">
          <p>
            <span class="text-neutral-500">{{ info.pointCount }} points over {{ fit.decades.toFixed(2) }} decades</span>
            →
            <span class="font-medium">{{ fit.stepPercent.toFixed(2) }} % between points</span>
          </p>
          <p class="mt-1 text-neutral-500 dark:text-neutral-400">
            That spacing is as closely as a corner can be located. There is no window, no FFT size
            and no smoothing: an AC sweep is already a frequency response, and every point of it is
            exact.
          </p>
          <p v-if="fit.bandInvalid" class="mt-1 text-red-700 dark:text-red-400">
            The analysed band must end above where it starts.
          </p>
          <p v-else-if="fit.bandNotCovered" class="mt-1 text-amber-700 dark:text-amber-400">
            The sweep covers {{ fmtHz(info.startFreqHz) }}–{{ fmtHz(info.endFreqHz) }}, which does not
            reach across the band being analysed. Corners outside it read as clamped.
          </p>
          <p v-if="referenceTrace && outputTrace && referenceTrace.unit !== outputTrace.unit" class="mt-1 text-amber-700 dark:text-amber-400">
            The two traces are in different units ({{ outputTrace.unit }} and {{ referenceTrace.unit }}),
            so their ratio is a transimpedance or an admittance rather than a gain.
          </p>
          <p v-if="unit !== 'V'" class="mt-1 text-amber-700 dark:text-amber-400">
            This is a current trace, in amps. Without a reference its levels are dB relative to 1 A.
          </p>
        </div>

        <details class="mt-4 text-xs">
          <summary class="cursor-pointer text-neutral-600 dark:text-neutral-300">Simulating for an import</summary>
          <div class="mt-2 space-y-2 text-neutral-600 dark:text-neutral-400">
            <p>
              Add <code class="rounded bg-[var(--surface-control)] px-1">.ac dec 100 1 1Meg</code> —
              <em>dec</em> for logarithmic spacing, 100 points per decade, then the range. Use
              <strong>dec</strong> rather than <strong>lin</strong>: a linear sweep puts almost every
              point in its top decade, so a log chart draws the bottom of the band from a handful of
              them.
            </p>
            <p>
              <code class="rounded bg-[var(--surface-control)] px-1">.options plotwinsize=0</code> and
              <code class="rounded bg-[var(--surface-control)] px-1">numdgt=7</code> do nothing here —
              they are about transient waveforms. An AC run is already exact.
            </p>
            <p>
              Pick the <strong>input node as the reference</strong> unless the source is a stiff
              <code class="rounded bg-[var(--surface-control)] px-1">AC 1</code>. Dividing is what
              makes the answer the circuit's gain rather than a reading of the source.
            </p>
            <p>
              <strong>An AC analysis is a small-signal linearisation about the operating point.</strong>
              It says nothing about distortion, clipping or slew limiting: a circuit that misbehaves
              badly at real levels still shows a perfectly clean AC response. That is what the
              transient import on an FFT measurement is for.
            </p>
            <p>
              Both simulators can be told to delete the result file when they close. Import before
              closing the simulator, or turn that off.
            </p>
          </div>
        </details>

        <p class="mt-3 text-[11px] text-neutral-500 dark:text-neutral-400">
          Everything above except the traces can be changed afterwards — the whole sweep is stored
          with the result, so <strong>Re-analyse</strong> in History details recomputes the corners
          without the file.
        </p>
      </template>

      <BusyNote v-else-if="!error" :label="busyLabel" class="py-1" />

      <BusyNote v-if="busy && info" :label="busyLabel" class="mt-4" />

      <div class="mt-5 flex justify-between gap-2">
        <div class="flex gap-2">
          <button type="button" class="rounded border px-3 py-1.5 text-sm btn-action" @click="browserOpen = true">
            Choose another file…
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
            :disabled="!canImport"
            @click="submit"
          >
            Import
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
