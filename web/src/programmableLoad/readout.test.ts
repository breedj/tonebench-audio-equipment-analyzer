import { describe, expect, it } from 'vitest'
import type { ProgrammableLoadReading, ProgrammableLoadStatus } from '../api/client'
import {
  LOAD_TONE_CLASS,
  formatLoadCurrent,
  formatLoadImpedance,
  formatLoadTemperatures,
  loadReadout,
  type LoadTone,
} from './readout'

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

describe('formatLoadImpedance', () => {
  it('labels the three rungs the way the front panel does', () => {
    expect(formatLoadImpedance(0)).toBe('Open')
    expect(formatLoadImpedance(4)).toBe('4 Ω')
    expect(formatLoadImpedance(8)).toBe('8 Ω')
  })

  // Null is a real state — a connect that skipped the reset leaves it there until a poll reads the
  // relays back — and reading it as Open would claim the DUT is unloaded when nobody knows.
  it('says unknown rather than Open when the state has not been read back', () => {
    expect(formatLoadImpedance(null)).toBe('?')
    expect(formatLoadImpedance(null)).not.toBe('Open')
  })

  it('still prints a rung this build has not heard of', () => {
    expect(formatLoadImpedance(2)).toBe('2 Ω')
  })
})

describe('formatLoadCurrent', () => {
  // The reading is quantised to ~48.5 mA, so a third decimal is precision the hardware does not
  // have. This is the guard for that: every value it can report rounds to two.
  it('never prints more than two decimals', () => {
    for (const amps of [0, 0.0485, 0.097, 0.1455, 1.2345, 9.87654]) {
      const text = formatLoadCurrent(amps)
      expect(text).toMatch(/^\d+\.\d{2} A$/)
    }
  })

  it('formats the figures the bench actually produced', () => {
    expect(formatLoadCurrent(0)).toBe('0.00 A')
    expect(formatLoadCurrent(0.097)).toBe('0.10 A')
    expect(formatLoadCurrent(0.146)).toBe('0.15 A')
  })
})

describe('formatLoadTemperatures', () => {
  it('shows both channels in whole degrees', () => {
    expect(formatLoadTemperatures(43, 45)).toBe('43 / 45 °C')
  })

  it('rounds rather than printing decimals the device cannot report', () => {
    expect(formatLoadTemperatures(23.4, 26.6)).toBe('23 / 27 °C')
  })
})

describe('loadReadout', () => {
  it('answers for a status that has not arrived yet without throwing', () => {
    const readout = loadReadout(null)

    expect(readout.impedance).toBe('—')
    expect(readout.temperature).toBeNull()
    expect(readout.title).toContain('Configuration')
  })

  it('points at Configuration when no load is selected', () => {
    const readout = loadReadout(status({ kind: 'None', connected: false, model: null }))

    expect(readout.model).toBe('Programmable load')
    expect(readout.title).toContain('Configuration ▸ Acquisition')
  })

  it('carries the server’s own sentence when it is selected but not connected', () => {
    const readout = loadReadout(
      status({ connected: false, reading: null, error: 'No HID device found with VID 0x16c0, PID 0x4e35.' }),
    )

    expect(readout.tone).toBe('fault')
    expect(readout.title).toBe('No HID device found with VID 0x16c0, PID 0x4e35.')
  })

  it('reads idle with nothing engaged and engaged with a load across the output', () => {
    expect(loadReadout(status({ impedanceOhms: 0 })).tone).toBe('idle')

    const engaged = loadReadout(status({ impedanceOhms: 8, dutPowerOn: true }))
    expect(engaged.tone).toBe('engaged')
    expect(engaged.impedance).toBe('8 Ω')
    expect(engaged.dutPower).toBe('DUT on')
  })

  it('shows the readings as the device reports them', () => {
    const readout = loadReadout(
      status({ reading: reading({ leftTemperatureC: 43, rightTemperatureC: 45, currentA: 0.097 }) }),
    )

    expect(readout.temperature).toBe('43 / 45 °C')
    expect(readout.current).toBe('0.10 A')
  })

  it('says the state is unknown rather than claiming it is open', () => {
    const readout = loadReadout(status({ impedanceOhms: null, dutPowerOn: null }))

    expect(readout.impedance).toBe('?')
    expect(readout.dutPower).toBe('DUT ?')
    expect(readout.title).toContain('not been read back')
  })

  it('warns about an over-temperature and names the threshold', () => {
    const readout = loadReadout(status({ reading: reading({ overTemperature: true }) }))

    expect(readout.tone).toBe('warn')
    expect(readout.title).toContain('60 °C')
  })

  // The only state nothing on screen can clear, so it outranks every other note.
  it('reports a latched over-current as a fault and names the cable as the remedy', () => {
    const readout = loadReadout(
      status({ reading: reading({ overCurrent: true, requiresReplug: true, overTemperature: true }) }),
    )

    expect(readout.tone).toBe('fault')
    expect(readout.title).toContain('USB')
  })

  it('warns about firmware below what the vendor application expects, with both versions', () => {
    const readout = loadReadout(status({ firmwareVersion: 37, firmwareSupported: false }))

    expect(readout.tone).toBe('warn')
    expect(readout.title).toContain('37')
    expect(readout.title).toContain('39')
    expect(readout.title).toContain('never flashes')
  })
})

describe('LOAD_TONE_CLASS', () => {
  // The same completeness check pages.test.ts gives the manual's group tones: a tone added to the
  // union without a class would render an unstyled box.
  it('has a class for every tone', () => {
    const tones: LoadTone[] = ['fault', 'warn', 'engaged', 'idle']

    for (const tone of tones) {
      expect(LOAD_TONE_CLASS[tone]).toBeTruthy()
    }

    expect(Object.keys(LOAD_TONE_CLASS).sort()).toEqual([...tones].sort())
  })
})
