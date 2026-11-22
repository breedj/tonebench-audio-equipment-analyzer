import { describe, expect, it } from 'vitest'
import {
  AUTOSCALE_MAX_CYCLES,
  AUTOSCALE_MIN_CYCLES,
  MIN_FILL_FRACTION,
  recordNeedsFit,
  timebaseAction,
  voltWindowNeedsFit,
} from './autoscale'
import { MAX_FILL_FRACTION, VERTICAL_DIVISIONS, fitVoltWindow } from './voltAxis'
import { AUTOSET_PERIODS, timebaseForCycles } from './autoset'
import { SCOPE_TIME_PER_DIVISION_LADDER, recordSecondsFor, timePerDivisionFor } from './timeAxis'
import { stepLadder } from '../panels/stepperGrid'

describe('the vertical autoscale', () => {
  it('leaves a trace the fit has just framed alone', () => {
    // The property the whole dead band is for: applying the fit must not produce a picture the
    // band then asks to fit again, or the knob moves on every record for ever.
    for (const span of [0.0003, 0.0031, 0.03, 0.31, 1.0, 3.1, 7.4, 19]) {
      const fit = fitVoltWindow(span / 2, -span / 2)
      expect(voltWindowNeedsFit(fit, span / 2, -span / 2), `span ${span}`).toBe(false)
    }
  })

  it('asks for a fit when the trace has grown past the margin the fit itself leaves', () => {
    const fit = fitVoltWindow(0.15, -0.15)
    // Inside the window but into the margin: ink that close to an edge is what the fraction is
    // there to keep clear, since the next capture's overshoot would be clipped with nothing saying so.
    const reach = fit.voltsPerDivision * (VERTICAL_DIVISIONS / 2) * MAX_FILL_FRACTION
    expect(voltWindowNeedsFit(fit, reach * 1.02, -reach * 1.02)).toBe(true)
    expect(voltWindowNeedsFit(fit, reach * 0.98, -reach * 0.98)).toBe(false)
  })

  it('asks for a fit when the trace has drifted off centre, even at the same size', () => {
    const fit = fitVoltWindow(0.15, -0.15)
    const span = 0.3
    // The same 300 mV, carried up on a DC offset until it leans on the top edge.
    const drift = fit.voltsPerDivision * (VERTICAL_DIVISIONS / 2)
    expect(voltWindowNeedsFit(fit, drift + span / 2, drift - span / 2)).toBe(true)
  })

  it('asks for a fit when the trace has shrunk below the band', () => {
    const fit = fitVoltWindow(0.15, -0.15)
    const window = fit.voltsPerDivision * VERTICAL_DIVISIONS
    const tooSmall = window * MIN_FILL_FRACTION * 0.9
    expect(voltWindowNeedsFit(fit, tooSmall / 2, -tooSmall / 2)).toBe(true)
    const stillFine = window * MIN_FILL_FRACTION * 1.1
    expect(voltWindowNeedsFit(fit, stillFine / 2, -stillFine / 2)).toBe(false)
  })

  it('leaves the knob alone on a record that measured nothing', () => {
    const fit = fitVoltWindow(0.15, -0.15)
    expect(voltWindowNeedsFit(fit, Number.NaN, Number.NaN)).toBe(false)
  })

  it('answers the reported case: 300 mV peak to peak belongs on the 50 mV rung', () => {
    // Reported 2026-11-22 — Autoset took a 300 mVpp signal to 100 mV/div where 50 fits perfectly
    // well. A real capture measures a little more than its nominal span, which is what took it
    // over the old three-quarters rule.
    expect(fitVoltWindow(0.155, -0.155).voltsPerDivision).toBeCloseTo(0.05, 10)
    expect(0.31 / (0.05 * VERTICAL_DIVISIONS)).toBeLessThanOrEqual(MAX_FILL_FRACTION)
  })
})

describe('the horizontal autoscale', () => {
  it('leaves a record the fit has just chosen alone', () => {
    // As above, over frequencies spread across the ladder rather than at its rungs.
    for (const hz of [21, 57, 140, 333, 1000, 2470, 6100, 15000, 44100]) {
      const period = 1 / hz
      const perDivision = timebaseForCycles(period)
      expect(perDivision, `${hz} Hz`).not.toBeNull()
      const cycles = recordSecondsFor(perDivision as number) / period
      expect(cycles, `${hz} Hz lands on ${cycles} cycles`).toBeGreaterThan(AUTOSCALE_MIN_CYCLES)
      expect(cycles, `${hz} Hz lands on ${cycles} cycles`).toBeLessThan(AUTOSCALE_MAX_CYCLES)
      expect(recordNeedsFit(recordSecondsFor(perDivision as number), period), `${hz} Hz`).toBe(false)
    }
  })

  it('asks for a fit at either end of the band and not inside it', () => {
    const period = 0.001
    expect(recordNeedsFit(period * (AUTOSCALE_MIN_CYCLES - 0.1), period)).toBe(true)
    expect(recordNeedsFit(period * (AUTOSCALE_MIN_CYCLES + 0.1), period)).toBe(false)
    expect(recordNeedsFit(period * AUTOSET_PERIODS, period)).toBe(false)
    expect(recordNeedsFit(period * (AUTOSCALE_MAX_CYCLES - 0.1), period)).toBe(false)
    expect(recordNeedsFit(period * (AUTOSCALE_MAX_CYCLES + 0.1), period)).toBe(true)
  })

  it('leaves the capture alone where no frequency was measured', () => {
    expect(recordNeedsFit(0.02, null)).toBe(false)
    expect(recordNeedsFit(0.02, Number.NaN)).toBe(false)
    expect(recordNeedsFit(0.02, 0)).toBe(false)
  })

  it('aims between the two bounds rather than at one of them', () => {
    // The fit targets five periods; a target outside the band would mean every fit asked to be
    // fitted again, which is the one arrangement this pair of constants exists to prevent.
    expect(AUTOSET_PERIODS).toBeGreaterThan(AUTOSCALE_MIN_CYCLES)
    expect(AUTOSET_PERIODS).toBeLessThan(AUTOSCALE_MAX_CYCLES)
  })
})

describe('what the horizontal autoscale does with one record', () => {
  const triggered = { forced: false }
  const forced = { forced: true }

  it('holds inside the band and fits outside it', () => {
    const period = 0.001
    expect(timebaseAction(period * 5, period, triggered)).toBe('hold')
    expect(timebaseAction(period * 1.5, period, triggered)).toBe('fit')
    expect(timebaseAction(period * 40, period, triggered)).toBe('fit')
  })

  it('widens where the record is too short to measure a period at all', () => {
    // The gap a band cannot reach, and the reason the search exists: measured in the app, 600 Hz
    // on a 10 ms record follows happily and a jump to 60 Hz left the knob stuck for ever.
    expect(timebaseAction(0.01, null, triggered)).toBe('widen')
  })

  it('holds on a record Auto forced, whatever that record measured', () => {
    // The guard that matters. A dead input is not silent -- it is the front end's own noise, in
    // which the crossing counter finds a high and different frequency on every record. Measured in
    // the app without this: the timebase collapsed to the bottom rung and then wandered.
    expect(timebaseAction(0.01, null, forced)).toBe('hold')
    expect(timebaseAction(0.005, 1e-6, forced)).toBe('hold')
    // And the same record, triggered, is fitted -- so this is the flag doing the work.
    expect(timebaseAction(0.005, 1e-6, triggered)).toBe('fit')
  })

  it('ends a search in a fit rather than on whatever rung first measured something', () => {
    // Measured in the app: a tone coming back at 150 Hz was first measurable at 2 ms/div -- three
    // cycles, inside the band -- so without this the knob stopped there, where the same signal
    // fitted from a press lands on five. The toggle has to land where the press does.
    const periodS = 1 / 150
    expect(timebaseAction(0.02, periodS, triggered, false)).toBe('hold')
    expect(timebaseAction(0.02, periodS, triggered, true)).toBe('fit')
  })

  it('finds a frequency far below the record in a handful of rungs, and then settles', () => {
    // The search as the app runs it: one rung per record until something is measurable, then the
    // fit. Six cycles at 600 Hz, jumped to 60.
    const periodS = 1 / 60
    let seconds = recordSecondsFor(0.001)
    const steps: number[] = []
    let searching = false
    for (let i = 0; i < 20; i++) {
      // A record shorter than a period measures nothing, which is what the analyzer answers.
      const measured = seconds > periodS ? periodS : null
      const action = timebaseAction(seconds, measured, triggered, searching)
      searching = action === 'widen'
      if (action === 'hold') break
      if (action === 'fit') {
        seconds = recordSecondsFor(timebaseForCycles(periodS) as number)
      } else {
        seconds = recordSecondsFor(
          stepLadder(SCOPE_TIME_PER_DIVISION_LADDER, timePerDivisionFor(seconds), 1),
        )
      }
      steps.push(seconds)
    }
    // 10 ms -> 20 -> 50, where 60 Hz is measurable, then fitted to five cycles -> 83.3 ms -> 100.
    expect(steps.length).toBeLessThanOrEqual(5)
    expect(seconds / periodS).toBeGreaterThan(AUTOSCALE_MIN_CYCLES)
    expect(seconds / periodS).toBeLessThan(AUTOSCALE_MAX_CYCLES)
    // And it lands on about five, not merely inside the band -- the fit is what ended it.
    expect(seconds / periodS).toBeCloseTo(6, 0)
    expect(timebaseAction(seconds, periodS, triggered)).toBe('hold')
  })

  it('stops at the top of the ladder rather than searching for ever', () => {
    // Noise, or a lone pulse: it triggers and has no period. The ladder's own top is what ends it.
    const top = SCOPE_TIME_PER_DIVISION_LADDER[SCOPE_TIME_PER_DIVISION_LADDER.length - 1]
    const seconds = recordSecondsFor(top)
    expect(timebaseAction(seconds, null, triggered)).toBe('widen')
    expect(
      stepLadder(SCOPE_TIME_PER_DIVISION_LADDER, timePerDivisionFor(seconds), 1),
    ).toBeCloseTo(top, 12)
  })
})
