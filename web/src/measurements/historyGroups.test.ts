import { describe, expect, it } from 'vitest'
import type { MeasurementSummary } from '../api/client'
import { groupHistory, isSimulation, showsGroupHeaders } from './historyGroups'

function item(resultId: number, source: string | null, name = `r${resultId}`): MeasurementSummary {
  return {
    resultId,
    projectName: 'p',
    measurementTypeName: 't',
    name,
    notes: null,
    timestampUtc: new Date(2026, 9, resultId).toISOString(),
    sampleRateHz: 48000,
    fftSize: 8192,
    channels: 1,
    channelHeadlines: {},
    source,
    annotationCount: 0,
  }
}

describe('groupHistory', () => {
  it('splits measured runs from imported simulations', () => {
    const groups = groupHistory([item(1, null), item(2, 'simulation'), item(3, null)])

    expect(groups.map((g) => g.id)).toEqual(['measurements', 'simulations'])
    expect(groups[0]!.items.map((i) => i.resultId)).toEqual([1, 3])
    expect(groups[1]!.items.map((i) => i.resultId)).toEqual([2])
  })

  it('puts measurements first, whatever order the server listed them in', () => {
    const groups = groupHistory([item(1, 'simulation'), item(2, null)])

    expect(groups.map((g) => g.label)).toEqual(['Measurements', 'Simulations'])
  })

  it('keeps the order within a group, which is the server timestamp ordering', () => {
    const groups = groupHistory([item(9, null), item(4, null), item(7, null)])

    expect(groups[0]!.items.map((i) => i.resultId)).toEqual([9, 4, 7])
  })

  // A project that has never had a simulation imported -- which is every project until someone
  // does -- must look exactly as it did before this feature: no header, no empty section.
  it('omits a group with nothing in it', () => {
    expect(groupHistory([item(1, null)]).map((g) => g.id)).toEqual(['measurements'])
    expect(groupHistory([item(1, 'simulation')]).map((g) => g.id)).toEqual(['simulations'])
  })

  it('has no groups at all for an empty listing', () => {
    expect(groupHistory([])).toEqual([])
  })

  // Null is what every row an older build wrote carries, and it means measured -- see migration
  // 014. An unrecognised value is treated the same way rather than inventing a third section.
  it.each([null, '', 'measured', 'something-else'])('reads source %o as measured', (source) => {
    expect(isSimulation(item(1, source))).toBe(false)
  })

  it('reads only the simulation marker as a simulation', () => {
    expect(isSimulation(item(1, 'simulation'))).toBe(true)
  })
})

describe('showsGroupHeaders', () => {
  it('draws headers only when there is more than one section', () => {
    expect(showsGroupHeaders(groupHistory([item(1, null)]))).toBe(false)
    expect(showsGroupHeaders(groupHistory([item(1, 'simulation')]))).toBe(false)
    expect(showsGroupHeaders(groupHistory([item(1, null), item(2, 'simulation')]))).toBe(true)
    expect(showsGroupHeaders(groupHistory([]))).toBe(false)
  })
})
