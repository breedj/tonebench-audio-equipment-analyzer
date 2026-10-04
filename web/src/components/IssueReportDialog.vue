<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import Modal from './Modal.vue'
import { api, issueReportDownloadUrl, type IssueReportResult } from '../api/client'
import {
  ISSUE_QUESTIONS,
  answersFor,
  describeBytes,
  estimateReportBytes,
  hasSomethingToReport,
  logsWithin,
  newestRecordingSet,
  type IssueReportContents,
} from '../issueReport'

/**
 * **Report an issue.** Six questions, a list of what else to put in the zip, and a button that
 * builds it. The user then sends that one file instead of answering the same three questions by
 * mail — which analyzer, which calibration, what was it playing, is the log still there.
 *
 * <p>The questions and the size arithmetic are `issueReport.ts`; what is here is the form and the
 * two things it needs from the app: the chart on screen as a PNG (`screenshot`, a callback rather
 * than a prop, so nothing is rendered until the user actually asks for a report) and a dump of the
 * client's own state, which the server cannot see.</p>
 *
 * <p><b>Nothing is sent anywhere.</b> The zip is written into a folder on this machine and the
 * dialog offers a download and an *Open folder*; where it goes after that is the user's decision.
 * That is deliberate rather than unfinished — the report carries this bench's calibrations and its
 * project paths.</p>
 */
const props = defineProps<{
  open: boolean
  /** The chart on screen as a PNG, base64 without the data-URL prefix, or null where there is no chart. */
  screenshot: () => Promise<string | null>
  /** Everything the browser knows and the server does not. Serialized as the report's `client` block. */
  clientState: () => unknown
}>()

const emit = defineEmits<{ close: [] }>()

const answers = reactive<Record<string, string>>({})
const selection = reactive({
  logDays: 2,
  includeScreenshot: true,
  includeDiagnostics: true,
  includeRecordings: false,
  includeSettings: true,
})

const contents = ref<IssueReportContents | null>(null)
const screenshotBytes = ref(0)
const building = ref(false)
const problem = ref<string | null>(null)
const result = ref<IssueReportResult | null>(null)

// The window the Log dropdown offers. 14 is the retention, so there is never a point offering more.
const LOG_DAY_OPTIONS = [0, 1, 2, 3, 7, 14]

watch(
  () => props.open,
  async (open) => {
    if (!open) return

    // The listing is re-read on every open rather than cached: a log file grows while the app runs,
    // and a recording appears the moment an acquisition with the recorder armed finishes. A figure
    // from when the dialog was last opened would be wrong in the one direction that matters.
    result.value = null
    problem.value = null
    try {
      contents.value = await api.getIssueReportContents()
    } catch (e) {
      contents.value = null
      problem.value = e instanceof Error ? e.message : String(e)
    }

    // Measured rather than guessed, because the chart's PNG is most of a small report's size and
    // because this is also how we find out whether there is a chart at all.
    screenshotBytes.value = 0
    try {
      const png = await props.screenshot()
      screenshotBytes.value = png ? Math.ceil((png.length * 3) / 4) : 0
      if (!png) selection.includeScreenshot = false
    } catch {
      screenshotBytes.value = 0
      selection.includeScreenshot = false
    }
  },
  { immediate: true },
)

const hasChart = computed(() => screenshotBytes.value > 0)

const logsChosen = computed(() => (contents.value ? logsWithin(contents.value.logs, selection.logDays) : []))
const recordingSet = computed(() => (contents.value ? newestRecordingSet(contents.value.recordings) : []))

const estimate = computed(() =>
  contents.value ? estimateReportBytes(contents.value, selection, screenshotBytes.value) : 0,
)

const canBuild = computed(() => !building.value && hasSomethingToReport(answers))

async function build() {
  building.value = true
  problem.value = null
  result.value = null

  try {
    // Taken again here rather than reusing the open-time capture: the user may well have changed
    // the view, the zoom or the overlay while typing, and the picture that belongs in the report is
    // the one that was on screen when they pressed the button.
    const png = selection.includeScreenshot ? await props.screenshot() : null

    let clientStateJson: string | null = null
    try {
      clientStateJson = JSON.stringify(props.clientState())
    } catch {
      // A state dump that cannot be serialized (a cycle, a DOM node that slipped in) is not a
      // reason to lose the report; the server writes a note in its place.
      clientStateJson = null
    }

    result.value = await api.buildIssueReport({
      answers: answersFor(answers),
      screenshotPngBase64: png,
      logDays: selection.logDays,
      includeDiagnostics: selection.includeDiagnostics,
      includeRecordings: selection.includeRecordings,
      includeSettings: selection.includeSettings,
      clientStateJson,
    })
  } catch (e) {
    problem.value = e instanceof Error ? e.message : String(e)
  } finally {
    building.value = false
  }
}

async function openFolder() {
  try {
    await api.openIssueReportFolder()
  } catch (e) {
    problem.value = e instanceof Error ? e.message : String(e)
  }
}
</script>

<template>
  <Modal :open="props.open" title="Report an issue" wide @close="emit('close')">
    <div class="flex max-h-[70vh] flex-col gap-4 overflow-y-auto text-sm">
      <p class="text-xs text-neutral-500 dark:text-neutral-400">
        Answer what you can — anything is better than nothing — and press <strong>Build report</strong>. It
        writes one zip holding your answers, the chart on screen, the log, and every setting and
        calibration of this bench. Nothing is sent anywhere: the zip stays on this machine until you
        attach it to a mail yourself.
      </p>

      <div v-for="question in ISSUE_QUESTIONS" :key="question.id" class="flex flex-col gap-1">
        <label :for="`issue-${question.id}`" class="flex items-baseline gap-2">
          <span>{{ question.question }}</span>
          <span v-if="question.wanted" class="text-[10px] uppercase tracking-wide text-neutral-400">
            most useful
          </span>
        </label>
        <textarea
          :id="`issue-${question.id}`"
          v-model="answers[question.id]"
          :rows="question.rows"
          :placeholder="question.placeholder"
          class="w-full rounded border border-neutral-300 px-2 py-1 font-sans text-sm dark:border-neutral-700"
        ></textarea>
      </div>

      <fieldset class="flex flex-col gap-2 rounded border border-neutral-200 p-3 dark:border-neutral-800">
        <legend class="px-1 text-xs uppercase tracking-wide text-neutral-500">What else goes in</legend>

        <label class="flex items-center justify-between gap-3">
          <span
            class="text-neutral-600 dark:text-neutral-300"
            title="The application log, one file per day. Today's is the one that covers what you just saw; a fault that was noticed the day after needs two."
          >
            Log
          </span>
          <span class="flex w-64 items-center gap-2">
            <select
              v-model.number="selection.logDays"
              class="flex-1 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
            >
              <option v-for="days in LOG_DAY_OPTIONS" :key="days" :value="days">
                {{ days === 0 ? 'none' : days === 1 ? 'today only' : `last ${days} days` }}
              </option>
            </select>
            <span class="w-24 text-right text-xs text-neutral-500">
              {{ logsChosen.length }} file{{ logsChosen.length === 1 ? '' : 's' }}
            </span>
          </span>
        </label>

        <label class="flex items-center justify-between gap-3">
          <span
            class="text-neutral-600 dark:text-neutral-300"
            :title="
              hasChart
                ? 'The chart on screen, exactly as the Screenshot button would export it.'
                : 'This screen has no chart to export, so there is nothing to include.'
            "
          >
            Screenshot of the chart
          </span>
          <span class="flex w-64 items-center gap-2">
            <input v-model="selection.includeScreenshot" type="checkbox" :disabled="!hasChart" />
            <span class="text-xs text-neutral-500">
              {{ hasChart ? describeBytes(screenshotBytes) : 'no chart on this screen' }}
            </span>
          </span>
        </label>

        <label class="flex items-center justify-between gap-3">
          <span
            class="text-neutral-600 dark:text-neutral-300"
            title="Per-record diagnostic captures, written only while Configuration ▸ Diagnostics has them armed. They are what distinguishes a gap in the samples from a noisy amplifier."
          >
            Diagnostic captures
          </span>
          <span class="flex w-64 items-center gap-2">
            <input v-model="selection.includeDiagnostics" type="checkbox" />
            <span class="text-xs text-neutral-500">
              {{
                contents && contents.diagnostics.length > 0
                  ? `${contents.diagnostics.length} file${contents.diagnostics.length === 1 ? '' : 's'}`
                  : 'none recorded'
              }}
            </span>
          </span>
        </label>

        <label class="flex items-center justify-between gap-3">
          <span
            class="text-neutral-600 dark:text-neutral-300"
            title="The newest acquisition's WAV pair — what went to the output and what came back in. The only thing that can show the wrong stimulus having been played. Large."
          >
            Recorded audio (newest acquisition)
          </span>
          <span class="flex w-64 items-center gap-2">
            <input
              v-model="selection.includeRecordings"
              type="checkbox"
              :disabled="recordingSet.length === 0"
            />
            <span class="text-xs" :class="recordingSet.length > 0 ? 'text-amber-600 dark:text-amber-500' : 'text-neutral-500'">
              {{
                recordingSet.length > 0
                  ? describeBytes(recordingSet.reduce((t, f) => t + f.bytes, 0))
                  : contents?.streamRecordingEnabled
                    ? 'armed, but nothing recorded yet'
                    : 'not armed'
              }}
            </span>
          </span>
        </label>

        <label class="flex items-center justify-between gap-3">
          <span
            class="text-neutral-600 dark:text-neutral-300"
            title="Every stored setting, including this bench's sound-card, loopback and impedance-fixture calibrations. Almost always the thing that explains a reading."
          >
            Settings and calibrations
          </span>
          <span class="flex w-64 items-center gap-2">
            <input v-model="selection.includeSettings" type="checkbox" />
            <span class="text-xs text-neutral-500">your bench's own figures</span>
          </span>
        </label>

        <p class="text-xs text-neutral-500 dark:text-neutral-400">
          About {{ describeBytes(estimate) }} before compression.
          <span v-if="contents && !contents.diagnosticLoggingEnabled" class="text-amber-600 dark:text-amber-500">
            Detailed logging is off, so the log holds less than it could — Configuration ▸ Diagnostics
            turns it on, and most faults here are intermittent enough to be worth reproducing once
            with it on.
          </span>
        </p>
      </fieldset>

      <p v-if="problem" class="rounded border border-red-300 px-3 py-2 text-xs text-red-800 dark:border-red-700/60 dark:text-red-300">
        {{ problem }}
      </p>

      <div
        v-if="result"
        class="flex flex-col gap-2 rounded border border-emerald-300 px-3 py-2 text-xs dark:border-emerald-700/60"
      >
        <p>
          <strong>{{ result.fileName }}</strong> — {{ describeBytes(result.bytes) }}
        </p>
        <p class="break-all font-mono text-[11px] text-neutral-500 dark:text-neutral-400">{{ result.path }}</p>
        <ul class="list-inside list-disc text-neutral-600 dark:text-neutral-300">
          <li v-for="line in result.included" :key="line">{{ line }}</li>
        </ul>
        <ul v-if="result.warnings.length" class="list-inside list-disc text-amber-700 dark:text-amber-500">
          <li v-for="line in result.warnings" :key="line">{{ line }}</li>
        </ul>
      </div>

      <div class="flex items-center justify-end gap-2 border-t border-neutral-200 pt-3 dark:border-neutral-800">
        <span v-if="!canBuild && !building" class="mr-auto text-xs text-neutral-500">
          Answer at least one question first.
        </span>
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 text-xs dark:border-neutral-700"
          @click="emit('close')"
        >
          Close
        </button>
        <button
          v-if="result"
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 text-xs dark:border-neutral-700"
          @click="openFolder"
        >
          Open folder
        </button>
        <!-- A plain link, so the browser's own download does the saving: a Blob built in JS would
             have to hold a report with a 256 MB WAV in it in memory first. -->
        <a
          v-if="result"
          :href="issueReportDownloadUrl(result.fileName)"
          :download="result.fileName"
          class="rounded bg-sky-600 px-3 py-1 text-xs text-white hover:bg-sky-500"
        >
          Download
        </a>
        <button
          v-else
          type="button"
          class="rounded bg-sky-600 px-3 py-1 text-xs text-white hover:bg-sky-500 disabled:bg-neutral-300 disabled:text-neutral-500 dark:disabled:bg-neutral-700 dark:disabled:text-neutral-400"
          :disabled="!canBuild"
          @click="build"
        >
          {{ building ? 'Building…' : 'Build report' }}
        </button>
      </div>
    </div>
  </Modal>
</template>
