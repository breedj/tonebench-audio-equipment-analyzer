<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import Modal from './Modal.vue'
import { api } from '../api/client'
import { useAppTheme, type ThemeMode } from '../composables/useAppTheme'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import { useDevice } from '../composables/useDevice'
import { useImpedanceFixture } from '../composables/useImpedanceFixture'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { useUpdateCheck } from '../composables/useUpdateCheck'
import { formatOhms } from '../impedance/equivalent'
import { formatHz } from '../response/format'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const { themeMode } = useAppTheme()
const { captureDelayMs } = useCaptureDelay()
const { config, apply: applyConfig } = useSpectrumConfig()
const { status: deviceStatus } = useDevice()
const { updateCheckEnabled } = useUpdateCheck()
const { fixture, hasCalibration, error: fixtureError, load: loadFixture, save: saveFixture, clear: clearFixture } =
  useImpedanceFixture()

/**
 * Which tab is showing. Grouped by what a setting is *about* rather than by where it is stored —
 * the auto-ranging delay lives on the device and the capture delay in the browser, but both are
 * things you reach for while setting up an acquisition.
 */
type ConfigTab = 'General' | 'Acquisition' | 'Measurement' | 'Impedance'
const TABS: ConfigTab[] = ['General', 'Acquisition', 'Measurement', 'Impedance']
const activeTab = ref<ConfigTab>('General')

// Fetched when the dialog opens rather than on mount: it is a round trip for something most
// sessions never look at, and reopening picks up a calibration stored from a measurement meanwhile.
watch(
  () => props.open,
  (open) => {
    if (open) void loadFixture(true)
  },
  { immediate: true },
)

// Staged locally like the bandwidth field below, applied on change. Only meaningful on a device
// with a hardware ranger (autoRangeStepDownDelayMs is null on the simulator, which has its own
// client-side ranger in App.vue instead) — the field is hidden in that case.
const autoRangeStepDownDelayMs = reactive({ value: 1000 })

watch(
  deviceStatus,
  (s) => {
    if (s?.autoRangeStepDownDelayMs != null) autoRangeStepDownDelayMs.value = s.autoRangeStepDownDelayMs
  },
  { immediate: true },
)

async function applyAutoRangeStepDownDelay() {
  deviceStatus.value = await api.setAutoRangeStepDownDelay(autoRangeStepDownDelayMs.value)
}

const THEME_OPTIONS: { value: ThemeMode; label: string }[] = [
  { value: 'Auto', label: 'Auto' },
  { value: 'Light', label: 'Light' },
  { value: 'Dark', label: 'Dark' },
]

// The band THD+N / SNR / noise floor are integrated over — global (applies to whatever is
// currently running, independent of which measurement type is selected), not part of any
// measurement type's saved params. Staged locally like SpectrumConfigPanel's own form, applied on
// change straight through the same live useSpectrumConfig() config this used to live next to.
const bandwidth = reactive({ loFreqHz: 20, hiFreqHz: 20000 })

watch(
  config,
  (c) => {
    if (!c) return
    bandwidth.loFreqHz = c.measurementLoFreqHz
    bandwidth.hiFreqHz = c.measurementHiFreqHz
  },
  { immediate: true },
)

async function applyBandwidth() {
  await applyConfig({ measurementLoFreqHz: bandwidth.loFreqHz, measurementHiFreqHz: bandwidth.hiFreqHz })
}

// --- Impedance fixture -------------------------------------------------------------------------

const impedance = reactive({ referenceOhms: 100, calibrationLevelValue: 0 })

watch(
  fixture,
  (f) => {
    if (!f) return
    impedance.referenceOhms = f.referenceOhms
    impedance.calibrationLevelValue = f.calibrationLevelValue
  },
  { immediate: true },
)

/**
 * The reference resistor is only editable while nothing is calibrated.
 *
 * <p>Once standards are stored the resistor stops being a mere default and becomes part of them:
 * both are stored as the measured channel ratio, and both depend on R. Editing it here would leave
 * curves that silently mis-correct every reading, so changing it means clearing the calibration
 * first — which the button below does explicitly rather than as a side effect.</p>
 */
const resistorLocked = computed(() => hasCalibration.value)

async function applyFixtureField() {
  if (!fixture.value) return

  await saveFixture({
    ...fixture.value,
    referenceOhms: resistorLocked.value ? fixture.value.referenceOhms : impedance.referenceOhms,
    calibrationLevelValue: impedance.calibrationLevelValue,
  })
}

async function clearStoredCalibration() {
  await clearFixture()
}
</script>

<template>
  <Modal :open="props.open" title="Configuration" :close-on-backdrop="false" @close="emit('close')">
    <div class="flex flex-col gap-4 text-sm">
      <!-- Tabs rather than one long scroll: the settings here have nothing to do with each other,
           and a page that mixes a theme picker with a fixture calibration makes both harder to find. -->
      <div class="flex gap-1 border-b border-neutral-200 dark:border-neutral-800" role="tablist">
        <button
          v-for="tab in TABS"
          :key="tab"
          type="button"
          role="tab"
          :aria-selected="activeTab === tab"
          class="-mb-px border-b-2 px-3 py-1.5 text-xs font-medium transition"
          :class="
            activeTab === tab
              ? 'border-neutral-900 text-neutral-900 dark:border-neutral-100 dark:text-neutral-100'
              : 'border-transparent text-neutral-500 hover:text-neutral-800 dark:text-neutral-400 dark:hover:text-neutral-200'
          "
          @click="activeTab = tab"
        >
          {{ tab }}
        </button>
      </div>

      <div class="flex min-h-48 flex-col gap-5">
        <template v-if="activeTab === 'General'">
          <label class="flex flex-col gap-1">
            Style
            <select
              v-model="themeMode"
              class="w-32 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            >
              <option v-for="o in THEME_OPTIONS" :key="o.value" :value="o.value">{{ o.label }}</option>
            </select>
          </label>

          <label
            class="flex items-center gap-2"
            title="Periodically checks GitHub for a newer release and shows a dismissible banner. Never installs anything automatically; fails silently if this machine has no internet access."
          >
            <input type="checkbox" v-model="updateCheckEnabled" />
            Check for updates
          </label>
        </template>

        <template v-else-if="activeTab === 'Acquisition'">
          <label
            class="flex flex-col gap-1"
            title="How long to wait, after Run or Single enables the generator, before capture actually starts. Lets the generator/output settle (e.g. through external gear) before the FFT starts averaging."
          >
            Capture delay (ms)
            <input
              type="number"
              min="0"
              step="10"
              v-model.number="captureDelayMs"
              class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
            />
          </label>

          <label
            v-if="deviceStatus?.autoRangeStepDownDelayMs != null"
            class="flex flex-col gap-1"
            title="How long a peak must stay under-range before the input range steps down to a more sensitive setting. Higher values ride out amplitude-modulated signals (e.g. two close tones beating) without chasing every dip; lower values react faster to a genuinely quieter signal."
          >
            Auto-range step-down delay (ms)
            <input
              type="number"
              min="0"
              step="100"
              v-model.number="autoRangeStepDownDelayMs.value"
              class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
              @change="applyAutoRangeStepDownDelay"
            />
          </label>
        </template>

        <template v-else-if="activeTab === 'Measurement'">
          <label
            class="flex flex-col gap-1"
            title="Bandwidth THD+N, SNR and the noise floor are measured over. Also keeps DC out of the noise integration. Applies globally, regardless of which measurement is selected."
          >
            Metrics bandwidth (Hz)
            <span class="flex items-center gap-1">
              <input
                type="number"
                min="0"
                v-model.number="bandwidth.loFreqHz"
                class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
                @change="applyBandwidth"
              />
              <span class="text-neutral-400">–</span>
              <input
                type="number"
                min="1"
                v-model.number="bandwidth.hiFreqHz"
                class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
                @change="applyBandwidth"
              />
            </span>
          </label>
        </template>

        <template v-else-if="activeTab === 'Impedance'">
          <p class="text-xs text-neutral-500 dark:text-neutral-400">
            The bench fixture, shared by every impedance measurement. Stored in its own configuration
            database rather than with your measurements — it describes this QA403 and these test
            leads, so it must not travel inside a measurement file copied to another machine.
          </p>

          <label
            class="flex flex-col gap-1"
            :title="
              resistorLocked
                ? 'Fixed by the stored calibration: the standards are only valid at the resistance they were captured through. Clear the calibration to change it.'
                : 'The series resistor a new impedance measurement starts from. Enter its measured value — the answer is directly proportional to it.'
            "
          >
            Reference resistor (Ω)
            <input
              type="number"
              min="0.1"
              step="any"
              :disabled="resistorLocked"
              v-model.number="impedance.referenceOhms"
              class="w-32 rounded border border-neutral-300 px-2 py-1 disabled:bg-neutral-100 disabled:text-neutral-500 dark:border-neutral-700 dark:bg-neutral-800 dark:disabled:bg-neutral-900 dark:disabled:text-neutral-400"
              @change="applyFixtureField"
            />
          </label>

          <label
            class="flex flex-col gap-1"
            title="The level calibration runs drive at, louder than a measurement's on purpose: a shorted fixture puts the measured channel about 60 dB below the reference one, and both share a single input range set from the loud one."
          >
            Calibration drive level (dBV)
            <input
              type="number"
              step="any"
              v-model.number="impedance.calibrationLevelValue"
              class="w-32 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
              @change="applyFixtureField"
            />
          </label>

          <section class="flex flex-col gap-2 border-t border-neutral-200 pt-3 dark:border-neutral-800">
            <h3 class="text-xs font-semibold text-neutral-900 dark:text-neutral-100">Stored calibration</h3>

            <p v-if="!hasCalibration" class="text-xs text-neutral-500 dark:text-neutral-400">
              Nothing stored yet. Capture an open — and ideally a short — on an impedance
              measurement, then press <strong>Store as global calibration</strong> there. It is
              captured from a measurement rather than here, because that is where the run controls
              and the live trace are.
            </p>

            <template v-else-if="fixture">
              <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
                <dt class="text-neutral-500 dark:text-neutral-400">Reference</dt>
                <dd class="font-mono">{{ formatOhms(fixture.referenceOhms) }}</dd>
                <dt class="text-neutral-500 dark:text-neutral-400">Standards</dt>
                <dd class="font-mono">{{ fixture.shortCal ? 'open + short' : 'open only' }}</dd>
                <dt class="text-neutral-500 dark:text-neutral-400">Band</dt>
                <dd class="font-mono">
                  {{ formatHz(fixture.capturedLowHz) }} – {{ formatHz(fixture.capturedHighHz) }}
                </dd>
                <dt class="text-neutral-500 dark:text-neutral-400">Captured</dt>
                <dd class="font-mono">
                  {{ fixture.capturedUtc ? new Date(fixture.capturedUtc).toLocaleString() : '—' }}
                </dd>
              </dl>

              <p class="text-xs text-neutral-500 dark:text-neutral-400">
                Outside the band above a calibration curve is held flat rather than measured, so a
                sweep that runs past it is extrapolating. The measurement panel says so when it does.
              </p>

              <button
                type="button"
                class="w-fit rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
                title="Removes the stored standards and unlocks the reference resistor. Measurements using their own calibration are unaffected."
                @click="clearStoredCalibration"
              >
                Clear calibration
              </button>
            </template>

            <p v-if="fixtureError" class="text-xs text-red-600 dark:text-red-400">{{ fixtureError }}</p>
          </section>
        </template>
      </div>

      <div class="flex justify-end border-t border-neutral-200 pt-3 dark:border-neutral-800">
        <button
          type="button"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          @click="emit('close')"
        >
          Close
        </button>
      </div>
    </div>
  </Modal>
</template>
