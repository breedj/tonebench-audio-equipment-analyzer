import { describe, expect, it } from 'vitest'
import type { ScopeChannelSlice, ScopeSliceMeasurements } from '../api/client'
import { scopeChannelReadings, scopeReadings } from './readings'

const always: ScopeSliceMeasurements = {
  maxV: 1,
  minV: -1,
  peakToPeakV: 2,
  meanV: 0.02,
  rmsV: 0.707,
  acRmsV: 0.707,
}

function labels(m: ScopeSliceMeasurements, voltsPerDivision = 0.5): string[] {
  return scopeReadings(m, voltsPerDivision).map((r) => r.label)
}

function valueOf(m: ScopeSliceMeasurements, label: string, voltsPerDivision = 0.5): string | undefined {
  return scopeReadings(m, voltsPerDivision).find((r) => r.label === label)?.value
}

function slice(
  channel: number,
  measurements: ScopeSliceMeasurements,
  gated: ScopeSliceMeasurements | null = null,
): ScopeChannelSlice {
  return { channel, x0: 0, dx: 1e-5, raw: true, samples: [0], min: null, max: null, measurements, gated }
}

describe('scopeReadings', () => {
  it('always has the six a window with samples in it always has', () => {
    expect(labels(always)).toEqual(['Maximum', 'Minimum', 'Peak to peak', 'Mean', 'RMS', 'AC RMS'])
  })

  it('leaves out a reading the waveform does not have rather than dashing it', () => {
    // A record with one settled level has no top, no base and therefore no edge or period measured
    // between them -- the honest consequence, not an omission. A column of dashes beside the six
    // that always read is worse than a short list.
    expect(labels({ ...always, topV: null, periodS: null, riseTimeS: null })).toEqual(labels(always))
  })

  it('takes a figure the server sent, including a zero', () => {
    // Zero overshoot is a measurement -- "it does not overshoot" -- and `if (value)` would drop it.
    expect(valueOf({ ...always, overshootPercent: 0 }, 'Overshoot')).toBe('0.0 %')
  })

  it('writes each quantity in its own unit, and volts in the graticule own', () => {
    const m: ScopeSliceMeasurements = {
      ...always,
      crestFactor: 1.414,
      topV: 1,
      baseV: -1,
      amplitudeV: 2,
      frequencyHz: 1000,
      periodS: 1e-3,
      dutyPercent: 25,
      riseTimeS: 4.1667e-4,
    }
    expect(valueOf(m, 'Crest factor')).toBe('1.41')
    expect(valueOf(m, 'Frequency')).toBe('1.00 kHz')
    // formatScopeTime's own precision, which follows the span it is given -- the same function the
    // axis labels and the timebase readout go through.
    expect(valueOf(m, 'Period')).toBe('1.000 ms')
    expect(valueOf(m, 'Duty')).toBe('25.0 %')
    expect(valueOf(m, 'Rise')).toBe('416.7 µs')
    // The unit follows the division, as every volts figure on this screen does.
    expect(valueOf(m, 'Amplitude', 0.5)).toBe('2.000 V')
    expect(valueOf(m, 'Amplitude', 0.001)).toBe('2000.00 mV')
  })

  it('answers nothing for a slice that carries no measurements', () => {
    expect(scopeReadings(null, 0.5)).toEqual([])
    expect(scopeReadings(undefined, 0.5)).toEqual([])
  })
})

describe('scopeChannelReadings', () => {
  it('reads the channels the display rail is showing, in their own labels', () => {
    const channels = [slice(0, always), slice(1, always)]
    expect(scopeChannelReadings(channels, 0.5, true, true).map((b) => b.label)).toEqual(['Left', 'Right'])
    expect(scopeChannelReadings(channels, 0.5, true, false).map((b) => b.label)).toEqual(['Left'])
    expect(scopeChannelReadings(channels, 0.5, false, false)).toEqual([])
  })

  it('answers nothing before anything is captured', () => {
    expect(scopeChannelReadings(null, 0.5, true, true)).toEqual([])
  })

  it('reads the gated set when asked for it, and the window own otherwise', () => {
    const gated: ScopeSliceMeasurements = { ...always, maxV: 5, minV: -5, peakToPeakV: 10 }
    const channels = [slice(0, always, gated)]
    const window = scopeChannelReadings(channels, 0.5, true, true)
    const gate = scopeChannelReadings(channels, 0.5, true, true, 'gate')
    expect(window[0].readings.find((r) => r.label === 'Peak to peak')?.value).toBe('2.000 V')
    expect(gate[0].readings.find((r) => r.label === 'Peak to peak')?.value).toBe('10.000 V')
  })

  it('leaves out a channel the gate measured nothing of, rather than falling back', () => {
    // The server answers null when the cursor window holds none of this record. The whole
    // window's figures under a "between the cursors" heading is the one answer worse than none.
    expect(scopeChannelReadings([slice(0, always, null)], 0.5, true, true, 'gate')).toEqual([])
  })
})
