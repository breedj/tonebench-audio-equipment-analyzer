import { describe, expect, it } from 'vitest'
import {
  componentComparison,
  equivalentComparison,
  measurementsComparison,
  type ComparisonSubject,
} from './comparison'
import { componentReading, impedanceReadings } from './readings'
import { driverTestTrace, impedanceTestTrace, portedDriverTestTrace } from './testTrace'

/** A resistive curve with a peak partway up, so every Measurements column has something to say. */
const flatish = impedanceTestTrace([20, 100, 1000, 10000, 20000], [8, 30, 6, 7, 12])

function subject(overrides: Partial<ComparisonSubject> = {}): ComparisonSubject {
  return { id: 'live', name: 'Current measurement', trace: flatish, referenceOhms: 100, generatorAmpDbv: 0, ...overrides }
}

describe('measurementsComparison', () => {
  it('quotes the readout panel, one row per curve, in the order it was given them', () => {
    const table = measurementsComparison([
      subject(),
      subject({ id: 'stored-7', name: 'Woofer, cold', trace: driverTestTrace(), referenceOhms: 99.7, generatorAmpDbv: -20 }),
    ])

    expect(table.title).toBe('Measurements')
    expect(table.columns.map((c) => c.label)).toEqual(
      ['Peak |Z|', 'at', 'Minimum |Z|', 'at', 'Median |Z|', 'Drive current', 'Reference R'],
    )
    expect(table.rows.map((r) => r.id)).toEqual(['live', 'stored-7'])
    expect(table.rows[0].cells).toEqual(['30.000 Ω', '100.0 Hz', '6.000 Ω', '1.00 kHz', '8.000 Ω', '9.259 mA', '100.0 Ω'])
  })

  it('reads each curve against its own reference resistor and generator level, not the screen’s', () => {
    // Two rows of one table describe two runs, and a stored one may have used a different fixture.
    // Taking the live reference for both would put a wrong drive current in the comparison.
    const [live, stored] = measurementsComparison([
      subject(),
      subject({ id: 'stored-1', name: 'Earlier', referenceOhms: 10, generatorAmpDbv: 0 }),
    ]).rows

    expect(live.cells[6]).toBe('100.0 Ω')
    expect(stored.cells[6]).toBe('10.000 Ω')
    // 1 V over (R + median Z): 8/108 vs 8/18 of an amp, so the smaller resistor draws far more.
    expect(live.cells[5]).toBe('9.259 mA')
    expect(stored.cells[5]).toBe('55.556 mA')
  })

  it('dashes a curve it cannot read rather than dropping its row', () => {
    const empty = impedanceTestTrace([], [])
    const table = measurementsComparison([subject({ id: 'stored-2', name: 'Empty', trace: empty })])
    expect(table.rows[0].cells).toEqual(['—', '—', '—', '—', '—', '—', '—'])
  })

  it('cannot disagree with the readout panel, because both read impedanceReadings', () => {
    const readings = impedanceReadings(flatish, 100, 0)!
    const cells = measurementsComparison([subject()]).rows[0].cells
    expect(cells[0]).toContain(readings.peak.ohms.toFixed(3))
  })
})

describe('componentComparison', () => {
  it('transposes the component fit, one row per curve', () => {
    const table = componentComparison(
      [subject({ id: 'stored-1', name: 'Driver A', trace: driverTestTrace() }), subject({ id: 'stored-2', name: 'Driver B', trace: driverTestTrace({ fsHz: 60 }) })],
      'Loudspeaker',
    )!

    expect(table.title).toBe('Loudspeaker')
    expect(table.columns.map((c) => c.label)).toEqual(['fs', 'Zmax', 'Re', 'f1 / f2', 'Qms', 'Qes', 'Qts'])
    expect(table.rows.map((r) => r.name)).toEqual(['Driver A', 'Driver B'])
    // The same function the side panel and the report call, so the cells are that fit's own values.
    expect(table.rows[0].cells).toEqual(componentReading(driverTestTrace(), 'Loudspeaker')!.rows.map((r) => r.value))
  })

  it('keeps a curve whose fit was withheld, as dashes plus the note that says why', () => {
    // A ported box's Thiele-Small rows are each arithmetically correct and physically meaningless,
    // so componentReading withholds them. Dropping the row instead would leave a reader thinking
    // that run had not been measured.
    const table = componentComparison(
      [subject({ id: 'stored-1', name: 'Sealed', trace: driverTestTrace() }), subject({ id: 'stored-2', name: 'Ported', trace: portedDriverTestTrace() })],
      'Loudspeaker',
    )!

    expect(table.rows[1].cells).toEqual(table.columns.map(() => '—'))
    expect(table.notes.map((n) => n.name)).toEqual(['Sealed', 'Ported'])
    expect(table.notes[1].text).toContain('ported (bass-reflex)')
  })

  it('is null when the fixture holds nothing nameable', () => {
    expect(componentComparison([subject()], 'None')).toBeNull()
  })
})

describe('equivalentComparison', () => {
  it('reads every curve at the one frequency, and names it in the title', () => {
    const table = equivalentComparison([subject(), subject({ id: 'stored-1', name: 'Earlier' })], 900)

    expect(table.title).toBe('Equivalent circuit at 900.0 Hz')
    expect(table.columns.map((c) => c.label)).toEqual(
      ['Measured at', '|Z|', 'Phase', 'Series R', 'Series X', 'Series L / C', 'Coherence'],
    )
    // 900 Hz lands on the 1 kHz column, and the block says which column it read.
    expect(table.rows[0].cells[0]).toBe('1.00 kHz')
    expect(table.rows[0].cells[1]).toBe('6.000 Ω')
  })

  it('heads the reactive column neutrally, since two curves at one frequency need not agree on its sign', () => {
    // The side panel names it after the sign of X; one header has to serve every row here, and each
    // cell still carries its own unit.
    const inductive = impedanceTestTrace([1000], [10], [45])
    const capacitive = impedanceTestTrace([1000], [10], [-45])
    const table = equivalentComparison(
      [subject({ id: 'a', name: 'L', trace: inductive }), subject({ id: 'b', name: 'C', trace: capacitive })],
      1000,
    )

    expect(table.columns[5].label).toBe('Series L / C')
    expect(table.rows[0].cells[5]).toContain('mH')
    expect(table.rows[1].cells[5]).toContain('µF')
  })
})

describe('column tooltips', () => {
  it('explains the headers the side panels used to explain', () => {
    // These tables replaced three side panels whose every row carried its meaning on a title. A
    // table of bare symbols -- Re, Qms, ESR -- is one only a reader who already knows them can use.
    const component = componentComparison([subject({ trace: driverTestTrace() })], 'Loudspeaker')

    expect(component).not.toBeNull()
    expect(component!.columns.find((c) => c.label === 'Re')?.title).toContain('Voice-coil resistance')
    expect(component!.columns.find((c) => c.label === 'f1 / f2')?.title).toContain('geometric mean')
    // A label nobody had to explain carries no tooltip rather than an empty one.
    expect(component!.columns.find((c) => c.label === 'Zmax')?.title).toBeUndefined()
  })

  it('tells the two "at" columns apart, which a label lookup could not', () => {
    // The first is the peak's frequency -- a driver's resonance -- and the second the minimum's.
    const ats = measurementsComparison([subject()]).columns.filter((c) => c.label === 'at')

    expect(ats).toHaveLength(2)
    expect(ats[0].title).toContain('resonance')
    expect(ats[1].title).toBeUndefined()
  })
})
