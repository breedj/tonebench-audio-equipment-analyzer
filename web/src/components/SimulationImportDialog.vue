<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { api, type SimulationFileInfo } from '../api/client'
import {
  simulationFit,
  SIMULATION_FFT_SIZES,
  SIMULATION_WINDOWS,
} from '../measurements/simulationAnalysis'
import { sweepLabel } from '../measurements/acSimulationAnalysis'
import { useManual } from '../manual/useManual'
import FileBrowserDialog from './FileBrowserDialog.vue'

/**
 * `sweepTarget` is set when the open measurement is a THD-vs-frequency one, where a `.step`ped run
 * becomes a single sweep rather than a result per step. Passed in rather than worked out here,
 * because which dialog opens is already decided from the kind in App.vue and two places deciding
 * it is one too many.
 */
const props = defineProps<{
  open: boolean
  measurementTypeId: number | null
  sweepTarget?: boolean
}>()
const emit = defineEmits<{ close: []; imported: [resultIds: number[]] }>()

// The manual page is the long form of the help block below -- the directives and what each does to
// the spectrum, the resolution arithmetic, what is refused and why. Its own <dialog> opens into the
// top layer, so it lands over this one and leaves the form behind it untouched, the same way the
// sound-card dialog's link does.
const { openManual } = useManual()

const browserOpen = ref(false)
// Where the file browser reopens. Choosing another file should land back in the folder the last
// one came from -- simulations for one amplifier live together, and starting over in the default
// projects folder every time means retyping the path.
const lastPath = ref<string | null>(null)
const info = ref<SimulationFileInfo | null>(null)
const error = ref<string | null>(null)
const busy = ref(false)

const form = reactive({
  leftTraceIndex: null as number | null,
  rightTraceIndex: null as number | null,
  startTimeMs: 0,
  levelOffsetDb: 0,
  fftSize: 8192,
  window: 'Hann' as string,
  measurementLoFreqHz: 20,
  measurementHiFreqHz: 20000,
  periodicRecord: true,
  name: '',
  notes: '',
})

// Opening with nothing picked goes straight to the file browser: the dialog has nothing to show
// until a file has been inspected.
watch(
  () => props.open,
  (open) => {
    if (!open) return
    info.value = null
    error.value = null
    browserOpen.value = true
  },
)

// Cancelling the file browser before anything has been inspected closes the whole dialog: there
// is nothing behind it to go back to.
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
    const found = await api.inspectSimulation(path, form.measurementHiFreqHz, form.measurementLoFreqHz)
    info.value = found
    chosenSweeps.value = found.sweeps.map((sw) => sw.index)

    // Default to something output-shaped if the circuit names one, then to the first voltage.
    // A current trace is a legitimate thing to look at but it is in amps, and a dBV chart is not
    // what anyone reaches for first; and the first voltage is simply whatever LTspice listed
    // first, which on the reference amplifier is a 284 V plate node.
    const voltages = found.traces.filter((t) => t.unit === 'V')
    const named = voltages.find((t) => /^v\((out|output|spk|speaker|load|vout)\d*\)$/i.test(t.name))
    form.leftTraceIndex = (named ?? voltages[0] ?? found.traces[0])?.index ?? null
    form.rightTraceIndex = null
    form.startTimeMs = found.startTimeS * 1e3
    form.fftSize = found.suggestedFftSize
    form.name = ''
  } catch (e) {
    info.value = null
    error.value = (e as Error).message
    // Deliberately keeps lastPath: a refused file is almost always in the folder the usable one
    // is in, and the first thing anyone does next is pick a different file from it.
  } finally {
    busy.value = false
  }
}

const leftTrace = computed(() => info.value?.traces.find((t) => t.index === form.leftTraceIndex) ?? null)
const rightTrace = computed(() => info.value?.traces.find((t) => t.index === form.rightTraceIndex) ?? null)

/** Volts unless a current trace was picked — the unit travels with the result. */
const unit = computed(() => leftTrace.value?.unit ?? 'V')

/**
 * Two channels in different units on one axis — V(out) on the left and a branch current on the
 * right, say. Each is measured correctly and the chart can only label one of them, so it is worth
 * saying before the import rather than leaving it to be noticed in the readouts.
 */
const unitsDiffer = computed(
  () => rightTrace.value !== null && leftTrace.value !== null && rightTrace.value.unit !== leftTrace.value.unit,
)

/**
 * Everything the choices on this form imply: the analysed length, the derived rate, the bin width
 * that follows from it, the largest FFT the run can fill and how coarse the bottom of the band
 * comes out. Reactive because changing "Analyse from" shortens the record, and in
 * simulationAnalysis.ts because the re-analysis dialog asks the identical questions of the stored
 * samples — two copies would eventually offer a setting the other one refuses.
 */
const fit = computed(() =>
  simulationFit({
    endTimeS: info.value?.endTimeS ?? 0,
    startTimeS: form.startTimeMs / 1e3,
    nativeRateHz: info.value?.nativeRateHz ?? 0,
    fftSize: form.fftSize,
    measurementLoFreqHz: form.measurementLoFreqHz,
    measurementHiFreqHz: form.measurementHiFreqHz,
  }),
)

/**
 * Which runs of a `.step`ped file to import. Every one by default: on an FFT measurement they
 * become a result each, and on a THD-vs-frequency one they become the points of a sweep, where
 * leaving some out simply shortens the curve.
 */
const chosenSweeps = ref<number[]>([])

const sweeps = computed(() => info.value?.sweeps ?? [])
const isStepped = computed(() => sweeps.value.length > 1)

function toggleSweep(index: number, on: boolean) {
  chosenSweeps.value = on
    ? [...chosenSweeps.value, index].sort((a, b) => a - b)
    : chosenSweeps.value.filter((i) => i !== index)
}

/** A sweep needs points: one run makes a one-point chart, which the server refuses outright. */
const tooFewForASweep = computed(
  () => props.sweepTarget === true && chosenSweeps.value.length < 2,
)

const canImport = computed(
  () =>
    props.measurementTypeId !== null &&
    info.value !== null &&
    form.leftTraceIndex !== null &&
    fit.value.usableS > 0 &&
    !fit.value.fftTooLarge &&
    (!isStepped.value || chosenSweeps.value.length > 0) &&
    !tooFewForASweep.value &&
    !busy.value,
)

function fmtStep(s: number): string {
  if (s >= 1e-3) return `${(s * 1e3).toFixed(3)} ms`
  if (s >= 1e-6) return `${(s * 1e6).toFixed(2)} µs`
  return `${(s * 1e9).toFixed(0)} ns`
}

function fmtRate(hz: number): string {
  return hz >= 1e6 ? `${(hz / 1e6).toFixed(3)} MHz` : `${(hz / 1e3).toFixed(1)} kHz`
}

async function submit() {
  if (!canImport.value || props.measurementTypeId === null || !info.value) return
  busy.value = true
  error.value = null
  try {
    const body = {
      path: info.value.path,
      leftTraceIndex: form.leftTraceIndex!,
      rightTraceIndex: form.rightTraceIndex,
      startTimeS: form.startTimeMs / 1e3,
      levelOffsetDb: form.levelOffsetDb,
      fftSize: form.fftSize,
      window: form.window,
      measurementLoFreqHz: form.measurementLoFreqHz,
      measurementHiFreqHz: form.measurementHiFreqHz,
      name: form.name.trim() || null,
      notes: form.notes.trim() || null,
      periodicRecord: form.periodicRecord,
      ...(isStepped.value ? { sweepIndices: chosenSweeps.value } : {}),
    }
    const resultIds = props.sweepTarget
      ? [(await api.importSimulationSweep(props.measurementTypeId, body)).resultId]
      : (await api.importSimulation(props.measurementTypeId, body)).resultIds
    emit('imported', resultIds)
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
    title="Open a simulation (LTspice .raw or QSpice .qraw)"
    @close="onBrowserClose"
    @select="inspect"
  />

  <div
    v-if="open && !browserOpen"
    class="fixed inset-0 z-40 flex items-start justify-center overflow-y-auto bg-black/40 p-6"
    @click.self="emit('close')"
  >
    <div class="w-full max-w-3xl rounded-lg border border-neutral-200 bg-[var(--surface-panel)] p-5 shadow-xl dark:border-neutral-800">
      <h2 class="mb-1 text-base font-semibold">Import simulation</h2>
      <p class="mb-4 text-xs text-neutral-500 dark:text-neutral-400">
        Reads an LTspice or QSpice transient run and analyses it exactly as the FFT screen analyses
        live audio, so its distortion figures mean the same thing.
      </p>

      <p v-if="error" class="mb-3 rounded border border-red-300 bg-red-50 p-2 text-xs text-red-700 dark:border-red-800 dark:bg-red-950 dark:text-red-300">
        {{ error }}
      </p>

      <template v-if="info">
        <!-- What the file is -->
        <div class="mb-4 rounded border border-neutral-200 p-3 text-xs dark:border-neutral-800">
          <p class="truncate font-mono text-[11px] text-neutral-500 dark:text-neutral-400" :title="info.path">{{ info.path }}</p>
          <dl class="mt-2 grid grid-cols-2 gap-x-6 gap-y-1 sm:grid-cols-3">
            <div><dt class="inline text-neutral-500">Run</dt> <dd class="inline">{{ info.plotname }}</dd></div>
            <div><dt class="inline text-neutral-500">Points</dt> <dd class="inline">{{ info.pointCount.toLocaleString() }}</dd></div>
            <div><dt class="inline text-neutral-500">Saved</dt> <dd class="inline">{{ (info.startTimeS * 1e3).toFixed(2) }}–{{ (info.endTimeS * 1e3).toFixed(2) }} ms</dd></div>
            <div><dt class="inline text-neutral-500">Timestep</dt> <dd class="inline">{{ fmtStep(info.medianStepS) }} median, {{ fmtStep(info.maxStepS) }} worst</dd></div>
            <div><dt class="inline text-neutral-500">Simulator rate</dt> <dd class="inline">{{ fmtRate(info.nativeRateHz) }}</dd></div>
            <div><dt class="inline text-neutral-500">Traces</dt> <dd class="inline">{{ info.traces.length }}</dd></div>
            <div v-if="isStepped">
              <dt class="inline text-neutral-500">Runs</dt>
              <dd class="inline">{{ sweeps.length }} (stepped)</dd>
            </div>
          </dl>
        </div>

        <!--
          A stepped run's steps. What they become depends on the measurement this was opened from,
          which is the whole reason the same file can be imported two ways: on an FFT measurement
          each is a spectrum of its own, on a THD-vs-frequency one they are the points of a curve.
        -->
        <fieldset v-if="isStepped" class="mb-3 rounded border border-neutral-200 p-2 dark:border-neutral-700">
          <legend class="px-1 text-xs text-neutral-500 dark:text-neutral-400">
            {{ sweepTarget ? 'Runs to use as points' : 'Runs to import' }}
          </legend>
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
            <template v-if="sweepTarget">
              Each becomes one point of a single sweep, at the fundamental its own analysis finds —
              so the step does not have to say what frequency it was.
            </template>
            <template v-else>
              Each is stored as its own result, named after the step.
            </template>
          </p>
          <p v-if="tooFewForASweep" class="mt-1 text-[11px] text-amber-700 dark:text-amber-400">
            A sweep needs at least two points.
          </p>
        </fieldset>

        <p
          v-for="warning in info.warnings"
          :key="warning"
          class="mb-2 rounded border border-amber-300 bg-amber-50 p-2 text-xs text-amber-800 dark:border-amber-700 dark:bg-amber-950/50 dark:text-amber-300"
        >
          {{ warning }}
        </p>

        <div class="grid gap-4 sm:grid-cols-2">
          <!-- Traces -->
          <label class="flex flex-col gap-1 text-sm">
            Left channel
            <select v-model.number="form.leftTraceIndex" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option v-for="t in info.traces" :key="t.index" :value="t.index">{{ t.name }} ({{ t.unit }})</option>
            </select>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Right channel
            <select v-model="form.rightTraceIndex" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option :value="null">— none —</option>
              <option v-for="t in info.traces" :key="t.index" :value="t.index">{{ t.name }} ({{ t.unit }})</option>
            </select>
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              A second trace, drawn like the right input — a stage's input beside its output, say.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Analyse from (ms)
            <input v-model.number="form.startTimeMs" type="number" step="0.1" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              Skip the startup transient. A tube amplifier's coupling caps and bias take time to
              settle, and a record straddling that measures the settling.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Level offset (dB)
            <input v-model.number="form.levelOffsetDb" type="number" step="0.1" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              If the bench sees this node through a divider or a pad. Applied at analysis time, so
              it can be corrected later without re-importing.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            FFT size
            <select v-model.number="form.fftSize" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option v-for="n in SIMULATION_FFT_SIZES" :key="n" :value="n" :disabled="n > fit.maxFftSize">
                {{ n.toLocaleString() }}{{ n === info.suggestedFftSize ? ' (suggested)' : '' }}{{ n > fit.maxFftSize ? ' — too large for this run' : '' }}
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
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Name
            <input
              v-model="form.name"
              type="text"
              :placeholder="leftTrace ? `${info.title.replace(/^\*?\s*/, '').split(/[\\/]/).pop()?.replace(/\.(raw|net|asc)$/i, '') ?? 'simulation'} - ${leftTrace.name}` : 'simulation'"
              class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            />
          </label>
        </div>

        <label class="mt-3 flex items-start gap-2 text-sm">
          <input v-model="form.periodicRecord" type="checkbox" class="mt-0.5 h-4 w-4 rounded border-neutral-400 dark:border-neutral-600" />
          <span>
            Lock the record to whole cycles
            <span class="block text-[11px] text-neutral-500 dark:text-neutral-400">
              Shortens the record by up to one cycle of the detected tone so it holds a whole number
              of them. Every harmonic then lands exactly on a bin, instead of leaking across its
              neighbours. Leave on unless the stimulus is noise or a multi-tone.
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
            Resolution is set by how long the simulation runs, not by the FFT size — it is
            1 ÷ {{ (fit.usableS * 1e3).toFixed(2) }} ms. A larger FFT only computes more empty spectrum
            above the band. To match the analyzer's 2.93 Hz you would need about 341 ms saved.
          </p>
          <p
            v-if="fit.resolution !== 'ok'"
            class="mt-1"
            :class="fit.resolution === 'tooCoarse' ? 'text-red-700 dark:text-red-400' : 'text-amber-700 dark:text-amber-400'"
          >
            <strong v-if="fit.resolution === 'tooCoarse'">This run is too short for this band.</strong>
            <strong v-else>Coarse at the bottom of the band.</strong>
            {{ form.measurementLoFreqHz }} Hz is only {{ fit.binsBelowBandEdge.toFixed(1) }} bins in at
            {{ fit.binWidthHz.toFixed(2) }} Hz resolution. Nothing here can improve that — save at least
            {{ fit.neededMs.toFixed(0) }} ms in LTspice if you need the low end.
          </p>
          <p v-if="fit.nyquistTooLow" class="mt-1 text-amber-700 dark:text-amber-400">
            At this FFT size the spectrum reaches only {{ (fit.nyquistHz / 1e3).toFixed(1) }} kHz,
            below the top of the measurement band. Raise the FFT size.
          </p>
          <p v-if="fit.fftTooLarge" class="mt-1 text-red-700 dark:text-red-400">
            This run holds {{ Math.floor(fit.usableS * info.nativeRateHz).toLocaleString() }} samples, so
            {{ fit.maxFftSize.toLocaleString() }} is the largest usable FFT size.
          </p>
          <p v-if="unitsDiffer" class="mt-1 text-amber-700 dark:text-amber-400">
            The two channels are in different units ({{ leftTrace?.unit }} and {{ rightTrace?.unit }}).
            Both are measured correctly, but one axis can only be labelled once.
          </p>
          <p v-if="unit !== 'V'" class="mt-1 text-amber-700 dark:text-amber-400">
            This is a current trace, in amps. Its levels are dB relative to 1 A, and the chart's dBV
            axis and the noise-density figures are written for volts — read the shape, not the
            absolute numbers.
          </p>
        </div>

        <p class="mt-3 text-[11px] text-neutral-500 dark:text-neutral-400">
          Every setting above except the traces can be changed afterwards — the samples are stored
          with the result, so <strong>Re-analyse</strong> in History details recomputes the spectrum
          without the file. Only the resolution cannot: that needs a longer simulation.
        </p>

        <!-- Settings to use in LTspice. Read every time, so it is here and not only in the manual. -->
        <details class="mt-4 text-xs">
          <summary class="cursor-pointer text-neutral-600 dark:text-neutral-300">Simulating for an import</summary>
          <div class="mt-2 space-y-2 text-neutral-600 dark:text-neutral-400">
            <p>
              Add <code class="rounded bg-[var(--surface-control)] px-1">.options plotwinsize=0</code>.
              Without it LTspice throws away most of the samples: measured on a real amplifier, that
              made THD read <strong>0.63 %</strong> against a true <strong>0.38 %</strong>. This
              import refuses such a file rather than reporting the high figure.
            </p>
            <p>
              Use <code class="rounded bg-[var(--surface-control)] px-1">.tran 0 &lt;stop&gt; &lt;start&gt; &lt;maxstep&gt;</code>
              — for example <code class="rounded bg-[var(--surface-control)] px-1">.tran 0 100m 5m 1u</code>.
              <em>maxstep</em> sets the simulator's sample rate, <em>start</em> skips the startup
              settling, and <em>stop − start</em> is what fixes your resolution.
            </p>
            <p>
              <code class="rounded bg-[var(--surface-control)] px-1">.options numdgt=7</code> is not
              needed: it doubles the file and measured identically, because the samples are stored
              at single precision either way and the arithmetic floor is far below it.
            </p>
            <p>
              <strong>QSpice</strong> writes the same format, so a <code class="rounded bg-[var(--surface-control)] px-1">.qraw</code>
              imports just as a <code class="rounded bg-[var(--surface-control)] px-1">.raw</code> does.
              It has no compression setting; the one thing to set is the timestep, because
              <code class="rounded bg-[var(--surface-control)] px-1">.tran 100m</code> alone lets it
              choose steps spanning picoseconds to tens of microseconds. Write
              <code class="rounded bg-[var(--surface-control)] px-1">.tran 0 100m 0 1u</code> instead.
            </p>
            <p>
              <code class="rounded bg-[var(--surface-control)] px-1">.four</code> is not read by this
              import, but it is worth adding as a cross-check: it makes the simulator write its own
              Fourier table to the log, independently of anything here. It analyses one cycle where
              this analyses the whole run, so expect close agreement rather than identical numbers.
            </p>
            <p>
              <strong>Import before closing the simulator.</strong> LTspice's
              <em>Automatically delete .raw files</em> (Tools &rsaquo; Control Panel &rsaquo;
              Operation) and QSpice's <em>Auto delete simulation data files</em> each throw the
              result away when the simulator closes, so with either ticked the file exists only
              while the run is still open. Untick it, or import first and close afterwards.
            </p>
          </div>
        </details>
      </template>

      <p v-else-if="!error" class="text-sm text-neutral-500 dark:text-neutral-400">Reading the file…</p>

      <div class="mt-5 flex justify-between gap-2">
        <div class="flex gap-2">
          <button type="button" class="rounded border px-3 py-1.5 text-sm btn-action" @click="browserOpen = true">
            Choose another file…
          </button>
          <button
            type="button"
            class="flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm btn-action"
            title="Importing a simulation: the directives to use, what each does to the spectrum, and what is refused."
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
