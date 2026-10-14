import type { ProgrammableLoadStatus } from '../api/client'

/**
 * What the status bar's load box says, and what colour it says it in.
 *
 * Pure, and not inside the component, for the usual reason here: nothing in a `.vue` is testable
 * under the node environment vitest runs in, and every line below is a rule the hardware forced
 * rather than a formatting preference — see `docs/qa451-protocol.md`.
 */

/** How worried the box looks. */
export type LoadTone =
  /** Selected but not connected, or the device reported something wrong. */
  | 'fault'
  /** Connected and working, but something is worth saying: hot, or old firmware. */
  | 'warn'
  /** Connected with a load engaged. */
  | 'engaged'
  /** Connected, nothing engaged. */
  | 'idle'

export interface LoadReadout {
  /** What the hardware calls itself, or a generic name before it has said. */
  model: string
  /** `Open` / `4 Ω` / `8 Ω` / `?`. */
  impedance: string
  /** `DUT on` / `DUT off` / `DUT ?`, or empty when there is nothing to say yet. */
  dutPower: string
  /** `43 / 45 °C`, or null when no reading has been taken. */
  temperature: string | null
  /** `0.10 A`, or null when no reading has been taken. */
  current: string | null
  tone: LoadTone
  /** The box's tooltip: the whole state in a sentence or two. */
  title: string
}

/** One Tailwind class string per tone. Kept beside the tone union so neither can drift. */
export const LOAD_TONE_CLASS: Record<LoadTone, string> = {
  fault: 'border-red-300 text-red-700 dark:border-red-900 dark:text-red-300',
  warn: 'border-amber-300 text-amber-800 dark:border-amber-900 dark:text-amber-300',
  engaged: 'border-emerald-300 text-emerald-800 dark:border-emerald-900 dark:text-emerald-300',
  idle: 'border-neutral-300 text-neutral-700 dark:border-neutral-700 dark:text-neutral-200',
}

/**
 * Ohms as the front panel labels them. An unrecognised value is still printed rather than dropped:
 * `supportedImpedances` comes from the device, and a build that has not heard of a rung should not
 * silently hide it.
 */
export function formatLoadImpedance(ohms: number | null): string {
  if (ohms === null) return '?'
  return ohms === 0 ? 'Open' : `${ohms} Ω`
}

/**
 * Two decimals, never more. The reading is quantised to about 48.5 mA, so a third decimal would be
 * precision the hardware does not have — every value it can report is a multiple of roughly 0.05 A.
 */
export function formatLoadCurrent(amps: number): string {
  return `${amps.toFixed(2)} A`
}

/** Whole degrees, both channels, in the order the device reports them. */
export function formatLoadTemperatures(leftC: number, rightC: number): string {
  return `${Math.round(leftC)} / ${Math.round(rightC)} °C`
}

export function loadReadout(status: ProgrammableLoadStatus | null): LoadReadout {
  const model = status?.model ?? 'Programmable load'
  const reading = status?.reading ?? null

  const notes: string[] = []
  let tone: LoadTone = 'idle'

  if (status === null || status.kind === 'None') {
    // The box is not rendered in this state at all — the Configuration setting gates it — but the
    // function still answers, so a caller cannot get an undefined back.
    return {
      model,
      impedance: '—',
      dutPower: '',
      temperature: null,
      current: null,
      tone: 'idle',
      title: 'No programmable load selected. Choose one in Configuration ▸ Acquisition.',
    }
  }

  if (!status.connected) {
    return {
      model,
      impedance: '—',
      dutPower: '',
      temperature: null,
      current: null,
      tone: 'fault',
      title:
        status.error ??
        'The selected programmable load is not connected. Open this box and press Connect.',
    }
  }

  if (status.impedanceOhms !== null && status.impedanceOhms !== 0) {
    tone = 'engaged'
    notes.push(`${formatLoadImpedance(status.impedanceOhms)} across the DUT's output.`)
  } else if (status.impedanceOhms === null) {
    notes.push('The engaged load has not been read back yet, so it is not known.')
  } else {
    notes.push('No load engaged.')
  }

  if (status.dutPowerOn === true) notes.push("The DUT's supply is switched on.")

  if (status.firmwareSupported === false) {
    tone = 'warn'
    notes.push(
      `Firmware ${status.firmwareVersion} is below the ${status.requiredFirmwareVersion} ` +
        "QuantAsylum's own application expects. Tonebench never flashes a QA451.",
    )
  }

  if (reading?.overTemperature) {
    tone = 'warn'
    notes.push('Over temperature — above 60 °C the device disconnects its loads until it cools.')
  }

  // Last, so it wins the tone: a latched fault is the only state nothing on screen can clear.
  if (reading?.requiresReplug) {
    tone = 'fault'
    notes.push(
      'Over-current has latched in the hardware. No command clears it — unplug the load’s USB ' +
        'cable, plug it back in, and press Connect.',
    )
  } else if (reading?.overCurrent) {
    tone = 'fault'
    notes.push('Over-current reported.')
  }

  return {
    model,
    impedance: formatLoadImpedance(status.impedanceOhms),
    dutPower: status.dutPowerOn === null ? 'DUT ?' : status.dutPowerOn ? 'DUT on' : 'DUT off',
    temperature: reading ? formatLoadTemperatures(reading.leftTemperatureC, reading.rightTemperatureC) : null,
    current: reading ? formatLoadCurrent(reading.currentA) : null,
    tone,
    title: `${notes.join(' ')} Click to open the load's controls.`,
  }
}
