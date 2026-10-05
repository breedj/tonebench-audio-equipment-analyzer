<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import TonebenchLogo from './TonebenchLogo.vue'
import { api } from '../api/client'
import { splitVersion, type AppVersion } from '../about/version'
import { SPLASH_FADE_MS, SPLASH_HOLD_MS } from '../splash'

/**
 * What the app shows while it is starting: the mark, the name and the version, for four seconds.
 *
 * <p>Deliberately the <b>about dialog's</b> front page rather than a design of its own — the same
 * tile logo, the same name over the same spaced-out subtitle, the same version/build/author rows and
 * the same QuantAsylum disclaimer. Two screens that introduce the application should not disagree
 * about what it is called or which version it is, and the quickest way to guarantee that is for the
 * second one to be the first one with the chrome taken off.</p>
 *
 * <p><b>It is an overlay, not a gate.</b> App.vue mounts, opens the last project and connects the
 * analyzer behind it, so the four seconds are spent on work rather than instead of it — by the time
 * it fades the bench is usually already up. Gating the app on a timer would have made the splash
 * cost four seconds instead of covering them.</p>
 *
 * <p><b>Dismissible by a click or any key.</b> Not in the request, and added anyway: this is a
 * window that gets reloaded a great many times in a bench session and in development, and four
 * seconds that cannot be skipped stop being a greeting after the third one. The hold is still the
 * four seconds asked for; this only means nobody has to wait them out twice.</p>
 */
const emit = defineEmits<{ done: [] }>()

/** False once the fade has started, so the element is still in the tree while it fades. */
const showing = ref(true)
const version = ref<AppVersion | null>(null)

let holdTimer: ReturnType<typeof setTimeout> | undefined
let fadeTimer: ReturnType<typeof setTimeout> | undefined

function dismiss() {
  if (!showing.value) return

  showing.value = false
  clearTimeout(holdTimer)
  // Unmounted by the parent only after the transition has run, or the splash would vanish rather
  // than fade. The two durations are one constant each so the CSS and this cannot drift.
  fadeTimer = setTimeout(() => emit('done'), SPLASH_FADE_MS)
}

onMounted(async () => {
  holdTimer = setTimeout(dismiss, SPLASH_HOLD_MS)

  window.addEventListener('keydown', dismiss, { once: true })

  try {
    version.value = splitVersion((await api.getAppInfo()).version)
  } catch {
    // The splash is a greeting, not a diagnosis: a version that did not arrive in time leaves the
    // row showing a dash rather than keeping the screen up or saying something alarming. The status
    // bar is what reports a server that is not answering, a moment later.
  }
})

onBeforeUnmount(() => {
  clearTimeout(holdTimer)
  clearTimeout(fadeTimer)
  window.removeEventListener('keydown', dismiss)
})
</script>

<template>
  <!-- `fixed`, so it covers the app without taking part in its layout: the charts behind it measure
       their own containers on mount (see useResultChartHeight), and an overlay that occupied space
       would have them measure the wrong thing and keep it. -->
  <div
    class="fixed inset-0 z-50 flex cursor-pointer flex-col items-center justify-center gap-6 bg-[var(--surface-ground)] px-6 text-center transition-opacity duration-300"
    :class="showing ? 'opacity-100' : 'pointer-events-none opacity-0'"
    role="status"
    aria-label="Tonebench is starting"
    title="Click to continue"
    @click="dismiss"
  >
    <TonebenchLogo :size="128" variant="tile" class="shrink-0" />

    <div>
      <div class="text-4xl leading-none font-bold tracking-tight">Tonebench</div>
      <div
        class="mt-2.5 text-[11px] font-semibold tracking-[0.2em] text-neutral-500 uppercase dark:text-neutral-400"
      >
        Audio Equipment Analyzer
      </div>
    </div>

    <dl class="grid grid-cols-[auto_auto] items-baseline gap-x-4 gap-y-1 text-sm">
      <dt class="text-right text-neutral-500 dark:text-neutral-400">Version</dt>
      <dd class="text-left font-medium">
        <span v-if="version">{{ version.release }}</span>
        <span v-else class="text-neutral-400 dark:text-neutral-500">&mdash;</span>
      </dd>

      <!-- Only on a build that carries a source revision, like the about dialog: it is what
           identifies a build made between two releases, which is most of them. -->
      <template v-if="version?.build">
        <dt class="text-right text-neutral-500 dark:text-neutral-400">Build</dt>
        <dd class="text-left font-mono text-[13px] font-medium">{{ version.build }}</dd>
      </template>

      <dt class="text-right text-neutral-500 dark:text-neutral-400">Author</dt>
      <dd class="text-left font-medium">Joost Breed</dd>
    </dl>
    <!-- The QuantAsylum attribution lives in the About dialog only. It belongs somewhere permanent
         and readable, not on a screen that shows for a moment at startup. -->
  </div>
</template>
