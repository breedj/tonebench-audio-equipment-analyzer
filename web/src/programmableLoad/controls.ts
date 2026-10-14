import type { ProgrammableLoadStatus } from '../api/client'

/**
 * Whether the load dialog's controls are pressable, and the sentence beside them when they are not.
 *
 * The branchiest part of the feature, so it lives here rather than in the dialog: nothing in a
 * `.vue` is testable here, and "which refusal wins" is exactly the kind of thing that rots quietly.
 */

/** Which command is in flight. One at a time — the session serialises them anyway. */
export type LoadCommand = 'connect' | 'kind' | 'impedance' | 'dutPower' | 'safeState'

export interface LoadImpedanceChoice {
  ohms: number
  label: string
}

export interface LoadControlState {
  /** Whether the load, DUT power and safe-state controls can be pressed. */
  enabled: boolean
  /** Why not, when not. Null when enabled — so "nothing is pressable" always has a reason beside it. */
  reason: string | null
  /** Whether to offer Connect. */
  showConnect: boolean
  /** The red strip's text when a fault has latched, else null. */
  replug: string | null
  /** The amber strip's text when the firmware is behind, else null. */
  firmware: string | null
  /** What the device says it can present, ready to render. */
  impedances: LoadImpedanceChoice[]
}

/**
 * The same sentence as `LoadEndpoints.RunningMessage`, duplicated here deliberately: the server
 * refuses with a 409 either way, and this app's rule is that a control which cannot work is
 * *visibly unavailable and says why* rather than live and then refused — see the status bar's
 * analyzer picker. The 409 stays the backstop for a run started in another tab.
 */
export const LOAD_RUNNING_REASON = 'A measurement is running — stop it before changing the load.'

function impedanceLabel(ohms: number): string {
  return ohms === 0 ? 'Open' : `${ohms} Ω`
}

export function loadControlState(
  status: ProgrammableLoadStatus | null,
  runnerActive: boolean,
  pending: LoadCommand | null,
): LoadControlState {
  const impedances: LoadImpedanceChoice[] = (status?.supportedImpedances ?? []).map((ohms) => ({
    ohms,
    label: impedanceLabel(ohms),
  }))

  const replug = status?.reading?.requiresReplug
    ? 'Over-current has latched in the QA451’s hardware. No command clears it — unplug its USB ' +
      'cable, plug it back in, and press Connect.'
    : null

  const firmware =
    status?.firmwareSupported === false
      ? `This load reports firmware ${status.firmwareVersion}, below the ` +
        `${status.requiredFirmwareVersion} QuantAsylum's own application expects for a ` +
        `${status.model ?? 'QA451'}. Tonebench never flashes it — use their application if an ` +
        'update is really wanted.'
      : null

  const base = { showConnect: false, replug, firmware, impedances }

  // Precedence, most general first. A measurement owns the bench, so it outranks everything: the
  // load is not the thing to fix while a sweep is mid-run.
  if (runnerActive) {
    return { ...base, enabled: false, reason: LOAD_RUNNING_REASON }
  }

  if (pending !== null) {
    return { ...base, enabled: false, reason: null }
  }

  if (status === null || status.kind === 'None') {
    return {
      ...base,
      enabled: false,
      reason: 'No programmable load is selected for this bench — choose one in Configuration ▸ Acquisition.',
    }
  }

  if (!status.connected) {
    return {
      ...base,
      enabled: false,
      showConnect: true,
      reason: status.error ?? 'No load is connected. Plug it in and press Connect.',
    }
  }

  // A latched fault deliberately leaves the controls enabled. The driver reports it rather than
  // enforcing it — nothing establishes that the firmware refuses commands while tripped — and Safe
  // state is the press a bench reaches for after a trip, so taking it away would be this app
  // inventing a rule the device does not have.
  return { ...base, enabled: true, reason: null }
}
