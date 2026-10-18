import { describe, expect, it } from 'vitest'
import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'

// The one fault in this feature that no ordinary test could see, because it lives in a `.vue` and
// nothing in a `.vue` is testable under the node environment vitest runs in: a chart that declares
// `annotationNumbers?: boolean` and gives it no default gets `false` from Vue, not undefined, so
// `props.annotationNumbers !== false` reads false and the ordinal badge is never drawn. Ten of the
// eleven annotated charts shipped that way for a day (Joost, 2026-10-18), and the suite was blind
// to it -- useAnnotationOverlay.test.ts passes `numbers: () => true` at every one of its call
// sites, which is the right thing for a composable test and exactly why it could not catch this.
//
// So this reads the components as text. It is the technique about/libraries.test.ts already uses on
// package.json and the .csproj files, and for the same reason: the thing being checked is a fact
// about the source rather than about any value this process can compute.

const componentsRoot = resolve(import.meta.dirname, '..', 'components')

/** Every chart component whose props declare the flag, with its source. */
function chartsDeclaringTheFlag(): { name: string; source: string }[] {
  return readdirSync(componentsRoot)
    .filter((name) => name.endsWith('.vue'))
    .map((name) => ({ name, source: readFileSync(join(componentsRoot, name), 'utf8') }))
    .filter((file) => /^\s*annotationNumbers\?:\s*boolean/m.test(file.source))
}

describe('the annotationNumbers prop', () => {
  it('is declared by the charts that take it as a prop, so this test is not vacuous', () => {
    // A guard on the guard: a rename that made the regex above match nothing would otherwise leave
    // every assertion below passing over an empty list.
    expect(chartsDeclaringTheFlag().map((f) => f.name).sort()).toEqual([
      'FrequencyResponseChart.vue',
      'ImpedanceChart.vue',
      'ImpedancePlaneChart.vue',
      'ImpulseHarmonicsChart.vue',
      'ImpulseResponseChart.vue',
      'OscilloscopeChart.vue',
      'ResponsePolarChart.vue',
      'ResponseSPlaneChart.vue',
      'SpectrogramChart.vue',
      'ThdSweepChart.vue',
    ])
  })

  it('carries a default of true wherever it is a prop, or its chart draws no ordinal badges', () => {
    for (const file of chartsDeclaringTheFlag()) {
      expect(file.source, `${file.name} must wrap defineProps in withDefaults`).toContain('withDefaults(defineProps<{')
      expect(file.source, `${file.name} must default annotationNumbers to true`).toMatch(
        /annotationNumbers:\s*true/,
      )
    }
  })

  it('is read through a guard that lets the report turn it off, not one that ignores the prop', () => {
    // `!== false` rather than `?? true` on purpose: with a default present the two agree for an
    // absent prop, and only this one honours the explicit `false` report/render.ts passes when the
    // Annotations table was left out of a chapter.
    for (const file of chartsDeclaringTheFlag()) {
      expect(file.source, `${file.name} must feed the overlay from the prop`).toMatch(
        /numbers:\s*\(\)\s*=>\s*props\.annotationNumbers\s*!==\s*false/,
      )
    }
  })
})
