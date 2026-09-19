import { ref } from 'vue'

export interface ConfirmOptions {
  title: string
  message: string
  confirmLabel?: string
  cancelLabel?: string
  /** Red confirm button, for the one thing here that cannot be undone. */
  danger?: boolean
}

interface ConfirmState extends ConfirmOptions {
  resolve: (confirmed: boolean) => void
}

/**
 * A yes/no question as a promise, rendered by the one ConfirmDialog mounted in App.vue — the
 * replacement for window.confirm, which the browser draws in its own style over the app's and
 * which some browsers let the user silence for the page. Module-level singleton like the other
 * shared stores here; a second question asked while one is showing answers the first with "no"
 * rather than stacking, since nothing in the app asks two at once.
 */
const state = ref<ConfirmState | null>(null)

export function useConfirm() {
  function confirm(options: ConfirmOptions): Promise<boolean> {
    state.value?.resolve(false)
    return new Promise<boolean>((resolve) => {
      state.value = { ...options, resolve }
    })
  }

  function answer(confirmed: boolean) {
    const pending = state.value
    state.value = null
    pending?.resolve(confirmed)
  }

  return { state, confirm, answer }
}
