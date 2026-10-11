<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Modal from './Modal.vue'
import { api, type FileBrowseResponse } from '../api/client'
import {
  ensureExtension,
  fileNameOf,
  isRooted,
  joinPath,
  OPENABLE_PROJECT_EXTENSIONS,
  parentPath,
  PROJECT_EXTENSION,
} from '../projects/projectFile'

// The in-app file dialog. The app is a server driving the system browser, so a web page cannot
// open the operating system's file dialog on the server's behalf; this lists the server's file
// system one directory at a time (GET /api/files/browse) and looks the same on Windows and Linux.
// Two modes: `open` picks an existing project file, `save` picks a folder and a file name.
//
// A project file has two extensions it may carry — the current .tbench and the .qa403 of the
// builds named after the QA403 — so `extension` (one, with its dot) is what save mode *writes*,
// while `listedExtensions` is what either mode *accepts*. Passing the single one as the browse
// filter would list a folder of pre-rename projects as empty.

const props = defineProps<{
  open: boolean
  mode: 'open' | 'save'
  /** Where to start; null starts in the default projects folder. In save mode a full file path pre-fills the name too. */
  startPath?: string | null
  /** Save mode: the file name to suggest when startPath has none. */
  suggestedName?: string
  /** The file extension listed and selected, with its dot; the project file's by default. */
  extension?: string
  /** The dialog's title; a default per mode when omitted. */
  title?: string
}>()
const emit = defineEmits<{ close: []; select: [path: string] }>()

const listing = ref<FileBrowseResponse | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)
/** The path box: typed by the user, or the listed directory. Enter navigates to a folder or selects a file. */
const pathInput = ref('')
const fileName = ref('')
const selectedFile = ref<string | null>(null)

const title = computed(() => props.title ?? (props.mode === 'open' ? 'Open project' : 'Save project file'))
const extension = computed(() => props.extension ?? PROJECT_EXTENSION)
/** Explicit prop: that one alone. Otherwise every extension a project file may carry. */
const listedExtensions = computed(() =>
  props.extension ? [props.extension] : OPENABLE_PROJECT_EXTENSIONS,
)
const endsWithListed = (path: string) => {
  const lower = path.toLowerCase()
  return listedExtensions.value.some((e) => lower.endsWith(e))
}
const separator = computed(() => listing.value?.separator ?? '/')

const resultPath = computed<string | null>(() => {
  if (props.mode === 'open') return selectedFile.value
  const name = fileName.value.trim()
  if (!listing.value || name.length === 0) return null
  return joinPath(listing.value.path, ensureExtension(name), separator.value)
})

async function browse(path: string | null) {
  loading.value = true
  error.value = null
  try {
    listing.value = await api.browseFiles(path, props.extension)
    pathInput.value = listing.value.path
    selectedFile.value = null
  } catch (e) {
    // No prefix to strip any more -- see api/client.ts's apiErrorMessage.
    error.value = (e as Error).message
  } finally {
    loading.value = false
  }
}

watch(
  () => props.open,
  (open) => {
    if (!open) return
    const start = props.startPath ?? null
    // A full file path starts in its folder, with its name in the box; anything else is a folder.
    const looksLikeFile = start !== null && endsWithListed(start)
    fileName.value = looksLikeFile ? fileNameOf(start) : (props.suggestedName ?? '')
    void browse(looksLikeFile ? parentPath(start) : start)
  },
)

function onPathEnter() {
  const typed = pathInput.value.trim()
  if (typed.length === 0) return
  if (!isRooted(typed)) {
    error.value = 'Type an absolute path.'
    return
  }
  // A file: select it outright in open mode, or take its name and folder in save mode. `.db` too,
  // so a database from before projects were one file each can be opened by typing its path — the
  // list shows only project files, and such a database opens fine when it holds a single project.
  if (endsWithListed(typed) || (extension.value === PROJECT_EXTENSION && typed.toLowerCase().endsWith('.db'))) {
    if (props.mode === 'open') {
      emit('select', typed)
      return
    }
    fileName.value = fileNameOf(typed)
    void browse(parentPath(typed))
    return
  }
  void browse(typed)
}

function chooseFile(path: string) {
  if (props.mode === 'open') {
    selectedFile.value = path
    return
  }
  fileName.value = fileNameOf(path)
}

function openFile(path: string) {
  if (props.mode === 'open') emit('select', path)
  else fileName.value = fileNameOf(path)
}

function confirm() {
  if (resultPath.value) emit('select', resultPath.value)
}

function formatModified(iso: string | null): string {
  if (!iso) return ''
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}
</script>

<template>
  <Modal :open="props.open" :title="title" size="lg" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <!-- Roots: drives on Windows, / and home on Linux, plus Documents and the projects folder. -->
      <div v-if="listing" class="flex flex-wrap gap-1">
        <button
          v-for="root in listing.roots"
          :key="root.path"
          type="button"
          class="rounded border border-neutral-300 px-2 py-0.5 text-xs hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800"
          :class="root.path === listing.path ? 'bg-neutral-100 dark:bg-neutral-800' : ''"
          :title="root.path"
          @click="browse(root.path)"
        >
          {{ root.label }}
        </button>
      </div>

      <div class="flex items-center gap-2">
        <button
          type="button"
          class="rounded border border-neutral-300 px-2 py-1 hover:bg-neutral-100 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
          title="Up one folder"
          aria-label="Up one folder"
          :disabled="!listing?.parent"
          @click="listing?.parent && browse(listing.parent)"
        >
          ↑
        </button>
        <input
          v-model="pathInput"
          type="text"
          spellcheck="false"
          class="min-w-0 flex-1 rounded border border-neutral-300 px-2 py-1 font-mono text-xs dark:border-neutral-700"
          title="A folder to list, or a project file to pick. Enter applies it."
          @keyup.enter="onPathEnter"
        />
      </div>

      <div
        class="h-64 overflow-y-auto rounded border border-neutral-200 dark:border-neutral-800"
        :class="loading ? 'opacity-50' : ''"
      >
        <p v-if="error" class="p-3 text-red-600 dark:text-red-400">{{ error }}</p>
        <template v-else-if="listing">
          <ul>
            <li v-for="dir in listing.directories" :key="dir.path">
              <button
                type="button"
                class="flex w-full items-center gap-2 px-3 py-1 text-left hover:bg-neutral-100 dark:hover:bg-neutral-800"
                @click="browse(dir.path)"
              >
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true" class="shrink-0 text-neutral-400">
                  <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z" />
                </svg>
                <span class="truncate">{{ dir.name }}</span>
              </button>
            </li>
            <li v-for="file in listing.files" :key="file.path">
              <button
                type="button"
                class="flex w-full items-center gap-2 px-3 py-1 text-left hover:bg-neutral-100 dark:hover:bg-neutral-800"
                :class="selectedFile === file.path ? 'bg-neutral-200 dark:bg-neutral-700' : ''"
                @click="chooseFile(file.path)"
                @dblclick="openFile(file.path)"
              >
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true" class="shrink-0 text-neutral-400">
                  <path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9Z" />
                  <path d="M14 3v6h6" />
                </svg>
                <span class="truncate">{{ file.name }}</span>
                <span class="ml-auto shrink-0 text-xs text-neutral-400">{{ formatModified(file.modifiedUtc) }}</span>
              </button>
            </li>
          </ul>
          <p v-if="listing.directories.length === 0 && listing.files.length === 0" class="p-3 text-neutral-400">
            Nothing here.
          </p>
        </template>
        <p v-else class="p-3 text-neutral-400">Loading…</p>
      </div>

      <label v-if="props.mode === 'save'" class="flex items-center gap-2">
        <span class="shrink-0">File name</span>
        <input
          v-model="fileName"
          type="text"
          spellcheck="false"
          class="min-w-0 flex-1 rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700"
          @keyup.enter="confirm"
        />
      </label>
      <p v-if="props.mode === 'save' && resultPath" class="truncate font-mono text-xs text-neutral-500" :title="resultPath">
        {{ resultPath }}
      </p>

      <div class="flex justify-end gap-2 pt-2">
        <button
          type="button"
          class="rounded border border-neutral-300 px-3 py-1 hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="emit('close')"
        >
          Cancel
        </button>
        <button
          type="button"
          class="rounded bg-neutral-900 px-3 py-1 text-white hover:bg-neutral-700 disabled:opacity-40 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          :disabled="!resultPath"
          @click="confirm"
        >
          {{ props.mode === 'open' ? 'Open' : 'Select' }}
        </button>
      </div>
    </div>
  </Modal>
</template>
