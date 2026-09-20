import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type OutputImpedanceStatus } from '../api/client'
import {
  ImpedanceTraceKind,
  ResponsePhase,
  STREAM_FLAG_OUTPUT_IMPEDANCE,
  StreamChannel,
  StreamMsgType,
  type ImpedanceFrame,
  type ImpedanceProgressFrame,
} from '../streaming/protocol'
import { useOutputImpedance } from './useOutputImpedance'

function status(overrides: Partial<OutputImpedanceStatus> = {}): OutputImpedanceStatus {
  return {
    running: false,
    phase: 'Idle',
    passesCompleted: 0,
    pointsMeasured: 0,
    pointCount: 0,
    generatorAmpDbv: 0,
    inputRangeDbv: null,
    binHz: 0,
    sampleRateHz: 0,
    fftSize: 0,
    loadOhms: 8,
    secondLoadOhms: 4,
    nominalOhms: 8,
    rightChannelIsReference: false,
    error: null,
    warning: null,
    trace: null,
    medianOhms: null,
    zoutAt1kOhms: null,
    dampingFactorAt1k: null,
    firstGainAt1kDb: null,
    secondGainAt1kDb: null,
    ...overrides,
  }
}

function progress(taken: number, total: number, phase: ResponsePhase): ImpedanceProgressFrame {
  return {
    header: {
      msgType: StreamMsgType.ImpedanceProgress,
      channel: StreamChannel.Left,
      flags: STREAM_FLAG_OUTPUT_IMPEDANCE,
      seq: phase,
      x0: -20,
      dx: total,
      nPoints: taken,
    },
    taken,
    total,
    phase,
    generatorAmpDbv: -20,
  }
}

function traceFrame(): ImpedanceFrame {
  return {
    header: {
      msgType: StreamMsgType.Impedance,
      channel: ImpedanceTraceKind.Impedance as unknown as StreamChannel,
      flags: STREAM_FLAG_OUTPUT_IMPEDANCE,
      seq: 0,
      x0: 20,
      dx: 20000,
      nPoints: 2,
    },
    kind: ImpedanceTraceKind.Impedance,
    startFreqHz: 20,
    endFreqHz: 20000,
    freqHz: Float32Array.from([20, 20000]),
    magnitudeMinDbOhms: Float32Array.from([-31, -24]),
    magnitudeMaxDbOhms: Float32Array.from([-31, -24]),
    phaseDeg: Float32Array.from([0, 0]),
    coherence: Float32Array.from([1, 1]),
    peakDbOhms: -24,
    peakFreqHz: 20000,
  }
}

describe('useOutputImpedance', () => {
  beforeEach(async () => {
    vi.restoreAllMocks()
    vi.spyOn(api, 'getOutputImpedanceStatus').mockResolvedValue(status())
    await useOutputImpedance().refresh()
    vi.restoreAllMocks()
  })

  it('labels the first pass, the pause and the second pass from the cumulative point count', () => {
    const store = useOutputImpedance()
    vi.spyOn(api, 'getOutputImpedanceStatus').mockResolvedValue(status())

    store.ingestProgress(progress(2, 40, ResponsePhase.Sweeping))
    expect(store.running.value).toBe(true)
    expect(store.waiting.value).toBe(false)
    expect(store.progressLabel.value).toBe('First load · point 3 of 20')

    store.ingestProgress(progress(20, 40, ResponsePhase.WaitingForLoadChange))
    expect(store.running.value).toBe(true)
    expect(store.waiting.value).toBe(true)
    expect(store.passesCompleted.value).toBe(1)
    expect(store.progressLabel.value).toContain('CONTINUE')

    store.ingestProgress(progress(25, 40, ResponsePhase.Sweeping))
    expect(store.waiting.value).toBe(false)
    expect(store.progressLabel.value).toBe('Second load · point 6 of 20')

    store.ingestProgress(progress(40, 40, ResponsePhase.Finished))
    expect(store.running.value).toBe(false)
    expect(store.progressLabel.value).toBe('Finished · 20 points, both passes')
  })

  it('takes a flagged impedance frame as the Zout curve, in ohms-in-dB, as a single impedance trace', () => {
    const store = useOutputImpedance()
    store.ingestTrace(traceFrame())
    expect(store.trace.value?.kind).toBe('Impedance')
    expect(store.traces.value).toHaveLength(1)
    expect(store.trace.value?.freqHz).toEqual([20, 20000])
  })

  it('routes the verbs to the output-impedance runner, and continue to its continue endpoint', async () => {
    const start = vi.spyOn(api, 'startOutputImpedance').mockResolvedValue({
      passCount: 2, pointCount: 20, frequenciesHz: [], sampleRateHz: 48000, averageCount: 1, estimatedSeconds: 60,
    })
    const cont = vi.spyOn(api, 'continueOutputImpedance').mockResolvedValue(status({ running: true, phase: 'Sweeping', passesCompleted: 1 }))
    const cancel = vi.spyOn(api, 'cancelOutputImpedance').mockResolvedValue(status({ phase: 'Cancelled' }))
    const clear = vi.spyOn(api, 'clearOutputImpedance').mockResolvedValue(status())

    const store = useOutputImpedance()
    await store.start({
      startFreqHz: 20, endFreqHz: 20000, stepsPerOctave: 3, levelMode: 'OutputPower', levelValue: 1, levelUnit: 'Watt',
      loadOhms: 8, secondLoadOhms: 4, nominalOhms: 8, rightChannelIsReference: false, averageCount: 1, adaptiveResolution: true,
    })
    expect(start).toHaveBeenCalledTimes(1)
    expect(store.pointCount.value).toBe(40)
    expect(store.phase.value).toBe(ResponsePhase.DeterminingLevel)

    await store.continueRun()
    expect(cont).toHaveBeenCalledTimes(1)
    expect(store.passesCompleted.value).toBe(1)

    await store.cancel()
    expect(cancel).toHaveBeenCalledTimes(1)
    expect(store.phase.value).toBe(ResponsePhase.Cancelled)

    await store.clear()
    expect(clear).toHaveBeenCalledTimes(1)
  })

  it('reads the readings back off the status, where alone they live', async () => {
    vi.spyOn(api, 'getOutputImpedanceStatus').mockResolvedValue(
      status({ phase: 'Finished', medianOhms: 0.028, dampingFactorAt1k: 286, inputRangeDbv: 18, warning: null }),
    )
    const store = useOutputImpedance()
    await store.refresh()
    expect(store.medianOhms.value).toBe(0.028)
    expect(store.dampingFactorAt1k.value).toBe(286)
    expect(store.inputRangeDbv.value).toBe(18)
  })
})
