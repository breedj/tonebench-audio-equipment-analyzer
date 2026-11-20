import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * Every dialog in this app renders through <code>Modal.vue</code>, which opens the native
 * <code>&lt;dialog&gt;</code> by calling <code>showModal()</code> when its <code>open</code> prop
 * says so.
 *
 * <p><b>Syncing only on a change is not enough, and it reads as though it were.</b> The watcher
 * carries <code>{ immediate: true }</code>, whose first run happens during setup — where the
 * template ref is still null, so it returns having done nothing. A dialog mounted with
 * <code>open</code> already true, i.e. one brought into existence by a <code>v-if</code> at the
 * moment it is wanted rather than mounted for the life of the screen and toggled, therefore never
 * opens: the element is in the DOM with the right title and nothing anywhere says why. Measured on
 * the oscilloscope's Math dialog, 2026-11-20.</p>
 *
 * <p>Read as text, like every audit of a <code>.vue</code> here: the suite runs on
 * <code>environment: node</code>, so there is no DOM to mount one against.</p>
 */

const modal = readFileSync(resolve(import.meta.dirname, 'Modal.vue'), 'utf8')

describe('Modal', () => {
  it('opens a dialog that mounts already open, not only one that is toggled', () => {
    expect(modal).toMatch(/onMounted\(\(\) => syncOpen\(props\.open\)\)/)
  })

  it('keeps following the prop afterwards', () => {
    expect(modal).toMatch(/watch\(\(\) => props\.open, syncOpen\)/)
  })

  it('still routes both directions through one function', () => {
    // One place that decides, so the mount and the change cannot come to disagree about what
    // `open` means — which is the whole reason the sync is a named function rather than a body.
    expect(modal).toMatch(/if \(open && !dialog\.open\) dialog\.showModal\(\)/)
    expect(modal).toMatch(/else if \(!open && dialog\.open\) dialog\.close\(\)/)
  })
})
