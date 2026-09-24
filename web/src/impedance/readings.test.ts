import { describe, expect, it } from 'vitest'
import {
  CAPACITOR_NO_SRF_NOTE,
  LOUDSPEAKER_NO_RESONANCE_NOTE,
  LOUDSPEAKER_NOTE,
  componentLabel,
  componentReading,
  equivalentAt,
  equivalentRows,
  formatBand,
  formatQ,
  formatRatio,
  impedanceReadings,
} from './readings'
import { ohmsToDbOhms } from './equivalent'
import { driverTestTrace, impedanceTestTrace, portedDriverTestTrace } from './testTrace'

describe('impedanceReadings', () => {
  it('reads the peak off the trace, the minimum and median off the curve, and the drive current by the runner formula', () => {
    const trace = impedanceTestTrace([20, 100, 1000, 10000, 20000], [8, 30, 6, 7, 12])
    const r = impedanceReadings(trace, 100, 0)!
    expect(r.peak).toEqual({ ohms: expect.closeTo(30, 6), freqHz: 100 })
    expect(r.min).toEqual({ ohms: expect.closeTo(6, 6), freqHz: 1000 })
    expect(r.medianOhms).toBeCloseTo(8, 6)
    // 0 dBV is 1 V RMS over the 100 Ω reference plus the 8 Ω median.
    expect(r.driveCurrentAmps).toBeCloseTo(1 / 108, 6)
  })

  it('averages the middle pair of an even count, and has no drive current without a generator level', () => {
    const trace = impedanceTestTrace([20, 200, 2000, 20000], [4, 8, 6, 10])
    const r = impedanceReadings(trace, 100, null)!
    expect(r.medianOhms).toBeCloseTo(7, 6)
    expect(r.driveCurrentAmps).toBeNull()
  })

  it('is null for an empty trace', () => {
    expect(impedanceReadings(impedanceTestTrace([], []), 100, 0)).toBeNull()
  })
})

describe('the format helpers', () => {
  it('quote a band, a ratio and a Q the way the readout panel does', () => {
    expect(formatBand({ fromHz: 1200, toHz: 48000 })).toBe('1.20 kHz–48.0 kHz')
    expect(formatBand(null)).toBe('—')
    expect(formatRatio(0.25)).toBe('0.250')
    expect(formatRatio(0.0005)).toBe('5.0e-4')
    expect(formatRatio(null)).toBe('—')
    expect(formatQ(2.456)).toBe('2.46')
    expect(formatQ(null)).toBe('—')
    expect(componentLabel('Loudspeaker')).toBe('Loudspeaker')
  })
})

describe('componentReading', () => {
  it('is null when the type says nothing is on the fixture', () => {
    expect(componentReading(driverTestTrace(), 'None')).toBeNull()
  })

  it('reports a driver as its Thiele-Small rows in the panel order, with the panel note', () => {
    const reading = componentReading(driverTestTrace(), 'Loudspeaker')!
    expect(reading.label).toBe('Loudspeaker')
    expect(reading.rows.map((r) => r.label)).toEqual(['fs', 'Zmax', 'Re', 'f1 / f2', 'Qms', 'Qes', 'Qts'])
    const byLabel = Object.fromEntries(reading.rows.map((r) => [r.label, r.value]))
    // The grid sample nearest 45 Hz, at 48 per octave.
    expect(byLabel.fs).toBe('44.90 Hz')
    expect(byLabel.Zmax).toBe('59.996 Ω')
    expect(byLabel.Qms).toBe('2.49')
    expect(reading.notes).toEqual([LOUDSPEAKER_NOTE])
  })

  it('withholds the Thiele-Small rows, and says why, when the curve has two peaks', () => {
    // A ported box. Every row would be arithmetically correct for whichever peak was chosen and
    // physically meaningless, so none of them is shown — see the note itself for the measurement
    // this came from.
    const reading = componentReading(portedDriverTestTrace(), 'Loudspeaker')!
    expect(reading.rows).toEqual([])
    expect(reading.notes).toHaveLength(1)
    expect(reading.notes[0]).toContain('ported (bass-reflex)')
    // Both peaks and the minimum between them, named, in ascending order.
    expect(reading.notes[0]).toContain('at 37.85 Hz and 104.0 Hz')
    expect(reading.notes[0]).toContain('minimum between them at 62.74 Hz')
  })

  it('keeps the Thiele-Small rows for a single resonance, which a sealed box or free air gives', () => {
    expect(componentReading(driverTestTrace(), 'Loudspeaker')!.notes).toEqual([LOUDSPEAKER_NOTE])
  })

  it('says so, with no rows, when a loudspeaker was named but the curve has no resonance', () => {
    const reading = componentReading(impedanceTestTrace([20, 100, 1000, 10000, 20000], [8, 8, 8, 8, 8]), 'Loudspeaker')!
    expect(reading.rows).toEqual([])
    expect(reading.notes).toEqual([LOUDSPEAKER_NO_RESONANCE_NOTE])
  })

  it('reports a capacitor below its self-resonance as C alone, with the note that says why', () => {
    // 10 µF with a D of 0.02 (a real loss, above the trust threshold), 20 Hz to 2 kHz — nowhere
    // near the SRF, so the phase sits a degree or so above −90° the whole way.
    const freqHz: number[] = []
    const ohms: number[] = []
    const phaseDeg: number[] = []
    for (let f = 20; f <= 2000; f *= 1.2) {
      const x = -1 / (2 * Math.PI * f * 10e-6)
      const r = 0.02 * Math.abs(x)
      freqHz.push(f)
      ohms.push(Math.hypot(r, x))
      phaseDeg.push((Math.atan2(x, r) * 180) / Math.PI)
    }
    const reading = componentReading(impedanceTestTrace(freqHz, ohms, phaseDeg), 'Capacitor')!
    expect(reading.label).toBe('Capacitor')
    expect(reading.rows.map((r) => r.label)).toEqual(['C', 'over', 'SRF', 'ESR', 'ESL', 'D'])
    expect(reading.rows[0].value).toBe('10.000 µF')
    expect(reading.rows[2].value).toBe('—')
    expect(reading.notes).toEqual([CAPACITOR_NO_SRF_NOTE])
  })
})

describe('equivalentAt', () => {
  it('reads the measured column nearest the frequency asked for, as a series R + jX', () => {
    // 45° at 1 kHz on 10 Ω: R and X are both 10/√2, and that reactance is 1.125 mH.
    const trace = impedanceTestTrace([100, 1000, 10000], [10, 10, 10], [0, 45, 0])
    const reading = equivalentAt(trace, 900)!

    expect(reading.freqHz).toBe(1000)
    expect(reading.equivalent.resistanceOhms).toBeCloseTo(7.0711, 3)
    expect(reading.equivalent.reactanceOhms).toBeCloseTo(7.0711, 3)
    expect(reading.equivalent.inductanceH).toBeCloseTo(0.0011254, 6)
    expect(reading.equivalent.capacitanceF).toBeNull()
  })

  it('takes |Z| as the midpoint of the column, not its maximum', () => {
    // The max carries the aggregation bias of a maximum over several bins — right for a peak
    // reading, wrong for "what is the impedance here".
    const trace = impedanceTestTrace([1000], [10])
    trace.magnitudeMinDbOhms = [ohmsToDbOhms(8)]
    trace.magnitudeMaxDbOhms = [ohmsToDbOhms(12)]
    expect(equivalentAt(trace, 1000)!.ohms).toBeCloseTo(10, 6)
  })

  it('is null for a curve with no columns', () => {
    expect(equivalentAt(impedanceTestTrace([], []), 1000)).toBeNull()
  })
})

describe('equivalentRows', () => {
  it('names the reactive row after the sign of the reactance', () => {
    const inductive = equivalentRows(equivalentAt(impedanceTestTrace([1000], [10], [45]), 1000))
    const capacitive = equivalentRows(equivalentAt(impedanceTestTrace([1000], [10], [-45]), 1000))

    expect(inductive.map((r) => r.label)).toEqual(['Measured at', '|Z|', 'Phase', 'Series R', 'Series X', 'Series inductance', 'Coherence'])
    expect(capacitive[5].label).toBe('Series capacitance')
    expect(equivalentRows(null)[5].label).toBe('Series L or C')
  })

  it('dashes every value when there is nothing to read, rather than dropping the rows', () => {
    expect(equivalentRows(null).map((r) => r.value)).toEqual(['—', '—', '—', '—', '—', '—', '—'])
  })
})
