import { describe, expect, it } from 'vitest'
import type { ResponseTrace } from '../composables/useFrequencyResponse'
import { crosstalkDirectionLabel, crosstalkReadings, formatCrosstalkReading, formatHz } from './readings'

function trace(freqHz: number[], db: number[], kind: ResponseTrace['kind'] = 'CrosstalkLeftToRight'): ResponseTrace {
  return {
    kind,
    startFreqHz: freqHz[0] ?? 0,
    endFreqHz: freqHz[freqHz.length - 1] ?? 0,
    freqHz,
    magnitudeMinDb: [...db],
    magnitudeMaxDb: [...db],
    phaseDeg: freqHz.map(() => 0),
    coherence: freqHz.map(() => 1),
    groupDelayS: freqHz.map(() => 0),
    phaseDelayS: freqHz.map(() => 0),
    oneDb: { dropDb: 1, symmetric: true, lowerHz: 0, upperHz: 0, bandwidthHz: 0, lowerClamped: false, upperClamped: false },
    threeDb: { dropDb: 3, symmetric: false, lowerHz: 0, upperHz: 0, bandwidthHz: 0, lowerClamped: false, upperClamped: false },
    peakDb: Math.max(...db),
    peakFreqHz: freqHz[db.indexOf(Math.max(...db))] ?? 0,
    referenceDb: Math.max(...db),
    referenceMode: 'Peak',
    referenceFellBackToPeak: false,
  }
}

describe('crosstalkReadings', () => {
  it('reads the nearest point to 1 kHz in log frequency, the worst point and the band ends', () => {
    const r = crosstalkReadings(trace([20, 200, 800, 1300, 5000, 20000], [-69, -69, -66, -64, -58, -51]))!
    // 800 and 1300 Hz straddle 1 kHz; 800 is nearer in log frequency (0.22 against 0.26), though 1300 is nearer in hertz.
    expect(r.at).toEqual({ freqHz: 800, db: -66 })
    expect(r.worst).toEqual({ freqHz: 20000, db: -51 })
    expect(r.low).toEqual({ freqHz: 20, db: -69 })
    expect(r.high).toEqual({ freqHz: 20000, db: -51 })
  })

  it('honours a different quoted frequency and is null for an empty trace', () => {
    const t = trace([100, 1000, 10000], [-70, -68, -60])
    expect(crosstalkReadings(t, { atHz: 10000 })!.at).toEqual({ freqHz: 10000, db: -60 })
    expect(crosstalkReadings(trace([], []))).toBeNull()
  })

  it('labels and formats', () => {
    expect(crosstalkDirectionLabel('CrosstalkLeftToRight')).toBe('L→R')
    expect(crosstalkDirectionLabel('CrosstalkRightToLeft')).toBe('R→L')
    expect(crosstalkDirectionLabel('Gain')).toBe('Gain')
    expect(formatHz(50)).toBe('50.00 Hz')
    expect(formatHz(1000)).toBe('1.00 kHz')
    expect(formatHz(20000)).toBe('20.0 kHz')
    expect(formatCrosstalkReading({ freqHz: 1000.49, db: -64.23 })).toBe('-64.2 dB at 1.00 kHz')
  })
})
