import { describe, expect, it } from 'vitest'
import type { DeviceStatus } from '../api/client'
import { devicePill, TELEMETRY_DASH, TELEMETRY_WARM_C, telemetryRows, type TelemetryKey } from './statusReadout'

function status(over: Partial<DeviceStatus> = {}): DeviceStatus {
  return {
    connected: true,
    productName: 'QA403',
    serialNumber: '1077527945',
    firmwareVersion: '60',
    deviceKind: 'Hardware',
    deviceLost: false,
    telemetry: { usbVoltageV: 4.93, usbCurrentMa: 850, temperatureC: 25 },
    ...over,
  } as DeviceStatus
}

function keys(s: DeviceStatus | null): TelemetryKey[] {
  return telemetryRows(s).map((row) => row.key)
}

function value(s: DeviceStatus, key: TelemetryKey): string {
  return telemetryRows(s).find((row) => row.key === key)?.value ?? ''
}

describe('telemetryRows', () => {
  it('reads a connected QA403 the way the vendor software does', () => {
    expect(keys(status())).toEqual(['serial', 'firmware', 'voltage', 'current', 'temperature'])
    expect(value(status(), 'voltage')).toBe('4.930 V')
    expect(value(status(), 'current')).toBe('850 mA')
    expect(value(status(), 'temperature')).toBe('25.0 °C')
  })

  // The row is what the bar's width is laid out around: a group that vanishes when the cable comes
  // out would move every control beside it, so an offline QA403 keeps its row and dashes it.
  it('keeps every row on an offline QA403, with dashes', () => {
    const offline = status({ connected: false, telemetry: null })
    expect(keys(offline)).toEqual(['serial', 'firmware', 'voltage', 'current', 'temperature'])
    expect(telemetryRows(offline).map((row) => row.value)).toEqual(Array(5).fill(TELEMETRY_DASH))
  })

  // The opposite case, and the reason the two halves are gated separately: an analyzer with no
  // sensors must not dash three readings it was never going to take.
  it('gives an analyzer with no sensors no sensor rows at all', () => {
    const simulator = status({ deviceKind: 'Simulated', productName: 'Simulated QA403', telemetry: null })
    expect(keys(simulator)).toEqual(['serial', 'firmware'])

    const card = status({ deviceKind: 'SoundCard', productName: 'MOTU M4', telemetry: null })
    expect(keys(card)).toEqual(['serial', 'firmware'])
  })

  it('shows nothing at all before the first status read, or for a disconnected sound card', () => {
    expect(telemetryRows(null)).toEqual([])
    expect(keys(status({ deviceKind: 'SoundCard', connected: false, telemetry: null }))).toEqual([])
  })

  it('calls out a board running warm, and only on the temperature', () => {
    const warm = status({ telemetry: { usbVoltageV: 4.93, usbCurrentMa: 850, temperatureC: 48.6 } })
    const rows = telemetryRows(warm)
    const temperature = rows.find((row) => row.key === 'temperature')!
    expect(temperature.value).toBe('48.6 °C')
    expect(temperature.valueClass).toContain('amber')
    expect(temperature.title).toContain(String(TELEMETRY_WARM_C))
    expect(rows.filter((row) => row.valueClass.includes('amber'))).toHaveLength(1)
  })

  it('leaves a board exactly at the threshold alone one tenth below it', () => {
    const at = status({ telemetry: { usbVoltageV: 5, usbCurrentMa: 0, temperatureC: TELEMETRY_WARM_C } })
    const below = status({ telemetry: { usbVoltageV: 5, usbCurrentMa: 0, temperatureC: TELEMETRY_WARM_C - 0.1 } })
    expect(telemetryRows(at).find((row) => row.key === 'temperature')!.valueClass).toContain('amber')
    expect(telemetryRows(below).find((row) => row.key === 'temperature')!.valueClass).not.toContain('amber')
  })
})

describe('devicePill', () => {
  it('names the device itself once it has answered', () => {
    expect(devicePill(status(), 'QA403')).toMatchObject({ name: 'QA403', state: 'connected', tone: 'connected' })
  })

  // An empty pill reads as a rendering fault rather than as an offline device, and a disconnected
  // analyzer has no productName to print.
  it('falls back to the dropdown’s name for an analyzer that has not answered', () => {
    expect(devicePill(status({ connected: false, productName: '' }), 'Sound card')).toMatchObject({
      name: 'Sound card',
      state: 'offline',
      tone: 'offline',
    })
    expect(devicePill(null, 'QA403').name).toBe('QA403')
  })

  it('distinguishes never-attached from unplugged-while-we-were-using-it', () => {
    expect(devicePill(status({ connected: false }), 'QA403').tone).toBe('offline')
    expect(devicePill(status({ connected: false, deviceLost: true }), 'QA403')).toMatchObject({
      state: 'removed',
      tone: 'removed',
    })
  })
})
