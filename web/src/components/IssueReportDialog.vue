<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import Modal from './Modal.vue'
import PanelIcon from './PanelIcon.vue'
import { api, issueReportDownloadUrl, type IssueReportResult } from '../api/client'
import {
  ISSUE_QUESTIONS,
  answersFor,
  describeBytes,
  describeIssueReportProblem,
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

/**
 * The glyph on each attachment row — a page, a camera, a trace, a level meter, a cog. Here rather
 * than in `panelIcons.ts` because these five are not panel headers: they are a file-type hint
 * inside one list, drawn in the neutral `.tb-row-glyph` circle rather than in a kind accent.
 */
const ROW_GLYPHS = {
  log: 'M6 2h9l5 5v15H6zM9 13h8M9 17h6',
  screenshot: 'M3 8h4l2-3h6l2 3h4v12H3z',
  diagnostics: 'M2 12h4l2-6 4 12 3-9 2 3h5',
  audio: 'M4 10v4M8 7v10M12 4v16M16 8v8M20 11v2',
  settings:
    'M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1',
} as const

/** The five row glyphs are all drawn the same way, so the attributes are bound rather than repeated. */
const ROW_SVG = {
  width: 13,
  height: 13,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  'stroke-width': 2,
  'stroke-linecap': 'round',
  'stroke-linejoin': 'round',
  'aria-hidden': 'true',
} as const

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
      problem.value = describeIssueReportProblem(e, 'listing')
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
    problem.value = describeIssueReportProblem(e, 'building')
  } finally {
    building.value = false
  }
}

async function openFolder() {
  try {
    await api.openIssueReportFolder()
  } catch (e) {
    problem.value = describeIssueReportProblem(e, 'openingFolder')
  }
}
</script>

<template>
  <!-- The 2026-10-13 chrome. Two cards: the questions, numbered, each with its answer indented
       under its own number; and what else goes in the zip, one row per attachment with its size at
       the right. The hint that used to sit above the buttons is on the footer strip beside them. -->
  <Modal :open="props.open" title="Report an issue" icon="bug" tone="red" size="lg" ground flush @close="emit('close')">
    <div class="flex max-h-[68vh] flex-col gap-3.5 overflow-y-auto p-4 text-[13px]">
      <p class="shrink-0 px-0.5 text-[12.5px] leading-[1.5] text-neutral-600 dark:text-neutral-300">
        Answer what you can — anything is better than nothing — and press <strong>Build report</strong>. It
        writes one zip holding your answers, the chart on screen, the log, and every setting and
        calibration of this bench. Nothing is sent anywhere: the zip stays on this machine until you
        attach it to a mail yourself.
      </p>

      <section class="dialog-card shrink-0 px-3.5 pt-1 pb-3.5">
        <div
          v-for="(question, i) in ISSUE_QUESTIONS"
          :key="question.id"
          class="flex flex-col gap-1.5 py-3"
          :class="i < ISSUE_QUESTIONS.length - 1 ? 'border-b border-neutral-100 dark:border-neutral-800' : ''"
        >
          <label :for="`issue-${question.id}`" class="flex items-center gap-2.5 text-[13.5px] font-semibold">
            <!-- The number carries the accent only while the answer is one of the useful ones; on
                 the rest it is grey, which is the whole of what `wanted` used to say in words. -->
            <span
              class="flex size-[22px] shrink-0 items-center justify-center rounded-full text-[12px] font-semibold"
              :style="
                question.wanted
                  ? { backgroundColor: 'var(--kind-red-chip-bg)', color: 'var(--kind-red-chip-fg)' }
                  : { backgroundColor: 'var(--surface-control)' }
              "
              :class="question.wanted ? '' : 'text-neutral-600 dark:text-neutral-300'"
            >
              {{ i + 1 }}
            </span>
            <span class="min-w-0">{{ question.question }}</span>
          </label>
          <!-- Indented by a wrapper rather than by a margin on the box itself: `tb-input` is
               `w-full`, so `ml-8` on it put 32 px of the textarea outside the card (Joost, 2026-10-13). -->
          <div class="pl-8">
            <textarea
              :id="`issue-${question.id}`"
              v-model="answers[question.id]"
              :rows="question.rows"
              :placeholder="question.placeholder"
              class="tb-input h-auto resize-y py-2 leading-[1.45]"
            ></textarea>
          </div>
        </div>
      </section>

      <section class="dialog-card shrink-0">
        <div class="flex items-center gap-2.5 px-3 py-2.5">
          <PanelIcon icon="paperclip" tone="indigo" :size="28" />
          <h3 class="flex-1 text-[14px] font-semibold">What else goes in</h3>
          <span class="font-mono text-[12px] text-neutral-500 dark:text-neutral-400">≈ {{ describeBytes(estimate) }}</span>
        </div>

        <div class="px-3.5 pb-1">
          <div class="tb-file-row border-t border-neutral-100 dark:border-neutral-800">
            <span class="tb-row-glyph">
              <svg v-bind="ROW_SVG"><path :d="ROW_GLYPHS.log" /></svg>
            </span>
            <label
              for="issue-log-days"
              title="The application log, one file per day. Today's is the one that covers what you just saw; a fault that was noticed the day after needs two."
            >
              Log
            </label>
            <span class="flex items-center gap-2.5">
              <select id="issue-log-days" v-model.number="selection.logDays" class="tb-input-sm w-[124px]">
                <option v-for="days in LOG_DAY_OPTIONS" :key="days" :value="days">
                  {{ days === 0 ? 'none' : days === 1 ? 'today only' : `last ${days} days` }}
                </option>
              </select>
              <span class="w-[120px] text-right font-mono text-[12px] text-neutral-500 dark:text-neutral-400">
                {{ logsChosen.length }} file{{ logsChosen.length === 1 ? '' : 's' }}
              </span>
            </span>
          </div>

          <label class="tb-file-row cursor-pointer border-t border-neutral-100 dark:border-neutral-800">
            <span class="tb-row-glyph">
              <svg v-bind="ROW_SVG"><path :d="ROW_GLYPHS.screenshot" /><circle cx="12" cy="13" r="3.5" /></svg>
            </span>
            <span
              :title="
                hasChart
                  ? 'The chart on screen, exactly as the Screenshot button would export it.'
                  : 'This screen has no chart to export, so there is nothing to include.'
              "
            >
              Screenshot of the chart
            </span>
            <span class="flex items-center gap-2.5">
              <span class="w-[120px] text-right text-[12px] text-neutral-500 dark:text-neutral-400">
                <span :class="hasChart ? 'font-mono' : ''">
                  {{ hasChart ? describeBytes(screenshotBytes) : 'no chart on this screen' }}
                </span>
              </span>
              <input v-model="selection.includeScreenshot" type="checkbox" class="tb-check" :disabled="!hasChart" />
            </span>
          </label>

          <label class="tb-file-row cursor-pointer border-t border-neutral-100 dark:border-neutral-800">
            <span class="tb-row-glyph">
              <svg v-bind="ROW_SVG"><path :d="ROW_GLYPHS.diagnostics" /></svg>
            </span>
            <span
              title="Per-record diagnostic captures, written only while Configuration ▸ Diagnostics has them armed. They are what distinguishes a gap in the samples from a noisy amplifier."
            >
              Diagnostic captures
            </span>
            <span class="flex items-center gap-2.5">
              <span class="w-[120px] text-right text-[12px] text-neutral-500 dark:text-neutral-400">
                {{
                  contents && contents.diagnostics.length > 0
                    ? `${contents.diagnostics.length} file${contents.diagnostics.length === 1 ? '' : 's'}`
                    : 'none recorded'
                }}
              </span>
              <input v-model="selection.includeDiagnostics" type="checkbox" class="tb-check" />
            </span>
          </label>

          <label class="tb-file-row cursor-pointer border-t border-neutral-100 dark:border-neutral-800">
            <span class="tb-row-glyph">
              <svg v-bind="ROW_SVG"><path :d="ROW_GLYPHS.audio" /></svg>
            </span>
            <span
              title="The newest acquisition's WAV pair — what went to the output and what came back in. The only thing that can show the wrong stimulus having been played. Large."
            >
              Recorded audio <span class="text-neutral-500 dark:text-neutral-400">(newest acquisition)</span>
            </span>
            <span class="flex items-center gap-2.5">
              <!-- The size is amber when there is one, because this is the row that turns a 500 kB
                   report into a 300 MB one. -->
              <span
                class="w-[120px] text-right text-[12px]"
                :class="recordingSet.length > 0 ? 'font-mono text-amber-600 dark:text-amber-500' : 'text-neutral-500 dark:text-neutral-400'"
              >
                {{
                  recordingSet.length > 0
                    ? describeBytes(recordingSet.reduce((t, f) => t + f.bytes, 0))
                    : contents?.streamRecordingEnabled
                      ? 'armed, nothing yet'
                      : 'not armed'
                }}
              </span>
              <input
                v-model="selection.includeRecordings"
                type="checkbox"
                class="tb-check"
                :disabled="recordingSet.length === 0"
              />
            </span>
          </label>

          <label class="tb-file-row cursor-pointer border-t border-neutral-100 dark:border-neutral-800">
            <span class="tb-row-glyph">
              <svg v-bind="ROW_SVG"><path :d="ROW_GLYPHS.settings" /><circle cx="12" cy="12" r="3" /></svg>
            </span>
            <span
              title="Every stored setting, including this bench's sound-card, loopback and impedance-fixture calibrations. Almost always the thing that explains a reading."
            >
              Settings and calibrations
            </span>
            <span class="flex items-center gap-2.5">
              <span class="w-[120px] text-right text-[12px] text-neutral-500 dark:text-neutral-400">
                your bench's own figures
              </span>
              <input v-model="selection.includeSettings" type="checkbox" class="tb-check" />
            </span>
          </label>
        </div>

        <p
          class="border-t border-neutral-200 px-3.5 py-2.5 text-[12px] text-neutral-500 dark:border-neutral-800 dark:text-neutral-400"
          :style="{ backgroundColor: 'var(--surface-ground)' }"
        >
          About <span class="font-mono text-neutral-900 dark:text-neutral-100">{{ describeBytes(estimate) }}</span> before
          compression.
          <span v-if="contents && !contents.diagnosticLoggingEnabled" class="text-amber-600 dark:text-amber-500">
            Detailed logging is off, so the log holds less than it could — Configuration ▸ Diagnostics
            turns it on, and most faults here are intermittent enough to be worth reproducing once
            with it on.
          </span>
        </p>
      </section>

      <p
        v-if="problem"
        class="dialog-card shrink-0 border-red-300 px-3 py-2 text-[12px] text-red-800 dark:border-red-700/60 dark:text-red-300"
      >
        {{ problem }}
      </p>

      <section v-if="result" class="dialog-card flex shrink-0 flex-col gap-2 border-emerald-300 p-3 text-[12px] dark:border-emerald-700/60">
        <p>
          <strong>{{ result.fileName }}</strong> — {{ describeBytes(result.bytes) }}
        </p>
        <p class="font-mono text-[11px] break-all text-neutral-500 dark:text-neutral-400">{{ result.path }}</p>
        <ul class="list-inside list-disc text-neutral-600 dark:text-neutral-300">
          <li v-for="line in result.included" :key="line">{{ line }}</li>
        </ul>
        <ul v-if="result.warnings.length" class="list-inside list-disc text-amber-700 dark:text-amber-500">
          <li v-for="line in result.warnings" :key="line">{{ line }}</li>
        </ul>
      </section>
    </div>

    <template #footer>
      <span
        v-if="!canBuild && !building"
        class="flex flex-1 items-center gap-1.5 text-[12px] text-neutral-500 dark:text-neutral-400"
      >
        <svg
          width="14"
          height="14"
          viewBox="0 0 24 24"
          fill="none"
          stroke="#b45309"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <circle cx="12" cy="12" r="9" />
          <path d="M12 11v5M12 8h.01" />
        </svg>
        Answer at least one question first.
      </span>
      <span v-else class="flex-1"></span>

      <button type="button" class="btn-secondary" @click="emit('close')">Close</button>
      <button v-if="result" type="button" class="btn-secondary" @click="openFolder">Open folder</button>
      <!-- A plain link, so the browser's own download does the saving: a Blob built in JS would
           have to hold a report with a 256 MB WAV in it in memory first. -->
      <a
        v-if="result"
        :href="issueReportDownloadUrl(result.fileName)"
        :download="result.fileName"
        class="btn-primary"
      >
        Download
      </a>
      <button v-else type="button" class="btn-primary" :disabled="!canBuild" @click="build">
        <svg
          width="14"
          height="14"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M21 8v13H3V8M1 3h22v5H1zM10 12h4" />
        </svg>
        {{ building ? 'Building…' : 'Build report' }}
      </button>
    </template>
  </Modal>
</template>
