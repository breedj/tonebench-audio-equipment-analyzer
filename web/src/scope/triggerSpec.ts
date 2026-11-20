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

/**
 * One option of a trigger control: the value the server takes, what the control reads, and the
 * figure it is drawn as.
 *
 * <p><b>The figure is the point of these being a listbox rather than a
 * <code>&lt;select&gt;</code>.</b> <em>Edge</em>, <em>Pulse width</em>, <em>Window</em>,
 * <em>Dropout</em> and <em>Runt</em> are five words a reader parses where five pictures of what
 * each one fires on are scanned — the argument <code>measurementIcons.ts</code> already makes about
 * thirty measurement names. <code>glyph</code> is the way out for the two that a picture would say
 * less about than a letter: an input channel is <b>L</b> or <b>R</b> on every scope ever made.</p>
 */
export interface ScopeOption<T extends string> {
  value: T
  label: string
  /** A figure from <code>scope/displayIcons.ts</code>. */
  icon?: string
  /** Or a letter, where that says more. */
  glyph?: string
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
  { value: 'Auto', label: 'Auto', icon: 'modeAuto' },
  { value: 'Normal', label: 'Normal', icon: 'modeNormal' },
  { value: 'Single', label: 'Single', icon: 'modeSingle' },
  { value: 'Roll', label: 'Roll', icon: 'modeRoll' },
])

/** What each mode does, for the control's tooltip — the difference between Auto and Normal is the one worth stating. */
export const SCOPE_RUN_MODE_HINTS: Readonly<Record<ScopeRunMode, string>> = Object.freeze({
  Auto: 'Re-arms after every capture, and draws an untriggered record if nothing crosses the level within the timeout. Always shows something.',
  Normal: 'Re-arms after every capture and waits as long as it takes. Nothing is drawn until the level is crossed.',
  Single: 'Captures one record and holds it.',
  Roll: 'Scrolls the newest samples continuously, with no trigger at all. For signals too slow to trigger on.',
})

export const SCOPE_TRIGGER_SLOPES: readonly ScopeOption<ScopeTriggerSlope>[] = Object.freeze([
  { value: 'Rising', label: 'Rising', icon: 'slopeRising' },
  { value: 'Falling', label: 'Falling', icon: 'slopeFalling' },
  { value: 'Either', label: 'Either', icon: 'slopeEither' },
])

/**
 * The trigger kinds, in the order a bench instrument offers them: the ordinary one first, then the
 * four that each select a fault an edge trigger cannot pick out of the signal around it.
 */
export const SCOPE_TRIGGER_KINDS: readonly ScopeOption<ScopeTriggerKind>[] = Object.freeze([
  { value: 'Edge', label: 'Edge', icon: 'kindEdge' },
  { value: 'PulseWidth', label: 'Pulse width', icon: 'kindPulse' },
  { value: 'Window', label: 'Window', icon: 'kindWindow' },
  { value: 'Dropout', label: 'Dropout', icon: 'kindDropout' },
  { value: 'Runt', label: 'Runt', icon: 'kindRunt' },
])

/**
 * What each kind is for, for the control's tooltip.
 *
 * <p>Each one says what it <em>finds</em> rather than how it works, because the reason to reach past
 * Edge is always a fault an edge trigger cannot pick out of the signal around it.</p>
 */
export const SCOPE_TRIGGER_KIND_HINTS: Readonly<Record<ScopeTriggerKind, string>> = Object.freeze({
  Edge: 'Fires on a crossing of the level. What a scope does unless you tell it otherwise.',
  PulseWidth:
    'Fires on a pulse whose width qualifies, and on nothing else. The glitch trigger: a crossover notch or a stuck output is one pulse among thousands that an edge trigger cannot pick out.',
  Window:
    'Fires when the signal enters or leaves a voltage band. For watching something that is supposed to stay put — a bias point, a rail, a mute.',
  Dropout:
    'Fires when a qualifying edge has NOT happened for long enough. The one trigger that waits for nothing — for a signal that stops, mutes or drops out.',
  Runt:
    'Fires on a pulse that crosses one threshold and fails to reach the other: an edge that did not make it. A runt is exactly as wide as a good pulse, so only its height tells them apart.',
})

/** Which slope means what, per kind: the word is the same and the thing it selects is not. */
export const SCOPE_TRIGGER_SLOPE_HINTS: Readonly<Record<ScopeTriggerKind, string>> = Object.freeze({
  Edge: 'Which direction through the level qualifies.',
  PulseWidth:
    'Which stretch is the pulse: Rising measures the stretch above the level, Falling the stretch below it. Either watches both.',
  Window: 'Rising fires on entering the band, Falling on leaving it, Either on both.',
  Dropout: 'Which direction counts as a sign of life. Either means no crossing of any kind.',
  Runt:
    'Which direction the failed pulse goes: Rising is a pulse up that never reaches the upper threshold, Falling a pulse down that never reaches the lower one.',
})

/**
 * How a width condition is spelled on screen.
 *
 * <p>Three conditions over one pair of bounds, because that is how a reader thinks about it — a
 * glitch is "shorter than", a stuck level is "longer than" — where the pair on the wire is a range
 * and says neither. <code>pulseWidthBounds</code> is the mapping, and
 * <code>pulseConditionFor</code> reads it back, so a stored measurement reopens on the condition it
 * was set with rather than always on "between".</p>
 */
export type ScopePulseCondition = 'ShorterThan' | 'LongerThan' | 'Between'

export const SCOPE_PULSE_CONDITIONS: readonly ScopeOption<ScopePulseCondition>[] = Object.freeze([
  { value: 'ShorterThan', label: 'Shorter than', icon: 'widthLess' },
  { value: 'LongerThan', label: 'Longer than', icon: 'widthGreater' },
  { value: 'Between', label: 'Between', icon: 'widthBetween' },
])

/** The two bounds a condition comes to, in seconds. Null for the upper one means unbounded above. */
export function pulseWidthBounds(
  condition: ScopePulseCondition,
  widthS: number,
  widthHiS: number,
): { loS: number; hiS: number | null } {
  switch (condition) {
    case 'ShorterThan':
      return { loS: 0, hiS: widthS }
    case 'LongerThan':
      // Null rather than a large number: "longer than" has no upper bound at all, and a bound
      // chosen to stand in for one would quietly stop firing on the pulse that never ends, which
      // is the fault this condition exists for.
      return { loS: widthS, hiS: null }
    case 'Between':
      return { loS: widthS, hiS: widthHiS }
  }
}

/**
 * Which condition a stored pair of bounds was set as.
 *
 * <p>A pair with no upper bound can only have been "longer than", and one with no lower bound only
 * "shorter than". Both present is "between" — including the case where they are equal, which is a
 * band of zero width and is the user's business rather than this function's.</p>
 */
export function pulseConditionFor(loS: number | null | undefined, hiS: number | null | undefined): ScopePulseCondition {
  const lo = typeof loS === 'number' && Number.isFinite(loS) ? loS : 0
  const hi = typeof hiS === 'number' && Number.isFinite(hiS) ? hiS : null
  if (hi === null) return 'LongerThan'
  if (lo <= 0) return 'ShorterThan'
  return 'Between'
}

/** The width the box shows for a condition — the bound that condition is set by. */
export function pulseWidthFor(condition: ScopePulseCondition, loS: number, hiS: number | null): number {
  return condition === 'ShorterThan' ? (hiS ?? 0) : loS
}

/**
 * The trigger sources this build implements.
 *
 * <p><code>Math</code> is on the wire and is deliberately not offered: the server refuses it by
 * name, and a dropdown entry whose only outcome is a refusal is worse than no entry.</p>
 *
 * <p><b>Stimulus is last because it is the odd one.</b> The other two name a channel to watch; this
 * one watches nothing at all and fires on the generator's own repeat, which is the whole point of
 * owning the stimulus — and the only way to hold a chirp or a noise sequence still, neither having
 * any feature an edge trigger could lock to.</p>
 */
export const SCOPE_TRIGGER_SOURCES: readonly ScopeOption<ScopeTriggerSource>[] = Object.freeze([
  { value: 'ChannelA', label: 'Left', glyph: 'L' },
  { value: 'ChannelB', label: 'Right', glyph: 'R' },
  { value: 'Stimulus', label: 'Stimulus', icon: 'sourceStimulus' },
])

/** What each source watches, for the control's tooltip. */
export const SCOPE_TRIGGER_SOURCE_HINTS: Readonly<Record<ScopeTriggerSource, string>> = Object.freeze({
  ChannelA: 'The left input.',
  ChannelB: 'The right input.',
  Math: 'Not implemented.',
  Stimulus:
    "The generator's own repeat rather than anything measured — no level, no slope, no jitter. Needs a stimulus whose repeat is exact: a chirp, a square or a triangle.",
})

/**
 * Whether the staged source replaces the kind and its controls.
 *
 * <p>Triggering on the generator's repeat reads no samples, so the kind, the level, the slope and
 * every width beside them do nothing. The server refuses a kind other than Edge alongside it rather
 * than ignoring one, and this is the client saying the same thing by not offering them.</p>
 */
export function triggerIsStimulus(source: ScopeTriggerSource): boolean {
  return source === 'Stimulus'
}

/**
 * Which stimuli the analyzer can lock to, as a sentence, or null where the staged one is fine.
 *
 * <p>Mirrors <code>OscilloscopeRequest.StimulusPeriodFrames</code>: a stimulus has a period to
 * trigger on only where it was <em>built</em> to a whole number of frames. A plain tone's period is
 * <code>fs / f</code>, which is a whole number only for frequencies nobody types — and it has a
 * crossing to trigger on anyway, which the message says rather than leaving a reader to wonder why
 * the obvious stimulus is the one that will not do.</p>
 */
export function stimulusSourceProblem(generatorKind: string | null | undefined): string | null {
  // The three the generator panel can stage with an exact repeat. A PERIODIC noise sequence
  // qualifies too and the server will take one, but nothing here can compose it: the generator
  // endpoint's noise block carries a level and no period, so noise staged from a screen is the
  // continuous kind and genuinely has no repeat. Listing it would be a client that accepts what
  // the server refuses, which is worse than a short list.
  const locks = ['EchoChirp', 'Square', 'Triangle']
  if (generatorKind && locks.includes(generatorKind)) return null
  return 'The stimulus trigger needs a stimulus whose repeat is exact: a chirp, a square or a triangle. A plain tone has a crossing to trigger on instead.'
}

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
  // Stimulus watches no channel at all, so it has nothing to be missing.
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

/** Where a window trigger's two lines go, and whether either of them is really there. */
export interface WindowIndicator {
  /** The upper bound, clamped to the window the way a single level is. */
  high: { levelV: number; offWindow: 'above' | 'below' | null }
  /** The lower bound. */
  low: { levelV: number; offWindow: 'above' | 'below' | null }
  /** The band between them, clipped to the window, or null where none of it is on screen. */
  band: { topV: number; bottomV: number } | null
}

/**
 * Where to draw a window trigger's band over a window running from <code>bottomV</code> to
 * <code>topV</code>.
 *
 * <p><b>The band is the control.</b> A window trigger's two levels are what it fires on, and two
 * numbers in a settings rail say nothing about where they sit against the signal — so the band is
 * to this kind what the hysteresis band is to the edge trigger: the one thing that makes an
 * invisible setting aimable. It replaces the hysteresis band rather than joining it, because two
 * translucent bands over each other, one of them a few millivolts tall, is unreadable.</p>
 *
 * <p>The pair is <b>sorted</b>, as the scanner sorts it, so typing the high figure in the low box
 * draws the same band the capture will use — a picture that disagreed with the trigger would be
 * worse than no picture. Each line is clamped to the edge and marked when it is off the window,
 * which is <code>triggerIndicator</code>'s own rule and for its own reason.</p>
 */
export function windowIndicator(
  levelV: number,
  levelLowV: number,
  topV: number,
  bottomV: number,
): WindowIndicator {
  const hi = Math.max(topV, bottomV)
  const lo = Math.min(topV, bottomV)
  const middle = (hi + lo) / 2

  const place = (v: number) =>
    Number.isFinite(v)
      ? { levelV: Math.min(Math.max(v, lo), hi), offWindow: (v > hi ? 'above' : v < lo ? 'below' : null) as 'above' | 'below' | null }
      : { levelV: middle, offWindow: null }

  const bandHigh = Math.max(levelV, levelLowV)
  const bandLow = Math.min(levelV, levelLowV)
  const high = place(bandHigh)
  const low = place(bandLow)

  let band: WindowIndicator['band'] = null
  if (Number.isFinite(bandHigh) && Number.isFinite(bandLow)) {
    const clippedTop = Math.min(bandHigh, hi)
    const clippedBottom = Math.max(bandLow, lo)
    // A band entirely off the window has nothing left to draw, and clamping it would paint a
    // hairline on the edge and claim the band was there.
    if (clippedTop > clippedBottom) band = { topV: clippedTop, bottomV: clippedBottom }
  }

  return { high, low, band }
}

/**
 * Why a window trigger's two levels will not do, or null.
 *
 * <p>The server refuses the same thing and names it; this is the client saying so before a run is
 * attempted, which is <code>triggerSourceProblem</code>'s arrangement one kind along.</p>
 */
export function windowLevelsProblem(
  levelV: number,
  levelLowV: number,
  kind: ScopeTriggerKind = 'Window',
): string | null {
  const runt = kind === 'Runt'
  if (!Number.isFinite(levelV) || !Number.isFinite(levelLowV))
    return runt ? 'Both runt thresholds must be voltages.' : 'Both window levels must be voltages.'
  if (levelV === levelLowV) {
    // The same two numbers read two ways, so one check -- but a runt is a pulse that reaches one
    // threshold and not the other, which is not what a band is, and a message about a "window" on
    // the runt screen would send a reader looking for a control that is not there.
    return runt
      ? 'A runt is a pulse that reaches one threshold and not the other, so the two must differ.'
      : 'The window trigger needs two different levels. Set one above the other to make a band.'
  }

  return null
}

/**
 * Why a dropout timeout will not do, or null.
 *
 * <p>Zero fires on every sample on which no edge happened, which is almost all of them — a trigger
 * that fires constantly while claiming to report an absence. The server refuses the same thing and
 * names it.</p>
 */
export function dropoutProblem(timeoutS: number): string | null {
  if (!Number.isFinite(timeoutS) || timeoutS <= 0)
    return 'The dropout trigger needs a timeout above zero: how long an absence has to last.'
  return null
}

/**
 * Why a width condition will not do, or null.
 *
 * <p>Two of these three are arithmetic the user cannot see. <b>A pulse narrower than one sample
 * cannot exist in the record</b>, so "shorter than 2 µs" at 192 kS/s — where a sample arrives every
 * 5.2 µs — is a condition that is perfectly reasonable to type and can never fire, which is the
 * worst way for a control to be wrong. And a condition with no bound at all fires on every pulse
 * while claiming to select one.</p>
 */
export function pulseWidthProblem(
  condition: ScopePulseCondition,
  widthS: number,
  widthHiS: number,
  sampleIntervalS: number,
): string | null {
  if (!Number.isFinite(widthS) || widthS <= 0) return 'The pulse width must be a positive time.'
  if (condition === 'Between') {
    if (!Number.isFinite(widthHiS) || widthHiS <= 0) return 'The longer width must be a positive time.'
    if (widthHiS < widthS) return 'The longer width cannot be below the shorter one.'
  }

  const upper = condition === 'ShorterThan' ? widthS : condition === 'Between' ? widthHiS : null
  if (upper !== null && Number.isFinite(sampleIntervalS) && sampleIntervalS > 0 && upper < sampleIntervalS) {
    return `A pulse shorter than one sample cannot be captured. At this rate a sample arrives every ${(sampleIntervalS * 1e6).toFixed(2)} µs.`
  }

  return null
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
