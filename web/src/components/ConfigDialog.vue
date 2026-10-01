<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import Modal from './Modal.vue'
import { api } from '../api/client'
import { useAppTheme, type ThemeMode } from '../composables/useAppTheme'
import { useAutosave } from '../composables/useAutosave'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import { useDateTimeFormat } from '../composables/useDateTimeFormat'
import { useDiagnosticLogging } from '../composables/useDiagnosticLogging'
import { useDevice } from '../composables/useDevice'
import { commitNumber } from '../config/numberField'
import { useImpedanceFixture } from '../composables/useImpedanceFixture'
import { useSpectrumConfig } from '../composables/useSpectrumConfig'
import { useStartupConfig } from '../composables/useStartupConfig'
import { useUpdateCheck } from '../composables/useUpdateCheck'
import { DATE_TIME_FORMATS, formatDateTime, type DateTimeFormatId } from '../dateTime'
import { formatOhms } from '../impedance/equivalent'
import { formatHz } from '../response/format'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const { themeMode } = useAppTheme()
const { captureDelayMs, set: setCaptureDelay } = useCaptureDelay()
const { config, apply: applyConfig } = useSpectrumConfig()
const { status: deviceStatus } = useDevice()
const { updateCheckEnabled } = useUpdateCheck()
const { showWelcomeOnStartup, load: loadStartupConfig, setShowWelcomeOnStartup } = useStartupConfig()
void loadStartupConfig()
const { autosaveEnabled, load: loadAutosave, setAutosaveEnabled } = useAutosave()
void loadAutosave()
const { dateTimeFormat, load: loadDateTimeFormat, setDateTimeFormat } = useDateTimeFormat()
void loadDateTimeFormat()
const {
  enabled: diagnosticLoggingEnabled,
  logsDirectory,
  error: diagnosticLoggingError,
  load: loadDiagnosticLogging,
  setEnabled: setDiagnosticLogging,
  openLogFolder,
} = useDiagnosticLogging()
void loadDiagnosticLogging()
const { fixture, hasCalibration, error: fixtureError, load: loadFixture, save: saveFixture, clear: clearFixture } =
  useImpedanceFixture()

/**
 * Which tab is showing. Grouped by what a setting is *about* rather than by where it is stored —
 * the auto-ranging delay lives on the device and the capture delay in the configuration database,
 * but both are things you reach for while setting up an acquisition.
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

/**
 * The three numeric settings are edited through their own draft strings, and that is not a style
 * choice — it is the fix for a bug that made them look broken.
 *
 * <p>Bound as <c>:value="theStoredRef"</c>, a field is overwritten on <b>every re-render</b>, not
 * merely when the stored value changes: Vue's prop patching carries an explicit
 * <c>next !== prev || key === "value"</c>, so an unchanged <c>value</c> prop is still written back
 * to the element. This dialog re-renders about once a second, because it reads the polled device
 * status — so typing "-3" into a field put it back to 0 before it could be committed, and emptying
 * one made a 0 appear under the cursor. Reported 2026-09-30. (It was External gain this was found
 * on; that setting now lives on the status bar, and its popover carries the same drafts for the
 * same reason.)</p>
 *
 * <p>With <c>v-model</c> on a draft the element's value <i>is</i> the bound value, so the patch is
 * a no-op and the text survives. The drafts are strings rather than numbers so a field can be
 * genuinely empty while it is being retyped, which <c>v-model.number</c> cannot express.</p>
 */
const captureDelayDraft = ref('')

watch(captureDelayMs, (ms) => (captureDelayDraft.value = String(ms)), { immediate: true })

// On change rather than on every keystroke: each write is a round trip that lands in the
// configuration database, and a half-typed "3" applied to the next sweep point is not a setting.
async function onCaptureDelayChange() {
  await setCaptureDelay(commitNumber(captureDelayDraft.value, { min: 0, max: 10000, fallback: 0 }))

  // Explicitly, not through the watcher: a draft that parsed back to the value already stored
  // ("abc" over a 0) changes nothing for the watcher to react to, and the field would keep the
  // text that was rejected.
  captureDelayDraft.value = String(captureDelayMs.value)
}


// The dropdown's rows carry a live example of "now" in each format, taken once per open: the
// pattern alone ("DD-MM-YYYY") reads as a puzzle, the date itself does not.
const dateFormatExampleAt = ref(new Date())
watch(
  () => props.open,
  (open) => {
    if (open) dateFormatExampleAt.value = new Date()
  },
)
const dateFormatOptions = computed(() =>
  DATE_TIME_FORMATS.map((f) => ({ id: f.id, label: `${formatDateTime(dateFormatExampleAt.value, f.id)}  —  ${f.label}` })),
)

const THEME_OPTIONS: { value: ThemeMode; label: string }[] = [
  { value: 'Auto', label: 'Auto' },
  { value: 'Light', label: 'Light' },
  { value: 'Dark', label: 'Dark' },
]

// The band THD+N / SNR / noise floor / band RMS / noise density are integrated over — live pipeline
// state, applied on change straight through the same useSpectrumConfig() config as the FFT
// settings panel, whose Bandwidth dropdown writes the same two fields (these are its "Custom"). It
// IS part of an FFT measurement type's saved params (MeasurementSaveService.ApplyAsync restores
// it); the earlier claim here that it was not had gone stale.
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
  <Modal :open="props.open" title="Configuration" @close="emit('close')">
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
              class="w-32 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            >
              <option v-for="o in THEME_OPTIONS" :key="o.value" :value="o.value">{{ o.label }}</option>
            </select>
          </label>

          <label
            class="flex flex-col gap-1"
            title="How the date and time are written on a screenshot's date line and in a report's Taken and Generated lines. System follows this PC's region settings; the fixed formats read the same wherever the document ends up."
          >
            Date and time format
            <select
              :value="dateTimeFormat"
              class="w-full rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
              @change="setDateTimeFormat(($event.target as HTMLSelectElement).value as DateTimeFormatId)"
            >
              <option v-for="o in dateFormatOptions" :key="o.id" :value="o.id">{{ o.label }}</option>
            </select>
          </label>

          <label
            class="flex items-center gap-2"
            title="Periodically checks GitHub for a newer release and shows a dismissible banner. Never installs anything automatically; fails silently if this machine has no internet access."
          >
            <input type="checkbox" v-model="updateCheckEnabled" />
            Check for updates
          </label>

          <label
            class="flex items-center gap-2"
            title="Unchecked, the last opened project is opened at startup if its file still exists."
          >
            <input
              type="checkbox"
              :checked="showWelcomeOnStartup"
              @change="setShowWelcomeOnStartup(($event.target as HTMLInputElement).checked)"
            />
            Show welcome page on startup
          </label>

          <label
            class="flex items-center gap-2"
            title="Saves every change to a measurement's settings into it the moment it is made, so the measurement's Save button never needs pressing. Unchecked, changes wait for Save and are shown by the red dot."
          >
            <input
              type="checkbox"
              :checked="autosaveEnabled"
              @change="setAutosaveEnabled(($event.target as HTMLInputElement).checked)"
            />
            Autosave measurement settings
          </label>

          <!-- The switch to ask a tester for. The folder line and the button are the other half
               of that request: a log file is only sendable by someone who can find it. -->
          <div
            class="flex flex-col gap-1.5 border-t border-neutral-200 pt-3 dark:border-neutral-800"
            title="Writes a detailed record to the log file — every request the app handles, with its outcome and timing, plus the device layer's own debug lines. Switch it on before reproducing a problem, then send the day's file from the folder below. Off, the app still keeps its ordinary log."
          >
            <label class="flex items-center gap-2">
              <input
                type="checkbox"
                :checked="diagnosticLoggingEnabled"
                @change="setDiagnosticLogging(($event.target as HTMLInputElement).checked)"
              />
              Detailed logging (for reporting a problem)
            </label>
            <div class="flex flex-wrap items-center gap-2 text-xs text-neutral-500 dark:text-neutral-400">
              <span class="min-w-0 break-all font-mono" :title="logsDirectory ?? ''">{{ logsDirectory ?? '…' }}</span>
              <button
                type="button"
                class="rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
                title="Opens the log folder in the file manager. The current file is named after today's date."
                @click="openLogFolder"
              >
                Open log folder
              </button>
            </div>
            <p v-if="diagnosticLoggingError" class="text-xs text-red-600 dark:text-red-400">{{ diagnosticLoggingError }}</p>
          </div>
        </template>

        <template v-else-if="activeTab === 'Acquisition'">
          <label
            class="flex flex-col gap-1"
            title="How long the device under test is given to settle after every generator change — each sweep point, every stimulus change on the FFT screen, and Run itself — before the analysis window starts filling. Raise it for a DUT that rings or drifts after a tone step (an output transformer, coupling capacitors, slow external gear)."
          >
            Capture delay (ms)
            <input
              type="number"
              min="0"
              max="10000"
              step="10"
              v-model="captureDelayDraft"
              @change="onCaptureDelayChange"
              class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
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
              class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
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
                class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
                @change="applyBandwidth"
              />
              <span class="text-neutral-400">–</span>
              <input
                type="number"
                min="1"
                v-model.number="bandwidth.hiFreqHz"
                class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
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
              class="w-32 rounded border border-neutral-300 px-2 py-1 disabled:bg-neutral-100 disabled:text-neutral-500 dark:border-neutral-700 dark:disabled:bg-neutral-900 dark:disabled:text-neutral-400"
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
              class="w-32 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
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
