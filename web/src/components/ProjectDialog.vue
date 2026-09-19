<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import FileBrowserDialog from './FileBrowserDialog.vue'
import Modal from './Modal.vue'
import { api, type Project } from '../api/client'
import {
  confirmLabel,
  dialogTitle,
  initialForm,
  locationFor,
  type ProjectDialogMode,
  type ProjectForm,
  showsIncludeResults,
  showsLocation,
  submittedForm,
  validateForm,
} from '../projects/projectDialogModel'

// One dialog for Project ▸ New…, Save As… and Properties…: name, description, and — except for
// Properties — where the file goes. What differs per mode lives in projectDialogModel.ts so it can
// be tested. The location follows the name (default folder + sanitized name + .qa403) until the
// user edits it or picks one with Browse…, which opens the in-app file dialog in save mode.

const props = defineProps<{ open: boolean; mode: ProjectDialogMode; current: Project | null }>()
const emit = defineEmits<{ close: []; confirm: [form: ProjectForm] }>()

const form = reactive<ProjectForm>(initialForm('new', null))
const locationTouched = ref(false)
const browserOpen = ref(false)
// The server's default folder and separator, fetched once the dialog first opens so the suggested
// location can be built client-side; until then the field just stays empty.
const defaultFolder = ref('')
const separator = ref('/')

const title = computed(() => dialogTitle(props.mode))
const confirmText = computed(() => confirmLabel(props.mode))
const showLocation = computed(() => showsLocation(props.mode))
const showIncludeResults = computed(() => showsIncludeResults(props.mode))
const problem = computed(() => validateForm(props.mode, form))

watch(
  () => props.open,
  async (open) => {
    // The file dialog is a nested <dialog>, and closing this one (Escape, or the request
    // succeeding) does not close it, so it is closed here.
    browserOpen.value = false
    if (!open) return
    Object.assign(form, initialForm(props.mode, props.current))
    locationTouched.value = false
    if (showLocation.value && defaultFolder.value.length === 0) {
      try {
        const listing = await api.browseFiles(null)
        defaultFolder.value = listing.defaultFolder
        separator.value = listing.separator
      } catch {
        // Browse… and a typed path still work; only the suggestion is missing.
      }
    }
    applySuggestedLocation()
  },
)

watch(() => form.name, applySuggestedLocation)

function applySuggestedLocation() {
  if (!showLocation.value) return
  const suggested = locationFor(props.mode, form.name, defaultFolder.value, separator.value, locationTouched.value)
  if (suggested !== null) form.location = suggested
}

function onLocationInput() {
  locationTouched.value = true
}

function onBrowsed(path: string) {
  browserOpen.value = false
  form.location = path
  locationTouched.value = true
}

function onConfirm() {
  if (problem.value) return
  emit('confirm', submittedForm(form))
}
</script>

<template>
  <Modal :open="props.open" :title="title" :close-on-backdrop="false" @close="emit('close')">
    <div class="flex flex-col gap-3 text-sm">
      <label class="flex flex-col gap-1">
        Name
        <input
          v-model="form.name"
          type="text"
          autofocus
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
          @keyup.enter="onConfirm"
        />
      </label>

      <label class="flex flex-col gap-1">
        Description
        <textarea
          v-model="form.notes"
          rows="3"
          class="rounded border border-neutral-300 px-2 py-1 dark:border-neutral-700 dark:bg-neutral-800"
        ></textarea>
      </label>

      <label v-if="showLocation" class="flex flex-col gap-1">
        File location
        <div class="flex gap-2">
          <input
            v-model="form.location"
            type="text"
            spellcheck="false"
            class="min-w-0 flex-1 rounded border border-neutral-300 px-2 py-1 font-mono text-xs dark:border-neutral-700 dark:bg-neutral-800"
            title="Where the .qa403 file is written. Follows the name until you change it."
            @input="onLocationInput"
            @keyup.enter="onConfirm"
          />
          <button
            type="button"
            class="rounded border border-neutral-300 px-2 py-1 hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800"
            @click="browserOpen = true"
          >
            Browse…
          </button>
        </div>
      </label>

      <label v-if="showIncludeResults" class="flex items-start gap-2" title="Checked: the whole file is copied, stored results included. Unchecked: a new file with this project's measurements and their settings only.">
        <input v-model="form.includeResults" type="checkbox" class="mt-0.5" />
        <span>
          Include results
          <span class="block text-xs text-neutral-500 dark:text-neutral-400">
            {{ form.includeResults ? 'Copies the whole project, stored results included.' : 'A new file with the measurements and their settings, no results.' }}
          </span>
        </span>
      </label>

      <div class="flex items-center justify-end gap-2 pt-2">
        <span v-if="problem && form.name.length > 0" class="mr-auto text-xs text-neutral-500 dark:text-neutral-400">{{ problem }}</span>
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
          :disabled="problem !== null"
          @click="onConfirm"
        >
          {{ confirmText }}
        </button>
      </div>
    </div>

    <FileBrowserDialog
      :open="browserOpen"
      mode="save"
      :start-path="form.location || null"
      :suggested-name="form.name"
      @close="browserOpen = false"
      @select="onBrowsed"
    />
  </Modal>
</template>
