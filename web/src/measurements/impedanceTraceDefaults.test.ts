import { describe, expect, it } from 'vitest'

import { preferredImpedanceTraces, type AcTraceChoice } from './acSimulationAnalysis'

/** Builds a trace list in the order a file lists it, the unit taken from the name. */
function traces(...names: string[]): AcTraceChoice[] {
  return names.map((name, index) => ({
    index,
    name,
    unit: /^i/i.test(name) ? 'A' : 'V',
  }))
}

describe('preferredImpedanceTraces', () => {
  it('opens on the source current and a driven node, not on the first of each', () => {
    // The reference amplifier's own order: an internal plate node first, a coupling capacitor's
    // current first. That pair is what the dialog used to offer.
    const list = traces('V(a1)', 'V(in)', 'V(out)', 'I(C1)', 'I(R1)', 'I(V1)')

    expect(preferredImpedanceTraces(list)).toEqual({
      voltageIndex: 1, // V(in)
      currentIndex: 5, // I(V1)
    })
  })

  it('prefers V1 over another source, whatever order they are listed in', () => {
    // Supplies and bias sources are numbered as they were drawn, so the signal source is V1 far
    // more often than it is anything else.
    expect(preferredImpedanceTraces(traces('V(in)', 'I(V2)', 'I(V1)')).currentIndex).toBe(2)
    expect(preferredImpedanceTraces(traces('V(in)', 'I(V1)', 'I(V2)')).currentIndex).toBe(1)
  })

  it('takes any source current when there is no V1', () => {
    expect(preferredImpedanceTraces(traces('V(in)', 'I(C1)', 'I(V4)')).currentIndex).toBe(2)
  })

  it('never reads a subcircuit pin current as a source current', () => {
    // `Ix(u1:1)` is a pin of a subcircuit, not a source, and there are often dozens of them.
    const list = traces('V(in)', 'Ix(u1:1)', 'Ix(v1:2)', 'I(C1)')
    expect(preferredImpedanceTraces(list).currentIndex).toBe(1) // the first current, unchanged
  })

  it('falls back to the first of each where nothing is named helpfully', () => {
    // A netlist of n001, n002… is the ordinary case for a schematic nobody labelled, and there the
    // old behaviour is still the only answer available.
    const list = traces('V(n001)', 'V(n002)', 'I(C1)', 'I(R1)')
    expect(preferredImpedanceTraces(list)).toEqual({ voltageIndex: 0, currentIndex: 2 })
  })

  it('recognises the names a one-port’s driven node actually carries', () => {
    for (const [name, at] of [
      ['V(in)', 1],
      ['V(input)', 1],
      ['V(src)', 1],
      ['V(source)', 1],
      ['V(vin)', 1],
      ['V(dut)', 1],
      ['V(load)', 1],
      ['V(spk)', 1],
      ['V(speaker)', 1],
      ['V(z)', 1],
      ['V(zin)', 1],
      ['V(in1)', 1],
      ['V(INPUT)', 1],
    ] as const) {
      expect(preferredImpedanceTraces(traces('V(a1)', name, 'I(V1)')).voltageIndex).toBe(at)
    }
  })

  it('does not match a node whose name merely contains one of those words', () => {
    // `V(preamp_in_hp)` is some node inside the circuit, not the one the source drives.
    const list = traces('V(a1)', 'V(preamp_in_hp)', 'V(loaded)', 'I(V1)')
    expect(preferredImpedanceTraces(list).voltageIndex).toBe(0)
  })

  it('answers null for a half that is not in the file', () => {
    expect(preferredImpedanceTraces(traces('V(in)', 'V(out)'))).toEqual({
      voltageIndex: 0,
      currentIndex: null,
    })
    expect(preferredImpedanceTraces(traces('I(V1)'))).toEqual({
      voltageIndex: null,
      currentIndex: 0,
    })
    expect(preferredImpedanceTraces([])).toEqual({ voltageIndex: null, currentIndex: null })
  })
})
