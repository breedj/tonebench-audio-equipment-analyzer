/**
 * The oscilloscope's trigger, as the panel stages it and as the chart draws it.
 *
 * <p>The level, the slope, the source and the run mode are <b>measurement settings</b>, not display
 * state: they decide what the next capture is, so they ride on the start request and are stored in
 * the measurement's own <code>params_json</code> beside the timebase. The one piece of this file
 * that is about drawing is <code>triggerIndicator</code>, and it is here rather than in the chart
 * because the rule it carries — a level off the window is clamped to the edge and <em>marked</em> —
 * is the kind of thing that has to be testable.</p>
 */

import type { ScopeRunMode, ScopeTriggerKind, ScopeTriggerSlope, ScopeTriggerSource } from '../api/client'

/** The staged trigger, one object so the panel can watch it as a unit. */
export interface ScopeTrigger {
  kind: ScopeTriggerKind
  source: ScopeTriggerSource
  slope: ScopeTriggerSlope
  levelV: number
  hysteresisV: number
  holdoffS: number
}

/**
 * What a fresh scope measurement triggers on.
 *
 * <p>A rising crossing of zero on the left channel: the one setting that finds something on almost
 * any audio signal, and the same thing the FFT screen's waveform panel has always done, so the two
 * screens open on the same picture. Kept in step with
 * <code>MeasurementSaveService.DefaultOscilloscopeParametersJson</code>, which is what a brand-new
 * measurement's <code>params_json</code> actually holds — the client's defaults never get a look in
 * there (the <code>ImpedanceStimulusDefaults</code> lesson), so the two are written twice and have
 * to agree.</p>
 */
export const DEFAULT_SCOPE_TRIGGER: Readonly<ScopeTrigger> = Object.freeze({
  kind: 'Edge' as ScopeTriggerKind,
  source: 'ChannelA' as ScopeTriggerSource,
  slope: 'Rising' as ScopeTriggerSlope,
  levelV: 0,
  hysteresisV: 0,
  holdoffS: 0,
})

/** One option of a dropdown: the value the server takes, and what the control reads. */
export interface ScopeOption<T extends string> {
  value: T
  label: string
}

/**
 * How the scope re-arms, in the order a bench instrument offers them.
 *
 * <p><b>Auto first, because it is the only one that always draws something.</b> Normal waits for a
 * crossing for ever, which on a dead input is indistinguishable from a scope that has hung — so it
 * is the right default only once a user knows the difference. Roll has no trigger at all and is
 * therefore last.</p>
 */
export const SCOPE_RUN_MODES: readonly ScopeOption<ScopeRunMode>[] = Object.freeze([
  { value: 'Auto', label: 'Auto' },
  { value: 'Normal', label: 'Normal' },
  { value: 'Single', label: 'Single' },
  { value: 'Roll', label: 'Roll' },
])

/** What each mode does, for the control's tooltip — the difference between Auto and Normal is the one worth stating. */
export const SCOPE_RUN_MODE_HINTS: Readonly<Record<ScopeRunMode, string>> = Object.freeze({
  Auto: 'Re-arms after every capture, and draws an untriggered record if nothing crosses the level within the timeout. Always shows something.',
  Normal: 'Re-arms after every capture and waits as long as it takes. Nothing is drawn until the level is crossed.',
  Single: 'Captures one record and holds it.',
  Roll: 'Scrolls the newest samples continuously, with no trigger at all. For signals too slow to trigger on.',
})

export const SCOPE_TRIGGER_SLOPES: readonly ScopeOption<ScopeTriggerSlope>[] = Object.freeze([
  { value: 'Rising', label: 'Rising' },
  { value: 'Falling', label: 'Falling' },
  { value: 'Either', label: 'Either' },
])

/**
 * The trigger sources this build implements.
 *
 * <p><code>Math</code> and <code>Stimulus</code> exist on the wire and are deliberately not offered:
 * the server refuses them by name, and a dropdown entry whose only outcome is a refusal is worse
 * than no entry. The stimulus-synchronous trigger is the interesting one of the two and is the whole
 * point of owning the generator — it arrives with the advanced triggers.</p>
 */
export const SCOPE_TRIGGER_SOURCES: readonly ScopeOption<ScopeTriggerSource>[] = Object.freeze([
  { value: 'ChannelA', label: 'Left' },
  { value: 'ChannelB', label: 'Right' },
])

/** Which captured channel the trigger watches. */
export function triggerChannel(source: ScopeTriggerSource): number {
  return source === 'ChannelB' ? 1 : 0
}

/**
 * Whether the trigger can see the channel it is set to watch.
 *
 * <p>The server refuses this combination and names it, so this is the client saying the same thing
 * before a run is attempted rather than a second rule: a trigger on the right channel with the
 * right channel not captured is a run that cannot start.</p>
 */
export function triggerSourceProblem(
  source: ScopeTriggerSource,
  processLeft: boolean,
  processRight: boolean,
): string | null {
  if (source === 'ChannelA' && !processLeft) return 'The trigger watches the left channel, which is not being captured.'
  if (source === 'ChannelB' && !processRight) return 'The trigger watches the right channel, which is not being captured.'
  return null
}

/** Where the indicator is drawn, and whether that is where the level actually is. */
export interface TriggerIndicator {
  /** The volts to draw the line at — the level itself, or the edge of the window it sits outside. */
  levelV: number
  /** Which way the real level lies when it is off the window, so an arrow can say so. Null when it is on screen. */
  offWindow: 'above' | 'below' | null
  /** The band ±hysteresis around the line, or null when the hysteresis is zero and there is no band to draw. */
  band: { topV: number; bottomV: number } | null
}

/**
 * Where to draw the trigger level over a window running from <paramref name="bottomV"/> to
 * <paramref name="topV"/>.
 *
 * <p><b>A level outside the window is clamped to the edge and marked, never hidden.</b> A scope that
 * drops its trigger indicator when the vertical window is scrolled away from it is a scope whose
 * trigger cannot be found again — and the level is still in force, so a picture that does not show
 * it is a picture that does not explain itself. An arrow at the edge is how every bench instrument
 * says "it is off this way", and it is also the affordance that makes the line draggable back.</p>
 *
 * <p>The <b>hysteresis band</b> comes back with it, as the one control whose effect is otherwise
 * completely invisible: it is what a user reaches for when the trace will not stand still, and
 * without the band there is nothing on screen to aim it at. It is clipped to the window rather than
 * suppressed, so a band hanging off the top still shows the half of itself that is on screen.</p>
 */
export function triggerIndicator(
  levelV: number,
  topV: number,
  bottomV: number,
  hysteresisV = 0,
): TriggerIndicator {
  const hi = Math.max(topV, bottomV)
  const lo = Math.min(topV, bottomV)

  if (!Number.isFinite(levelV)) return { levelV: (hi + lo) / 2, offWindow: null, band: null }

  const offWindow = levelV > hi ? 'above' : levelV < lo ? 'below' : null
  const drawn = Math.min(Math.max(levelV, lo), hi)

  let band: TriggerIndicator['band'] = null
  if (Number.isFinite(hysteresisV) && hysteresisV > 0) {
    const bandTop = Math.min(levelV + hysteresisV, hi)
    const bandBottom = Math.max(levelV - hysteresisV, lo)
    // A band entirely off the window has nothing left to draw; clamping it would draw a hairline on
    // the edge and claim the level was there.
    if (bandTop > bandBottom) band = { topV: bandTop, bottomV: bandBottom }
  }

  return { levelV: drawn, offWindow, band }
}

/**
 * The level half way between a record's extremes — a scope's "50 %" button.
 *
 * <p>The midpoint and not the mean, which is the whole point: a square wave at a 10 % duty cycle has
 * a mean near its base, and a trigger set there fires on the noise around the base rather than on
 * the edge. Mirrors <code>TriggerAutoLevel.FiftyPercent</code> server-side, which is where the same
 * rule is written for the runner's own use.</p>
 */
export function fiftyPercentLevel(maxV: number, minV: number): number | null {
  if (!Number.isFinite(maxV) || !Number.isFinite(minV)) return null
  return (maxV + minV) / 2
}

/**
 * The hysteresis <code>TriggerAutoLevel.HysteresisFor</code> would pick for a record of this size —
 * a fiftieth of its peak-to-peak.
 *
 * <p>Offered rather than applied, because a hysteresis that followed the signal would change what
 * the trigger does every capture, which is the opposite of what a noise-reject control is for.</p>
 */
export function suggestedHysteresis(maxV: number, minV: number): number | null {
  if (!Number.isFinite(maxV) || !Number.isFinite(minV)) return null
  const span = Math.abs(maxV - minV)
  return span > 0 ? span * 0.02 : null
}
