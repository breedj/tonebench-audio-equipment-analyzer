<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api } from '../api/client'
import { useRecentProjects } from '../composables/useRecentProjects'
import { useStartupConfig } from '../composables/useStartupConfig'
import { useManual } from '../manual/useManual'
import { formatLastOpened } from '../projects/recent'
import { splitVersion } from '../about/version'
import TonebenchLogo from './TonebenchLogo.vue'

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
  <!-- Laid out as Joost's "Welcome page" mockup of 2026-10-12: the mark and the version over three
       action cards, the simple guide as a band of its own, and the recent files as a list of rows
       rather than a bare <ul>. 880 px, centred — wider than the 2xl it was, because three cards
       side by side need it, and `auto-fit` is what stacks them on a narrow window. -->
  <div class="flex flex-1 justify-center overflow-y-auto px-4 py-10">
    <div class="flex w-full max-w-[880px] flex-col gap-7">
      <div class="flex items-center gap-4">
        <!-- The mark in a white tile with a hairline, which is the mockup's framing — the `glyph`
             variant, because the tile the component draws is the dark app icon and this is a page. -->
        <span
          class="panel-surface flex h-16 w-16 shrink-0 items-center justify-center rounded-2xl border border-neutral-200 dark:border-neutral-800"
        >
          <TonebenchLogo :size="38" variant="glyph" aria-hidden="true" />
        </span>
        <div class="flex flex-col gap-1.5">
          <h1 class="text-[28px] font-semibold tracking-[-0.01em]">Tonebench</h1>
          <span
            v-if="version"
            class="self-start rounded-full border border-neutral-200 px-2 py-0.5 font-mono text-xs text-neutral-600 dark:border-neutral-800 dark:text-neutral-300"
            :style="{ backgroundColor: 'var(--surface-track)' }"
            >v{{ version }}</span
          >
        </div>
      </div>

      <!-- The three ways in. New project is the filled one: it is what someone with nothing open is
           most likely to want, and the mockup gives it the ink card. -->
      <div class="grid gap-3.5" style="grid-template-columns: repeat(auto-fit, minmax(240px, 1fr))">
        <button type="button" class="welcome-card-primary" @click="emit('new')">
          <span class="welcome-card-primary-chip flex h-11 w-11 shrink-0 items-center justify-center rounded-full">
            <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <path d="M6 2h9l5 5v15H6zM14 2v6h6M13 12v6M10 15h6" />
            </svg>
          </span>
          <span class="flex flex-col gap-1">
            <span class="text-[15px] font-semibold">New project…</span>
            <span class="welcome-card-primary-note text-[13px] leading-snug">
              Create a new <span class="font-mono">.tbench</span> project file.
            </span>
          </span>
        </button>

        <button type="button" class="welcome-card" @click="emit('open')">
          <span
            class="flex h-11 w-11 shrink-0 items-center justify-center rounded-full"
            :style="{ backgroundColor: 'var(--kind-cyan-chip-bg)', color: 'var(--kind-cyan-chip-fg)' }"
          >
            <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v1H3z" />
              <path d="M3 10h18l-2 9H5z" />
            </svg>
          </span>
          <span class="flex flex-col gap-1">
            <span class="text-[15px] font-semibold">Open project…</span>
            <span class="text-[13px] leading-snug text-neutral-600 dark:text-neutral-400">
              Browse for an existing <span class="font-mono">.tbench</span> file.
            </span>
          </span>
        </button>

        <button type="button" class="welcome-card" @click="openManual('projects')">
          <span
            class="flex h-11 w-11 shrink-0 items-center justify-center rounded-full"
            :style="{ backgroundColor: 'var(--kind-amber-chip-bg)', color: 'var(--kind-amber-chip-fg)' }"
          >
            <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <path d="M5 4h11a3 3 0 0 1 3 3v13H8a3 3 0 0 1-3-3z" />
              <path d="M5 17a3 3 0 0 1 3-3h11" />
            </svg>
          </span>
          <span class="flex flex-col gap-1">
            <span class="text-[15px] font-semibold">Manual</span>
            <span class="text-[13px] leading-snug text-neutral-600 dark:text-neutral-400">
              Reference for every measurement and setting.
            </span>
          </span>
        </button>
      </div>

      <!-- The one place a first-time user is certain to look. The manual's own switcher is inside a
           dialog nobody opens until they have a reason to, so the entrance is offered here. -->
      <div
        class="panel-surface flex flex-wrap items-center gap-3.5 rounded-xl border border-neutral-200 px-4 py-3.5 dark:border-neutral-800"
      >
        <span
          class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full"
          :style="{ backgroundColor: 'var(--kind-green-chip-bg)', color: 'var(--kind-green-chip-fg)' }"
        >
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M4 19V5a2 2 0 0 1 2-2h12v18H6a2 2 0 0 1-2-2zM8 7h6M8 11h6" />
          </svg>
        </span>
        <div class="flex min-w-[320px] flex-1 flex-col gap-0.5">
          <span class="text-sm font-semibold">New to audio measurement?</span>
          <span class="text-[13px] text-neutral-600 dark:text-neutral-400">
            The simple guide covers what to plug in, what to press, and what the numbers mean.
          </span>
        </div>
        <button
          type="button"
          class="inline-flex h-9 shrink-0 items-center gap-1.5 rounded-lg border border-neutral-200 px-3.5 text-[13px] font-semibold hover:bg-neutral-100 dark:border-neutral-800 dark:hover:bg-neutral-800"
          :style="{ backgroundColor: 'var(--surface-ground)' }"
          @click="openManual('guide-welcome')"
        >
          Start the simple guide
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M5 12h14M13 6l6 6-6 6" />
          </svg>
        </button>
      </div>

      <section class="flex flex-col gap-2.5">
        <div class="flex items-center gap-2">
          <h2
            class="m-0 rounded-full px-2.5 py-0.5 text-[11px] font-semibold uppercase tracking-[0.06em]"
            :style="{ backgroundColor: 'var(--kind-violet-chip-bg)', color: 'var(--kind-violet-chip-fg)' }"
          >
            Recent projects
          </h2>
          <span class="text-xs text-neutral-500 dark:text-neutral-400">{{ recent.length }}</span>
          <span class="h-px flex-1 bg-neutral-200 dark:bg-neutral-800"></span>
        </div>

        <p v-if="recentError" class="text-sm text-red-600 dark:text-red-400">{{ recentError }}</p>
        <p v-else-if="recent.length === 0" class="text-sm text-neutral-500 dark:text-neutral-400">
          Nothing yet. Create a project, or open a <span class="font-mono">.tbench</span> file.
        </p>
        <div
          v-else
          class="panel-surface divide-y divide-neutral-200 overflow-hidden rounded-xl border border-neutral-200 dark:divide-neutral-800 dark:border-neutral-800"
        >
          <div v-for="entry in recent" :key="entry.path" class="flex items-center gap-3.5 py-3 pl-3.5 pr-2">
            <span
              class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full"
              :style="{ backgroundColor: 'var(--kind-violet-chip-bg)', color: 'var(--kind-violet-chip-fg)' }"
            >
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M6 2h9l5 5v15H6zM14 2v6h6M9 17v-3M12 17v-6M15 17v-4" />
              </svg>
            </span>
            <button
              type="button"
              class="flex min-w-0 flex-1 flex-col gap-0.5 text-left"
              :class="entry.exists ? 'hover:underline' : 'cursor-default'"
              :disabled="!entry.exists"
              :title="entry.exists ? entry.path : `${entry.path} — file not found`"
              @click="entry.exists && emit('openRecent', entry.path)"
            >
              <span class="truncate text-sm font-semibold" :class="entry.exists ? '' : 'text-neutral-400 line-through dark:text-neutral-600'">
                {{ entry.name }}
              </span>
              <span class="truncate font-mono text-xs text-neutral-500 dark:text-neutral-400">
                {{ entry.path }}
                <span v-if="!entry.exists" class="ml-1 font-sans text-red-600 dark:text-red-400">file not found</span>
              </span>
            </button>
            <span class="shrink-0 text-xs text-neutral-500 dark:text-neutral-400" :title="entry.lastOpenedUtc">
              {{ formatLastOpened(entry.lastOpenedUtc) }}
            </span>
            <button
              type="button"
              class="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
              title="Remove from this list (the file is not touched)"
              aria-label="Remove from recent projects"
              @click="removeRecent(entry.path)"
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true">
                <path d="M6 6l12 12M18 6L6 18" />
              </svg>
            </button>
          </div>
        </div>
      </section>

      <label
        class="tb-check-row"
        title="Unchecked, the last opened project is opened at startup if its file still exists."
      >
        <input
          type="checkbox"
          class="tb-check"
          :checked="showWelcomeOnStartup"
          @change="setShowWelcomeOnStartup(($event.target as HTMLInputElement).checked)"
        />
        Show welcome page on startup
      </label>
    </div>
  </div>
</template>
