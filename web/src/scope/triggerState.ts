/**
 * What a bench oscilloscope prints in the corner of its screen: Armed, Ready, Trig'd, Stop, Auto,
 * Scan.
 *
 * <p>Asked for 2026-11-21, with Siglent's own six and their own definitions. They are worth more
 * than a progress bar on this screen, and that is why they replace one: a scope's run does not
 * <em>progress</em> — Normal and Auto go round the same loop for as long as they are left, so a
 * track that fills says nothing, where a word that changes says whether the analyzer is waiting for
 * a trigger it is never going to get.</p>
 *
 * <p><b>The server reports facts and this decides the word.</b> `OscilloscopeStatus` carries the
 * engine's state and whether the pre-trigger is filled; the run mode and whether the last record
 * was forced are already on it. Which of the six that comes to is presentation, and it also needs
 * two of those four at once, so it is one function here rather than a string chosen in four places
 * on the server.</p>
 */

import type { ScopeRunMode } from '../api/client'
import type { RunTone } from '../measurements/runStatus'

/** The six, spelled as a bench instrument spells them. */
export type ScopeTriggerState = 'Armed' | 'Ready' | "Trig'd" | 'Stop' | 'Auto' | 'Scan'

/** What the badge is made of: the word, and how it is tinted. */
export interface ScopeTriggerBadge {
  state: ScopeTriggerState
  /**
   * The status bar's own tones, so one state is one colour wherever it is shown.
   *
   * <p><b>Auto is amber</b>, which is the one choice here worth stating: it is the "it needs
   * you" tone on every other screen, and a scope drawing on its timeout is exactly that — the
   * trace on screen is not of a triggered event, and the level, the slope or the source wants
   * looking at. Armed and Ready are blue (working), Trig'd and Scan green (acquiring), Stop
   * grey.</p>
   */
  tone: RunTone
  /** What the state means, for the badge's own tooltip. */
  detail: string
}

const TONES: Record<ScopeTriggerState, RunTone> = {
  Armed: 'busy',
  Ready: 'busy',
  "Trig'd": 'ok',
  Stop: 'idle',
  Auto: 'waiting',
  Scan: 'ok',
}

const DETAIL: Record<ScopeTriggerState, string> = {
  Armed:
    'Collecting the samples that go before the trigger. Nothing can trigger it yet — a crossing now would have no pre-trigger history behind it.',
  Ready: 'The pre-trigger is filled and the analyzer is waiting for a trigger.',
  "Trig'd": 'A trigger fired; the samples that go after it are still arriving.',
  Stop: 'Not acquiring. Whatever was last captured is on screen.',
  Auto: 'Auto mode is drawing without a trigger, because none arrived inside the timeout. Check the level, the slope and the source.',
  Scan: 'Roll: there is no trigger at all, and the newest window slides across the screen.',
}

/** What the engine said it was doing, as `OscilloscopeStatus.engineState` spells it. */
export type ScopeEngineStateName = 'Idle' | 'Waiting' | 'Filling' | 'Held' | 'Rolling'

export interface ScopeTriggerInput {
  /** The engine's own state. Anything this build does not know reads as stopped. */
  engineState?: string | null
  /** Whether a trigger found now would have its pre-trigger history — Armed against Ready. */
  preTriggerReady?: boolean
  /** The mode the run was started in; `Auto` is the only one that forces a record. */
  mode: ScopeRunMode
  /** Whether the record on screen was forced rather than triggered. */
  forced?: boolean
  /** False once the run has stopped, whatever the engine still says. */
  running: boolean
}

/**
 * The word for what the analyzer is doing.
 *
 * <p>Two of the six are decisions rather than a mapping. <b>Auto</b> is shown only while an Auto run
 * is <em>actually</em> forcing — the mode alone is not enough, since an Auto run with a good trigger
 * goes round Ready and Trig'd like any other and saying Auto there would hide the one thing this
 * badge is read for. And <b>Held</b> reads as <b>Stop</b>: a Single that has its record is finished,
 * which is exactly what a bench scope says after one.</p>
 */
export function scopeTriggerBadge(input: ScopeTriggerInput): ScopeTriggerBadge {
  const state = stateOf(input)
  return { state, tone: TONES[state], detail: DETAIL[state] }
}

function stateOf(input: ScopeTriggerInput): ScopeTriggerState {
  if (!input.running) return 'Stop'

  switch (input.engineState) {
    case 'Rolling':
      return 'Scan'
    case 'Filling':
      return "Trig'd"
    case 'Waiting':
      // Forced *and* still scanning is a scope drawing on the timeout rather than on the signal.
      if (input.mode === 'Auto' && input.forced === true) return 'Auto'
      return input.preTriggerReady === true ? 'Ready' : 'Armed'
    case 'Held':
    case 'Idle':
    default:
      return 'Stop'
  }
}
