import { onBeforeUnmount, onMounted, ref, type Ref } from 'vue'

/**
 * A media query as a reactive boolean, for the few layout decisions that live in a template's
 * logic rather than in a Tailwind class — a stored preference that should only apply above some
 * width, say. `false` until mounted (and in a test environment with no `matchMedia`), so a first
 * render never claims a width it has not measured.
 */
export function useMediaQuery(query: string): Ref<boolean> {
  const matches = ref(false)
  let list: MediaQueryList | null = null
  const update = () => {
    matches.value = list?.matches ?? false
  }
  onMounted(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return
    list = window.matchMedia(query)
    update()
    list.addEventListener('change', update)
  })
  onBeforeUnmount(() => {
    list?.removeEventListener('change', update)
    list = null
  })
  return matches
}

/**
 * The four widths the measurement screens are arranged against, and the order they fold in.
 * Joost's, 2026-10-08, and the order is the point: each one buys the chart back the width of the
 * thing it folds, so what goes first is what the chart can best do without.
 *
 *   ≥ 1440   everything open: the sidebar honours its pin, both rails are columns.
 *   1280     the sidebar folds to its icon rail (hover still floats the full menu over the page).
 *   1080     the display rail folds to its strip — the chart's own controls, so it goes late.
 *   1024     the settings rail folds to its strip. Below this, and only below this, the three
 *            regions stop being columns and stack (MeasurementLayout's `lg`).
 *
 * Each is a *minimum*: below it the thing folds whatever the stored preference says, and the
 * preference is left untouched, so widening the window restores the layout the user last chose.
 * The two rails can still be opened by hand at any width — see `railCollapse.ts`.
 *
 * Until now there was one such width, 1080, for the sidebar pin, and everything else keyed off
 * Tailwind's `xl`: at 1280 and below the display rail stopped being a column and flowed *under*
 * the chart instead of folding, which is what Joost reported — a panel moving to the bottom when
 * what he wanted was the chart to keep its width.
 */
export const SIDEBAR_PIN_MIN_WIDTH_PX = 1440

/** @see SIDEBAR_PIN_MIN_WIDTH_PX */
export const DISPLAY_RAIL_MIN_WIDTH_PX = 1280

/** @see SIDEBAR_PIN_MIN_WIDTH_PX */
export const SETTINGS_RAIL_MIN_WIDTH_PX = 1080
