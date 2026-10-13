<script setup lang="ts">
import { ref, watch } from 'vue'
import Modal from './Modal.vue'
import TonebenchLogo from './TonebenchLogo.vue'
import { api } from '../api/client'
import { LIBRARY_GROUPS } from '../about/libraries'
import { splitVersion, type AppVersion } from '../about/version'

// Two views behind one dialog rather than two dialogs: "which libraries does this use" is a
// question about the thing the about box is describing, so it belongs behind it, and a nested
// <dialog> would have to manage its own backdrop and its own Escape on top of this one's.
//
// The list itself lives in about/libraries.ts, not here — nothing in a .vue is testable under the
// node environment vitest runs in, and that list is checked against package.json and the .csproj
// files so a dependency bump cannot leave a stale version on screen.

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: [] }>()

const view = ref<'about' | 'libraries'>('about')

const version = ref<AppVersion | null>(null)
const versionFailed = ref(false)

const REPOSITORY_URL = 'https://github.com/breedj/tonebench-audio-equipment-analyzer'

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    // Always back to the front page: reopening the dialog to read the version and landing on a
    // scrolled licences list reads as the wrong thing having opened, the same way the manual's
    // own scroll reset does.
    view.value = 'about'
    if (version.value !== null) return
    try {
      version.value = splitVersion((await api.getAppInfo()).version)
    } catch {
      // The dialog is still worth showing without it — it carries the credits and the links, and
      // this is the one request in the app whose failure means the server went away mid-click.
      versionFailed.value = true
    }
  },
)
</script>

<template>
  <!-- The 2026-10-13 chrome: a tinted glyph and a 17 px title, the body on the ground tone with its
       content in cards, the one action on a strip of its own. -->
  <Modal
    :open="props.open"
    :title="view === 'about' ? 'About' : 'Open-source libraries'"
    :icon="view === 'about' ? 'info' : 'book'"
    :tone="view === 'about' ? 'blue' : 'green'"
    size="lg"
    ground
    flush
    @close="emit('close')"
  >
    <template v-if="view === 'about'">
      <div class="flex flex-col gap-3.5 p-4">
        <section class="dialog-card flex flex-col gap-3.5 p-[18px]">
          <div class="flex items-center gap-4">
            <!-- The shipped icon itself, tile and all — this is the one place in the app that is
                 showing the reader *the application*, so it shows the mark they will find in their
                 taskbar rather than the bare glyph the header wears. -->
            <TonebenchLogo :size="68" class="shrink-0" />
            <div class="flex min-w-0 flex-col gap-1.5">
              <span class="text-2xl leading-none font-semibold tracking-tight">Tonebench</span>
              <span class="text-[11px] font-semibold tracking-[0.12em] text-neutral-500 uppercase dark:text-neutral-400">
                Audio equipment analyzer
              </span>
            </div>
            <span class="flex-1"></span>
            <span
              v-if="version"
              class="self-start rounded-full border border-neutral-200 bg-neutral-100 px-2 py-px font-mono text-[12px] text-neutral-600 dark:border-neutral-700 dark:bg-neutral-800 dark:text-neutral-300"
            >
              v{{ version.release }}
            </span>
          </div>

          <p class="text-[13.5px] leading-[1.55] text-neutral-600 dark:text-neutral-300">
            A cross-platform analyzer application for the QuantAsylum QA403 and for sound cards, with
            its own USB transport, its own DSP and a local measurement database.
          </p>
        </section>

        <section class="dialog-card">
          <dl class="px-4 py-1 text-[13px]">
            <div class="grid grid-cols-[110px_minmax(0,1fr)] gap-3 py-2">
              <dt class="text-neutral-500 dark:text-neutral-400">Version</dt>
              <dd class="font-mono font-medium">
                <span v-if="version">{{ version.release }}</span>
                <span v-else-if="versionFailed" class="text-neutral-400 dark:text-neutral-500">unavailable</span>
                <span v-else class="text-neutral-400 dark:text-neutral-500">…</span>
              </dd>
            </div>

            <!-- Only on a build that carries a source revision. It is what identifies a build made
                 between two releases, which is most of them and every one a bug report comes from. -->
            <div
              v-if="version?.build"
              class="grid grid-cols-[110px_minmax(0,1fr)] gap-3 border-t border-neutral-100 py-2 dark:border-neutral-800"
            >
              <dt class="text-neutral-500 dark:text-neutral-400">Build</dt>
              <dd class="font-mono">{{ version.build }}</dd>
            </div>

            <div class="grid grid-cols-[110px_minmax(0,1fr)] gap-3 border-t border-neutral-100 py-2 dark:border-neutral-800">
              <dt class="text-neutral-500 dark:text-neutral-400">Author</dt>
              <dd class="font-medium">Joost Breed</dd>
            </div>

            <div class="grid grid-cols-[110px_minmax(0,1fr)] gap-3 border-t border-neutral-100 py-2 dark:border-neutral-800">
              <dt class="text-neutral-500 dark:text-neutral-400">Source</dt>
              <dd class="min-w-0">
                <a
                  :href="REPOSITORY_URL"
                  target="_blank"
                  rel="noopener"
                  class="inline-flex items-center gap-1.5 font-medium text-blue-700 hover:underline dark:text-blue-300"
                >
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
                    <path d="M16 18l6-6-6-6M8 6l-6 6 6 6" />
                  </svg>
                  <span class="truncate">breedj/tonebench-audio-equipment-analyzer</span>
                </a>
              </dd>
            </div>
          </dl>

          <button
            type="button"
            class="flex w-full items-center gap-2 border-t border-neutral-200 px-4 py-3 text-left font-semibold hover:bg-neutral-100 dark:border-neutral-800 dark:hover:bg-neutral-800"
            :style="{ backgroundColor: 'var(--surface-ground)' }"
            @click="view = 'libraries'"
          >
            <svg
              width="15"
              height="15"
              viewBox="0 0 24 24"
              fill="none"
              stroke="#15803d"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="M4 19V5a2 2 0 0 1 2-2h12v18H6a2 2 0 0 1-2-2zM8 7h6M8 11h6" />
            </svg>
            <span class="flex-1">Open-source libraries</span>
            <svg
              width="14"
              height="14"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              class="text-neutral-500"
              aria-hidden="true"
            >
              <path d="M5 12h14M13 6l6 6-6 6" />
            </svg>
          </button>
        </section>

        <p class="px-0.5 text-[12px] leading-[1.5] text-neutral-500 dark:text-neutral-400">
          QA403 and QA40x are products of QuantAsylum. This application is an independent one and is
          neither produced nor endorsed by them.
        </p>
      </div>
    </template>

    <template v-else>
      <div class="flex flex-col gap-3.5 p-4">
        <p class="px-0.5 text-[12.5px] leading-[1.5] text-neutral-600 dark:text-neutral-300">
          This application stands on the work below. Each name links to the project itself.
        </p>

        <!-- Capped rather than allowed to grow: the dialog is centred, so a list long enough to
             exceed the window would push its own header off the top of the screen. -->
        <div class="max-h-[52vh] overflow-y-auto">
          <section v-for="group in LIBRARY_GROUPS" :key="group.title" class="dialog-card mb-3.5 p-3.5 last:mb-0">
            <h3 class="text-[13px] font-semibold">{{ group.title }}</h3>
            <p class="mt-0.5 text-[12px] text-neutral-500 dark:text-neutral-400">{{ group.blurb }}</p>

            <ul class="mt-2 divide-y divide-neutral-100 dark:divide-neutral-800">
              <li v-for="library in group.items" :key="library.name" class="flex gap-4 py-2">
                <div class="min-w-0 flex-1">
                  <a
                    :href="library.url"
                    target="_blank"
                    rel="noopener"
                    class="text-[13px] font-medium text-blue-700 hover:underline dark:text-blue-300"
                  >
                    {{ library.name }}</a
                  ><span v-if="library.version" class="ml-1.5 font-mono text-[11.5px] text-neutral-400 dark:text-neutral-500">{{
                    library.version
                  }}</span>
                  <p class="mt-0.5 text-[12px] text-neutral-500 dark:text-neutral-400">{{ library.role }}</p>
                </div>
                <div class="w-40 shrink-0 text-right text-[12px] text-neutral-500 dark:text-neutral-400">
                  {{ library.licence }}
                </div>
              </li>
            </ul>
          </section>
        </div>
      </div>
    </template>

    <template #footer>
      <button v-if="view === 'libraries'" type="button" class="btn-secondary" @click="view = 'about'">← About</button>
      <span class="flex-1"></span>
      <button type="button" class="btn-primary" @click="emit('close')">Close</button>
    </template>
  </Modal>
</template>
