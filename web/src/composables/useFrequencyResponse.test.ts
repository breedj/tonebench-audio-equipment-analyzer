import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type FrequencyResponseStatus } from '../api/client'
import { useFrequencyResponse } from './useFrequencyResponse'
import {
  ResponsePhase,
  ResponseReferenceMode,
  ResponseTraceKind,
  STREAM_FLAG_CROSSTALK,
  STREAM_FLAG_STEPPED_RESPONSE,
  StreamChannel,
  StreamMsgType,
  type FrequencyResponseFrame,
  type FrequencyResponseProgressFrame,
} from '../streaming/protocol'

/**
 * Two measurements, two states. They share every component that draws them, which is exactly why
 * sharing the state as well was wrong: running one put its curves on the other's screen. `target`
 * says which state the exported refs read; a live frame says which state it belongs to, through the
 * origin flag the server stamps on it.
 */

function status(overrides: Partial<FrequencyResponseStatus> = {}): FrequencyResponseStatus {
  return { ...emptyStatus(), ...overrides }
}

function emptyStatus(): FrequencyResponseStatus {
  return {
    running: false,
    phase: 'Idle',
    continuous: false,
    passesCompleted: 0,
    averagesTaken: 0,
    averageCount: 0,
    generatorAmpDbv: 0,
    binHz: 0,
    sampleRateHz: 0,
    fftSize: 0,
    latencySamples: null,
    error: null,
    traces: [],
    pointsMeasured: 0,
    pointCount: 0,
  }
}

/**
 * A progress frame's header carries the origin flag, which is what decides the state it lands in —
 * and, with it, whether its (taken, total) pair counts records or points.
 */
function progress(
  fields: Omit<FrequencyResponseProgressFrame, 'header'>,
  stepped: boolean | 'crosstalk' = false,
): FrequencyResponseProgressFrame {
  return {
    header: {
      msgType: StreamMsgType.FrequencyResponseProgress,
      channel: StreamChannel.Left,
      flags: originFlag(stepped),
      seq: fields.phase,
      x0: fields.generatorAmpDbv,
      dx: fields.total,
      nPoints: fields.taken,
    },
    ...fields,
  }
}

function originFlag(origin: boolean | 'crosstalk'): number {
  if (origin === 'crosstalk') return STREAM_FLAG_CROSSTALK
  return origin ? STREAM_FLAG_STEPPED_RESPONSE : 0
}

/** One live curve, with the same origin flag on its header. */
function traceFrame(kind: ResponseTraceKind, peakDb: number, stepped: boolean | 'crosstalk' = false): FrequencyResponseFrame {
  const bandwidth = {
    dropDb: 3,
    lowerHz: 20,
    upperHz: 20000,
    bandwidthHz: 19980,
    lowerClamped: false,
    upperClamped: false,
  }

  return {
    header: {
      msgType: StreamMsgType.FrequencyResponse,
      channel: kind as unknown as StreamChannel,
      flags: originFlag(stepped),
      seq: 0,
      x0: 20,
      dx: 20000,
      nPoints: 2,
    },
    kind,
    startFreqHz: 20,
    endFreqHz: 20000,
    freqHz: Float32Array.from([20, 20000]),
    magnitudeMinDb: Float32Array.from([0, 0]),
    magnitudeMaxDb: Float32Array.from([0, 0]),
    phaseDeg: Float32Array.from([0, 0]),
    coherence: Float32Array.from([1, 1]),
    oneDb: { ...bandwidth, dropDb: 1, symmetric: true },
    threeDb: { ...bandwidth, symmetric: false },
    peakDb,
    peakFreqHz: 1000,
    referenceDb: peakDb,
    referenceMode: ResponseReferenceMode.BandMedian,
    referenceFellBackToPeak: false,
  }
}

describe('useFrequencyResponse', () => {
  // Both states are module-level, so each test has to put them back. Done by applying an empty
  // status to each rather than by poking the refs: that is the same path a real reset takes.
  beforeEach(async () => {
    vi.restoreAllMocks()
    const response = useFrequencyResponse()

    vi.spyOn(api, 'getFrequencyResponseStatus').mockResolvedValue(status())
    vi.spyOn(api, 'getSteppedResponseStatus').mockResolvedValue(status())
    vi.spyOn(api, 'getCrosstalkStatus').mockResolvedValue(status())
    response.setTarget('Crosstalk')
    await response.refresh()
    response.setTarget('Stepped')
    await response.refresh()
    response.setTarget('Chirp')
    await response.refresh()

    vi.restoreAllMocks()
  })

  it('polls the chirp runner by default and the stepped runner once retargeted', async () => {
    const chirp = vi.spyOn(api, 'getFrequencyResponseStatus').mockResolvedValue(status())
    const stepped = vi.spyOn(api, 'getSteppedResponseStatus').mockResolvedValue(status())

    const response = useFrequencyResponse()
    await response.refresh()
    expect(chirp).toHaveBeenCalledTimes(1)
    expect(stepped).not.toHaveBeenCalled()

    response.setTarget('Stepped')
    await response.refresh()
    expect(stepped).toHaveBeenCalledTimes(1)
    expect(chirp).toHaveBeenCalledTimes(1)
  })

  it('routes cancel and clear to the targeted runner', async () => {
    const chirpCancel = vi.spyOn(api, 'cancelFrequencyResponse').mockResolvedValue(status())
    const steppedCancel = vi.spyOn(api, 'cancelSteppedResponse').mockResolvedValue(status())
    const chirpClear = vi.spyOn(api, 'clearFrequencyResponse').mockResolvedValue(status())
    const steppedClear = vi.spyOn(api, 'clearSteppedResponse').mockResolvedValue(status())

    const response = useFrequencyResponse()
    response.setTarget('Stepped')
    await response.cancel()
    await response.clear()

    expect(steppedCancel).toHaveBeenCalledTimes(1)
    expect(steppedClear).toHaveBeenCalledTimes(1)
    expect(chirpCancel).not.toHaveBeenCalled()
    expect(chirpClear).not.toHaveBeenCalled()
  })

  it('keeps one measurement’s curves off the other’s screen', () => {
    // The reported bug: run one measurement and its curves appeared on the other. Both states are
    // module-level and every component that draws them is shared, so nothing but the routing keeps
    // them apart.
    const response = useFrequencyResponse()

    response.ingestTrace(traceFrame(ResponseTraceKind.Gain, -1.5, true))

    response.setTarget('Chirp')
    expect(response.traces.value).toEqual([])

    response.setTarget('Stepped')
    expect(response.traces.value.map((t) => t.peakDb)).toEqual([-1.5])
  })

  it('routes a live frame by its own origin, not by the screen that is open', () => {
    // A stepped run is minutes long, so switching away to look at an old chirp result mid-run is
    // ordinary. Its frames must keep landing in the stepped state rather than painting the chart in
    // front of you.
    const response = useFrequencyResponse()
    response.setTarget('Chirp')

    response.ingestTrace(traceFrame(ResponseTraceKind.Gain, -2.5, true))
    response.ingestProgress(progress({ taken: 9, total: 40, phase: ResponsePhase.Sweeping, generatorAmpDbv: -20 }, true))

    expect(response.traces.value).toEqual([])
    expect(response.running.value).toBe(false)

    response.setTarget('Stepped')
    expect(response.traces.value.map((t) => t.peakDb)).toEqual([-2.5])
    expect(response.pointsMeasured.value).toBe(9)
    expect(response.running.value).toBe(true)
  })

  it('lands a crosstalk-flagged frame in the crosstalk state and nowhere else', () => {
    const response = useFrequencyResponse()
    response.setTarget('Stepped')

    response.ingestTrace(traceFrame(ResponseTraceKind.CrosstalkLeftToRight, -64, 'crosstalk'))
    response.ingestProgress(progress({ taken: 5, total: 80, phase: ResponsePhase.Sweeping, generatorAmpDbv: -20 }, 'crosstalk'))

    expect(response.traces.value).toEqual([])
    expect(response.running.value).toBe(false)
    response.setTarget('Chirp')
    expect(response.traces.value).toEqual([])

    response.setTarget('Crosstalk')
    expect(response.traces.value.map((t) => t.kind)).toEqual(['CrosstalkLeftToRight'])
    expect(response.pointsMeasured.value).toBe(5)
    expect(response.pointCount.value).toBe(80)
    expect(response.running.value).toBe(true)
  })

  it('routes the crosstalk verbs to the crosstalk runner', async () => {
    const statusSpy = vi.spyOn(api, 'getCrosstalkStatus').mockResolvedValue(status())
    const cancel = vi.spyOn(api, 'cancelCrosstalk').mockResolvedValue(status())
    const clear = vi.spyOn(api, 'clearCrosstalk').mockResolvedValue(status())
    const stepped = vi.spyOn(api, 'cancelSteppedResponse').mockResolvedValue(status())

    const response = useFrequencyResponse()
    response.setTarget('Crosstalk')
    await response.refresh()
    await response.cancel()
    await response.clear()

    expect(statusSpy).toHaveBeenCalledTimes(1)
    expect(cancel).toHaveBeenCalledTimes(1)
    expect(clear).toHaveBeenCalledTimes(1)
    expect(stepped).not.toHaveBeenCalled()
  })

  it('counts a two-pass crosstalk run over both passes and names the pass in the label', async () => {
    vi.spyOn(api, 'startCrosstalk').mockResolvedValue({
      passCount: 2,
      pointCount: 40,
      frequenciesHz: [],
      sampleRateHz: 192000,
      averageCount: 1,
      estimatedSeconds: 100,
    })

    const response = useFrequencyResponse()
    response.setTarget('Crosstalk')
    await response.startCrosstalk({
      startFreqHz: 20,
      endFreqHz: 20000,
      stepsPerOctave: 4,
      direction: 'Both',
      levelMode: 'GeneratorVoltage',
      levelValue: -20,
      levelUnit: 'Dbv',
      loadOhms: 8,
      averageCount: 1,
      adaptiveResolution: true,
    })
    expect(response.pointCount.value).toBe(80)
    expect(response.running.value).toBe(true)

    response.ingestProgress(progress({ taken: 2, total: 80, phase: ResponsePhase.Sweeping, generatorAmpDbv: -20 }, 'crosstalk'))
    expect(response.progressLabel.value).toBe('Pass 1 of 2 (L→R) · point 3 of 40')

    response.ingestProgress(progress({ taken: 40, total: 80, phase: ResponsePhase.Sweeping, generatorAmpDbv: -20 }, 'crosstalk'))
    expect(response.progressLabel.value).toBe('Pass 2 of 2 (R→L) · point 1 of 40')

    response.ingestProgress(progress({ taken: 80, total: 80, phase: ResponsePhase.Sweeping, generatorAmpDbv: -20 }, 'crosstalk'))
    expect(response.progressLabel.value).toBe('Pass 2 of 2 (R→L) · point 40 of 40')
  })

  it('applies a status to the targeted measurement alone', async () => {
    vi.spyOn(api, 'getSteppedResponseStatus').mockResolvedValue(
      status({ phase: 'Finished', pointsMeasured: 30, pointCount: 30, generatorAmpDbv: -12 }),
    )

    const response = useFrequencyResponse()
    response.setTarget('Stepped')
    await response.refresh()
    expect(response.generatorAmpDbv.value).toBe(-12)

    response.setTarget('Chirp')
    expect(response.generatorAmpDbv.value).toBe(0)
    expect(response.pointCount.value).toBe(0)
  })

  it('reads a progress frame as points when stepped and as records when chirping', () => {
    const response = useFrequencyResponse()

    response.ingestProgress(progress({ taken: 3, total: 8, phase: ResponsePhase.Measuring, generatorAmpDbv: -20 }))
    response.ingestProgress(progress({ taken: 12, total: 50, phase: ResponsePhase.Sweeping, generatorAmpDbv: -20 }, true))

    response.setTarget('Chirp')
    expect(response.averagesTaken.value).toBe(3)
    expect(response.averageCount.value).toBe(8)
    expect(response.pointCount.value).toBe(0)

    response.setTarget('Stepped')
    expect(response.pointsMeasured.value).toBe(12)
    expect(response.pointCount.value).toBe(50)
    expect(response.averagesTaken.value).toBe(0)
  })

  it('ignores a probe frame, whose counts are neither records nor points', () => {
    // Both probes reuse this frame with their own pass counts. Taking their total would rewrite the
    // readout with a probe's numbers and leave it wrong for the rest of the run.
    const response = useFrequencyResponse()
    response.setTarget('Stepped')
    response.ingestProgress(progress({ taken: 7, total: 50, phase: ResponsePhase.Sweeping, generatorAmpDbv: -20 }, true))

    response.ingestProgress(progress({ taken: 1, total: 5, phase: ResponsePhase.DeterminingLevel, generatorAmpDbv: -30 }, true))

    expect(response.pointsMeasured.value).toBe(7)
    expect(response.pointCount.value).toBe(50)
    // The phase and the generator level do follow it — those are what the probe is reporting.
    expect(response.phase.value).toBe(ResponsePhase.DeterminingLevel)
    expect(response.generatorAmpDbv.value).toBe(-30)
    expect(response.running.value).toBe(true)
  })

  it('counts a stepped run as running while it sweeps', () => {
    const response = useFrequencyResponse()
    response.setTarget('Stepped')

    response.ingestProgress(progress({ taken: 4, total: 20, phase: ResponsePhase.Sweeping, generatorAmpDbv: 0 }, true))
    expect(response.running.value).toBe(true)

    response.ingestProgress(progress({ taken: 20, total: 20, phase: ResponsePhase.Finished, generatorAmpDbv: 0 }, true))
    expect(response.running.value).toBe(false)
  })

  it('counts points rather than records in the progress label', () => {
    const response = useFrequencyResponse()
    response.setTarget('Stepped')

    response.ingestProgress(progress({ taken: 11, total: 50, phase: ResponsePhase.Sweeping, generatorAmpDbv: 0 }, true))
    expect(response.progressLabel.value).toBe('Point 12 of 50')

    // The last point is not "point 51 of 50" while it is still being measured.
    response.ingestProgress(progress({ taken: 50, total: 50, phase: ResponsePhase.Sweeping, generatorAmpDbv: 0 }, true))
    expect(response.progressLabel.value).toBe('Point 50 of 50')
  })

  it('mentions the averages per point only when there is more than one', async () => {
    vi.spyOn(api, 'getSteppedResponseStatus').mockResolvedValue(
      status({ phase: 'Sweeping', running: true, averagesTaken: 4, averageCount: 4, pointsMeasured: 2, pointCount: 30 }),
    )

    const response = useFrequencyResponse()
    response.setTarget('Stepped')
    await response.refresh()

    expect(response.progressLabel.value).toBe('Point 3 of 30 · 4 averages')
  })

  it('says how much of a cancelled stepped run survived', async () => {
    vi.spyOn(api, 'cancelSteppedResponse').mockResolvedValue(
      status({ phase: 'Cancelled', pointsMeasured: 14, pointCount: 50 }),
    )

    const response = useFrequencyResponse()
    response.setTarget('Stepped')
    await response.cancel()

    expect(response.progressLabel.value).toBe('Stopped after 14 of 50 points')
  })

  it('derives the phase kind from the request a stepped run was started with', async () => {
    // Nothing polls status during a run, so the panel would otherwise be gated on the settings of
    // the *previous* one.
    vi.spyOn(api, 'startSteppedResponse').mockResolvedValue({
      pointCount: 10,
      frequenciesHz: [],
      sampleRateHz: 48000,
      averageCount: 1,
      estimatedSeconds: 10,
    })

    const response = useFrequencyResponse()
    response.setTarget('Stepped')

    const base = {
      startFreqHz: 20,
      endFreqHz: 20000,
      stepsPerOctave: 5,
      levelMode: 'GeneratorVoltage' as const,
      levelValue: -20,
      levelUnit: 'Dbv' as const,
      loadOhms: 8,
      generateLeft: true,
      averageCount: 1,
      adaptiveResolution: true,
      bandwidthReference: 'BandMedian' as const,
      bandwidthReferenceLowHz: 200,
      bandwidthReferenceHighHz: 2000,
    }

    await response.startStepped({ ...base, generateRight: true, rightChannelIsReference: true })
    expect(response.phaseKind.value).toBe('DutResponse')
    expect(response.rightChannelIsReference.value).toBe(true)

    await response.startStepped({ ...base, generateRight: true, rightChannelIsReference: false })
    expect(response.phaseKind.value).toBe('ChannelDifference')
    expect(response.rightChannelIsReference.value).toBe(false)

    // No stimulus on the right input, so there is nothing to compare the left against.
    await response.startStepped({ ...base, generateRight: false, rightChannelIsReference: false })
    expect(response.phaseKind.value).toBe('None')
  })

  it('leaves the chirp reporting a per-trace phase', async () => {
    vi.spyOn(api, 'startFrequencyResponse').mockResolvedValue({
      sampleRateHz: 48000,
      fftSize: 32768,
      binHz: 1.46,
      averageCount: 1,
      recordSeconds: 0.68,
      continuous: false,
    })

    const response = useFrequencyResponse()
    response.setTarget('Chirp')

    await response.start({
      frequencySpanHz: 24000,
      resolutionMultiplier: 1,
      levelMode: 'GeneratorVoltage',
      levelValue: -20,
      levelUnit: 'Dbv',
      loadOhms: 8,
      rightChannelIsReference: true,
      smoothingDenominator: 24,
      averageCount: 1,
      chirpStartHz: 2,
      bandwidthReference: 'BandMedian',
      bandwidthReferenceLowHz: 200,
      bandwidthReferenceHighHz: 2000,
      continuous: false,
    })

    expect(response.phaseKind.value).toBe('PerTrace')
    expect(response.rightChannelIsReference.value).toBe(true)
  })
})
