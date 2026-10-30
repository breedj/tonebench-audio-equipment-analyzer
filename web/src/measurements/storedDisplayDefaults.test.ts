import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// <p><b>What a measurement does not state about its chart reads as the app's own default, never as
// whatever the last measurement left on screen.</b> Every `*DisplayFromParams` takes a `base` for
// the fields a stored `params_json` is silent about, and it defaults to that kind's frozen default
// display -- which is what the PDF report passes, and what `impulseDisplayFromParams` reading "a
// stored null through the same constant" means. App.vue passed `current*Display()` instead, for all
// six kinds, and that single argument is the whole of the fault.</p>
//
// <p>A <em>new</em> measurement is where it bites, because the server seeds a new type's params
// with its measurement settings and no `chart*` field at all: every display field is therefore
// absent, the base is the entire answer, and the new measurement opened wearing the outgoing one's
// view -- which the dirty watcher then saved under it. Reported 2026-10-30 against the impulse
// screen's decay surface, where six view sliders make it unmissable; it was true of all six kinds
// and of every field. It also made the screen and the report disagree about one measurement, the
// report having always restored over the defaults.</p>
//
// <p>It lives in a `.vue`, and nothing in a `.vue` is testable under the node environment vitest
// runs in, so this reads App.vue as text -- the technique `charts/annotationNumbersDefault.test.ts`
// and `about/libraries.test.ts` already use, and for their reason: the thing being checked is a
// fact about the source rather than about any value this process can compute.</p>

const appSource = readFileSync(resolve(import.meta.dirname, '..', 'App.vue'), 'utf8')

/** The six restores, named as App.vue spells them. */
const RESTORES = [
  'sweepDisplayFromParams',
  'responseDisplayFromParams',
  'impedanceDisplayFromParams',
  'crosstalkDisplayFromParams',
  'outputImpedanceDisplayFromParams',
  'impulseDisplayFromParams',
] as const

/** Every argument list App.vue passes to `fn`, as written. */
function callsIn(fn: string): string[] {
  return [...appSource.matchAll(new RegExp(`\\b${fn}\\(([^)]*)\\)`, 'g'))].map((m) => m[1].trim())
}

describe('App.vue restoring a measurement type s display', () => {
  it('calls every one of the six restores, so the assertions below are not vacuous', () => {
    // A guard on the guard: a rename that made the search find nothing would otherwise leave
    // every expectation here passing over an empty list.
    for (const fn of RESTORES) expect(callsIn(fn), fn).toHaveLength(1)
  })

  it('restores over the kind s own defaults, never over what is on screen', () => {
    for (const fn of RESTORES) {
      // One argument, the JSON. A `base` of any kind would be a second -- and the one that was
      // there, `current*Display()`, is what put the previous measurement's view on a new one.
      expect(callsIn(fn)[0], fn).toBe('paramsJson')
    }
  })

  it('gives the fields it reads straight out of the JSON a default of their own too', () => {
    // These sit beside the shared restore because they belong to a panel no report draws, so the
    // display records have nothing to say about them -- which also means they get no base, and a
    // bare `if (typeof stored.x === ...)` leaves the previous measurement's value in place. Each
    // is written as an expression that answers for an absent field.
    for (const spelling of [
      "sweepAutofitOnDone.value = stored.chartAutofitOnDone === true",
      "responseAutofitOnDone.value = stored.chartAutofitOnDone === true",
      "impedanceAutofitOnDone.value = stored.chartAutofitOnDone !== false",
      "impulseView.value = stored.chartView ?? 'Impulse'",
    ]) {
      expect(appSource, spelling).toContain(spelling)
    }
  })
})
