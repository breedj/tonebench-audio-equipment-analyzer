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
 * The width from which the measurement sidebar honours its pin. Below it the sidebar behaves as
 * the hover rail whatever the stored preference says — a pinned 15.4rem column on a 1024 px
 * window takes a quarter of it — and the preference itself is left alone, so widening the window
 * brings the pinned sidebar back.
 */
export const SIDEBAR_PIN_MIN_WIDTH_PX = 1080
