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
  <Modal :open="props.open" :title="view === 'about' ? 'About' : 'Open-source libraries'" size="lg" @close="emit('close')">
    <template v-if="view === 'about'">
      <div class="flex items-center gap-4">
        <TonebenchLogo :size="76" class="shrink-0" />
        <div class="min-w-0">
          <div class="text-2xl leading-none font-bold tracking-tight">Tonebench</div>
          <div class="mt-1.5 text-[11px] font-semibold tracking-[0.2em] text-neutral-500 uppercase dark:text-neutral-400">
            Audio Equipment Analyzer
          </div>
        </div>
      </div>

      <p class="mt-4 text-sm text-neutral-600 dark:text-neutral-300">
        A cross-platform analyzer application for the QuantAsylum QA403 and for sound cards, with its
        own USB transport, its own DSP and a local measurement database.
      </p>

      <dl class="mt-4 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 border-t border-neutral-200 pt-4 text-sm dark:border-neutral-800">
        <dt class="text-neutral-500 dark:text-neutral-400">Version</dt>
        <dd class="font-medium">
          <span v-if="version">{{ version.release }}</span>
          <span v-else-if="versionFailed" class="text-neutral-400 dark:text-neutral-500">unavailable</span>
          <span v-else class="text-neutral-400 dark:text-neutral-500">…</span>
        </dd>

        <!-- Only on a build that carries a source revision. It is what identifies a build made
             between two releases, which is most of them and every one a bug report comes from. -->
        <template v-if="version?.build">
          <dt class="text-neutral-500 dark:text-neutral-400">Build</dt>
          <dd class="font-mono text-[13px] font-medium">{{ version.build }}</dd>
        </template>

        <dt class="text-neutral-500 dark:text-neutral-400">Author</dt>
        <dd class="font-medium">Joost Breed</dd>

        <dt class="text-neutral-500 dark:text-neutral-400">Source</dt>
        <dd>
          <a
            :href="REPOSITORY_URL"
            target="_blank"
            rel="noopener"
            class="font-medium text-blue-700 underline underline-offset-2 hover:no-underline dark:text-blue-300"
          >
            breedj/tonebench-audio-equipment-analyzer
          </a>
        </dd>
      </dl>

      <div class="mt-4 border-t border-neutral-200 pt-4 dark:border-neutral-800">
        <button
          type="button"
          class="text-sm font-medium text-blue-700 underline underline-offset-2 hover:no-underline dark:text-blue-300"
          @click="view = 'libraries'"
        >
          Open-source libraries →
        </button>
      </div>

      <p class="mt-4 text-xs text-neutral-500 dark:text-neutral-400">
        QA403 and QA40x are products of QuantAsylum. This application is an independent one and is
        neither produced nor endorsed by them.
      </p>
    </template>

    <template v-else>
      <button
        type="button"
        class="text-sm font-medium text-blue-700 underline underline-offset-2 hover:no-underline dark:text-blue-300"
        @click="view = 'about'"
      >
        ← About
      </button>

      <p class="mt-3 text-sm text-neutral-600 dark:text-neutral-300">
        This application stands on the work below. Each name links to the project itself.
      </p>

      <!-- Capped rather than allowed to grow: the dialog is centred, so a list long enough to
           exceed the window would push its own header off the top of the screen. -->
      <div class="mt-4 max-h-[52vh] overflow-y-auto pr-1">
        <section v-for="group in LIBRARY_GROUPS" :key="group.title" class="mb-5 last:mb-0">
          <h3 class="text-sm font-semibold">{{ group.title }}</h3>
          <p class="mt-0.5 text-xs text-neutral-500 dark:text-neutral-400">{{ group.blurb }}</p>

          <ul class="mt-2 divide-y divide-neutral-200 dark:divide-neutral-800">
            <li v-for="library in group.items" :key="library.name" class="flex gap-4 py-2">
              <div class="min-w-0 flex-1">
                <a
                  :href="library.url"
                  target="_blank"
                  rel="noopener"
                  class="text-sm font-medium text-blue-700 underline underline-offset-2 hover:no-underline dark:text-blue-300"
                >
                  {{ library.name }}</a
                ><span v-if="library.version" class="ml-1.5 text-xs text-neutral-400 dark:text-neutral-500">{{
                  library.version
                }}</span>
                <p class="mt-0.5 text-xs text-neutral-500 dark:text-neutral-400">{{ library.role }}</p>
              </div>
              <div class="w-40 shrink-0 text-right text-xs text-neutral-500 dark:text-neutral-400">
                {{ library.licence }}
              </div>
            </li>
          </ul>
        </section>
      </div>
    </template>
  </Modal>
</template>
