import { describe, expect, it } from 'vitest'
import type { ScopeChannelSlice, ScopeRecordResponse, ScopeSliceMeasurements } from '../api/client'
import {
  scopeMeasurementFormatter,
  scopeMeasurementRows,
  scopeMeasurementSources,
  scopeStatsSignature,
} from './measurementRows'

function measurements(partial: Partial<ScopeSliceMeasurements> = {}): ScopeSliceMeasurements {
  return { maxV: 1, minV: -1, peakToPeakV: 2, meanV: 0, rmsV: 0.707, acRmsV: 0.707, ...partial }
}

function slice(channel: number, partial: Partial<ScopeSliceMeasurements> = {}): ScopeChannelSlice {
  return {
    channel,
    x0: -0.005,
    dx: 1e-5,
    raw: false,
    samples: null,
    min: [-1],
    max: [1],
    measurements: measurements(partial),
    gated: null,
    residual: null,
  }
}

function record(extra: Partial<ScopeRecordResponse> = {}): ScopeRecordResponse {
  return {
    sampleRateHz: 192_000,
    frameCount: 3840,
    triggerOffsetS: 0.005,
    fromS: -0.005,
    toS: 0.015,
    spliced: false,
    forced: false,
    channels: [slice(0, { frequencyHz: 1000 }), slice(1)],
    ...extra,
  }
}

const both = { showLeft: true, showRight: true, source: 'window' as const }

describe('scopeMeasurementSources', () => {
  it('is one source per drawn input', () => {
    expect(scopeMeasurementSources(record(), true, true).map((s) => s.label)).toEqual(['Left', 'Right'])
    expect(scopeMeasurementSources(record(), true, false).map((s) => s.label)).toEqual(['Left'])
  })

  it('adds the math trace when one is drawn, in its own unit', () => {
    const withMath = record({
      maths: [{
        slot: 1,
        operation: 'ATimesB',
        sourceA: 0,
        sourceB: 1,
        unit: 'VoltSquared',
        samples: null,
        min: null,
        max: null,
        measurements: measurements(),
        gated: null,
        clampedSamples: 0,
      }],
    })
    const sources = scopeMeasurementSources(withMath, true, true)
    expect(sources).toHaveLength(3)
    expect(sources[2].unit).toBe('VoltSquared')
    // Named by the sockets it reads, which is how the display rail names the same trace \u2014 the
    // picker offering "A \u00d7 B" beside a button reading "L \u00d7 R" is one trace described two ways.
    expect(sources[2].label).toBe('L \u00d7 R (V\u00b2)')
  })

  it('is nothing at all before anything has been captured', () => {
    expect(scopeMeasurementSources(null, true, true)).toEqual([])
  })
})

describe('scopeMeasurementRows', () => {
  it('is measurement-major and source-minor, so the two channels sit side by side', () => {
    // Comparing the two channels' figure for one reading is the commonest thing there is on a
    // stereo capture, and that comparison across a table's length is no comparison at all.
    const rows = scopeMeasurementRows(record(), ['pkpk', 'rms'], both)
    expect(rows.map((r) => `${r.sourceLabel} ${r.label}`)).toEqual([
      'Left Pk-Pk',
      'Right Pk-Pk',
      'Left RMS',
      'Right RMS',
    ])
  })

  it('follows the display rail, so a channel taken off the chart takes its rows with it', () => {
    const rows = scopeMeasurementRows(record(), ['pkpk'], { ...both, showRight: false })
    expect(rows.map((r) => r.sourceLabel)).toEqual(['Left'])
  })

  it('dashes a reading the waveform has not got rather than leaving the cell blank', () => {
    const rows = scopeMeasurementRows(record(), ['freq'], both)
    expect(rows[0].text).toBe('1.000 kHz')
    expect(rows[1].value).toBeNull()
    expect(rows[1].text).toBe('—')
  })

  it('drops an id this build cannot read rather than drawing an empty row', () => {
    expect(scopeMeasurementRows(record(), ['fromTheFuture'], both)).toEqual([])
  })

  it('reads the gated set when the cursors are gating, never falling back to the window', () => {
    const gated = record({
      channels: [
        { ...slice(0), gated: measurements({ peakToPeakV: 0.5 }) },
        { ...slice(1), gated: null },
      ],
    })
    const rows = scopeMeasurementRows(gated, ['pkpk'], { ...both, source: 'gate' })
    expect(rows[0].text).toBe('500.0 mV')
    // The server answers null when the cursor window holds none of this record, and the whole
    // window's figure under a "between the cursors" heading is the one answer worse than none.
    expect(rows[1].value).toBeNull()
  })

  it('gives the Channel delay group one row off the X–Y figure', () => {
    const withXy = record({
      xy: {
        x: [],
        y: [],
        frameCount: 100,
        windowFrameCount: 100,
        spanS: 0.01,
        correlation: 0.98,
        phaseDeg: 90,
        amplitudeRatio: 0.5,
      },
    })
    const rows = scopeMeasurementRows(withXy, ['phase', 'skew', 'correlation'], both)
    expect(rows).toHaveLength(3)
    expect(rows[0].sourceLabel).toBe('L → R')
    expect(rows[0].text).toBe('90.00°')
    // A quarter of a cycle of 1 kHz is 250 µs — the phase written as a time rather than a second
    // measurement, which is what makes it exactly as defined as the phase is.
    expect(rows[1].value).toBeCloseTo(0.00025, 12)
    expect(rows[2].text).toBe('0.9800')
  })

  it('dashes the pair readings when no figure was fetched', () => {
    const rows = scopeMeasurementRows(record(), ['phase'], both)
    expect(rows[0].value).toBeNull()
    expect(rows[0].text).toBe('—')
  })

  it('keeps an area off the math trace, whose integral has no name anyone quotes', () => {
    const withMath = record({
      maths: [{
        slot: 1,
        operation: 'ATimesB',
        sourceA: 0,
        sourceB: 1,
        unit: 'VoltSquared',
        samples: null,
        min: null,
        max: null,
        measurements: measurements({ areaVs: 1 }),
        gated: null,
        clampedSamples: 0,
      }],
    })
    const rows = scopeMeasurementRows(withMath, ['area'], both)
    expect(rows.map((r) => r.sourceLabel)).toEqual(['Left', 'Right'])
  })

  /**
   * The same rule as the area beside it and through the same predicate: a slew rate is a level
   * over a time, so on a derivative it would be V/s². The picker and this builder read one
   * function, because the two disagreeing is a reading a user can tick and never see.
   */
  it('keeps a slew rate off the math trace, whose slope has no name anyone quotes', () => {
    const withMath = record({
      maths: [{
        slot: 1,
        operation: 'DerivativeA',
        sourceA: 0,
        sourceB: 1,
        unit: 'VoltPerSecond',
        samples: null,
        min: null,
        max: null,
        measurements: measurements({ topV: 1, baseV: -1, riseTimeS: 4e-6 }),
        gated: null,
        clampedSamples: 0,
      }],
    })
    expect(scopeMeasurementRows(withMath, ['riseSlew'], both).map((r) => r.sourceLabel)).toEqual([
      'Left',
      'Right',
    ])
    expect(scopeMeasurementRows(withMath, ['riseSlew:math1'], both)).toEqual([])
  })

  it('keys a row by its measurement and its source, so statistics follow the right one', () => {
    const rows = scopeMeasurementRows(record(), ['pkpk'], both)
    expect(rows.map((r) => r.key)).toEqual(['pkpk:ch0', 'pkpk:ch1'])
  })

  it('is nothing at all before anything has been captured', () => {
    expect(scopeMeasurementRows(null, ['pkpk'], both)).toEqual([])
  })
})

describe('scopeMeasurementFormatter', () => {
  it('writes a level at its own magnitude, not at the axis’s', () => {
    // THE rule this file exists for: a standard deviation is routinely three orders below the
    // value it belongs to, and on the value's ladder it reads 0.001 V or simply 0.000.
    const volts = scopeMeasurementFormatter('level', 'Volt')
    // Four significant figures, which is what every reading on this screen is written to — and the
    // magnitude is the figure's own, so the same formatter writes a value in millivolts and its
    // standard deviation in microvolts.
    expect(volts(0.306_864)).toBe('306.9 mV')
    expect(volts(0.000_757)).toBe('757.0 µV')
  })

  it('writes a derived trace in the operation’s own unit', () => {
    expect(scopeMeasurementFormatter('level', 'VoltSquared')(0.5)).toBe('500.0 mV²')
    expect(scopeMeasurementFormatter('level', 'Ratio')(0.5)).toBe('0.500')
  })

  /**
   * The rule <code>formatMathValue</code> exists for, on the reading it matters most to: a slew
   * rate scales its <b>denominator</b>. 2.4 MV/s and 2.400 V/µs are the same figure and the second
   * is the one every amplifier datasheet quotes.
   */
  it('writes a slew rate by scaling its denominator, never its numerator', () => {
    const rate = scopeMeasurementFormatter('rate', 'Volt')
    expect(rate(2.4e6)).toContain('V/µs')
    expect(rate(2.4e6)).not.toContain('MV')
    // And a slow one stays in the unit it is in rather than reading 0.000 V/us.
    expect(rate(500)).toContain('V/s')
  })

  it('writes an angle in degrees and a ratio bare', () => {
    expect(scopeMeasurementFormatter('angle', 'Volt')(-90)).toBe('-90.00°')
    expect(scopeMeasurementFormatter('ratio', 'Volt')(0.98)).toBe('0.9800')
  })

  it('keeps a statistic that the axis formatters would have rounded to nothing', () => {
    // formatHz bottoms out at hundredths of a hertz and the time axis at microseconds, because
    // both write in the unit the graticule is in. A spread of 40 mHz or a jitter of 800 ps is
    // exactly what a statistics column is read for, so the readout uses its own SI ladder.
    expect(scopeMeasurementFormatter('frequency', 'Volt')(0.04)).toBe('40.00 mHz')
    expect(scopeMeasurementFormatter('time', 'Volt')(8e-10)).toBe('800.0 ps')
    // And it still agrees with the screen above 1 Hz and 1 µs.
    expect(scopeMeasurementFormatter('frequency', 'Volt')(1000)).toBe('1.000 kHz')
    expect(scopeMeasurementFormatter('time', 'Volt')(0.00025)).toBe('250.0 µs')
  })

  it('writes a percentage finely enough for a statistic of one', () => {
    expect(scopeMeasurementFormatter('percent', 'Volt')(49.97)).toBe('49.97 %')
    expect(scopeMeasurementFormatter('percent', 'Volt')(0.0123)).toBe('0.0123 %')
  })
})

describe('scopeStatsSignature', () => {
  const base = 'capture'
  const win = { fromS: -0.005, toS: 0.015 }
  const sig = (
    captureSignature = base,
    rec = record(),
    reading: 'window' | 'gate' = 'window',
    w: { fromS: number; toS: number } | null = win,
  ) => scopeStatsSignature(captureSignature, rec, reading, w)

  it('is stable while nothing about the reading changes', () => {
    expect(sig()).toBe(sig())
  })

  it('ignores the record’s own span, which moves a picosecond every capture', () => {
    // THE regression, and it made the feature look as though it had never been wired up: fromS is
    // measured from the INTERPOLATED trigger position, so it differs in its thirteenth decimal from
    // one capture to the next. Reading it here cleared the statistics on every record and left the
    // count sitting at 1 for ever.
    expect(sig(base, record({ fromS: -0.004998697102069855, toS: 0.014996094564596812 }))).toBe(
      sig(base, record({ fromS: -0.004998698412595938, toS: 0.014996093254070728 })),
    )
  })

  // Each of these makes the records already counted a mean of something else.
  it.each([
    ['the capture configuration', () => sig('other')],
    ['the window that was asked for', () => sig(base, record(), 'window', { fromS: -0.005, toS: 0.02 })],
    ['a zoom going away', () => sig(base, record(), 'window', null)],
    ['the sample rate', () => sig(base, record({ sampleRateHz: 48_000 }))],
    ['the cursors gating', () => sig(base, record(), 'gate')],
  ])('changes when %s does', (_what, build) => {
    expect(build()).not.toBe(sig())
  })

  it('changes when the math channel does, which the capture signature cannot see', () => {
    const withMath = (operation: string) =>
      record({
        maths: [{
          slot: 1,
          operation,
          sourceA: 0,
          sourceB: 1,
          unit: 'Volt',
          samples: null,
          min: null,
          max: null,
          measurements: measurements(),
          gated: null,
          clampedSamples: 0,
        }],
      })
    expect(sig(base, withMath('AMinusB'))).not.toBe(sig(base, withMath('BMinusA')))
  })
})

describe('choosing a source per measurement', () => {
  /**
   * The rule that makes this change free of a migration, and the case that matters most in it.
   * Every selection stored before the picker had a Source control is a bare id, and a bare id
   * meant "shown for every input the chart is drawing" — so it still has to.
   */
  it('shows a bare id on every drawn source, as it always did', () => {
    const rows = scopeMeasurementRows(record(), ['pkpk'], both)

    expect(rows.map((r) => r.key)).toEqual(['pkpk:ch0', 'pkpk:ch1'])
  })

  it('shows an entry that names a source on that source alone', () => {
    const rows = scopeMeasurementRows(record(), ['pkpk:ch1'], both)

    expect(rows.map((r) => r.key)).toEqual(['pkpk:ch1'])
    expect(rows[0].sourceLabel).toBe('Right')
  })

  it('puts one measurement on several traces when it is asked for on each', () => {
    const rows = scopeMeasurementRows(record(), ['pkpk:ch0', 'rms:ch1'], both)

    expect(rows.map((r) => r.key)).toEqual(['pkpk:ch0', 'rms:ch1'])
  })

  /**
   * A selection outlives any one capture, so asking for a reading on a trace this run is not
   * reading is an ordinary thing to have asked for — it is simply no row until it is.
   */
  it('is no row rather than an empty one where the source is not being read', () => {
    const rows = scopeMeasurementRows(record(), ['pkpk:ch1'], {
      showLeft: true,
      showRight: false,
      source: 'window',
    })

    expect(rows).toEqual([])
  })

  it('still keeps area off the math trace, even asked for by name', () => {
    const withMath = record({
      math: {
        operation: 'AMinusB',
        unit: 'Volt',
        x0: -0.005,
        dx: 1e-5,
        raw: false,
        samples: null,
        min: [-1],
        max: [1],
        clampedSamples: 0,
        measurements: measurements(),
        gated: null,
      },
    } as Partial<ScopeRecordResponse>)

    expect(scopeMeasurementRows(withMath, ['area:math'], both)).toEqual([])
    // And the same measurement on a channel is unaffected.
    expect(scopeMeasurementRows(withMath, ['area:ch0'], both).map((r) => r.key)).toEqual(['area:ch0'])
  })

  /** A pair reading has no source to name, so an entry for it stays whole. */
  it('ignores a source on a Channel delay entry', () => {
    const rows = scopeMeasurementRows(record(), ['phase'], both)

    expect(rows.map((r) => r.key)).toEqual(['phase:pair'])
  })
})
