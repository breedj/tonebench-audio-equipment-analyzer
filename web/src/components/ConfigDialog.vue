<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import Modal from './Modal.vue'
import { useAppTheme, type ThemeMode } from '../composables/useAppTheme'
import { useAutosave } from '../composables/useAutosave'
import { useCaptureDelay } from '../composables/useCaptureDelay'
import { useDateTimeFormat } from '../composables/useDateTimeFormat'
import { useDiagnosticLogging } from '../composables/useDiagnosticLogging'
import { useDiagnostics } from '../composables/useDiagnostics'
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
const {
  recording: streamRecording,
  capture: recordCapture,
  error: diagnosticsError,
  load: loadDiagnostics,
  refresh: refreshDiagnostics,
  setRecording: setStreamRecording,
  setCapture: setRecordCapture,
  openRecordingsFolder,
  openDiagnosticsFolder,
} = useDiagnostics()
void loadDiagnostics()
const { fixture, hasCalibration, error: fixtureError, load: loadFixture, save: saveFixture, clear: clearFixture } =
  useImpedanceFixture()

// Staged like the auto-range delay below, applied on change: typing a cap one digit at a time would
// otherwise send a PUT per keystroke, and 2 MB is a value the server rejects.
const recordingCapMb = reactive({ value: 256 })

watch(
  streamRecording,
  (status) => {
    if (status) recordingCapMb.value = status.maxFileMegabytes
  },
  { immediate: true },
)

/** One line about the last recording, which is what says whether it is worth sending. */
const lastRecordingSummary = computed(() => {
  const status = streamRecording.value
  if (!status?.lastName) return null

  const megabytes = status.lastBytes / 1024 / 1024
  const capped = status.lastReachedCap ? ', stopped at the cap' : ''
  return `${status.lastName}: ${status.lastSeconds.toFixed(1)} s, ${megabytes.toFixed(1)} MB${capped}`
})

/**
 * Which tab is showing. Grouped by what a setting is *about* rather than by where it is stored — the
 * metrics bandwidth is live pipeline state and the capture delay a configuration document, but both
 * are things you reach for while setting up an acquisition.
 *
 * Everything here is global: it applies whichever measurement is selected and whichever analyzer is
 * attached. The two settings that are not — the experimental 384 kHz rate and the auto-ranging
 * step-down delay — moved to the QA403's own dialog on the status bar (Qa403Dialog.vue), beside the
 * sound card's. A field that appears only when particular hardware is plugged in does not belong in
 * a dialog whose first line promises that nothing here depends on the device.
 */
type ConfigTab = 'General' | 'Acquisition' | 'Measurement' | 'Impedance' | 'Diagnostics'
const TABS: ConfigTab[] = ['General', 'Acquisition', 'Measurement', 'Impedance', 'Diagnostics']
const activeTab = ref<ConfigTab>('General')

// Fetched when the dialog opens rather than on mount: it is a round trip for something most
// sessions never look at, and reopening picks up a calibration stored from a measurement meanwhile.
watch(
  () => props.open,
  (open) => {
    if (!open) return
    void loadFixture(true)
    // The record counters and the last recording move while the dialog is shut, and a stale
    // "0 records observed" reads as a diagnostic that is not working.
    void refreshDiagnostics()
  },
  { immediate: true },
)

/**
 * The capture delay is edited through its own draft string, and that is not a style choice — it is
 * the fix for a bug that made these fields look broken.
 *
 * <p>Bound as <c>:value="theStoredRef"</c>, a field is overwritten on <b>every re-render</b>, not
 * merely when the stored value changes: Vue's prop patching carries an explicit
 * <c>next !== prev || key === "value"</c>, so an unchanged <c>value</c> prop is still written back
 * to the element. This dialog used to re-render about once a second, because it read the polled
 * device status — so typing "-3" into a field put it back to 0 before it could be committed, and
 * emptying one made a 0 appear under the cursor. Reported 2026-09-30. (It was External gain this was
 * found on; that setting now lives on the status bar, and its popover carries the same drafts for
 * the same reason.)</p>
 *
 * <p>The poll itself has gone — the two device settings that needed it moved to
 * <c>Qa403Dialog.vue</c>, which stages its own field the same way — but the draft stays: any watcher
 * or sibling write re-renders this dialog, and a field that cannot survive a re-render is a field
 * waiting for the next one.</p>
 *
 * <p>With <c>v-model</c> on a draft the element's value <i>is</i> the bound value, so the patch is
 * a no-op and the text survives. The draft is a string rather than a number so the field can be
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

          <!-- Said rather than left to be hunted for: both of these were on this tab until
               2026-10-05, and someone who knew where they were will come back here first. -->
          <p class="text-xs text-neutral-500 dark:text-neutral-400">
            The experimental 384 kHz sample rate and the auto-range step-down delay belong to the
            analyzer rather than to the bench, and are now in <strong>QA403 settings</strong> — the
            gear beside the status bar's Analyzer picker.
          </p>
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

        <template v-else-if="activeTab === 'Diagnostics'">
          <p class="text-xs text-neutral-500 dark:text-neutral-400">
            What the app records about its own behaviour, for a problem report. The log is always
            written; the two switches below it write much more and are off until asked for.
          </p>

          <!-- The switch to ask a tester for. The folder line and the button are the other half
               of that request: a log file is only sendable by someone who can find it. -->
          <div
            class="flex flex-col gap-1.5"
            title="Adds to the log file every request the app handles, with its outcome and timing, plus the device layer's own debug lines. On by default, because the faults worth reporting here are intermittent — the first anyone hears of one is after the run that showed it. Off, the app still keeps its ordinary log."
          >
            <label class="flex items-center gap-2">
              <input
                type="checkbox"
                :checked="diagnosticLoggingEnabled"
                @change="setDiagnosticLogging(($event.target as HTMLInputElement).checked)"
              />
              Detailed logging
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

          <div class="flex flex-col gap-1.5 border-t border-neutral-200 pt-3 dark:border-neutral-800">
            <label
              class="flex items-center gap-2"
              title="Writes every acquisition to disk as two WAV files: what the analyzer digitized, and the stimulus the generator played. One pair per Run or per sweep. Both are 32-bit float in volts, so any audio editor shows the same levels the app does."
            >
              <input
                type="checkbox"
                :checked="streamRecording?.enabled ?? false"
                @change="setStreamRecording(($event.target as HTMLInputElement).checked)"
              />
              Record every acquisition to WAV
            </label>
            <p class="text-xs text-neutral-500 dark:text-neutral-400">
              1.5 MB per second per direction at 192 kHz, so each file stops at the cap and only the
              newest {{ streamRecording?.keptRecordings ?? 10 }} recordings are kept. The output file
              shows what the generator was asked to play; it cannot show a gap in what the DAC
              managed to play, which the log's transfer summary reports instead.
            </p>
            <label
              class="flex items-center gap-2 text-xs"
              title="Recording stops when either file reaches this size; the measurement itself carries on, and the log says the file was cut short. 256 MB is about 2.8 minutes at 192 kHz stereo."
            >
              <span class="text-neutral-500 dark:text-neutral-400">Stop each file at</span>
              <input
                type="number"
                min="16"
                max="4096"
                step="16"
                v-model.number="recordingCapMb.value"
                class="w-24 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
                @change="setStreamRecording(streamRecording?.enabled ?? false, recordingCapMb.value)"
              />
              <span class="text-neutral-500 dark:text-neutral-400">MB</span>
            </label>
            <div class="flex flex-wrap items-center gap-2 text-xs text-neutral-500 dark:text-neutral-400">
              <span class="min-w-0 break-all font-mono" :title="streamRecording?.directory ?? ''">
                {{ streamRecording?.directory ?? '…' }}
              </span>
              <button
                type="button"
                class="rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
                title="Opens the recordings folder in the file manager."
                @click="openRecordingsFolder"
              >
                Open recordings folder
              </button>
            </div>
            <p v-if="streamRecording?.recording" class="text-xs text-amber-600 dark:text-amber-400">
              Recording now: {{ streamRecording.currentName }}
            </p>
            <p v-else-if="lastRecordingSummary" class="text-xs text-neutral-500 dark:text-neutral-400">
              Last recording — {{ lastRecordingSummary }}
            </p>
            <p v-if="(streamRecording?.droppedBuffers ?? 0) > 0" class="text-xs text-amber-600 dark:text-amber-400">
              {{ streamRecording?.droppedBuffers }} buffers dropped while writing, so a file has a
              splice in it. The measurements are unaffected — this is the recorder falling behind the
              disk, not the sample stream.
            </p>
          </div>

          <div class="flex flex-col gap-1.5 border-t border-neutral-200 pt-3 dark:border-neutral-800">
            <label
              class="flex items-center gap-2"
              title="Logs one row per analysed record, and saves the samples of any record whose noise floor jumps clear of its own recent baseline or that contains a held-constant span — the waveform of a glitch, which the spectrum cannot distinguish from a noisy DUT. Resets when the app restarts."
            >
              <input
                type="checkbox"
                :checked="recordCapture?.enabled ?? false"
                @change="setRecordCapture({ enabled: ($event.target as HTMLInputElement).checked })"
              />
              Capture suspect records (until the app restarts)
            </label>
            <label
              class="flex items-center gap-2 text-xs"
              title="How far above its own recent baseline a record's noise floor must jump to be captured. 6 dB is well clear of the couple of dB a healthy floor breathes by."
            >
              <span class="text-neutral-500 dark:text-neutral-400">Capture when the floor jumps</span>
              <input
                type="number"
                min="1"
                step="1"
                :value="recordCapture?.triggerThresholdDb ?? 6"
                class="w-20 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
                @change="setRecordCapture({ triggerThresholdDb: Number(($event.target as HTMLInputElement).value) })"
              />
              <span class="text-neutral-500 dark:text-neutral-400">dB</span>
            </label>
            <div class="flex flex-wrap items-center gap-2 text-xs text-neutral-500 dark:text-neutral-400">
              <span class="min-w-0 break-all font-mono" :title="recordCapture?.directory ?? ''">
                {{ recordCapture?.directory ?? '…' }}
              </span>
              <button
                type="button"
                class="rounded border border-neutral-300 px-2 py-0.5 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
                title="Opens the diagnostics folder in the file manager."
                @click="openDiagnosticsFolder"
              >
                Open diagnostics folder
              </button>
            </div>
            <p v-if="recordCapture" class="text-xs text-neutral-500 dark:text-neutral-400">
              {{ recordCapture.recordsObserved }} records observed, {{ recordCapture.capturesWritten }} captured.
            </p>
          </div>

          <p v-if="diagnosticsError" class="text-xs text-red-600 dark:text-red-400">{{ diagnosticsError }}</p>
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
                class="w-fit rounded bg-neutral-900 px-2 py-1 text-xs text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
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
