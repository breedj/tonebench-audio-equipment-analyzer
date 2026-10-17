<script setup lang="ts">
/**
 * Project ▸ Export…: picks measurements and saved results out of the open project and writes them
 * into another `.tbench`, new or existing.
 *
 * The result is an ordinary project file, which is the whole design: the person it is sent to can
 * open it from their own Project menu, or import selected measurements from it into a project they
 * already have. So "export into a new file" and "export into an existing one" are the same
 * operation, and nothing here is a second file format to explain.
 *
 * It opens showing the open project's own contents, and the destination is chosen second — the
 * opposite way round from the Import dialog, which has nothing to show until a file is picked.
 */
import { computed, reactive, ref, watch } from 'vue'
import { api, type ProjectContents, type ProjectContentsType, type Project } from '../api/client'
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
import { PROJECT_EXTENSION, fileNameOf, parentPath } from '../projects/projectFile'
import FileBrowserDialog from './FileBrowserDialog.vue'
import Modal from './Modal.vue'
import ProjectTransferTree from './ProjectTransferTree.vue'

const props = defineProps<{ open: boolean; project: Project | null }>()
const emit = defineEmits<{ close: []; exported: [] }>()

const browserOpen = ref(false)
const loading = ref(false)
const busy = ref(false)
const error = ref<string | null>(null)
/**
 * What the transfer did, once it has. The dialog stays open to say it rather than closing on a
 * success nobody sees: a rename or a skip is the one thing about a transfer the user has to be told,
 * and there is no app-wide notice line to put it in (the status bar's is for errors, and in red).
 */
const done = ref<string | null>(null)

/** The open project's tree, loaded when the dialog opens. */
const contents = ref<ProjectContents | null>(null)
const selection = ref<TransferSelectionState>(EMPTY_SELECTION)

/** The destination, and whatever the server could tell us about it. */
const destinationPath = ref<string | null>(null)
const destination = ref<ProjectContents | null>(null)
const destinationError = ref<string | null>(null)
const checkingDestination = ref(false)

const form = reactive({ name: '', notes: '' })
const defaultPolicy = ref<NameClashPolicy>('Rename')
const policies = ref(new Map<number, NameClashPolicy>())

const types = computed<readonly ProjectContentsType[]>(() => contents.value?.types ?? [])

/** A destination that exists and is a project; null for one that will be created. */
const existing = computed(() => destination.value)

const clashes = computed(() =>
  existing.value ? clashingTypes(types.value, existing.value.types) : new Map<number, ProjectContentsType>(),
)
const policyFor = (typeId: number) => policies.value.get(typeId) ?? defaultPolicy.value
const blocked = computed(() => mergeBlocked(types.value, clashes.value, policyFor))
const unknown = computed(() => unknownKinds(types.value))

const resultCount = computed(() => selectedResultCount(types.value, selection.value))
const totalBytes = computed(() => selectedBytes(types.value, selection.value))

/** Over this, the transfer is worth warning about before it is started. */
const LARGE_TRANSFER_BYTES = 100 * 1024 * 1024

const readiness = computed(() =>
  transferReadiness({
    types: types.value,
    selection: selection.value,
    hasTarget: destinationPath.value !== null && destinationError.value === null,
    busy: busy.value || loading.value || checkingDestination.value,
    blocked: blocked.value,
    policyFor,
  }),
)

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    error.value = null
    done.value = null
    destinationError.value = null
    destinationPath.value = null
    destination.value = null
    selection.value = EMPTY_SELECTION
    policies.value = new Map()
    defaultPolicy.value = 'Rename'
    form.name = props.project?.name ?? ''
    form.notes = ''

    loading.value = true
    try {
      contents.value = await api.getProjectContents()
    } catch (e) {
      contents.value = null
      error.value = (e as Error).message
    } finally {
      loading.value = false
    }
  },
)

/**
 * Looks at the chosen destination so the dialog can say what will happen to it — appended to, or
 * created — and so the clash choices can be offered against the names it already holds. A file that
 * is not a project is reported with the server's own sentence and the path is kept, because the next
 * thing anyone does is pick a different file from the same folder.
 */
async function chooseDestination(path: string) {
  browserOpen.value = false
  destinationPath.value = path
  destination.value = null
  destinationError.value = null
  policies.value = new Map()

  checkingDestination.value = true
  try {
    destination.value = await api.getProjectContents(path)
    form.name = destination.value.projectName
  } catch (e) {
    // A path that does not exist yet is the ordinary case for an export, not a fault: the server
    // says so and the file is created on the way out.
    const message = (e as Error).message
    if (/does not exist/i.test(message)) {
      form.name = props.project?.name ?? ''
    } else {
      destinationError.value = message
    }
  } finally {
    checkingDestination.value = false
  }
}

async function submit() {
  if (!readiness.value.ready || destinationPath.value === null) return

  busy.value = true
  error.value = null
  try {
    const report = await api.exportProject({
      path: destinationPath.value,
      projectName: existing.value ? null : form.name.trim() || null,
      projectNotes: existing.value ? null : form.notes.trim() || null,
      items: transferItems(types.value, selection.value, policies.value),
      onNameClash: defaultPolicy.value,
    })

    done.value = transferSummary(report, 'exported')
    emit('exported')
  } catch (e) {
    error.value = `${(e as Error).message} Nothing was exported.`
  } finally {
    busy.value = false
  }
}

function setPolicy(typeId: number, policy: NameClashPolicy) {
  const next = new Map(policies.value)
  next.set(typeId, policy)
  policies.value = next
}

const destinationFolder = computed(() =>
  destinationPath.value ? parentPath(destinationPath.value) : null,
)
const destinationName = computed(() =>
  destinationPath.value ? fileNameOf(destinationPath.value) : null,
)
</script>

<template>
  <FileBrowserDialog
    :open="browserOpen"
    mode="save"
    :start-path="destinationPath"
    :suggested-name="props.project?.name ?? 'measurements'"
    title="Export to a project file"
    remember-as="projectTransfer"
    @close="browserOpen = false"
    @select="chooseDestination"
  />

  <Modal
    :open="props.open && !browserOpen"
    title="Export measurements"
    icon="copy"
    tone="teal"
    size="lg"
    ground
    flush
    @close="emit('close')"
  >
    <div v-if="done" class="flex flex-col gap-3 p-4 text-[13px]">
      <section class="dialog-card shrink-0 p-3.5">
        <p class="text-[13px] font-semibold text-emerald-700 dark:text-emerald-400">{{ done }}</p>
        <p class="mt-1.5 truncate font-mono text-[11.5px] text-neutral-500 dark:text-neutral-400" :title="destinationPath ?? ''">
          {{ destinationPath }}
        </p>
        <p class="mt-2 text-[12px] text-neutral-600 dark:text-neutral-300">
          That one file holds everything — there is no second file beside it to remember. It can be
          opened from the Project menu like any project, or imported from.
        </p>
      </section>
    </div>

    <div v-else class="flex flex-col gap-3 p-4 text-[13px]">
      <p class="shrink-0 px-0.5 text-[12.5px] leading-[1.5] text-neutral-600 dark:text-neutral-300">
        Writes the measurements you tick into another project file, which can be e-mailed or copied
        like any other file. Whoever receives it can open it as a project of its own, or import from
        it into one of theirs.
      </p>

      <!-- Where it goes. First, because everything below it depends on what is already in there. -->
      <section class="dialog-card shrink-0 p-3.5">
        <div class="flex items-center gap-2.5">
          <div class="min-w-0 flex-1">
            <p v-if="destinationName" class="truncate text-[13px] font-semibold" :title="destinationPath ?? ''">
              {{ destinationName }}
            </p>
            <p v-else class="text-[13px] text-neutral-500 dark:text-neutral-400">No file chosen yet</p>
            <p v-if="destinationFolder" class="truncate font-mono text-[11px] text-neutral-500 dark:text-neutral-400">
              {{ destinationFolder }}
            </p>
          </div>
          <button type="button" class="btn-secondary" :disabled="busy" @click="browserOpen = true">
            {{ destinationPath ? 'Change…' : 'Choose file…' }}
          </button>
        </div>

        <p v-if="destinationError" class="mt-2 text-[12px] text-red-600 dark:text-red-400">
          {{ destinationError }}
        </p>

        <p v-else-if="checkingDestination" class="mt-2 text-[12px] text-neutral-500 dark:text-neutral-400">
          Reading that file…
        </p>

        <template v-else-if="existing">
          <p class="mt-2 text-[12px] text-neutral-600 dark:text-neutral-300">
            Appending to <strong>{{ existing.projectName }}</strong
            >, which already holds
            {{ existing.types.length }} measurement{{ existing.types.length === 1 ? '' : 's' }}. It keeps
            its own name and description.
          </p>
        </template>

        <!-- A file that is not there yet is the ordinary case, and it needs a project name of its
             own: a project file holds exactly one project row, and that name is what the recipient
             sees in their title bar. -->
        <div v-else-if="destinationPath" class="mt-2.5 grid gap-2.5 sm:grid-cols-2">
          <label class="tb-field">
            Project name
            <input v-model="form.name" type="text" class="tb-input" :disabled="busy" />
          </label>
          <label class="tb-field">
            Description
            <input v-model="form.notes" type="text" class="tb-input" :disabled="busy" placeholder="optional" />
          </label>
          <p class="text-[11.5px] text-neutral-500 sm:col-span-2 dark:text-neutral-400">
            A new project file will be created, carrying {{ PROJECT_EXTENSION }}.
          </p>
        </div>
      </section>

      <p v-if="loading" class="shrink-0 px-0.5 text-[12px] text-neutral-500 dark:text-neutral-400">
        Loading this project's measurements…
      </p>

      <ProjectTransferTree
        v-else
        caption="Measurements to export"
        :types="types"
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
        v-if="totalBytes > LARGE_TRANSFER_BYTES"
        class="shrink-0 px-0.5 text-[12px] text-amber-700 dark:text-amber-400"
      >
        This copies about {{ formatBytes(totalBytes) }} and can take a minute. The window will not
        respond while it runs.
      </p>

      <p v-if="error" class="shrink-0 px-0.5 text-[12px] text-red-600 dark:text-red-400">{{ error }}</p>
    </div>

    <template #footer>
      <template v-if="done">
        <span class="flex-1"></span>
        <button type="button" class="btn-primary" @click="emit('close')">Close</button>
      </template>
      <template v-else>
      <span v-if="busy" class="flex-1 truncate text-[12px] text-neutral-500 dark:text-neutral-400">
        Copying…
      </span>
      <!-- Not truncated, unlike the figures beside it: this is the one footer line that says what
           to do about something, and "Change its ch…" is the half that matters cut off. It wraps
           instead, which only makes the strip taller. -->
      <span v-else-if="readiness.reason" class="flex-1 text-[12px] leading-snug text-neutral-500 dark:text-neutral-400">
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
          <path d="M12 3v12M8 11l4 4 4-4M4 19h16" />
        </svg>
        Export
      </button>
      </template>
    </template>
  </Modal>
</template>
