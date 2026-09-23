import { describe, expect, it } from 'vitest'
import { harmonicLevelAt, harmonicRows, impulseReadingRows, impulseReferenceLabel, type ImpulseRunFacts } from './impulseReadings'
import { harmonicTestCurve, impulseTestTrace } from './impulseTestData'

function run(over: Partial<ImpulseRunFacts> = {}): ImpulseRunFacts {
  return {
    referenceMode: 'None',
    referenceName: null,
    latencySamples: null,
    averagesTaken: 4,
    averageCount: 4,
    generatorAmpDbv: -12.5,
    ...over,
  }
}

function rowsToObject(rows: { label: string; value: string }[]) {
  return Object.fromEntries(rows.map((r) => [r.label, r.value]))
}

describe('impulseReferenceLabel', () => {
  it('names each reference the way the Display panel does', () => {
    expect(impulseReferenceLabel('None', null)).toBe('the generated sweep')
    expect(impulseReferenceLabel('RightChannel', null)).toBe('right input channel')
    expect(impulseReferenceLabel('StoredLoopback', 'bench loopback')).toBe('bench loopback')
    expect(impulseReferenceLabel('StoredLoopback', null)).toBe('stored loopback')
  })
})

describe('impulseReadingRows', () => {
  it('quotes the impulse and the run in the panel order', () => {
    expect(rowsToObject(impulseReadingRows(impulseTestTrace(), run()))).toEqual({
      Peak: '954 mV',
      Gate: '−5 / +16 ms',
      'Resolved above': '48 Hz',
      Record: '4032 samples at 192.0 kHz',
      Reference: 'the generated sweep',
      Records: '4 of 4',
      Generator: '-12.50 dBV',
    })
  })

  it('adds the removed delay in samples and milliseconds, and only when there is one', () => {
    const rows = rowsToObject(impulseReadingRows(impulseTestTrace(), run({ referenceMode: 'RightChannel', latencySamples: 48.2 })))
    expect(rows['DUT delay']).toBe('48.2 samples (0.251 ms)')
    expect(rows.Reference).toBe('right input channel')
    expect(impulseReadingRows(impulseTestTrace(), run()).some((r) => r.label === 'DUT delay')).toBe(false)
  })

  it('shows a stored result the count alone, since it did not keep how many records it took', () => {
    expect(rowsToObject(impulseReadingRows(impulseTestTrace(), run({ averagesTaken: null }))).Records).toBe('4')
  })

  it('reports the run alone when there is no impulse, and omits a generator level it has not got', () => {
    const rows = impulseReadingRows(null, run({ generatorAmpDbv: null }))
    expect(rows.map((r) => r.label)).toEqual(['Reference', 'Records'])
  })
})

describe('harmonicLevelAt', () => {
  const h2 = harmonicTestCurve(2, 9600, -60)

  it('reads the level nearest the asked-for frequency in log f', () => {
    expect(harmonicLevelAt(h2, 1000)).toBeCloseTo(-60, 0)
    expect(harmonicLevelAt(h2, 100)).toBeCloseTo(-54, 0)
  })

  it('is null past the order’s ceiling, and where the curve has no point near enough', () => {
    expect(harmonicLevelAt(h2, 10000)).toBeNull()
    expect(harmonicLevelAt(harmonicTestCurve(3, 6400, -70), 10000)).toBeNull()
    // A curve that stops at 40 Hz must not answer for 100 Hz, even though 40 is its nearest point.
    expect(harmonicLevelAt({ order: 2, fundamentalHz: [20, 40], levelDbc: [-50, -52], upperValidHz: 20000 }, 100)).toBeNull()
  })
})

describe('harmonicRows', () => {
  it('lays out a row per order, lowest first, with its ceiling and the three quoted levels', () => {
    const { headers, cells } = harmonicRows([harmonicTestCurve(3, 6400, -70), harmonicTestCurve(2, 9600, -60)])
    expect(headers).toEqual(['Order', 'Valid up to', '100.0 Hz', '1.00 kHz', '10.0 kHz'])
    expect(cells.map((c) => c[0])).toEqual(['H2', 'H3'])
    expect(cells[0][1]).toBe('9.60 kHz')
    expect(cells[0][2]).toBe('-54.0 dBc')
    expect(cells[0][3]).toBe('-60.0 dBc')
    // Both orders stop below 10 kHz.
    expect(cells[0][4]).toBe('—')
    expect(cells[1][4]).toBe('—')
  })
})
