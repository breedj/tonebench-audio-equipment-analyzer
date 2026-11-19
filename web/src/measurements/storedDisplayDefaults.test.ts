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

/** The seven restores, named as App.vue spells them. */
const RESTORES = [
  'sweepDisplayFromParams',
  'responseDisplayFromParams',
  'impedanceDisplayFromParams',
  'crosstalkDisplayFromParams',
  'outputImpedanceDisplayFromParams',
  'impulseDisplayFromParams',
  'scopeDisplayFromParams',
] as const

/** Every argument list App.vue passes to `fn`, as written. */
function callsIn(fn: string): string[] {
  return [...appSource.matchAll(new RegExp(`\\b${fn}\\(([^)]*)\\)`, 'g'))].map((m) => m[1].trim())
}

describe('App.vue restoring a measurement type s display', () => {
  it('calls every one of the seven restores, so the assertions below are not vacuous', () => {
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

/**
 * The same seven restores, now as a question about where this file <em>declares</em> what they read.
 *
 * <p>Each of them is called from an <code>immediate</code> watcher, so each runs <em>during
 * setup</em> -- and a <code>const</code> is in its temporal dead zone until its own line has run.
 * A ref one of these writes, declared further down the file, therefore throws
 * <code>ReferenceError</code>, and the restore stops dead at that line with every assignment after
 * it never made. <b>Vue catches what a watcher throws</b>, so it reaches the console and nothing
 * else: the panel draws perfectly, with the rest of the settings it was restoring silently at their
 * defaults. That is the <code>ScopeConfigPanel</code> fault -- see the <code>defineModel</code>
 * cases in <code>triggerSpec.test.ts</code>, which pin the same rule one layer in -- and it reached
 * the scope screen a second time through <code>scopeViewWindow</code>, firing only where setup ran
 * with a measurement already open. That is Vite HMR rather than a real page load, where the type
 * arrives asynchronously, so the screen worked until it did not.</p>
 *
 * <p>The list is read out of the source rather than written down, so a kind added later is covered
 * without anyone remembering to come here.</p>
 */
const RESTORE_FUNCTIONS = [...appSource.matchAll(/function (applyStored\w*Display)\(/g)].map((m) => m[1])

/** Where App.vue declares `name` at the top level of setup, or -1. */
function declarationIndex(name: string): number {
  const m = appSource.match(new RegExp(`^(?:const|let) ${name}\\b`, 'm'))
  return m?.index ?? -1
}

/** A restore's body: from its signature to the first `}` in the first column, which closes it. */
function bodyOf(fn: string): string {
  const start = appSource.indexOf(`function ${fn}(`)
  expect(start, fn).toBeGreaterThan(-1)
  const end = appSource.indexOf('\n}', start)
  expect(end, fn).toBeGreaterThan(start)
  return appSource.slice(start, end)
}

describe('App.vue declaring what its display restores read', () => {
  it('finds every restore, so the assertions below are not vacuous', () => {
    expect(RESTORE_FUNCTIONS).toHaveLength(7)
  })

  it('declares everything a restore touches before the restore itself', () => {
    let checked = 0
    for (const fn of RESTORE_FUNCTIONS) {
      const at = appSource.indexOf(`function ${fn}(`)
      // Every identifier in the body that this file also declares at the top level of setup.
      // Anything declared inside the body, imported, or only ever a property name is simply not
      // found by `declarationIndex` and is skipped.
      const names = new Set([...bodyOf(fn).matchAll(/[A-Za-z_$][A-Za-z0-9_$]*/g)].map((m) => m[0]))
      for (const name of names) {
        const declared = declarationIndex(name)
        if (declared === -1) continue
        checked += 1
        expect(declared, `${fn} reads ${name}, which is declared below it`).toBeLessThan(at)
      }
    }
    // A guard on the guard: a rename that found nothing would leave this passing over an empty set.
    expect(checked).toBeGreaterThan(60)
  })

  it('declares the viewport state the scope screen s immediate watchers reach for', () => {
    // Named as well as swept above, because these two are the pair that was wrong: the restore
    // clears `scopeViewWindow`, and the display path's own `immediate` watcher is within reach of
    // `SCOPE_DEFAULT_COLUMNS`. A plain constant in a dead zone fails exactly as a ref does, which
    // is why the number is named here too.
    for (const name of ['scopeViewWindow', 'SCOPE_DEFAULT_COLUMNS']) {
      const declared = declarationIndex(name)
      expect(declared, name).toBeGreaterThan(-1)
      expect(declared, name).toBeLessThan(appSource.indexOf('function applyStoredScopeDisplay('))
      expect(declared, name).toBeLessThan(appSource.indexOf('void setScopeOverlayPath('))
    }
  })
})
