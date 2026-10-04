import { computed, ref, watch } from 'vue'
import { readStoredPreference, writeStoredPreference } from '../browserStorage'

// Module-level singleton (same pattern as useChartDisplay.ts/useSpectrumConfig.ts) — the
// Configuration dialog's selector and every component that reads colors off the current theme
// (SpectrumChart.vue's canvas draws, which Tailwind's `dark:` classes can't reach) must agree on
// one value.

export type ThemeMode = 'Auto' | 'Light' | 'Dark'

const STORAGE_KEY = 'tonebench.themeMode'

function loadInitial(): ThemeMode {
  const stored = readStoredPreference(STORAGE_KEY)
  return stored === 'Light' || stored === 'Dark' || stored === 'Auto' ? stored : 'Auto'
}

const themeMode = ref<ThemeMode>(loadInitial())

const media = window.matchMedia('(prefers-color-scheme: dark)')
const systemPrefersDark = ref(media.matches)
media.addEventListener('change', (e) => {
  systemPrefersDark.value = e.matches
})

/** What actually renders right now — Auto follows the OS, Light/Dark pin it regardless of the OS. */
const isDark = computed(() => (themeMode.value === 'Auto' ? systemPrefersDark.value : themeMode.value === 'Dark'))

// Tailwind's `dark:` variant is configured (see style.css's @custom-variant) to key off this class
// on <html> rather than the raw media query, so a manual Light/Dark choice can override the OS.
watch(
  isDark,
  (dark) => {
    document.documentElement.classList.toggle('dark', dark)
  },
  { immediate: true },
)

watch(themeMode, (mode) => {
  writeStoredPreference(STORAGE_KEY, mode)
})

export function useAppTheme() {
  return { themeMode, isDark }
}
