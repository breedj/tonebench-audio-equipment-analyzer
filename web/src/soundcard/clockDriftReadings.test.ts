import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * The clock-drift dialog counts toward a minimum number of readings, and that minimum is the
 * server's: below it `ClockDriftRunner` draws no verdict and stores nothing, so a dialog counting
 * toward a different number would either promise a figure that is not coming or hide a run that has
 * already produced one.
 *
 * <p>The dialog mirrors the constant rather than being sent it, because it is a constant of the
 * build and not a property of a run — the same reasoning `cardList.test.ts` applies to
 * `PreferenceOf`. What makes a mirror safe is a test that reads both sides, which is this one, in
 * the shape `UdevRulesTests` uses on the other side of the wire: nothing else notices an edit to a
 * number in another language's project.</p>
 */
const read = (relative: string) =>
  readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8')
    .replace(/^﻿/, '')
    .replace(/\r\n/g, '\n')

const dialog = read('../components/ClockDriftDialog.vue')
const runner = read('../../../src/Tonebench.App/Services/ClockDriftRunner.cs')

function only(source: string, pattern: RegExp, what: string): number {
  const matches = [...source.matchAll(pattern)]
  expect(matches.length, `expected exactly one ${what}`).toBe(1)
  return Number(matches[0][1])
}

describe('the clock drift minimum is one number', () => {
  it('the dialog counts toward the figure the runner gates on', () => {
    const client = only(dialog, /const MIN_CLOCK_DRIFT_READINGS = (\d+)/g, 'client constant')
    const server = only(runner, /public const int MinReadings = (\d+);/g, 'server constant')

    expect(client).toBe(server)
  })

  /**
   * And the gate is the one that decides whether a verdict exists, which is in turn what
   * `RememberAsync` stores on. Pinned by name rather than by value: a gate that went back to a bare
   * `>= 3` would leave the dialog's own count saying thirty while a run of four stored a figure.
   */
  it('and the runner gates its verdict on it', () => {
    expect(runner).toContain('points.Length >= MinReadings')
  })
})
