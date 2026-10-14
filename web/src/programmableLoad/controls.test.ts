import { describe, expect, it } from 'vitest'
import type { ProgrammableLoadReading, ProgrammableLoadStatus } from '../api/client'
import { LOAD_RUNNING_REASON, loadControlState } from './controls'

function reading(patch: Partial<ProgrammableLoadReading> = {}): ProgrammableLoadReading {
  return {
    leftTemperatureC: 24,
    rightTemperatureC: 24,
    currentA: 0,
    overTemperature: false,
    overCurrent: false,
    requiresReplug: false,
    rawStatusBits: 0x040,
    ...patch,
  }
}

function status(patch: Partial<ProgrammableLoadStatus> = {}): ProgrammableLoadStatus {
  return {
    kind: 'Qa451',
    connected: true,
    model: 'QA451B',
    firmwareVersion: 39,
    requiredFirmwareVersion: 39,
    firmwareSupported: true,
    usbSerial: 'C39491461B001B00',
    supportedImpedances: [0, 4, 8],
    impedanceOhms: 0,
    dutPowerOn: false,
    reading: reading(),
    error: null,
    ...patch,
  }
}

describe('loadControlState', () => {
  it('enables everything on a connected, healthy load', () => {
    const state = loadControlState(status(), false, null)

    expect(state.enabled).toBe(true)
    expect(state.reason).toBeNull()
    expect(state.showConnect).toBe(false)
    expect(state.replug).toBeNull()
    expect(state.firmware).toBeNull()
  })

  it('offers what the device says it has, labelled as the panel does', () => {
    const state = loadControlState(status({ supportedImpedances: [0, 4, 8] }), false, null)

    expect(state.impedances).toEqual([
      { ohms: 0, label: 'Open' },
      { ohms: 4, label: '4 Ω' },
      { ohms: 8, label: '8 Ω' },
    ])
  })

  it('does not hard-code three rungs', () => {
    const state = loadControlState(status({ supportedImpedances: [0, 2] }), false, null)

    expect(state.impedances.map((i) => i.label)).toEqual(['Open', '2 Ω'])
  })

  // A measurement owns the bench: changing the load under a running sweep would leave a curve whose
  // halves were measured against different DUT loading, with nothing in the result saying so.
  it('stands down while a measurement is running, and says so', () => {
    const state = loadControlState(status(), true, null)

    expect(state.enabled).toBe(false)
    expect(state.reason).toBe(LOAD_RUNNING_REASON)
  })

  it('outranks every other refusal while a measurement runs', () => {
    const state = loadControlState(status({ connected: false, kind: 'None' }), true, null)

    expect(state.reason).toBe(LOAD_RUNNING_REASON)
    expect(state.showConnect).toBe(false)
  })

  it('disables without a sentence while a command is in flight', () => {
    // The pressed control shows its own busy note; a second sentence would be noise.
    const state = loadControlState(status(), false, 'dutPower')

    expect(state.enabled).toBe(false)
    expect(state.reason).toBeNull()
  })

  it('sends a bench with no load selected to Configuration', () => {
    const state = loadControlState(status({ kind: 'None', connected: false, reading: null }), false, null)

    expect(state.enabled).toBe(false)
    expect(state.reason).toContain('Configuration ▸ Acquisition')
    expect(state.showConnect).toBe(false)
  })

  it('offers Connect with the server’s own reason when it is not connected', () => {
    const state = loadControlState(
      status({ connected: false, reading: null, error: 'No HID device found with VID 0x16c0, PID 0x4e35.' }),
      false,
      null,
    )

    expect(state.enabled).toBe(false)
    expect(state.showConnect).toBe(true)
    expect(state.reason).toBe('No HID device found with VID 0x16c0, PID 0x4e35.')
  })

  it('falls back to its own sentence when the server offered none', () => {
    const state = loadControlState(status({ connected: false, reading: null }), false, null)

    expect(state.reason).toContain('Connect')
  })

  /**
   * The driver reports a latched fault rather than enforcing it, and Safe state is the press a bench
   * reaches for after a trip — so the controls stay live and the strip explains the only remedy.
   */
  it('reports a latched over-current but leaves the controls pressable', () => {
    const state = loadControlState(
      status({ reading: reading({ overCurrent: true, requiresReplug: true }) }),
      false,
      null,
    )

    expect(state.enabled).toBe(true)
    expect(state.replug).toContain('USB')
    expect(state.replug).toContain('No command clears it')
  })

  it('names both firmware versions and says this app never flashes one', () => {
    const state = loadControlState(status({ firmwareVersion: 37, firmwareSupported: false }), false, null)

    expect(state.enabled).toBe(true)
    expect(state.firmware).toContain('37')
    expect(state.firmware).toContain('39')
    expect(state.firmware).toContain('never flashes')
  })

  it('reports the strips even while disabled, so a refusal does not hide a fault', () => {
    const state = loadControlState(
      status({ firmwareSupported: false, firmwareVersion: 37, reading: reading({ requiresReplug: true }) }),
      true,
      null,
    )

    expect(state.enabled).toBe(false)
    expect(state.replug).not.toBeNull()
    expect(state.firmware).not.toBeNull()
  })
})
