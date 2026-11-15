import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * A two-card bench may keep a different host API on each card.
 *
 * The sound-card picker collapses each side's list towards the *other* side's host API, and repairs
 * a mismatched pair through `matchHostApis`. Both exist for one reason, stated in
 * `collapseToBestApi`'s own remarks: PortAudio cannot open a single stream across two host APIs —
 * measured 2026-11-11, a MOTU under ASIO against a G6 under WASAPI refused at every rate — so the
 * picker's natural selection was the one combination that could not work.
 *
 * **Two streams remove that premise.** Since 2026-11-15 a card on each half of a split is opened on
 * a stream of its own, on a transfer thread of its own, and the pair above connects: verified on
 * this bench as `ASIO MOTU M Series -> Windows WASAPI Luidsprekers (TOPPING USB DAC)`. Matching
 * there is no longer a safety net, it is the silent loss of ASIO on whichever card had it — which
 * is the headline benefit of the whole feature, since it is how the better converter keeps the
 * better driver. Seen doing exactly that before this rule existed: a restart dragged the MOTU off
 * ASIO onto WASAPI to follow the DAC.
 *
 * **It is a narrowing, not a removal, and the case it keeps is the reason.** A stored document
 * whose two endpoints name one interface under two *different* host APIs is still one duplex
 * stream, and still has to be repaired; `separateDevices` is true there (the two keys differ) while
 * `isSplitBench` is false, which is exactly the pair of conditions below.
 *
 * **Widened from `twoCardBench` to `isSplitBench` when the three device dialogs became one.** The
 * premise was always "one stream across two host APIs", and *no* split opens one — so a split with
 * a card on one half and a QA40x on the other was being matched too, which on that bench drags the
 * *stored* card of a direction the bench does not even open. Unreachable while the halves could
 * only be changed in another dialog, and reachable from the dropdowns here.
 *
 * Read as text because it lives in a `.vue`, which is not mountable under this suite's node
 * environment — the technique `splitNarrowing.test.ts` and `settingsInEffect.test.ts` use.
 */
const source = readFileSync(
  fileURLToPath(new URL('../components/DeviceSettingsDialog.vue', import.meta.url)),
  'utf8',
)
  .replace(/^﻿/, '')
  .replace(/\r\n/g, '\n')

describe('host APIs are matched only where one stream carries both directions', () => {
  it('offers each card its own best host API on a two-card bench', () => {
    // Both lists, because giving one side the loosening and not the other produces a pair the
    // dropdowns disagree about.
    const matches = source.match(
      /separateDevices\.value && !isSplitBench\.value \? form\.(output|input)CardKey : ''/g,
    )
    expect(matches, 'both card lists gate the cross-collapse').toHaveLength(2)
  })

  it('leaves a split alone instead of repairing it', () => {
    const at = source.indexOf('const matched = matchHostApis(')
    expect(at, 'the repair exists').toBeGreaterThan(-1)

    const guard = source.slice(source.lastIndexOf('() => {', at), at)
    expect(guard).toContain('if (!separateDevices.value || isSplitBench.value) return')
  })

  it('still repairs the one-stream pair, which is why this is a narrowing', () => {
    // The guard reads "not separate, OR a split" -- so a document whose two endpoints are one
    // interface under two host APIs (separate, not a split) still falls through to the repair.
    // A guard written on isSplitBench alone would pass the test above and lose this.
    expect(source).toContain('if (!separateDevices.value || isSplitBench.value) return')
    expect(source).not.toContain('if (isSplitBench.value) return\n    const matched')
  })
})
