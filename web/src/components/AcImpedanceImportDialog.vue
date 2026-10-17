<script setup lang="ts">
/**
 * Imports a simulator's `.ac` run as an impedance curve, so a modelled loudspeaker, crossover or
 * output transformer can be drawn under the measured one.
 *
 * <p>Its own dialog rather than a mode on `AcSimulationImportDialog`, for the reason those two are
 * already separate: the forms disagree about what they ask for. This one picks a voltage *and* a
 * current and has a sign to settle; that one picks an output and an optional reference and has a
 * band and a bandwidth reference. What they share is the file picker and the sweep list.</p>
 */
import { computed, reactive, ref, watch } from 'vue'
import { api, type AcSimulationFileInfo } from '../api/client'
import { sweepLabel } from '../measurements/acSimulationAnalysis'
import { useManual } from '../manual/useManual'
import FileBrowserDialog from './FileBrowserDialog.vue'
import BusyNote from './BusyNote.vue'

const props = defineProps<{ open: boolean; measurementTypeId: number | null }>()
const emit = defineEmits<{ close: []; imported: [resultIds: number[]] }>()

const { openManual } = useManual()

const browserOpen = ref(false)
const lastPath = ref<string | null>(null)
const info = ref<AcSimulationFileInfo | null>(null)
const busy = ref(false)
const error = ref<string | null>(null)
const chosenSweeps = ref<number[]>([])

const form = reactive({
  voltageTraceIndex: null as number | null,
  currentTraceIndex: null as number | null,
  currentDirection: 'Auto',
  name: '',
  notes: '',
})

watch(
  () => props.open,
  (open) => {
    if (!open) return
    error.value = null
    if (info.value === null) browserOpen.value = true
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

    // An impedance needs one of each, so default to the first of each rather than to the first two
    // traces — on the reference amplifier the first listed trace is an internal plate node.
    form.voltageTraceIndex = found.traces.find((t) => t.unit === 'V')?.index ?? null
    form.currentTraceIndex = found.traces.find((t) => t.unit === 'A')?.index ?? null
    form.currentDirection = 'Auto'
    form.name = ''
    chosenSweeps.value = found.sweeps.map((sw) => sw.index)
  } catch (e) {
    info.value = null
    chosenSweeps.value = []
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

const voltages = computed(() => info.value?.traces.filter((t) => t.unit === 'V') ?? [])
const currents = computed(() => info.value?.traces.filter((t) => t.unit === 'A') ?? [])

const sweeps = computed(() => info.value?.sweeps ?? [])
const isStepped = computed(() => sweeps.value.length > 1)

function toggleSweep(index: number, on: boolean) {
  chosenSweeps.value = on
    ? [...chosenSweeps.value, index].sort((a, b) => a - b)
    : chosenSweeps.value.filter((i) => i !== index)
}

/**
 * A file with no current trace cannot produce an impedance at all, and saying so here is far
 * better than the server's refusal: the usual cause is that the schematic never asked the
 * simulator to save one.
 */
const noCurrent = computed(() => info.value !== null && currents.value.length === 0)

const canImport = computed(
  () =>
    props.measurementTypeId !== null &&
    info.value !== null &&
    form.voltageTraceIndex !== null &&
    form.currentTraceIndex !== null &&
    (!isStepped.value || chosenSweeps.value.length > 0) &&
    !busy.value,
)

function fmtHz(hz: number): string {
  if (hz >= 1e6) return `${(hz / 1e6).toFixed(2)} MHz`
  if (hz >= 1e3) return `${(hz / 1e3).toFixed(2)} kHz`
  return `${hz.toPrecision(3)} Hz`
}

/** See SimulationImportDialog's own. */
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
    const result = await api.importAcImpedance(props.measurementTypeId, {
      path: info.value.path,
      voltageTraceIndex: form.voltageTraceIndex!,
      currentTraceIndex: form.currentTraceIndex!,
      currentDirection: form.currentDirection,
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
      <h2 class="mb-1 text-base font-semibold">Import simulated impedance</h2>
      <p class="mb-4 text-xs text-neutral-500 dark:text-neutral-400">
        Reads an LTspice or QSpice <code>.ac</code> sweep as ohms: one node voltage divided by the
        current through the source driving it, at every frequency the simulator solved.
      </p>

      <p v-if="error" class="mb-3 rounded border border-red-300 bg-red-50 p-2 text-xs text-red-700 dark:border-red-800 dark:bg-red-950 dark:text-red-300">
        {{ error }}
      </p>

      <template v-if="info">
        <div class="mb-3 rounded border border-neutral-200 p-2 text-xs dark:border-neutral-800">
          <p class="truncate font-mono text-[11px] text-neutral-500 dark:text-neutral-400" :title="info.path">{{ info.path }}</p>
          <dl class="mt-2 grid grid-cols-2 gap-x-6 gap-y-1 sm:grid-cols-3">
            <div><dt class="mr-1 inline text-neutral-500">Run</dt><dd class="inline">{{ info.plotname }}</dd></div>
            <div><dt class="mr-1 inline text-neutral-500">Points</dt><dd class="inline">{{ info.pointCount.toLocaleString() }}</dd></div>
            <div>
              <dt class="mr-1 inline text-neutral-500">Sweep</dt>
              <dd class="inline">{{ fmtHz(info.startFreqHz) }}–{{ fmtHz(info.endFreqHz) }}</dd>
            </div>
            <div><dt class="mr-1 inline text-neutral-500">Voltages</dt><dd class="inline">{{ voltages.length }}</dd></div>
            <div><dt class="mr-1 inline text-neutral-500">Currents</dt><dd class="inline">{{ currents.length }}</dd></div>
            <div v-if="isStepped">
              <dt class="mr-1 inline text-neutral-500">Sweeps</dt>
              <dd class="inline">{{ sweeps.length }} (stepped)</dd>
            </div>
          </dl>
        </div>

        <p
          v-if="noCurrent"
          class="mb-2 rounded border border-amber-300 bg-amber-50 p-2 text-xs text-amber-800 dark:border-amber-700 dark:bg-amber-950/50 dark:text-amber-300"
        >
          This file holds no current trace, so there is nothing to divide by. Add the source's
          current to what the simulation saves — in LTspice, <code>.save I(V1)</code> beside the
          node voltages, or simply save everything.
        </p>

        <p
          v-for="warning in info.warnings"
          :key="warning"
          class="mb-2 rounded border border-amber-300 bg-amber-50 p-2 text-xs text-amber-800 dark:border-amber-700 dark:bg-amber-950/50 dark:text-amber-300"
        >
          {{ warning }}
        </p>

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
            Each one is stored as its own result, named after the step.
          </p>
        </fieldset>

        <div class="grid gap-4 sm:grid-cols-2">
          <label class="flex flex-col gap-1 text-sm">
            Voltage across it
            <select v-model.number="form.voltageTraceIndex" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option v-for="t in voltages" :key="t.index" :value="t.index">{{ t.name }}</option>
            </select>
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              The node the source drives — the top of the thing being measured.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Current through it
            <select v-model.number="form.currentTraceIndex" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option v-for="t in currents" :key="t.index" :value="t.index">{{ t.name }}</option>
            </select>
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              Normally the source's own current, <code>I(V1)</code>. With a 1 A source this divides
              by one, which is the same as reading the voltage as ohms.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Current direction
            <select v-model="form.currentDirection" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
              <option value="Auto">Automatic</option>
              <option value="AsStored">As the simulator stored it</option>
              <option value="Inverted">Inverted</option>
            </select>
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              A source's current flows into its own + terminal, which puts V/I half a turn round.
              Automatic flips it when most of the curve comes out with a negative real part, which
              no passive one-port can have, and says so.
            </span>
          </label>

          <label class="flex flex-col gap-1 text-sm">
            Name
            <input v-model="form.name" type="text" placeholder="Simulated Jamo 504" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700" />
            <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
              Blank names it after the file and the two traces.
            </span>
          </label>
        </div>

        <label class="mt-4 flex flex-col gap-1 text-sm">
          Notes
          <textarea v-model="form.notes" rows="2" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"></textarea>
        </label>

        <details class="mt-4 rounded border border-neutral-200 p-2 text-xs dark:border-neutral-800">
          <summary class="cursor-pointer text-neutral-600 dark:text-neutral-400">What this can and cannot tell you</summary>
          <div class="mt-2 space-y-2 text-neutral-600 dark:text-neutral-400">
            <p>
              The curve is stored as it was solved — the whole sweep, every point, with coherence 1,
              because exact arithmetic has no second estimate to disagree with. The resonance
              readout and the component fit read it exactly as they read a measured one.
            </p>
            <p>
              <strong>There is no fixture here, and the settings say so.</strong> No reference
              resistor, no open or short standard, no drive level: a division of two solved
              quantities needs none of them, and the stored settings carry zeros rather than
              plausible-looking figures.
            </p>
            <p>
              <strong>An AC analysis is a small-signal linearisation about the operating point.</strong>
              A driver's impedance rise with excursion, a core's saturation, a crossover's
              inductor going non-linear at power — none of that is in here.
            </p>
            <p>
              The <strong>current direction</strong> can be changed afterwards with
              <strong>Re-analyse</strong> in History details — negating an impedance leaves the
              magnitude alone and moves the phase half a turn, so the stored curve can be turned
              back over without the file. Nothing else can: there is no band and no reference level
              on an impedance curve, and which two traces were divided is settled here, because the
              other columns are not read. Re-import if one of them was the wrong pick.
            </p>
          </div>
        </details>
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
            class="rounded border px-3 py-1.5 text-sm btn-primary disabled:opacity-50"
            :disabled="!canImport"
            @click="submit"
          >
            {{ busy ? 'Importing…' : 'Import' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
