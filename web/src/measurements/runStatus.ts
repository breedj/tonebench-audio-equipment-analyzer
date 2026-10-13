// What the measurement status bar (MeasurementStatusBar.vue) shows: which of five states a run is
// in, and how far through it is. The bar itself is in the mockups of 2026-10-12; this is the part of
// it that is arithmetic rather than markup, and so the part that can be tested.
//
// Five tones rather than one per phase: a bar that is a different colour in "Determining level" and
// in "Measuring" says nothing a reader can act on, while "it is working" / "it finished" / "you
// stopped it" / "it needs you" / "it failed" are four different situations and the fifth is nothing
// having happened yet.
import type { ResponsePhaseName } from '../api/client'
import { ResponsePhase, SweepPhase } from '../streaming/protocol'

export type RunTone = 'idle' | 'busy' | 'waiting' | 'ok' | 'cancelled' | 'error'

export interface RunToneLook {
  /** The 7 px dot's fill. */
  dot: string
  /** The label beside it. */
  label: string
  /** The progress bar's fill. */
  fill: string
}

/**
 * Tailwind classes rather than the mockup's hexes, so the one place a colour is spelled is still a
 * palette entry. The dark-theme twins are a step lighter throughout: the mockup's #16a34a on
 * #121417 is legible but joyless, and these dots are small.
 */
const LOOKS: Record<RunTone, RunToneLook> = {
  idle: {
    dot: 'bg-neutral-400 dark:bg-neutral-500',
    label: 'text-neutral-500 dark:text-neutral-400',
    fill: 'bg-neutral-300 dark:bg-neutral-600',
  },
  busy: {
    dot: 'bg-blue-500 dark:bg-blue-400',
    label: 'text-blue-700 dark:text-blue-300',
    fill: 'bg-blue-500 dark:bg-blue-400',
  },
  waiting: {
    dot: 'bg-amber-500 dark:bg-amber-400',
    label: 'text-amber-700 dark:text-amber-300',
    fill: 'bg-amber-500 dark:bg-amber-400',
  },
  ok: {
    dot: 'bg-green-600 dark:bg-green-400',
    label: 'text-green-700 dark:text-green-300',
    fill: 'bg-green-600 dark:bg-green-400',
  },
  cancelled: {
    dot: 'bg-red-600 dark:bg-red-400',
    label: 'text-red-700 dark:text-red-300',
    // Grey, not red: the points already measured are good, and a red bar claims they are not.
    // The mockup's own choice (#94a3b8 against a #dc2626 dot).
    fill: 'bg-neutral-400 dark:bg-neutral-500',
  },
  error: {
    dot: 'bg-red-600 dark:bg-red-400',
    label: 'text-red-700 dark:text-red-300',
    fill: 'bg-red-500 dark:bg-red-400',
  },
}

export function runToneLook(tone: RunTone): RunToneLook {
  return LOOKS[tone]
}

/** The THD / IMD sweep runners' phase. */
export function sweepRunTone(phase: SweepPhase): RunTone {
  switch (phase) {
    case SweepPhase.Idle:
      return 'idle'
    case SweepPhase.Finished:
      return 'ok'
    case SweepPhase.Cancelled:
      return 'cancelled'
    case SweepPhase.Failed:
      return 'error'
    default:
      return 'busy'
  }
}

/**
 * The frequency-response, stepped, crosstalk, impedance and output-impedance runners' phase — one
 * enumeration shared by all five. `WaitingForLoadChange` is the only phase that is neither busy nor
 * over: the output-impedance run has stopped and is waiting for the bench to be re-wired, which is
 * the one state the bar has to make unmistakable.
 */
export function responseRunTone(phase: ResponsePhase): RunTone {
  switch (phase) {
    case ResponsePhase.Idle:
      return 'idle'
    case ResponsePhase.Finished:
      return 'ok'
    case ResponsePhase.Cancelled:
      return 'cancelled'
    case ResponsePhase.Failed:
      return 'error'
    case ResponsePhase.WaitingForLoadChange:
      return 'waiting'
    default:
      return 'busy'
  }
}

/**
 * How full the bar is, 0..1 — or null where there is nothing to be a fraction of, which the bar
 * draws as an empty track rather than as a full or an absent one.
 *
 * A finished run reads 1 whatever the counts say: a chirp measurement has no points at all, and a
 * sweep that stopped early still *finished* its last point, so `done/total` would leave the bar
 * short of the end on a run that is over. A cancelled one keeps its fraction, because how far it
 * got is the whole of what it has left to say.
 */
export function progressFraction(tone: RunTone, done: number, total: number): number | null {
  if (tone === 'ok') return 1
  if (tone === 'idle') return 0
  if (!Number.isFinite(total) || total <= 0) return null
  if (!Number.isFinite(done) || done < 0) return 0
  return Math.min(done / total, 1)
}

/**
 * The same mapping for the runners that report their phase by *name* rather than by ordinal — the
 * impulse-response runner, which is polled over REST rather than streamed, so its status carries
 * `ResponsePhaseName` instead of the wire's enum.
 */
export function responseRunToneByName(phase: ResponsePhaseName): RunTone {
  switch (phase) {
    case 'Idle':
      return 'idle'
    case 'Finished':
      return 'ok'
    case 'Cancelled':
      return 'cancelled'
    case 'Failed':
      return 'error'
    case 'WaitingForLoadChange':
      return 'waiting'
    default:
      return 'busy'
  }
}

/**
 * The word beside the dot, and the detail that follows the bar — split out of the one progress
 * string each runner already produces, so the bar can show the mockup's short coloured state word
 * without any runner having to grow a second label.
 *
 * The runners' strings are all "<what> · <where>" ("Point 7/31 · 1.26 kHz", "Measuring · pass 2 ·
 * record 1 of 4", "Finished · 50 points"), so the first segment is the headline and the rest is the
 * detail. For every state but "working" the headline is fixed instead — a finished sweep says
 * "Finished · 50 points" and the count belongs after the bar, not in the word.
 */
export function statusHeadline(tone: RunTone, progressLabel: string): string {
  switch (tone) {
    case 'idle':
      return 'Idle'
    case 'ok':
      return 'Finished'
    case 'cancelled':
      return 'Cancelled'
    case 'error':
      return 'Failed'
    default:
      return progressLabel.split(' · ')[0]?.trim() || 'Running'
  }
}

/** Everything the headline left behind, ready to print after the bar. Empty when there is none. */
export function statusDetail(tone: RunTone, progressLabel: string): string {
  if (tone === 'idle') return ''
  const parts = progressLabel.split(' · ')
  // A finished or cancelled run keeps its whole string as detail, because its headline is fixed and
  // its first segment ("Finished", "Stopped after 14 of 50 points") is where the count lives.
  if (tone === 'ok' || tone === 'cancelled' || tone === 'error') {
    const rest = parts[0]?.trim() === statusHeadline(tone, progressLabel) ? parts.slice(1) : parts
    return rest.join(' · ').trim()
  }
  return parts.slice(1).join(' · ').trim()
}
