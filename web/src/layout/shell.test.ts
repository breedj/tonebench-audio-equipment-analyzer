import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * The app's shell is one viewport-tall column that never scrolls itself.
 *
 * <p><b>What it costs when it does.</b> The root carried <code>h-screen</code> and no
 * <code>overflow-hidden</code>, and the three content-sized children in it — the update banner, the
 * header and the status bar — carried no <code>shrink-0</code>. So anything that pushed the column
 * past the viewport spilled into the <em>document</em>, which grew a scrollbar of its own: using it
 * took the status bar up out of sight, which is what was reported on 2026-11-20. A status bar that
 * can be scrolled away is not a status bar.</p>
 *
 * <p>The easiest way in was a horizontal scrollbar, since <code>vh</code> units ignore one — so
 * part of the fix is <code>h-dvh</code>, which does not, and <code>overflow-hidden</code>, which
 * makes that question moot. <b>Neither was enough</b>: measured in the running app with both in
 * place, the document still scrolled 709&nbsp;px, because <code>overflow-hidden</code> on a
 * <em>static</em> box clips only what it is the containing block for — and the <code>sr-only</code>
 * panel-hint spans are <code>position: absolute</code> at a static position deep inside the scrolled
 * <code>&lt;main&gt;</code>, so they were laid out against the initial containing block and escaped
 * every clip above them. Hence <code>relative</code> on the shell, which is the half that actually
 * closes it.</p>
 *
 * <p>Nothing audited the shell before this, which is why it went unnoticed; and it is all markup in
 * <code>.vue</code> files against a real DOM while this suite runs on <code>environment: node</code>,
 * so it is read as text — the technique the persistence and v-model audits already use.</p>
 */

const read = (relative: string) =>
  readFileSync(resolve(import.meta.dirname, '..', relative), 'utf8')

describe('the app shell', () => {
  const app = read('App.vue')

  it('is a viewport-tall column that cannot scroll the document', () => {
    expect(app).toMatch(/class="relative flex h-dvh flex-col overflow-hidden/)
    // `h-screen` is `100vh`, which ignores a horizontal scrollbar; one in a class is one too many
    // now. (The word is left free in prose, where this file's own note explains the swap.)
    for (const attribute of app.match(/class="[^"]*"/g) ?? []) {
      expect(attribute).not.toMatch(/\bh-screen\b/)
    }
  })

  it('is a containing block, so nothing absolute inside it can escape to the document', () => {
    // The clip above is only as good as this: without it an absolutely positioned descendant with
    // no closer positioned ancestor is laid out against the initial containing block, and a
    // clipping ancestor it is not contained by cannot clip it. That is a whole class of fault and
    // not one element's, which is why it is pinned on the shell rather than on the spans.
    const shell = (app.match(/class="[^"]*h-dvh[^"]*"/) ?? [''])[0]
    expect(shell).toMatch(/\brelative\b/)
  })

  it('gives way in the middle rather than at either end', () => {
    // The one child that may shrink is the one that clips and so has a floor of zero.
    expect(app).toMatch(/class="flex flex-1 overflow-hidden"/)
  })

  it('holds the header at its own height', () => {
    const header = app.slice(app.indexOf('<header'), app.indexOf('</header>'))
    expect(header).toMatch(/\bshrink-0\b/)
  })
})

describe('the two bars the shell cannot be allowed to squash', () => {
  it('pins the status bar', () => {
    const footer = read('components/DeviceStatusBar.vue')
    const open = footer.slice(footer.indexOf('<footer'), footer.indexOf('>', footer.indexOf('<footer')))
    expect(open).toMatch(/\bshrink-0\b/)
  })

  it('pins the update banner', () => {
    const banner = read('components/UpdateBanner.vue')
    expect(banner.slice(banner.indexOf('<template>'))).toMatch(/\bshrink-0\b/)
  })
})
