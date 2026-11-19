import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import {
  capturedScopeChannels,
  DEFAULT_SCOPE_DISPLAY,
  scopeChannelDrawn,
  scopeDisplayFromParams,
  scopeDisplayToParams,
} from './display'

describe('DEFAULT_SCOPE_DISPLAY', () => {
  it('opens on the axis the first capture will land on', () => {
    // 20 ms at 25 % pre-trigger, which is what DefaultOscilloscopeParametersJson seeds the
    // measurement itself with.
    expect(DEFAULT_SCOPE_DISPLAY.timeFromS).toBeCloseTo(-0.005, 12)
    expect(DEFAULT_SCOPE_DISPLAY.timeToS).toBeCloseTo(0.015, 12)
  })

  /**
    * Thick lines and statistics both changed default on 2026-11-19, so both are pinned: a default
    * is what a NEW measurement is seeded with and what a stored null reads as, and the half that is
    * easy to break is the second one — see 'keeps a deliberate false rather than reading it as
    * absent' below.
    */
  it('draws a thick trace, because a hairline is what a reader is trying to follow', () => {
    expect(DEFAULT_SCOPE_DISPLAY.thickLines).toBe(true)
  })

  it('keeps statistics beside each reading', () => {
    expect(DEFAULT_SCOPE_DISPLAY.measurementStatistics).toBe(true)
  })

  /**
   * And nothing fits a finished capture on its own any more. The control and the behaviour were
   * both removed; a stored chartAutofitOnDone is left in params_json where nothing reads it, which
   * is what this pins — reading it back must not resurrect a field.
   */
  it('has no autofit-when-done, and does not read a stored one', () => {
    const display = scopeDisplayFromParams(JSON.stringify({ chartAutofitOnDone: true }))
    expect('autofitOnDone' in display).toBe(false)
    expect(scopeDisplayToParams(display)).not.toHaveProperty('chartAutofitOnDone')
  })

  it('draws the trigger level, which is the control a user drags', () => {
    expect(DEFAULT_SCOPE_DISPLAY.showTriggerLevel).toBe(true)
  })
})

describe('scopeDisplayFromParams', () => {
  it('reads the app default when nothing is stored', () => {
    expect(scopeDisplayFromParams(null)).toEqual(DEFAULT_SCOPE_DISPLAY)
    expect(scopeDisplayFromParams('')).toEqual(DEFAULT_SCOPE_DISPLAY)
  })

  it('reads the app default from a document that will not parse', () => {
    expect(scopeDisplayFromParams('{ not json')).toEqual(DEFAULT_SCOPE_DISPLAY)
  })

  it('restores every display field it finds', () => {
    const display = scopeDisplayFromParams(
      JSON.stringify({
        chartVoltsPerDivision: 0.05,
        chartVoltOffsetV: -1.5,
        chartTimeFromS: -0.001,
        chartTimeToS: 0.003,
        chartThickLines: true,
        chartShowDataPoints: true,
        chartShowTriggerLevel: false,
        chartAutofitOnDone: false,
      }),
    )

    expect(display.voltsPerDivision).toBe(0.05)
    expect(display.voltOffsetV).toBe(-1.5)
    expect(display.timeFromS).toBe(-0.001)
    expect(display.timeToS).toBe(0.003)
    expect(display.thickLines).toBe(true)
    expect(display.showDataPoints).toBe(true)
    expect(display.showTriggerLevel).toBe(false)
  })

  it('keeps a deliberate false rather than reading it as absent', () => {
    // The half a changed default can break: `?? base` on a boolean reads `false` as "not stored".
    // Both of these now default to TRUE, so a stored false is exactly the case at risk -- a bench's
    // own measurements must not have their settings rewritten by a default moving under them.
    const display = scopeDisplayFromParams(
      JSON.stringify({ chartThickLines: false, chartMeasurementStatistics: false }),
    )
    expect(display.thickLines).toBe(false)
    expect(display.measurementStatistics).toBe(false)
  })

  it('takes the time window as a pair or not at all', () => {
    // A stored "from" with no "to" would be read against this build's default "to", which is a
    // window the measurement never had.
    const display = scopeDisplayFromParams(JSON.stringify({ chartTimeFromS: -0.001 }))
    expect(display.timeFromS).toBe(DEFAULT_SCOPE_DISPLAY.timeFromS)
    expect(display.timeToS).toBe(DEFAULT_SCOPE_DISPLAY.timeToS)
  })

  it('refuses a reversed stored window', () => {
    const display = scopeDisplayFromParams(JSON.stringify({ chartTimeFromS: 0.01, chartTimeToS: -0.01 }))
    expect(display.timeFromS).toBe(DEFAULT_SCOPE_DISPLAY.timeFromS)
  })

  it('refuses a V/div that is not a magnification', () => {
    expect(scopeDisplayFromParams(JSON.stringify({ chartVoltsPerDivision: 0 })).voltsPerDivision).toBe(
      DEFAULT_SCOPE_DISPLAY.voltsPerDivision,
    )
    expect(scopeDisplayFromParams(JSON.stringify({ chartVoltsPerDivision: -1 })).voltsPerDivision).toBe(
      DEFAULT_SCOPE_DISPLAY.voltsPerDivision,
    )
  })

  it('falls back to the record the measurement takes when no window was stored', () => {
    // A measurement saved before the window existed, or one whose window was never touched: the
    // axis its own capture lands on is a better answer than this build's default 20 ms.
    const display = scopeDisplayFromParams(JSON.stringify({ recordSeconds: 0.001, preTriggerFraction: 0.5 }))
    expect(display.timeFromS).toBeCloseTo(-0.0005, 12)
    expect(display.timeToS).toBeCloseTo(0.0005, 12)
  })

  it('prefers a stored window over the record it was taken from', () => {
    const display = scopeDisplayFromParams(
      JSON.stringify({ recordSeconds: 1, preTriggerFraction: 0.5, chartTimeFromS: -0.002, chartTimeToS: 0.002 }),
    )
    expect(display.timeFromS).toBe(-0.002)
  })

  it('reads the base it is given rather than the frozen default', () => {
    const base = { ...DEFAULT_SCOPE_DISPLAY, thickLines: true }
    expect(scopeDisplayFromParams(null, base).thickLines).toBe(true)
  })

  it('reads a stored annotation in milliseconds and volts', () => {
    const display = scopeDisplayFromParams(
      JSON.stringify({ chartAnnotations: [{ id: 1, style: 'Point', x: 1.5, y: 0.25, label: 'edge' }] }),
    )
    expect(display.annotations).toHaveLength(1)
    expect(display.annotations[0].xQuantity).toBe('ms')
    expect(display.annotations[0].yQuantity).toBe('volt')
  })

  it('never hands back the frozen default array itself', () => {
    // Mutating it would change what every later measurement opens with.
    const display = scopeDisplayFromParams(null)
    expect(display.annotations).not.toBe(DEFAULT_SCOPE_DISPLAY.annotations)
  })
})

describe('scopeDisplayToParams', () => {
  it('round-trips through fromParams', () => {
    const display = {
      ...DEFAULT_SCOPE_DISPLAY,
      voltsPerDivision: 0.02,
      voltOffsetV: 0.5,
      timeFromS: -0.0001,
      timeToS: 0.0009,
      thickLines: true,
      showDataPoints: true,
      showTriggerLevel: false,
    }

    const back = scopeDisplayFromParams(JSON.stringify(scopeDisplayToParams(display)))
    expect(back).toEqual({ ...display, annotations: [] })
  })
})


describe('the residual', () => {
  it('is off until a measurement says otherwise', () => {
    // It costs a least-squares sine fit per channel per fetch, so it is asked for rather than
    // always sent -- and a measurement saved before it existed states nothing and reads as off.
    expect(DEFAULT_SCOPE_DISPLAY.showResidual).toBe(false)
    expect(scopeDisplayFromParams(JSON.stringify({ chartVoltsPerDivision: 0.2 })).showResidual).toBe(false)
  })

  it('round-trips through the params it is written to', () => {
    const params = scopeDisplayToParams({ ...DEFAULT_SCOPE_DISPLAY, showResidual: true })
    expect(params.chartShowResidual).toBe(true)
    expect(scopeDisplayFromParams(JSON.stringify(params)).showResidual).toBe(true)
  })
})

describe('the per-channel display pair', () => {
  it('draws both inputs until a measurement says otherwise', () => {
    expect(DEFAULT_SCOPE_DISPLAY.showLeft).toBe(true)
    expect(DEFAULT_SCOPE_DISPLAY.showRight).toBe(true)
    // A measurement saved before the pair existed states neither, and reads as both -- the other
    // half of "a changed default seeds new measurements and re-reads a stored null".
    const stored = scopeDisplayFromParams(JSON.stringify({ chartVoltsPerDivision: 0.2 }))
    expect(stored.showLeft).toBe(true)
    expect(stored.showRight).toBe(true)
  })

  it('keeps a channel that was deliberately taken off the screen', () => {
    const stored = scopeDisplayFromParams(JSON.stringify({ chartShowLeft: true, chartShowRight: false }))
    expect(stored.showLeft).toBe(true)
    expect(stored.showRight).toBe(false)
  })

  it('round-trips through the params it is written to', () => {
    const display = { ...DEFAULT_SCOPE_DISPLAY, showLeft: false, showRight: true }
    const params = scopeDisplayToParams(display)
    expect(params.chartShowLeft).toBe(false)
    expect(params.chartShowRight).toBe(true)
    expect(scopeDisplayFromParams(JSON.stringify(params)).showLeft).toBe(false)
  })
})


describe('capturedScopeChannels', () => {
  it('reads the channels off the record, which is the capture setting as the chart sees it', () => {
    expect(capturedScopeChannels([{ channel: 0 }])).toEqual({ left: true, right: false })
    expect(capturedScopeChannels([{ channel: 1 }])).toEqual({ left: false, right: true })
    expect(capturedScopeChannels([{ channel: 0 }, { channel: 1 }])).toEqual({ left: true, right: true })
  })

  it('offers both before anything has been captured', () => {
    // There is no record to ask, and a reference drawn on an empty chart has to be able to show
    // everything it holds.
    expect(capturedScopeChannels(null)).toEqual({ left: true, right: true })
    expect(capturedScopeChannels(undefined)).toEqual({ left: true, right: true })
    expect(capturedScopeChannels([])).toEqual({ left: true, right: true })
  })
})

describe('scopeChannelDrawn', () => {
  const shownBoth = { left: true, right: true }
  const leftOnly = { left: true, right: false }

  it('refuses a channel the measurement is not capturing, however the rail is set', () => {
    // THE regression, reported 2026-11-18: capturing the left input alone, a checked history item
    // whose stored capture has both channels drew its right one as well -- because the rail's
    // Right box stays ticked while a left-only measurement runs. It is disabled rather than
    // cleared, so it was not a control the reader could turn off.
    expect(scopeChannelDrawn(1, shownBoth, leftOnly)).toBe(false)
    expect(scopeChannelDrawn(0, shownBoth, leftOnly)).toBe(true)
  })

  it('still answers to the rail for a channel that is captured', () => {
    expect(scopeChannelDrawn(0, { left: false, right: true }, shownBoth)).toBe(false)
    expect(scopeChannelDrawn(1, { left: true, right: false }, shownBoth)).toBe(false)
    expect(scopeChannelDrawn(1, shownBoth, shownBoth)).toBe(true)
  })

  it('leaves the rail in charge before the first capture', () => {
    // Nothing captured yet means both are offered, so a reference is the whole of what is on the
    // chart and draws what it has -- the geometry it decides included.
    const captured = capturedScopeChannels([])
    expect(scopeChannelDrawn(1, shownBoth, captured)).toBe(true)
    expect(scopeChannelDrawn(1, leftOnly, captured)).toBe(false)
  })

  it('reads anything that is not channel 1 as the left input', () => {
    // The chart's own convention, shared with CHANNEL_COLORS and the slice labels.
    expect(scopeChannelDrawn(0, leftOnly, leftOnly)).toBe(true)
    expect(scopeChannelDrawn(7, leftOnly, leftOnly)).toBe(true)
  })
})


describe('the chart asks for the gate', () => {
  // A rule nothing calls is the fault this change exists to fix, one layer up: the gate above was
  // always right about the live traces and the reference loop simply did not ask it. So this reads
  // the component as text, the way numberField.test.ts audits its own class of fault -- the chart
  // is a .vue against a real DOM and the suite runs on environment: 'node'.
  const chart = readFileSync(
    resolve(import.meta.dirname, '../components/OscilloscopeScreenChart.vue'),
    'utf8',
  )

  it('gates both the live traces and the references through scopeChannelDrawn', () => {
    expect(chart).toContain("from '../scope/display'")
    expect(chart).toContain('scopeChannelDrawn')
    // Twice: the live filter and the reference loop. One call means one of them went back to
    // spelling the rule out for itself.
    expect(chart.match(/scopeChannelDrawn\(/g)?.length).toBe(2)
  })

  it('no longer decides a channel from the display rail alone', () => {
    expect(chart).not.toContain('? props.showRight : props.showLeft')
    expect(chart).not.toContain('? !props.showRight : !props.showLeft')
  })

  it('takes what is captured from the record rather than re-deriving it', () => {
    expect(chart).toContain('capturedScopeChannels(record?.channels)')
  })
})

describe('the chosen measurements', () => {
  it('comes back as it was stored, in the catalogue order', () => {
    const stored = JSON.stringify({ chartMeasurements: ['freq', 'pkpk'], chartMeasurementStatistics: true })
    const display = scopeDisplayFromParams(stored)
    expect(display.measurements).toEqual(['pkpk', 'freq'])
    expect(display.measurementStatistics).toBe(true)
  })

  it('opens on this build default when the measurement was saved before the readout existed', () => {
    // Absent is not an empty table: a screen that shows nothing under its chart reads as a feature
    // that failed rather than as one nobody has set up.
    const display = scopeDisplayFromParams(JSON.stringify({ chartVoltsPerDivision: 0.5 }))
    expect(display.measurements).toEqual([...DEFAULT_SCOPE_DISPLAY.measurements])
    expect(display.measurementStatistics).toBe(DEFAULT_SCOPE_DISPLAY.measurementStatistics)
  })

  /**
   * The format the Source control introduced, and the half of it that matters: a bare id is what
   * every selection stored before that control is, and it still means every source.
   */
  it('round-trips a selection that names its sources', () => {
    const stored = JSON.stringify({ chartMeasurements: ['pkpk:ch0', 'rms:ch1'] })
    const display = scopeDisplayFromParams(stored)

    expect(display.measurements).toEqual(['pkpk:ch0', 'rms:ch1'])
    expect(scopeDisplayToParams(display).chartMeasurements).toEqual(['pkpk:ch0', 'rms:ch1'])
  })

  it('keeps a bare id bare, so nothing saved before the Source control changes meaning', () => {
    const display = scopeDisplayFromParams(JSON.stringify({ chartMeasurements: ['pkpk', 'rms'] }))

    expect(display.measurements).toEqual(['pkpk', 'rms'])
  })

  it('keeps an empty list empty, which is a reader who cleared it', () => {
    expect(scopeDisplayFromParams(JSON.stringify({ chartMeasurements: [] })).measurements).toEqual([])
  })

  it('drops an id this build cannot read rather than carrying a row that is always blank', () => {
    const stored = JSON.stringify({ chartMeasurements: ['pkpk', 'fromTheFuture'] })
    expect(scopeDisplayFromParams(stored).measurements).toEqual(['pkpk'])
  })

  it('round-trips through the save path', () => {
    const display = { ...DEFAULT_SCOPE_DISPLAY, measurements: ['rms', 'phase'], measurementStatistics: true }
    const back = scopeDisplayFromParams(JSON.stringify(scopeDisplayToParams(display)))
    expect(back.measurements).toEqual(['rms', 'phase'])
    expect(back.measurementStatistics).toBe(true)
  })

  it('copies the list rather than handing the default out to be mutated', () => {
    // The frozen-default trap: a caller that pushed onto this would change what every later
    // measurement opens with.
    const a = scopeDisplayFromParams(null)
    a.measurements.push('max')
    expect(scopeDisplayFromParams(null).measurements).toEqual([...DEFAULT_SCOPE_DISPLAY.measurements])
  })
})

describe('the plot under the table', () => {
  it('comes back as it was stored', () => {
    const stored = JSON.stringify({ chartTrendRow: 'pkpk:ch1', chartTrendView: 'Distribution' })
    const display = scopeDisplayFromParams(stored)

    expect(display.trendRow).toBe('pkpk:ch1')
    expect(display.trendView).toBe('Distribution')
  })

  it('opens on the trend of whatever is first when the measurement was saved before the plot existed', () => {
    const display = scopeDisplayFromParams(JSON.stringify({ chartVoltsPerDivision: 0.5 }))

    expect(display.trendRow).toBe('')
    expect(display.trendView).toBe('Trend')
  })

  /**
   * A row key names a catalogue id and a source, and which sources exist depends on what the
   * capture carries rather than on any table — so this one is deliberately not validated here, and
   * the panel's fallback to its first row is what makes an unknown key harmless.
   */
  it('keeps a row key naming something this build has never heard of', () => {
    const display = scopeDisplayFromParams(JSON.stringify({ chartTrendRow: 'fromTheFuture:ch9' }))

    expect(display.trendRow).toBe('fromTheFuture:ch9')
  })

  /** Unlike the key, the view is one of two and a third would reach the painter as neither. */
  it('refuses a view it cannot draw', () => {
    const display = scopeDisplayFromParams(JSON.stringify({ chartTrendView: 'Waterfall' }))

    expect(display.trendView).toBe(DEFAULT_SCOPE_DISPLAY.trendView)
  })

  it('round-trips through the save path', () => {
    const display = { ...DEFAULT_SCOPE_DISPLAY, trendRow: 'freq:ch0', trendView: 'Distribution' as const }
    const back = scopeDisplayFromParams(JSON.stringify(scopeDisplayToParams(display)))

    expect(back.trendRow).toBe('freq:ch0')
    expect(back.trendView).toBe('Distribution')
  })
})

describe('the pass/fail mask', () => {
  it('comes back as it was stored', () => {
    const stored = JSON.stringify({
      chartMaskOn: true,
      chartMaskToleranceV: 0.02,
      chartMaskToleranceS: 5e-6,
    })
    const display = scopeDisplayFromParams(stored)

    expect(display.maskOn).toBe(true)
    expect(display.maskToleranceV).toBe(0.02)
    expect(display.maskToleranceS).toBe(5e-6)
  })

  it('opens off, at 50 mV and nothing in time, when the measurement was saved before it existed', () => {
    const display = scopeDisplayFromParams(JSON.stringify({ chartVoltsPerDivision: 0.5 }))

    expect(display.maskOn).toBe(false)
    expect(display.maskToleranceV).toBe(0.05)
    expect(display.maskToleranceS).toBe(0)
  })

  /** A negative tolerance is not a narrower band, it is no band — and a box must not be able to show one. */
  it('refuses a negative tolerance', () => {
    const stored = JSON.stringify({ chartMaskToleranceV: -1, chartMaskToleranceS: -1 })
    const display = scopeDisplayFromParams(stored)

    expect(display.maskToleranceV).toBe(DEFAULT_SCOPE_DISPLAY.maskToleranceV)
    expect(display.maskToleranceS).toBe(DEFAULT_SCOPE_DISPLAY.maskToleranceS)
  })

  /**
   * No result id is stored, which is the point: nothing can go stale against a deleted result or
   * name a different run after an import.
   */
  it('stores no reference of its own', () => {
    const params = scopeDisplayToParams({ ...DEFAULT_SCOPE_DISPLAY, maskOn: true })

    expect(JSON.stringify(params)).not.toMatch(/referenceid|resultid/i)
  })

  it('round-trips through the save path', () => {
    const display = {
      ...DEFAULT_SCOPE_DISPLAY,
      maskOn: true,
      maskToleranceV: 0.1,
      maskToleranceS: 1e-5,
    }
    const back = scopeDisplayFromParams(JSON.stringify(scopeDisplayToParams(display)))

    expect(back.maskOn).toBe(true)
    expect(back.maskToleranceV).toBe(0.1)
    expect(back.maskToleranceS).toBe(1e-5)
  })
})
