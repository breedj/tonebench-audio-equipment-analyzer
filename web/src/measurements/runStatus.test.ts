import { describe, expect, it } from 'vitest'
import { ResponsePhase, SweepPhase } from '../streaming/protocol'
import {
  progressFraction,
  responseRunTone,
  responseRunToneByName,
  runToneLook,
  statusDetail,
  statusHeadline,
  sweepRunTone,
} from './runStatus'

describe('run tone', () => {
  it('maps the four phases both runners share the same way', () => {
    expect(sweepRunTone(SweepPhase.Idle)).toBe('idle')
    expect(responseRunTone(ResponsePhase.Idle)).toBe('idle')
    expect(sweepRunTone(SweepPhase.Finished)).toBe('ok')
    expect(responseRunTone(ResponsePhase.Finished)).toBe('ok')
    expect(sweepRunTone(SweepPhase.Cancelled)).toBe('cancelled')
    expect(responseRunTone(ResponsePhase.Cancelled)).toBe('cancelled')
    expect(sweepRunTone(SweepPhase.Failed)).toBe('error')
    expect(responseRunTone(ResponsePhase.Failed)).toBe('error')
  })

  it('calls every working phase busy', () => {
    for (const phase of [SweepPhase.DeterminingLevel, SweepPhase.NoiseFloor, SweepPhase.Sweeping]) {
      expect(sweepRunTone(phase)).toBe('busy')
    }
    for (const phase of [
      ResponsePhase.DeterminingLevel,
      ResponsePhase.DeterminingRange,
      ResponsePhase.Measuring,
      ResponsePhase.Sweeping,
      ResponsePhase.Analyzing,
      ResponsePhase.Calibrating,
    ]) {
      expect(responseRunTone(phase)).toBe('busy')
    }
  })

  it('singles out the output-impedance run waiting for the load to be changed', () => {
    // Neither busy nor over: the run has stopped and needs the bench re-wired, which is the one
    // state the bar exists to make unmistakable.
    expect(responseRunTone(ResponsePhase.WaitingForLoadChange)).toBe('waiting')
  })

  it('draws a cancelled bar in grey, not red — the points it did measure are good', () => {
    expect(runToneLook('cancelled').fill).not.toContain('red')
    expect(runToneLook('cancelled').dot).toContain('red')
    // Which is what tells it apart from a failure, whose bar IS red.
    expect(runToneLook('error').fill).toContain('red')
  })
})

describe('progressFraction', () => {
  it('fills a finished run whatever its counts say', () => {
    // A chirp measurement has no points at all, and a sweep stopped early still finished its last
    // point — done/total would leave the bar short of the end on a run that is over.
    expect(progressFraction('ok', 0, 0)).toBe(1)
    expect(progressFraction('ok', 14, 50)).toBe(1)
  })

  it('keeps a cancelled run at how far it got', () => {
    expect(progressFraction('cancelled', 4, 50)).toBeCloseTo(0.08)
  })

  it('empties an idle one', () => {
    expect(progressFraction('idle', 9, 50)).toBe(0)
  })

  it('has no fraction where there is nothing to be a fraction of', () => {
    expect(progressFraction('busy', 0, 0)).toBeNull()
  })

  it('never runs past the end', () => {
    expect(progressFraction('busy', 51, 50)).toBe(1)
  })
})

describe('statusHeadline / statusDetail', () => {
  it('splits a working runner at its first separator', () => {
    expect(statusHeadline('busy', 'Point 7/31 · 1.26 kHz')).toBe('Point 7/31')
    expect(statusDetail('busy', 'Point 7/31 · 1.26 kHz')).toBe('1.26 kHz')
    expect(statusHeadline('busy', 'Measuring · pass 2 · record 1 of 4')).toBe('Measuring')
    expect(statusDetail('busy', 'Measuring · pass 2 · record 1 of 4')).toBe('pass 2 · record 1 of 4')
  })

  it('fixes the word for every state that is not working, and keeps the count as detail', () => {
    expect(statusHeadline('ok', 'Finished · 50 points')).toBe('Finished')
    expect(statusDetail('ok', 'Finished · 50 points')).toBe('50 points')
    expect(statusHeadline('cancelled', 'Stopped after 14 of 50 points')).toBe('Cancelled')
    // Its own first segment is not the headline, so none of it is thrown away.
    expect(statusDetail('cancelled', 'Stopped after 14 of 50 points')).toBe('Stopped after 14 of 50 points')
  })

  it('says something even when the runner says nothing', () => {
    expect(statusHeadline('busy', '')).toBe('Running')
    expect(statusHeadline('idle', '')).toBe('Idle')
    expect(statusDetail('idle', 'Point 7/31 · 1.26 kHz')).toBe('')
  })
})

describe('responseRunToneByName', () => {
  it('agrees with the ordinal mapping on every phase', () => {
    // The impulse runner is polled over REST and so reports a name; the other five stream an
    // ordinal. The two must not drift, or one screen's bar would be a different colour from
    // another's in the same state.
    const pairs: [ResponsePhase, Parameters<typeof responseRunToneByName>[0]][] = [
      [ResponsePhase.Idle, 'Idle'],
      [ResponsePhase.DeterminingLevel, 'DeterminingLevel'],
      [ResponsePhase.DeterminingRange, 'DeterminingRange'],
      [ResponsePhase.Measuring, 'Measuring'],
      [ResponsePhase.Sweeping, 'Sweeping'],
      [ResponsePhase.Analyzing, 'Analyzing'],
      [ResponsePhase.Calibrating, 'Calibrating'],
      [ResponsePhase.Finished, 'Finished'],
      [ResponsePhase.Cancelled, 'Cancelled'],
      [ResponsePhase.Failed, 'Failed'],
      [ResponsePhase.WaitingForLoadChange, 'WaitingForLoadChange'],
    ]
    for (const [ordinal, name] of pairs) {
      expect(responseRunToneByName(name)).toBe(responseRunTone(ordinal))
    }
  })
})
