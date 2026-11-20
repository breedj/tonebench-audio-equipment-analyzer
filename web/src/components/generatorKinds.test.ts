import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { STIMULUS_KINDS, stimulusKind, stimulusKindLabel } from '../generator/stimulusKinds'
import { scopeToggleIcon } from '../scope/displayIcons'

/**
 * <b>Every stimulus the picker offers must put its own settings block on the request.</b>
 *
 * <p><code>GeneratorChannelPanel.currentConfig</code> is a switch on the staged kind, and a kind
 * that falls through its default sends <code>{ kind, tones: [] }</code> and nothing else — which
 * the server fills in from the settings records' own defaults. That is not an error anywhere: the
 * run starts, the analyzer plays, and what comes out is a <em>plausible</em> stimulus that is not
 * the one the panel is showing. It is the <code>ChirpStartHz</code> fault in another costume, and
 * it was real for an afternoon here: the four kinds added for bench-scope work were in the picker
 * and in the forms before they were in this switch, so a sawtooth typed at 1 kHz / −6 dBV would
 * have played at the record default of −20.</p>
 *
 * <p>It lives in a <code>.vue</code> and nothing in a <code>.vue</code> is testable under the node
 * environment vitest runs in, so this reads the component as text — the technique
 * <code>charts/seriesScale.test.ts</code> and <code>measurements/storedDisplayDefaults.test.ts</code>
 * already use, and for their reason: the thing being checked is a fact about the source.</p>
 */
const source = readFileSync(resolve(import.meta.dirname, 'GeneratorChannelPanel.vue'), 'utf8')

/**
 * The picker's own list.
 *
 * <p><b>Imported rather than parsed</b>, since the list moved out of the component and into
 * <code>generator/stimulusKinds.ts</code> on 2026-11-20. It used to be located by the literal
 * `const KINDS` and `= [` inside the `.vue`, bounded by the array's own closing bracket because
 * reading to the next declaration once swept a channel picker's Left/Right/Mix in with the kinds.
 * A module needs none of that and cannot be mis-bounded.</p>
 */
function offeredKinds(): string[] {
  return STIMULUS_KINDS.map((option) => option.value)
}

/** The kinds `currentConfig` names in a `case`. */
function configuredKinds(): string[] {
  const fn = source.slice(source.indexOf('function currentConfig('), source.indexOf('defineExpose('))
  return [...fn.matchAll(/case '(\w+)':/g)].map((m) => m[1])
}

describe('the generator panel request', () => {
  it('finds both lists, so the assertion below is not vacuous', () => {
    expect(offeredKinds().length).toBeGreaterThan(5)
    expect(configuredKinds().length).toBeGreaterThan(5)
  })

  it('sends a settings block for every kind the picker offers', () => {
    const configured = new Set(configuredKinds())
    for (const kind of offeredKinds()) {
      expect(configured.has(kind), `${kind} is in the picker but not in currentConfig`).toBe(true)
    }
  })

  it('offers no kind the generator has no such thing as', () => {
    // The old parse could read past the array's own closing bracket and sweep the arbitrary
    // channel picker's Left/Right/Mix in with the kinds. It cannot now, and this says so.
    for (const notAKind of ['Mix', 'OneOver24', 'Smpte', 'Off', 'EchoChirp']) {
      expect(offeredKinds()).not.toContain(notAKind)
    }
  })

  it('gives every kind it offers a figure and a sentence', () => {
    for (const option of STIMULUS_KINDS) {
      expect(scopeToggleIcon(option.icon), `${option.value} has no figure`).not.toEqual([])
      expect(option.title.length, `${option.value} has no sentence`).toBeGreaterThan(30)
    }
  })

  it('names a kind it does not offer rather than showing a blank', () => {
    // A stored measurement can carry EchoChirp, which the picker hides. The button still has to
    // read as something.
    expect(stimulusKind('EchoChirp')).toBeNull()
    expect(stimulusKindLabel('EchoChirp')).toBe('EchoChirp')
    expect(stimulusKindLabel('Tones')).toBe('Sine tones')
  })

  it('offers the four stimuli a bench scope needs', () => {
    // Named rather than left to the sweep above: the sweep only says the two lists agree, and two
    // empty lists agree too. These are the kinds this file was written for.
    const offered = offeredKinds()
    for (const kind of ['Sawtooth', 'Pulse', 'ToneBurst', 'DcLevel', 'Arbitrary']) {
      expect(offered).toContain(kind)
    }
  })
})
