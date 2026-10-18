import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type MeasurementDetail } from '../api/client'
import { useAnnotations } from './useAnnotations'
import type { ChartAnnotation } from '../measurements/chartAnnotations'

/**
 * The radio group that decides whose annotations are on the charts — the open measurement's, one
 * saved result's, or none — and the writes that follow from it. None of this was testable while it
 * was a bare ref; all of it matters, because the one thing a user must not be able to do by accident
 * is edit a stored result while believing they are editing the measurement.
 */

function note(id: number, text: string): ChartAnnotation {
  return {
    id,
    style: 'point',
    text,
    view: 'Spectrum',
    x: 1000,
    y: -40,
    x2: null,
    y2: null,
    xQuantity: 'hz',
    yQuantity: 'dbv',
    channel: 0,
    labelOffsetX: 0.45,
    labelOffsetY: 14,
  }
}

/** A stored result carrying `annotations` in its own params_json, as the detail endpoint answers. */
function detail(resultId: number, name: string, annotations: unknown): MeasurementDetail {
  return {
    resultId,
    kind: 'FFTSpectrum',
    name,
    paramsJson: JSON.stringify({ fftSize: 65536, chartAnnotations: annotations }),
  } as unknown as MeasurementDetail
}

describe('useAnnotations', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    // The store is a module-level singleton, so each test re-establishes it -- the idiom the
    // useTrash and useProjects tests use. applyLoadedLive is also what a measurement switch calls,
    // so this is the state a screen arrives in.
    useAnnotations().applyLoadedLive([])
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('shows the open measurement’s own notes to begin with, so nothing saved becomes invisible', () => {
    const a = useAnnotations()
    a.applyLoadedLive([note(1, 'hum')])

    expect(a.source.value).toBe('live')
    expect(a.shown.value.map((n) => n.text)).toEqual(['hum'])
    expect(a.sourceName.value).toBeNull()
  })

  it('turns the shown set off when its own icon is pressed again', async () => {
    const a = useAnnotations()
    a.applyLoadedLive([note(1, 'hum')])

    await a.show('live')

    expect(a.source.value).toBeNull()
    expect(a.shown.value).toEqual([])
    // Off, not lost: the measurement still has its note and lighting it again brings it back.
    expect(a.live.value.map((n) => n.text)).toEqual(['hum'])
  })

  it('reads a chosen result’s own stored notes and names it', async () => {
    const a = useAnnotations()
    a.applyLoadedLive([note(1, 'the measurement’s')])
    vi.spyOn(api, 'getMeasurement').mockResolvedValue(detail(7, 'run 3', [note(1, 'the result’s')]))

    await a.show(7)

    expect(a.source.value).toBe(7)
    expect(a.sourceName.value).toBe('run 3')
    expect(a.shown.value.map((n) => n.text)).toEqual(['the result’s'])
    // And the measurement's own are untouched behind it.
    expect(a.live.value.map((n) => n.text)).toEqual(['the measurement’s'])
  })

  it('writes an edit to whichever set is shown, never to the other', async () => {
    const a = useAnnotations()
    const write = vi.spyOn(api, 'setMeasurementAnnotations').mockResolvedValue(undefined)
    a.applyLoadedLive([note(1, 'the measurement’s')])

    // Shown = the measurement: the edit lands there and nothing is sent anywhere.
    a.shown.value = [note(1, 'retyped')]
    expect(a.live.value.map((n) => n.text)).toEqual(['retyped'])
    expect(write).not.toHaveBeenCalled()

    vi.spyOn(api, 'getMeasurement').mockResolvedValue(detail(7, 'run 3', []))
    await a.show(7)

    // Shown = a result: the edit lands on the result and the measurement keeps what it had.
    a.shown.value = [note(1, 'about the result')]
    expect(a.live.value.map((n) => n.text)).toEqual(['retyped'])
    await a.flushPendingWrite()
    expect(write).toHaveBeenCalledWith(7, [expect.objectContaining({ text: 'about the result' })])
  })

  it('collects a run of edits into one write, because typing a label is a keystroke each', async () => {
    vi.useFakeTimers()
    const a = useAnnotations()
    const write = vi.spyOn(api, 'setMeasurementAnnotations').mockResolvedValue(undefined)
    vi.spyOn(api, 'getMeasurement').mockResolvedValue(detail(7, 'run 3', []))
    await a.show(7)

    a.shown.value = [note(1, 'h')]
    a.shown.value = [note(1, 'hu')]
    a.shown.value = [note(1, 'hum')]
    expect(write).not.toHaveBeenCalled()

    await vi.runAllTimersAsync()

    expect(write).toHaveBeenCalledTimes(1)
    expect(write).toHaveBeenCalledWith(7, [expect.objectContaining({ text: 'hum' })])
  })

  it('sends what is owed before it switches away, or the last edit before a click is lost', async () => {
    vi.useFakeTimers()
    const a = useAnnotations()
    const write = vi.spyOn(api, 'setMeasurementAnnotations').mockResolvedValue(undefined)
    vi.spyOn(api, 'getMeasurement').mockResolvedValue(detail(7, 'run 3', []))
    await a.show(7)
    a.shown.value = [note(1, 'only just typed')]

    await a.show('live')

    expect(write).toHaveBeenCalledWith(7, [expect.objectContaining({ text: 'only just typed' })])
  })

  it('arms the measurement’s own set when nothing is shown, so Add can never place into the dark', () => {
    const a = useAnnotations()
    a.applyLoadedLive([note(1, 'hum')])
    a.source.value = null

    a.arm('point')

    expect(a.source.value).toBe('live')
    expect(a.armed.value).toBe('point')
    expect(a.shown.value.map((n) => n.text)).toEqual(['hum'])
  })

  it('leaves a result that is already shown alone when a chart is armed', async () => {
    const a = useAnnotations()
    vi.spyOn(api, 'getMeasurement').mockResolvedValue(detail(7, 'run 3', []))
    await a.show(7)

    a.arm('box')

    // Arming must not drag the selection back to the measurement: the user is looking at a result,
    // and the note they are about to place belongs to it.
    expect(a.source.value).toBe(7)
    expect(a.armed.value).toBe('box')
  })

  it('shows nothing, and says why, when a result’s notes cannot be read', async () => {
    const a = useAnnotations()
    a.applyLoadedLive([note(1, 'the measurement’s')])
    vi.spyOn(api, 'getMeasurement').mockRejectedValue(new Error('no such result'))

    await expect(a.show(7)).rejects.toThrow('no such result')

    expect(a.source.value).toBeNull()
    expect(a.error.value).toContain('no such result')
    // A failed read must not cost the measurement its own list.
    expect(a.live.value.map((n) => n.text)).toEqual(['the measurement’s'])
  })

  it('says so rather than throwing when a write fails, since nothing is waiting to catch it', async () => {
    const a = useAnnotations()
    vi.spyOn(api, 'getMeasurement').mockResolvedValue(detail(7, 'run 3', []))
    vi.spyOn(api, 'setMeasurementAnnotations').mockRejectedValue(new Error('database is locked'))
    await a.show(7)

    a.shown.value = [note(1, 'hum')]
    await a.flushPendingWrite()

    expect(a.error.value).toContain('database is locked')
  })

  it('puts the selection back on the measurement being opened', async () => {
    const a = useAnnotations()
    vi.spyOn(api, 'getMeasurement').mockResolvedValue(detail(7, 'run 3', [note(1, 'the result’s')]))
    await a.show(7)

    a.applyLoadedLive([note(1, 'another measurement’s')])

    // A result of the measurement being left behind must not stay lit over a different
    // measurement's curves, nor leave the panel editing a row the History list no longer shows.
    expect(a.source.value).toBe('live')
    expect(a.sourceName.value).toBeNull()
    expect(a.shown.value.map((n) => n.text)).toEqual(['another measurement’s'])
  })

  it('reads a result that stored no annotations as none, not as the measurement’s', async () => {
    const a = useAnnotations()
    a.applyLoadedLive([note(1, 'the measurement’s')])
    vi.spyOn(api, 'getMeasurement').mockResolvedValue(detail(7, 'older run', undefined))

    await a.show(7)

    expect(a.shown.value).toEqual([])
  })
})
