import type { DeviceStatus } from '../api/client'

/**
 * What the status bar's device pill and telemetry group say, and what colour they say it in.
 *
 * Pure, and not inside the component, for the usual reason here: nothing in a `.vue` is testable
 * under the node environment vitest runs in. What is worth pinning is which rows exist in which
 * state — a QA403 that has gone offline keeps its row and shows dashes, where an analyzer that has
 * no sensors at all has no row to dash — and the one threshold below, which is a judgement about
 * the hardware rather than a formatting choice.
 */

/**
 * Above this the board temperature is called out in amber. Not a fault and not the device's own
 * limit: a QA403 sitting on a bench reads about 25 °C, and the vendor's own software shows the
 * figure without ever colouring it. 45 °C is "this is warmer than a QA403 on a bench normally
 * runs" — worth a glance before a noise measurement is believed, since the analyzer's own floor
 * moves with it — rather than "stop". Nothing in the app acts on it.
 */
export const TELEMETRY_WARM_C = 45

/** The dash a reading that cannot be taken shows. An em dash, as the mockup draws it. */
export const TELEMETRY_DASH = '—'

export type TelemetryKey = 'serial' | 'firmware' | 'voltage' | 'current' | 'temperature'

export interface TelemetryRow {
  key: TelemetryKey
  /** The caption before the value, or `''` where the glyph alone says it (the three sensors). */
  label: string
  value: string
  /** The whole row in a sentence — the group has no room to explain itself. */
  title: string
  /** The 13 px glyph's `d`, drawn stroked. */
  icon: string
  /** Tailwind text colour for the glyph. */
  iconClass: string
  /** Tailwind classes for the value, so a reading worth noticing can say so on its own. */
  valueClass: string
}

/**
 * The glyphs, from the mockup. Stroked rather than filled, at stroke-width 2.2, so they read at
 * 13 px against the bar's 12 px text.
 */
const ICON = {
  serial: 'M4 7V5a1 1 0 0 1 1-1h2M17 4h2a1 1 0 0 1 1 1v2M20 17v2a1 1 0 0 1-1 1h-2M7 20H5a1 1 0 0 1-1-1v-2M8 8v8M12 8v8M16 8v8',
  firmware: 'M4 4h16v16H4zM9 9h6v6H9zM9 1v3M15 1v3M9 20v3M15 20v3M1 9h3M1 15h3M20 9h3M20 15h3',
  voltage: 'M13 2 4 14h7l-1 8 9-12h-7z',
  current: 'M3 12h4l3-8 4 16 3-8h4',
  temperature: 'M14 14.8V4a2 2 0 1 0-4 0v10.8a4 4 0 1 0 4 0z',
} as const

/** The ordinary value colour — the bar's own text, not the muted caption beside it. */
const VALUE = 'text-neutral-900 dark:text-neutral-100'
const VALUE_WARM = 'font-semibold text-amber-700 dark:text-amber-400'

/**
 * The bar's identity and sensor readings, in the vendor software's order, or an empty array when
 * this analyzer has nothing of the sort to report.
 *
 * Two separate gates, because the two halves answer differently. **Identity** (serial, firmware) is
 * whatever the connected device called itself, so it is shown for any connected analyzer. **The
 * three sensors** are the QA403's own housekeeping — its USB supply rail, what it draws, and how
 * warm it has become — and an analyzer that does not measure them must not show dashes for
 * something it was never going to answer. A QA403 selected but *offline* keeps both halves and
 * dashes them: the row is what the bar's width is laid out around, and a group that vanishes the
 * moment a cable is pulled moves every control beside it.
 *
 * Three sensors where the vendor's status bar has four: its ISO Current reads `---` on a QA403, and
 * the register behind it answers a constant (4626, i.e. 0x1212) on this model rather than a
 * current, so there is nothing to show.
 */
export function telemetryRows(status: DeviceStatus | null): TelemetryRow[] {
  if (!status) return []

  // The real analyzer, whether or not it is answering right now. The simulator is deliberately not
  // included although it models a QA403 elsewhere in this bar: it has no sensors, so there is
  // nothing for a dash to stand in for. QA40x, because the kind covers the QA402 as well, whose
  // 0x11/0x12/0x16 sensors are the same three.
  const isQa40x = status.deviceKind === 'Hardware'
  const telemetry = status.telemetry

  const rows: TelemetryRow[] = []

  if (status.connected || isQa40x) {
    rows.push({
      key: 'serial',
      label: 'S/N',
      value: status.connected ? status.serialNumber : TELEMETRY_DASH,
      title: 'Serial number',
      icon: ICON.serial,
      iconClass: 'text-neutral-400 dark:text-neutral-500',
      valueClass: VALUE,
    })
    rows.push({
      key: 'firmware',
      label: 'FW',
      value: status.connected ? status.firmwareVersion : TELEMETRY_DASH,
      title: 'Firmware version',
      icon: ICON.firmware,
      iconClass: 'text-sky-600 dark:text-sky-400',
      valueClass: VALUE,
    })
  }

  if (telemetry || isQa40x) {
    rows.push({
      key: 'voltage',
      label: '',
      value: telemetry ? `${telemetry.usbVoltageV.toFixed(3)} V` : TELEMETRY_DASH,
      title: 'USB supply voltage',
      icon: ICON.voltage,
      iconClass: 'text-amber-600 dark:text-amber-400',
      valueClass: VALUE,
    })
    rows.push({
      key: 'current',
      label: '',
      value: telemetry ? `${telemetry.usbCurrentMa.toFixed(0)} mA` : TELEMETRY_DASH,
      title: 'USB supply current',
      icon: ICON.current,
      iconClass: 'text-violet-600 dark:text-violet-400',
      valueClass: VALUE,
    })
    const warm = telemetry != null && telemetry.temperatureC >= TELEMETRY_WARM_C
    rows.push({
      key: 'temperature',
      label: '',
      value: telemetry ? `${telemetry.temperatureC.toFixed(1)} °C` : TELEMETRY_DASH,
      title: warm
        ? `Board temperature — above ${TELEMETRY_WARM_C} °C, which is warmer than this analyzer normally runs. Its own noise floor moves with it.`
        : 'Board temperature',
      icon: ICON.temperature,
      iconClass: warm ? 'text-amber-500' : 'text-orange-600 dark:text-orange-400',
      valueClass: warm ? VALUE_WARM : VALUE,
    })
  }

  return rows
}

/** Connected, selected but not answering, or gone while we were using it. */
export type DeviceTone = 'connected' | 'offline' | 'removed'

export interface DevicePill {
  /** What the device calls itself once it has answered, otherwise what the Analyzer dropdown calls it. */
  name: string
  /** The one word beside it. */
  state: string
  tone: DeviceTone
  title: string
}

/**
 * The pill at the left-hand end: a dot, a name and a word. `kindLabel` is what the Analyzer
 * dropdown calls the *selected* analyzer, used before one has answered — a disconnected QA403 has
 * no `productName` to print, and an empty pill reads as a rendering fault rather than as an
 * offline device.
 */
export function devicePill(status: DeviceStatus | null, kindLabel: string): DevicePill {
  if (!status) {
    return {
      name: kindLabel,
      state: 'starting…',
      tone: 'offline',
      title: 'Waiting for the first status read.',
    }
  }
  if (status.connected) {
    return {
      name: status.productName,
      state: 'connected',
      tone: 'connected',
      title: 'The analyzer is open and answering.',
    }
  }
  if (status.deviceLost) {
    return {
      name: kindLabel,
      state: 'removed',
      tone: 'removed',
      title:
        'The analyzer stopped responding — it was unplugged or powered off. Plug it back in and press Connect.',
    }
  }
  return {
    name: kindLabel,
    state: 'offline',
    tone: 'offline',
    title: 'No analyzer is open. Press Connect to look for one.',
  }
}

/** One class string per tone, kept beside the union so neither can drift. */
export const DEVICE_DOT_CLASS: Record<DeviceTone, string> = {
  connected: 'bg-emerald-500 ring-emerald-100 dark:ring-emerald-900/60',
  offline: 'bg-neutral-400 ring-neutral-200 dark:ring-neutral-700',
  removed: 'bg-red-500 ring-red-100 dark:ring-red-900/60',
}
