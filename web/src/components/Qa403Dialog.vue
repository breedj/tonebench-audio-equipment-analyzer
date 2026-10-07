<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import type { DeviceStatus } from '../api/client'
import { api } from '../api/client'
import { useExperimental384k } from '../composables/useExperimental384k'
import { useManual } from '../manual/useManual'
import Modal from './Modal.vue'

/**
 * The QA403's own settings, reached from the gear beside the status bar's Analyzer picker — the same
 * place and the same button the sound card's settings have.
 *
 * <p>These two were in Configuration ▸ Acquisition, where everything else is global: it applies
 * whichever measurement is selected and whichever analyzer is attached. Neither of these is. The
 * experimental rate exists because of what <em>this</em> converter does at 384 kHz, and the
 * step-down delay is a register in <em>this</em> device's own ranger — a sound-card bench has no
 * ranger at all, and the field hid itself there, which is a setting that comes and goes with the
 * hardware sitting in a dialog that promises not to.</p>
 *
 * <p><b>Shown for the simulator too</b>, deliberately. The simulator models a QA403 and mirrors its
 * generator rate ceiling (<c>MaxGeneratorSampleRateHz</c>), so the 384 kHz opt-in is read by both —
 * the status bar already calls that bench "the QA403 bench" where the external gain is concerned.
 * It has no hardware ranger, so the delay field is absent there and says why.</p>
 */
const props = defineProps<{ open: boolean; status: DeviceStatus | null }>()
const emit = defineEmits<{ close: [] }>()

const { experimental384kEnabled, load: loadExperimental384k, setExperimental384kEnabled } = useExperimental384k()
void loadExperimental384k()

const { openManual } = useManual()

/**
 * Staged locally and applied on change, not per keystroke: each write is a round trip to the
 * device's own register, and a half-typed "1" is not a setting.
 */
const stepDownDelayMs = reactive({ value: 1000 })
const error = ref<string | null>(null)

watch(
  () => props.status?.autoRangeStepDownDelayMs,
  (ms) => {
    if (ms != null) stepDownDelayMs.value = ms
  },
  { immediate: true },
)

/**
 * Only a device with a ranger of its own has this. Null on the simulator and on a sound card, where
 * the ranging is the client's (App.vue's `meters` watcher) and there is nothing to delay.
 */
const hasHardwareRanger = computed(() => props.status?.autoRangeStepDownDelayMs != null)

async function applyStepDownDelay() {
  try {
    await api.setAutoRangeStepDownDelay(stepDownDelayMs.value)
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}
</script>

<template>
  <Modal :open="props.open" title="QA403 settings" @close="emit('close')">
    <div class="flex flex-col gap-5 text-sm">
      <p class="text-xs text-neutral-500 dark:text-neutral-400">
        Settings that belong to this analyzer rather than to the bench. Everything global — the
        capture delay, the metrics bandwidth, the diagnostics — is in Configuration.
      </p>

      <label
        class="flex items-start gap-2"
        title="Offers 384 kHz in the FFT screen's Sample rate list, and a 200 kHz entry in its frequency To list. Measured on a QA403 rather than assumed, since QuantAsylum does not document this rate: the ADC really does run at it, and with an external generator driving the input the analyzer is flat to within about 1.2 dB out to 180 kHz. The DAC does not — a tone comes back 12 dB low with about 44 % distortion, and its near-DC content cycles the output protection relay, which a software stop does not clear. So the app refuses to play a stimulus above 192 kHz, and this rate is for capturing a signal something else is driving."
      >
        <input
          type="checkbox"
          class="mt-0.5"
          :checked="experimental384kEnabled"
          @change="setExperimental384kEnabled(($event.target as HTMLInputElement).checked)"
        />
        <span>
          Allow 384 kHz sample rate (experimental)
          <span class="block text-xs text-neutral-500">
            Capture only — the generator is refused above 192 kHz. Buys real input bandwidth, to
            about 180 kHz.
          </span>
        </span>
      </label>

      <label
        v-if="hasHardwareRanger"
        class="flex flex-col gap-1"
        title="How long a peak must stay under-range before the input range steps down to a more sensitive setting. Higher values ride out amplitude-modulated signals (e.g. two close tones beating) without chasing every dip; lower values react faster to a genuinely quieter signal."
      >
        Auto-range step-down delay (ms)
        <span class="text-xs text-neutral-500">
          Written into the analyzer's own ranger, and kept — it is applied again to whichever
          analyzer this bench opens next.
        </span>
        <input
          type="number"
          min="0"
          step="100"
          v-model.number="stepDownDelayMs.value"
          class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @change="applyStepDownDelay"
        />
      </label>
      <p v-else class="text-xs text-neutral-500 dark:text-neutral-400">
        The auto-range step-down delay belongs to the analyzer's own ranger, which this one does not
        have — connect a QA403 to set it.
      </p>

      <p v-if="error" class="text-red-600 dark:text-red-400">{{ error }}</p>

      <div class="flex items-center justify-end gap-2">
        <button
          type="button"
          class="mr-auto flex items-center gap-1.5 rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800"
          title="What both of these do, in full — including the measurements behind the 384 kHz warning."
          @click="openManual('settings')"
        >
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
            <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
            <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2Z" />
          </svg>
          Manual
        </button>
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-100 dark:border-neutral-700"
          @click="emit('close')"
        >
          Close
        </button>
      </div>
    </div>
  </Modal>
</template>
