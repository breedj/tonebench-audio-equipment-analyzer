<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api } from '../api/client'
import { useRecentProjects } from '../composables/useRecentProjects'
import { useStartupConfig } from '../composables/useStartupConfig'
import { useManual } from '../manual/useManual'
import { formatLastOpened } from '../projects/recent'
import { splitVersion } from '../about/version'

// What the app shows while no project is open: the ten most recently opened project files, the
// two ways to get one (open a file, make a new one), the startup preference, and the manual. The
// actions themselves are App.vue's — the same handlers the Project menu uses — so this only emits.

const emit = defineEmits<{ open: []; new: []; openRecent: [path: string] }>()

const { recent, error: recentError, load: loadRecent, remove: removeRecent } = useRecentProjects()
const { showWelcomeOnStartup, load: loadStartup, setShowWelcomeOnStartup } = useStartupConfig()
const { openManual } = useManual()
const version = ref<string | null>(null)

onMounted(async () => {
  void loadRecent()
  void loadStartup()
  try {
    // The release number only — the assembly's informational version carries the full forty-character
    // source revision after a '+', which this line printed verbatim until 2026-09-30. The about
    // dialog is where the build hash belongs, beside the rest of what a bug report needs.
    version.value = splitVersion((await api.getAppInfo()).version).release
  } catch {
    // The version line is decoration, so a failure just leaves it out rather than showing an error.
    // (It used to read the update check's copy of the version, which is opt-out and touches GitHub;
    // /api/app answers from the assembly, so an air-gapped bench sees its version like anyone else.)
  }
})
</script>

<template>
  <div class="flex flex-1 items-start justify-center overflow-y-auto p-8">
    <div class="w-full max-w-2xl">
      <h1 class="text-2xl font-semibold">QA403 audio analyzer</h1>
      <p v-if="version" class="mt-1 text-xs text-neutral-500 dark:text-neutral-400">Version {{ version }}</p>

      <div class="mt-6 flex flex-wrap gap-2">
        <button
          type="button"
          class="rounded bg-neutral-900 px-4 py-2 text-sm font-medium text-white hover:bg-neutral-700 dark:bg-neutral-100 dark:text-neutral-900 dark:hover:bg-white"
          @click="emit('new')"
        >
          New project…
        </button>
        <button
          type="button"
          class="rounded border border-neutral-300 px-4 py-2 text-sm hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="emit('open')"
        >
          Open project…
        </button>
        <button
          type="button"
          class="ml-auto flex items-center gap-1.5 rounded border border-neutral-300 px-4 py-2 text-sm hover:bg-neutral-100 dark:border-neutral-700 dark:hover:bg-neutral-800"
          @click="openManual('projects')"
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
            <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
            <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2Z" />
          </svg>
          Manual
        </button>
      </div>

      <!-- The one place a first-time user is certain to look. The manual's own switcher is inside a
           dialog nobody opens until they have a reason to, so the entrance is offered here. -->
      <p class="mt-3 text-xs text-neutral-500 dark:text-neutral-400">
        New to audio measurement?
        <button
          type="button"
          class="text-blue-700 underline underline-offset-2 dark:text-blue-300"
          @click="openManual('guide-welcome')"
        >
          Start with the simple guide
        </button>
        — what to plug in, what to press, and what the numbers mean.
      </p>

      <h2 class="mt-8 text-sm font-semibold text-neutral-600 dark:text-neutral-300">Recent projects</h2>
      <p v-if="recentError" class="mt-2 text-sm text-red-600 dark:text-red-400">{{ recentError }}</p>
      <p v-else-if="recent.length === 0" class="mt-2 text-sm text-neutral-500 dark:text-neutral-400">
        Nothing yet. Create a project, or open a <span class="font-mono">.qa403</span> file.
      </p>
      <ul v-else class="mt-2 divide-y divide-neutral-200 rounded border border-neutral-200 dark:divide-neutral-800 dark:border-neutral-800">
        <li v-for="entry in recent" :key="entry.path" class="flex items-center gap-3 px-3 py-2">
          <button
            type="button"
            class="min-w-0 flex-1 text-left"
            :class="entry.exists ? 'hover:underline' : 'cursor-default'"
            :disabled="!entry.exists"
            :title="entry.exists ? entry.path : `${entry.path} — file not found`"
            @click="entry.exists && emit('openRecent', entry.path)"
          >
            <span class="block truncate text-sm" :class="entry.exists ? '' : 'text-neutral-400 line-through dark:text-neutral-600'">
              {{ entry.name }}
            </span>
            <span class="block truncate font-mono text-xs text-neutral-500 dark:text-neutral-400">
              {{ entry.path }}
              <span v-if="!entry.exists" class="ml-1 font-sans text-red-600 dark:text-red-400">file not found</span>
            </span>
          </button>
          <span class="shrink-0 text-xs text-neutral-500 dark:text-neutral-400" :title="entry.lastOpenedUtc">
            {{ formatLastOpened(entry.lastOpenedUtc) }}
          </span>
          <button
            type="button"
            class="shrink-0 text-neutral-400 hover:text-neutral-900 dark:hover:text-neutral-100"
            title="Remove from this list (the file is not touched)"
            aria-label="Remove from recent projects"
            @click="removeRecent(entry.path)"
          >
            ✕
          </button>
        </li>
      </ul>

      <label class="mt-6 flex items-center gap-2 text-sm" title="Unchecked, the last opened project is opened at startup if its file still exists.">
        <input
          type="checkbox"
          :checked="showWelcomeOnStartup"
          @change="setShowWelcomeOnStartup(($event.target as HTMLInputElement).checked)"
        />
        Show welcome page on startup
      </label>
    </div>
  </div>
</template>
