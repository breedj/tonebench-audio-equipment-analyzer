import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type MeasurementDetail, type MeasurementType, type Project, type ProjectSession } from '../api/client'
import { useMeasurements } from './useMeasurements'
import { useProjects } from './useProjects'

const amp: Project = { id: 1, name: 'GM70 amp', notes: null, createdUtc: '2026-10-17T10:00:00Z' }

function type(id: number, name: string): MeasurementType {
  return { id, projectId: 1, kind: 'ThdVsFrequency', name, notes: null, paramsJson: '{}', sortOrder: id, createdUtc: '' }
}

function detail(resultId: number, measurementTypeId: number): MeasurementDetail {
  return {
    resultId,
    measurementTypeId,
    projectName: amp.name,
    measurementTypeName: `m${measurementTypeId}`,
    kind: 'ThdVsFrequency',
    name: `run ${resultId}`,
    notes: null,
    timestampUtc: '',
    paramsJson: '{}',
    sampleRateHz: 192000,
    fftSize: 131072,
    channels: 1,
  } as unknown as MeasurementDetail
}

/**
 * A checked history item outlives a measurement *switch* on purpose — that is the comparison the
 * overlay list is most useful for. It must not outlive the measurement itself: delete a
 * measurement with one of its results checked on, make a new one of the same kind, and the old
 * result was still being drawn, with no History row to uncheck it from (reported 2026-10-17).
 */
describe('useProjects: overlays of a measurement that is gone', () => {
  beforeEach(async () => {
    vi.restoreAllMocks()
    vi.spyOn(api, 'getProjectSession').mockResolvedValue({ open: true, path: 'C:\\P\\amp.tbench', project: amp } satisfies ProjectSession)
    vi.spyOn(api, 'listRecentProjects').mockResolvedValue([])
    await useProjects().refreshSession()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('are dropped when its type stops being in the project, and the others are kept', async () => {
    const measurements = useMeasurements()
    measurements.overlays.value = [
      { id: 10, detail: detail(10, 7), visible: true } as never,
      { id: 11, detail: detail(11, 8), visible: true } as never,
      { id: 12, detail: detail(12, 7), visible: false } as never,
    ]

    vi.spyOn(api, 'listMeasurementTypes').mockResolvedValue([type(8, 'still here')])
    await useProjects().loadTypes(1)

    expect(measurements.overlays.value.map((o) => o.id)).toEqual([11])
  })

  it('are all unchecked when a measurement is created, whichever measurement they belong to', async () => {
    const measurements = useMeasurements()
    measurements.overlays.value = [
      { id: 10, detail: detail(10, 7), visible: true } as never,
      { id: 11, detail: detail(11, 8), visible: false } as never,
    ]

    vi.spyOn(api, 'createMeasurementType').mockResolvedValue({ id: 9 })
    vi.spyOn(api, 'listMeasurementTypes').mockResolvedValue([type(7, 'one'), type(8, 'two'), type(9, 'new')])
    await useProjects().createMeasurementType(1, { kind: 'ThdVsFrequency', name: 'new', notes: null })

    // Not the prune's doing: every one of these belongs to a measurement that is still there.
    expect(measurements.overlays.value).toEqual([])
  })

  it('leaves the list object alone when nothing is dropped, so no chart redraws for free', async () => {
    const measurements = useMeasurements()
    measurements.overlays.value = [{ id: 11, detail: detail(11, 8), visible: true } as never]
    // Read back rather than kept: a ref hands out a reactive proxy, not the array that went in.
    const before = measurements.overlays.value

    vi.spyOn(api, 'listMeasurementTypes').mockResolvedValue([type(8, 'still here')])
    await useProjects().loadTypes(1)

    expect(measurements.overlays.value).toBe(before)
  })
})
