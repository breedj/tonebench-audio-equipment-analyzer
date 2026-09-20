import { describe, expect, it } from 'vitest'
import type { ImpedanceTrace } from '../composables/useImpedance'
import { ohmsToDbOhms } from '../impedance/equivalent'
import { formatDampingFactor, nearestInLogFrequency, zoutReadings } from './readings'

function trace(freqHz: number[], ohms: number[], phaseDeg?: number[]): ImpedanceTrace {
  const db = ohms.map(ohmsToDbOhms)
  return {
    kind: 'Impedance',
    startFreqHz: freqHz[0] ?? 0,
    endFreqHz: freqHz[freqHz.length - 1] ?? 0,
    freqHz,
    magnitudeMinDbOhms: db,
    magnitudeMaxDbOhms: [...db],
    phaseDeg: phaseDeg ?? freqHz.map(() => 0),
    coherence: freqHz.map(() => 1),
    peakDbOhms: Math.max(...db),
    peakFreqHz: freqHz[db.indexOf(Math.max(...db))] ?? 0,
  }
}

describe('zoutReadings', () => {
  it('quotes the three specification points, the worst and the median, with the damping factor against the nominal', () => {
    // The BC-1's figures: 0.028 ohm at LF rising to 0.066 at 20 kHz.
    const r = zoutReadings(trace([20, 100, 1000, 10000, 20000], [0.028, 0.028, 0.028, 0.038, 0.066]), 8, false)!
    expect(r.low.freqHz).toBe(20)
    expect(r.at1k.freqHz).toBe(1000)
    expect(r.high.freqHz).toBe(20000)
    expect(r.at1k.dampingFactor).toBeCloseTo(286, 0)
    expect(r.high.dampingFactor).toBeCloseTo(121, 0)
    expect(r.worst).toMatchObject({ freqHz: 20000 })
    expect(r.worst.ohms).toBeCloseTo(0.066, 6)
    expect(r.medianOhms).toBeCloseTo(0.028, 6)
    // Magnitude only: no coil to quote.
    expect(r.topEquivalent).toBeNull()
  })

  it('reads the series R and L at the top point when the run was referenced', () => {
    // 0.19 ohm at +81.5 degrees at 20 kHz: 0.028 ohm resistive, 0.188 ohm reactive = 1.5 uH.
    const r = zoutReadings(trace([1000, 20000], [0.028, 0.19], [0, 81.5]), 8, true)!
    expect(r.topEquivalent).not.toBeNull()
    expect(r.topEquivalent!.resistanceOhms).toBeCloseTo(0.028, 2)
    expect(r.topEquivalent!.inductanceH! * 1e6).toBeCloseTo(1.5, 1)
  })

  it('quotes the nearest point in log frequency, gives a zero impedance no damping factor, and is null for an empty trace', () => {
    // 800 and 1300 straddle 1 kHz; 800 is nearer in log frequency though 1300 is nearer in hertz.
    expect(nearestInLogFrequency([20, 800, 1300, 20000], 1000)).toBe(1)
    const r = zoutReadings(trace([20, 1000], [0, 0.5]), 8, false)!
    expect(r.low.dampingFactor).toBeNull()
    expect(r.at1k.dampingFactor).toBe(16)
    expect(zoutReadings(trace([], []), 8, false)).toBeNull()
  })

  it('formats a damping factor with the precision its size deserves', () => {
    expect(formatDampingFactor(286.4)).toBe('286')
    expect(formatDampingFactor(12.44)).toBe('12.4')
    expect(formatDampingFactor(3.954)).toBe('3.95')
    expect(formatDampingFactor(null)).toBe('—')
  })
})
