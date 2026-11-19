import { ref } from 'vue'

/**
 * Holds what is being typed into a number box, so a re-render cannot throw it away.
 *
 * <p><b>The fault this exists for.</b> Vue's <code>patchProp</code> carries an explicit carve-out
 * past its own equality check for <code>value</code>, and <code>patchDOMProp</code> then writes the
 * element whenever it differs from the binding — which, half way through typing, it always does. So
 * a box bound as <code>:value="someComputed"</code> is re-stamped with the committed figure on
 * <em>every</em> re-render, not merely when that figure changes. On a panel that re-renders several
 * times a second, which is any rail reading a live record, the result is a box you cannot type
 * into: "0.5" on its way to "0.05" is replaced before <code>change</code> can fire.</p>
 *
 * <p>It is the same carve-out that makes an open <code>&lt;select&gt;</code> flicker, one element
 * along, and this project has now paid for it three times — the Configuration dialog's capture
 * delay, the oscilloscope's Periods field, and the scope rail's mask tolerance, which is what
 * prompted this being shared rather than open-coded a fourth time.</p>
 *
 * <p><b>Why a draft and not <code>v-model</code>.</b> Vue's own <code>vModelText</code> directive
 * skips its write while the element is focused, which is why the boxes on these rails that <em>can</em>
 * use <code>v-model</code> never showed the fault. The ones that cannot are the ones that convert:
 * a tolerance stored in seconds and shown in the rail's own time unit has no single ref to bind.</p>
 *
 * <p><b>A string, deliberately.</b> The field has to be able to be genuinely empty while it is being
 * retyped, and to hold "-" or "0." on the way to a number — none of which <code>v-model.number</code>
 * can express. Nothing here parses: the owner's existing change handler does that, with whatever
 * clamping it already applies, and this only decides what the box <em>shows</em> until then.</p>
 */
export interface NumberDrafts {
  /**
   * What the box should show: the draft while one is being typed, the committed figure otherwise.
   *
   * @param name Any stable name for the box; only this composable sees it.
   * @param committed What the box shows when nobody is typing in it.
   */
  show(name: string, committed: number | string): string
  /** For <code>@input</code>: keeps what is in the box as it is typed. */
  type(name: string, event: Event): void
  /**
   * For <code>@change</code> and <code>@blur</code>: hands the box back to its committed figure.
   *
   * <p>Called <em>before</em> the owner's own change handler, so that handler is free to write a
   * rejected value straight back into <code>event.target.value</code> the way these handlers
   * already do — the next render then agrees with it rather than with a stale draft.</p>
   */
  end(name: string): void
}

export function useNumberDrafts(): NumberDrafts {
  // A plain object in a ref rather than a reactive Map: the set of names is fixed by the template,
  // so there is nothing to iterate and replacement is what the renders watch.
  const drafts = ref<Record<string, string>>({})

  return {
    show(name, committed) {
      const draft = drafts.value[name]
      return draft !== undefined ? draft : String(committed)
    },
    type(name, event) {
      drafts.value = { ...drafts.value, [name]: (event.target as HTMLInputElement).value }
    },
    end(name) {
      if (!(name in drafts.value)) return
      const next = { ...drafts.value }
      delete next[name]
      drafts.value = next
    },
  }
}
