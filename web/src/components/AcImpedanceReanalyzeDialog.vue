<script setup lang="ts">
/**
 * Turns an imported impedance curve over, without the file.
 *
 * <p>The direction is the <em>only</em> thing an impedance import can be asked to change, and that
 * is not an apology for a thin dialog — it is the whole list. An impedance curve has no band, no
 * corner search and no reference level, and which two traces were divided is settled at import
 * because the other columns of the file were never read. What the direction has that those do not
 * is that it is exactly recoverable: negating an impedance leaves the magnitude untouched and moves
 * the phase half a turn, so the stored row carries everything needed to turn it back over.</p>
 *
 * <p>Its own dialog rather than an arm of `AcSimulationReanalyzeDialog`, which offers a band, a
 * bandwidth reference and a level offset — every one of which would be refused here.</p>
 */
import { computed, reactive, ref, watch } from 'vue'
import { api } from '../api/client'
import {
  parseStoredAcImpedance,
  type StoredAcImpedance,
} from '../measurements/acSimulationAnalysis'
import { useManual } from '../manual/useManual'
import BusyNote from './BusyNote.vue'
import InlineCodeText from './InlineCodeText.vue'

const props = defineProps<{ open: boolean; resultId: number | null }>()
const emit = defineEmits<{ close: []; reanalyzed: [resultId: number] }>()

const { openManual } = useManual()

const stored = ref<StoredAcImpedance | null>(null)
const resultName = ref('')
const error = ref<string | null>(null)
const busy = ref(false)

const form = reactive({ currentDirection: 'AsStored' })

watch(
  () => [props.open, props.resultId] as const,
  async ([open, resultId]) => {
    if (!open || resultId === null) return
    stored.value = null
    error.value = null
    busy.value = true
    try {
      const detail = await api.getMeasurement(resultId)
      const found = parseStoredAcImpedance(detail.paramsJson)
      if (!found) {
        error.value =
          'This result is not an imported simulated impedance, so there is no current direction ' +
          'to change. Only imported simulations can be re-analysed.'
        return
      }
      resultName.value = detail.name
      stored.value = found
      form.currentDirection = found.currentDirection
    } catch (e) {
      error.value = (e as Error).message
    } finally {
      busy.value = false
    }
  },
  { immediate: true },
)

const changed = computed(
  () => stored.value !== null && form.currentDirection !== stored.value.currentDirection,
)

const flipped = computed(() => stored.value?.currentDirection === 'Inverted')

async function apply() {
  if (props.resultId === null || !stored.value || busy.value) return
  busy.value = true
  error.value = null
  try {
    await api.reanalyzeAcImpedance(props.resultId, { currentDirection: form.currentDirection })
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
    <div class="w-full max-w-xl rounded-lg border border-neutral-200 bg-[var(--surface-panel)] p-5 shadow-xl dark:border-neutral-800">
      <h2 class="mb-1 text-base font-semibold">Re-analyse simulated impedance</h2>
      <p class="mb-4 text-xs text-neutral-500 dark:text-neutral-400">
        <span v-if="resultName" class="font-medium">{{ resultName }}</span>
        <span v-if="resultName"> — </span>rebuilt from the curve stored with it. The file is not
        needed.
      </p>

      <p v-if="error" class="mb-3 rounded border border-red-300 bg-red-50 p-2 text-xs text-red-700 dark:border-red-800 dark:bg-red-950 dark:text-red-300">
        <InlineCodeText :text="error" />
      </p>

      <template v-if="stored">
        <div class="mb-3 rounded border border-neutral-200 p-2 text-xs dark:border-neutral-800">
          <p class="truncate font-mono text-[11px] text-neutral-500 dark:text-neutral-400" :title="stored.source.path">
            {{ stored.source.path }}
          </p>
          <dl class="mt-2 grid grid-cols-2 gap-x-6 gap-y-1">
            <div>
              <dt class="mr-1 inline text-neutral-500">Impedance</dt>
              <dd class="inline">{{ stored.source.outputTrace }} / {{ stored.source.referenceTrace }}</dd>
            </div>
            <div>
              <dt class="mr-1 inline text-neutral-500">Points</dt>
              <dd class="inline">{{ stored.source.pointCount }}</dd>
            </div>
          </dl>
        </div>

        <label class="flex flex-col gap-1 text-sm">
          Current direction
          <select v-model="form.currentDirection" class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700">
            <option value="Auto">Automatic</option>
            <option value="AsStored">As the simulator stored it</option>
            <option value="Inverted">Inverted</option>
          </select>
          <span class="text-[11px] text-neutral-500 dark:text-neutral-400">
            <template v-if="flipped">
              This curve was <strong>flipped</strong> at import — the simulator's current flowed into
              its own + terminal, which puts V/I half a turn round. Choose
              <em>As the simulator stored it</em> to undo that.
            </template>
            <template v-else>
              This curve was taken <strong>as the simulator stored it</strong>. Choose
              <em>Inverted</em> to turn it over.
            </template>
            Automatic re-decides from the curve as it stands, so it leaves an already-correct one
            alone.
          </span>
        </label>

        <p class="mt-4 text-[11px] text-neutral-500 dark:text-neutral-400">
          Nothing else about an imported impedance can change: there is no band, no corner search and
          no reference level on an impedance curve, and which two traces were divided was settled at
          import — the other columns of the file were never read. Re-import to pick different ones.
        </p>
      </template>

      <BusyNote v-if="busy" label="Re-analysing…" class="mt-4" />

      <div class="mt-5 flex justify-between gap-2">
        <button type="button" class="rounded border px-3 py-1.5 text-sm btn-action" @click="openManual('simulation-import')">
          Manual
        </button>
        <div class="flex gap-2">
          <button type="button" class="rounded border px-3 py-1.5 text-sm btn-action" @click="emit('close')">Cancel</button>
          <button
            type="button"
            class="rounded border px-3 py-1.5 text-sm btn-primary disabled:opacity-50"
            :disabled="stored === null || busy || !changed"
            @click="apply"
          >
            {{ busy ? 'Applying…' : 'Apply' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
