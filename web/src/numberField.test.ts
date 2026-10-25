import { describe, expect, it } from 'vitest'
import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { clampNumberField } from './numberField'

describe('clampNumberField', () => {
  it('holds a value inside its floor and ceiling', () => {
    expect(clampNumberField(5, 1, 10)).toBe(5)
    expect(clampNumberField(-3, 1, 10)).toBe(1)
    expect(clampNumberField(99, 1, 10)).toBe(10)
  })

  it('has no ceiling unless it is given one', () => {
    expect(clampNumberField(1e9, 0.001)).toBe(1e9)
  })

  /**
   * An emptied box is `''` rather than NaN — Vue's `.number` modifier hands back the original
   * string when it cannot parse one — and every panel carrying one of these fields already has its
   * own answer for an empty box, which this must not pre-empt by inventing a number.
   */
  it('hands back anything that is not a finite number untouched', () => {
    expect(clampNumberField('' as unknown as number, 1, 10)).toBe('')
    expect(clampNumberField(Number.NaN, 1, 10)).toBeNaN()
  })

  /** `freqCeilingHz` is undefined until the sample rate is known, which is the default's whole job. */
  it('treats an absent ceiling as no ceiling', () => {
    expect(clampNumberField(50000, 2, undefined)).toBe(50000)
  })
})

// The guard, and the reason this file reads the components as text at all: HTML anchors a number
// input's step grid on `min`, so the values its spinner offers are min + k x step rather than
// k x step. `min="2" step="1000"` offers 2, 1002, 2002 — and a field sitting at 20000 steps up to
// 20002 and back down to 19002. Reported 2026-10-25 against the decay charts' To (Hz) box; eight
// inputs had it, including both From/To pairs, the two sweep stop thresholds, the gate's After and
// the decay span. Nothing but the source can say whether a field has fallen back into it, which is
// the technique annotationNumbersDefault.test.ts and about/libraries.test.ts already use.

const sourceRoot = resolve(import.meta.dirname)

function vueFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) return vueFiles(path)
    return entry.isFile() && entry.name.endsWith('.vue') ? [path] : []
  })
}

interface NumberInput {
  file: string
  min: number
  step: number
}

/** Every `type="number"` input that spells out both a literal min and a literal step. */
function boundedNumberInputs(): NumberInput[] {
  const found: NumberInput[] = []

  for (const path of vueFiles(sourceRoot)) {
    const source = readFileSync(path, 'utf8')
    for (const tag of source.match(/<input\b[^>]*>/gs) ?? []) {
      if (!tag.includes('type="number"')) continue
      const min = /(?<![:\w-])min="([^"]+)"/.exec(tag)
      const step = /(?<![:\w-])step="([^"]+)"/.exec(tag)
      if (!min || !step) continue

      const floor = Number(min[1])
      const grid = Number(step[1])
      if (!Number.isFinite(floor) || !Number.isFinite(grid) || grid <= 0) continue
      found.push({ file: path.slice(sourceRoot.length + 1), min: floor, step: grid })
    }
  }

  return found
}

describe('a number input with both a min and a step', () => {
  it('is spelled the way this test can read, so the check is not vacuous', () => {
    // The app does still have such fields; a markup change that made the regexes match nothing
    // would otherwise leave the assertion below passing over an empty list.
    expect(boundedNumberInputs().length).toBeGreaterThan(5)
  })

  it('steps on round numbers, because min is the step base', () => {
    const offGrid = boundedNumberInputs().filter((input) => {
      const remainder = Math.abs(input.min % input.step)
      return remainder > 1e-9 && Math.abs(remainder - input.step) > 1e-9
    })

    expect(
      offGrid.map((input) => `${input.file}: min=${input.min} step=${input.step}`),
    ).toEqual([])
  })
})
