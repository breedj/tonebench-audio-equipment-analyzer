<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import type { DeviceStatus } from '../api/client'
import { api } from '../api/client'
import { useExperimental384k } from '../composables/useExperimental384k'
import { useManual } from '../manual/useManual'
import Modal from './Modal.vue'
import HeaderIcon from './HeaderIcon.vue'

/**
 * The QA40x's own settings, reached from the gear beside the status bar's Analyzer picker — the same
 * place and the same button the sound card's settings have.
 *
 * <p><b>QA40x, not QA403</b>: the gear is keyed on the analyzer <em>kind</em>, so this dialog opens
 * on a QA402 bench too — see the picker's own entry in DeviceStatusBar. The experimental rate below
 * is the one setting here that really is QA403-only (<c>Qa40xModel.Capabilities</c> ignores the
 * flag for a QA402), and that row says so rather than the title narrowing to it.</p>
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
 * the status bar already calls that bench "QA40x" where the external gain is concerned.
 * It has no hardware ranger, so the delay field is absent there and says why.</p>
 */
const props = defineProps<{ open: boolean; status: DeviceStatus | null }>()
/** `configuration` carries the one cross-reference in the prose: the global settings live there. */
const emit = defineEmits<{ close: []; configuration: [] }>()

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
  <!-- The 2026-10-13 dialog chrome. The two settings are one card: both belong to the analyzer
       rather than to the bench, which is the whole of what this dialog is for. -->
  <Modal
    :open="props.open"
    title="QA40x settings"
    icon="soundcard"
    tone="green"
    ground
    flush
    @close="emit('close')"
  >
    <div class="flex flex-col gap-3.5 p-4">
      <p class="m-0 text-[12.5px] leading-relaxed text-neutral-600 dark:text-neutral-400">
        Settings that belong to this analyzer rather than to the bench. Everything global — the
        capture delay, the metrics bandwidth, the diagnostics — is in
        <button type="button" class="font-semibold text-blue-700 underline underline-offset-2 dark:text-blue-300" @click="emit('configuration')">
          Configuration</button>.
      </p>

      <section class="dialog-card">
        <label
          class="flex cursor-pointer items-start gap-3 p-3.5"
          title="A QA403 only — a QA402 ignores this setting. Offers 384 kHz in the FFT screen's Sample rate list, and a 200 kHz entry in its frequency To list. Measured on a QA403 rather than assumed, since QuantAsylum does not document this rate: the ADC really does run at it, and with an external generator driving the input the analyzer is flat to within about 1.2 dB out to 180 kHz. The DAC does not — a tone comes back 12 dB low with about 44 % distortion, and its near-DC content cycles the output protection relay, which a software stop does not clear. So the app refuses to play a stimulus above 192 kHz, and this rate is for capturing a signal something else is driving."
        >
          <input
            type="checkbox"
            class="tb-check mt-0.5 h-4 w-4"
            :checked="experimental384kEnabled"
            @change="setExperimental384kEnabled(($event.target as HTMLInputElement).checked)"
          />
          <span class="flex flex-1 flex-col gap-1">
            <span class="flex flex-wrap items-center gap-2">
              <span class="text-sm font-semibold">Allow 384 kHz sample rate</span>
              <span
                class="rounded-full px-2 py-0.5 text-[11px] font-semibold uppercase tracking-[0.04em]"
                :style="{ backgroundColor: 'var(--kind-amber-chip-bg)', color: 'var(--kind-amber-chip-fg)' }"
                >Experimental</span
              >
            </span>
            <span class="text-[12.5px] leading-relaxed text-neutral-600 dark:text-neutral-400">
              Capture only — the generator is refused above 192 kHz. Buys real input bandwidth, to
              about 180 kHz.
            </span>
          </span>
        </label>

        <div
          v-if="hasHardwareRanger"
          class="flex items-start gap-3 border-t border-neutral-100 p-3.5 dark:border-neutral-800"
          title="How long a peak must stay under-range before the input range steps down to a more sensitive setting. Higher values ride out amplitude-modulated signals (e.g. two close tones beating) without chasing every dip; lower values react faster to a genuinely quieter signal."
        >
          <label class="flex flex-1 flex-col gap-1" for="qa40x-step-down">
            <span class="text-sm font-semibold">Auto-range step-down delay</span>
            <span class="text-[12.5px] leading-relaxed text-neutral-600 dark:text-neutral-400">
              Written into the analyzer's own ranger, and kept — it is applied again to whichever
              analyzer this bench opens next.
            </span>
          </label>
          <span class="flex shrink-0 items-center gap-2">
            <input
              id="qa40x-step-down"
              type="number"
              min="0"
              step="100"
              v-model.number="stepDownDelayMs.value"
              class="tb-input h-9 w-24 text-right font-mono"
              @change="applyStepDownDelay"
            />
            <span class="text-[12.5px] text-neutral-500 dark:text-neutral-400">ms</span>
          </span>
        </div>
        <p v-else class="border-t border-neutral-100 p-3.5 text-[12.5px] text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">
          The auto-range step-down delay belongs to the analyzer's own ranger, which this one does
          not have — connect a QA40x to set it.
        </p>
      </section>

      <p v-if="error" class="text-[12.5px] text-red-600 dark:text-red-400">{{ error }}</p>
    </div>

    <template #footer>
      <button
        type="button"
        class="btn-secondary mr-auto"
        title="What both of these do, in full — including the measurements behind the 384 kHz warning."
        @click="openManual('settings')"
      >
        <HeaderIcon name="manual" :size="16" />
        Manual
      </button>
      <button type="button" class="btn-primary" @click="emit('close')">Close</button>
    </template>
  </Modal>
</template>
