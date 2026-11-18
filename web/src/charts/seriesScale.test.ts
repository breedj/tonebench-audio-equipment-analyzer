import { describe, expect, it } from 'vitest'
import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'

/**
 * A uPlot series may never be handed a scale that can be `undefined`.
 *
 * <p><b>Because `scale: undefined` is not "no scale".</b> uPlot keys such a series under the
 * literal string <code>"undefined"</code> and gives that scale <code>auto: true, distr: 1</code>,
 * so the curve is auto-ranged to <i>its own</i> data and fills the plot height whatever the axis
 * window says. Nothing else on the chart moves with it — the axis, the cursor readout, the
 * measurements and the CSV all read the trace — so only the one line is wrong, which is what makes
 * it so hard to see.</p>
 *
 * <p>It has cost this project twice. `ImpedanceChart` drew a 7.8555–7.8703 Ω resistor as a ±30 %
 * swing (its `PlottedSeries.scale` is optional, and everything on the ohms axis omits it). Then
 * `OscilloscopeScreenChart`, written afterwards, repeated it: its `ChannelSeries.scale` is set only
 * by the math trace, so every waveform went onto the phantom scale while the min/max band — which
 * spells the fallback — stayed on the volts axis. One channel then drew as <b>two traces</b>, and
 * only when the signal was small inside its window; filling the window the two coincide, which is
 * why it survived review and a first round of testing. Reported from a bench 2026-11-18: ±0.14 V
 * of real signal in a ±2 V window drew a second curve at ±1.75 V that did not move when the
 * generator level did, because an auto-ranged scale always fills the plot.</p>
 *
 * <p>An audit rather than a unit test because the fault lives in a <code>.vue</code> against a real
 * canvas, and this suite runs on <code>environment: 'node'</code> — the technique
 * <code>numberField.test.ts</code> already uses for its own class of fault.</p>
 */
describe('a uPlot series never takes a scale that can be undefined', () => {
  const componentDir = resolve(import.meta.dirname, '../components')

  /**
   * `scale: something.something,` — a property access, which is the shape that can be optional.
   * A fallback (`?? 'y'`) breaks the match, and so does a plain constant or a literal, which is
   * what makes those the safe spellings.
   */
  const RISKY = /scale:\s*[A-Za-z_$][\w$]*(?:\?)?\.[\w$.]+\s*,/g

  const offenders: string[] = []
  for (const entry of readdirSync(componentDir, { withFileTypes: true })) {
    if (!entry.isFile() || !entry.name.endsWith('.vue')) continue
    const source = readFileSync(join(componentDir, entry.name), 'utf8')
    for (const match of source.match(RISKY) ?? []) offenders.push(`${entry.name}: ${match.trim()}`)
  }

  it('is spelled with a fallback in every chart', () => {
    expect(offenders).toEqual([])
  })

  it('still recognises the shape it is looking for', () => {
    // So a regex that silently stopped matching anything cannot let this file pass by default.
    expect('series: [{ scale: channel.scale, stroke: x }]'.match(RISKY)).not.toBeNull()
    expect("scale: channel.scale ?? 'y',".match(RISKY)).toBeNull()
    expect('scale: MATH_SCALE,'.match(RISKY)).toBeNull()
    expect("scale: 'y',".match(RISKY)).toBeNull()
  })
})
