<script setup lang="ts">
/**
 * Project ▸ Import…: reads another project file and copies the measurements and results you tick
 * into the open project.
 *
 * It opens the file browser immediately, because there is nothing to show until a file has been
 * read — and cancelling that before anything was read closes the whole dialog, since there is
 * nothing behind it to go back to. That is `SimulationImportDialog`'s shape, including keeping the
 * last folder after a file was refused: the usable one is almost always next to it.
 *
 * The source file is never written to. A file that arrived by e-mail may be read-only or carry an
 * un-checkpointed log, and the server reads a throwaway copy where it has to — see
 * `ProjectTransferSource`.
 */
import { computed, ref, watch } from 'vue'
import { api, type ProjectContents, type ProjectContentsType, type MeasurementType } from '../api/client'
import {
  EMPTY_SELECTION,
  clashingTypes,
  formatBytes,
  mergeBlocked,
  selectedBytes,
  selectedResultCount,
  transferItems,
  transferReadiness,
  transferSummary,
  unknownKinds,
  type NameClashPolicy,
  type TransferSelectionState,
} from '../projects/transferSelection'
import { IMPULSE_RESPONSE_KIND } from '../measurements/kinds'
import { fileNameOf, parentPath } from '../projects/projectFile'
import FileBrowserDialog from './FileBrowserDialog.vue'
import Modal from './Modal.vue'
import ProjectTransferTree from './ProjectTransferTree.vue'

const props = defineProps<{
  open: boolean
  /** The open project's measurements, for spotting a name that is already taken. */
  types: readonly MeasurementType[]
}>()
const emit = defineEmits<{ close: []; imported: [] }>()

const browserOpen = ref(false)
const busy = ref(false)
const reading = ref(false)
const error = ref<string | null>(null)
/**
 * What the import did, once it has. The dialog stays open to say it rather than closing on a
 * success nobody sees: a rename or a skip is the one thing the user has to be told, and there is no
 * app-wide notice line to put it in (the status bar's is for errors, and in red).
 */
const done = ref<string | null>(null)

const contents = ref<ProjectContents | null>(null)
const selection = ref<TransferSelectionState>(EMPTY_SELECTION)
/** Kept after a refused file: the usable one is almost always in the same folder. */
const lastPath = ref<string | null>(null)

const defaultPolicy = ref<NameClashPolicy>('Rename')
const policies = ref(new Map<number, NameClashPolicy>())

const sourceTypes = computed<readonly ProjectContentsType[]>(() => contents.value?.types ?? [])

/**
 * The open project's measurements in the tree's shape, purely so the clash check has names and
 * kinds to compare against. The sidebar's list is what the user is looking at, so it is the right
 * source — no second request.
 */
const destinationTypes = computed<ProjectContentsType[]>(() =>
  props.types.map((t) => ({
    typeId: t.id,
    kind: t.kind,
    name: t.name,
    notes: t.notes,
    sortOrder: t.sortOrder,
    results: [],
  })),
)

const clashes = computed(() => clashingTypes(sourceTypes.value, destinationTypes.value))
const policyFor = (typeId: number) => policies.value.get(typeId) ?? defaultPolicy.value
const blocked = computed(() => mergeBlocked(sourceTypes.value, clashes.value, policyFor))
const unknown = computed(() => unknownKinds(sourceTypes.value))

const resultCount = computed(() => selectedResultCount(sourceTypes.value, selection.value))
const totalBytes = computed(() => selectedBytes(sourceTypes.value, selection.value))

const LARGE_TRANSFER_BYTES = 100 * 1024 * 1024

/**
 * A measurement's loopback calibration is a row in *this* bench's configuration database, named by
 * an id the other bench's file carries. The stored results are unaffected — the calibration was
 * divided out before they were written — but the measurement's own settings point at a number that
 * means something else here, so it is worth one line before the import rather than a surprise in
 * the panel afterwards.
 */
const importsAnImpulseMeasurement = computed(() =>
  sourceTypes.value.some((t) => selection.value.types.has(t.typeId) && t.kind === IMPULSE_RESPONSE_KIND),
)

const readiness = computed(() =>
  transferReadiness({
    types: sourceTypes.value,
    selection: selection.value,
    hasTarget: contents.value !== null,
    busy: busy.value || reading.value,
    blocked: blocked.value,
    policyFor,
  }),
)

watch(
  () => props.open,
  (open) => {
    if (!open) return
    contents.value = null
    selection.value = EMPTY_SELECTION
    policies.value = new Map()
    defaultPolicy.value = 'Rename'
    error.value = null
    done.value = null
    browserOpen.value = true
  },
)

/** Cancelling the browser before anything has been read closes the dialog: there is nothing behind it. */
function onBrowserClose() {
  browserOpen.value = false
  if (!contents.value) emit('close')
}

async function read(path: string) {
  browserOpen.value = false
  lastPath.value = path
  reading.value = true
  error.value = null
  try {
    contents.value = await api.getProjectContents(path)
    selection.value = EMPTY_SELECTION
    policies.value = new Map()
  } catch (e) {
    contents.value = null
    error.value = (e as Error).message
  } finally {
    reading.value = false
  }
}

async function submit() {
  if (!readiness.value.ready || !contents.value) return

  busy.value = true
  error.value = null
  try {
    const report = await api.importProject({
      path: contents.value.path,
      items: transferItems(sourceTypes.value, selection.value, policies.value),
      onNameClash: defaultPolicy.value,
    })

    done.value = transferSummary(report, 'imported')
    emit('imported')
  } catch (e) {
    error.value = `${(e as Error).message} Nothing was imported.`
  } finally {
    busy.value = false
  }
}

function setPolicy(typeId: number, policy: NameClashPolicy) {
  const next = new Map(policies.value)
  next.set(typeId, policy)
  policies.value = next
}

const sourceName = computed(() => (contents.value ? fileNameOf(contents.value.path) : lastPath.value ? fileNameOf(lastPath.value) : null))
const sourceFolder = computed(() => (contents.value ? parentPath(contents.value.path) : null))
</script>

<template>
  <FileBrowserDialog
    :open="browserOpen"
    mode="open"
    :start-path="lastPath"
    title="Import from a project file"
    remember-as="projectTransfer"
    @close="onBrowserClose"
    @select="read"
  />

  <Modal
    :open="props.open && !browserOpen"
    title="Import measurements"
    icon="paperclip"
    tone="violet"
    size="lg"
    ground
    flush
    @close="emit('close')"
  >
    <div v-if="done" class="flex flex-col gap-3 p-4 text-[13px]">
      <section class="dialog-card shrink-0 p-3.5">
        <p class="text-[13px] font-semibold text-emerald-700 dark:text-emerald-400">{{ done }}</p>
        <p class="mt-2 text-[12px] text-neutral-600 dark:text-neutral-300">
          They are in the sidebar now, with their results in History. Nothing that was already here
          was changed, and the file they came from is untouched.
        </p>
      </section>
    </div>

    <div v-else class="flex flex-col gap-3 p-4 text-[13px]">
      <p class="shrink-0 px-0.5 text-[12.5px] leading-[1.5] text-neutral-600 dark:text-neutral-300">
        Copies measurements out of another project file into this one. The file is only read — it is
        left exactly as it arrived.
      </p>

      <section class="dialog-card shrink-0 p-3.5">
        <div class="flex items-center gap-2.5">
          <div class="min-w-0 flex-1">
            <p v-if="sourceName" class="truncate text-[13px] font-semibold" :title="contents?.path ?? lastPath ?? ''">
              {{ sourceName }}
            </p>
            <p v-else class="text-[13px] text-neutral-500 dark:text-neutral-400">No file chosen yet</p>
            <p v-if="contents" class="truncate text-[11.5px] text-neutral-500 dark:text-neutral-400">
              {{ contents.projectName
              }}<span v-if="contents.projectNotes"> — {{ contents.projectNotes }}</span>
            </p>
            <p v-else-if="sourceFolder" class="truncate font-mono text-[11px] text-neutral-500 dark:text-neutral-400">
              {{ sourceFolder }}
            </p>
          </div>
          <button type="button" class="btn-secondary" :disabled="busy" @click="browserOpen = true">
            {{ contents ? 'Change…' : 'Choose file…' }}
          </button>
        </div>

        <p v-if="error" class="mt-2 text-[12px] text-red-600 dark:text-red-400">{{ error }}</p>
        <p v-else-if="reading" class="mt-2 text-[12px] text-neutral-500 dark:text-neutral-400">Reading that file…</p>
      </section>

      <ProjectTransferTree
        v-if="contents"
        caption="Measurements to import"
        :types="sourceTypes"
        :selection="selection"
        :busy="busy"
        :clashes="clashes"
        :blocked="blocked"
        :policies="policies"
        :default-policy="defaultPolicy"
        :unknown="unknown"
        @update:selection="selection = $event"
        @update:policy="setPolicy"
      />

      <p
        v-if="importsAnImpulseMeasurement"
        class="shrink-0 px-0.5 text-[12px] text-neutral-500 dark:text-neutral-400"
      >
        An impulse-response measurement's loopback calibration does not travel with it — it belongs
        to the bench that took it. Its stored results are already corrected; only the measurement's
        own settings will need a calibration picked here before it is run again.
      </p>

      <p
        v-if="totalBytes > LARGE_TRANSFER_BYTES"
        class="shrink-0 px-0.5 text-[12px] text-amber-700 dark:text-amber-400"
      >
        This copies about {{ formatBytes(totalBytes) }} and can take a minute. The window will not
        respond while it runs.
      </p>
    </div>

    <template #footer>
      <template v-if="done">
        <span class="flex-1"></span>
        <button type="button" class="btn-primary" @click="emit('close')">Close</button>
      </template>
      <template v-else>
      <span v-if="busy" class="flex-1 truncate text-[12px] text-neutral-500 dark:text-neutral-400">Copying…</span>
      <span v-else-if="readiness.reason" class="flex-1 truncate text-[12px] text-neutral-500 dark:text-neutral-400">
        {{ readiness.reason }}
      </span>
      <span v-else class="flex-1 truncate text-[12px] text-neutral-500 dark:text-neutral-400">
        {{ resultCount }} result{{ resultCount === 1 ? '' : 's' }} · {{ formatBytes(totalBytes) }}
      </span>
      <button type="button" class="btn-secondary" :disabled="busy" @click="emit('close')">Cancel</button>
      <button type="button" class="btn-primary" :disabled="!readiness.ready" @click="submit">
        <svg
          width="15"
          height="15"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
          aria-hidden="true"
        >
          <path d="M12 15V3M8 7l4-4 4 4M4 19h16" />
        </svg>
        Import
      </button>
      </template>
    </template>
  </Modal>
</template>
