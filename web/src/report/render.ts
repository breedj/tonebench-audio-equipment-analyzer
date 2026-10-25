import { ref } from 'vue'
import { api, type DecaySurface, type ImpulseBurstDecayRequest, type ImpulseCsdRequest } from '../api/client'
import { CHART_EXPORT_HEIGHT_PX, CHART_EXPORT_WIDTH_PX, type ChartExportOptions } from '../charts/exportChart'
import { csdBlockShiftSamples } from '../measurements/decay'
import DecaySurfaceChart from '../components/DecaySurfaceChart.vue'
import FrequencyResponseChart from '../components/FrequencyResponseChart.vue'
import ImpedanceChart from '../components/ImpedanceChart.vue'
import ImpedancePlaneChart from '../components/ImpedancePlaneChart.vue'
import ImpulseHarmonicsChart from '../components/ImpulseHarmonicsChart.vue'
import ImpulseResponseChart from '../components/ImpulseResponseChart.vue'
import OscilloscopeChart from '../components/OscilloscopeChart.vue'
import ResponsePolarChart from '../components/ResponsePolarChart.vue'
import ResponseSPlaneChart from '../components/ResponseSPlaneChart.vue'
import SpectrumChart from '../components/SpectrumChart.vue'
import ThdSweepChart from '../components/ThdSweepChart.vue'
import { monochromeChartPng } from '../charts/exportChart'
import { SPECTRUM_DISPLAY_KEY } from '../composables/useChartDisplay'
import type { OverlayEntry } from '../composables/useMeasurements'
import { SWEEP_POINTS_KEY } from '../composables/useSweep'
import { BURST_FREQUENCY_COLUMNS, DECAY_DISPLAY_COLUMNS } from '../measurements/decay'
import { responseChartFlags } from '../response/display'
import { sweepChartWindow } from '../sweep/display'
import { sweepEndFreqHzFromParams } from '../sweep/points'
import { sweepXLabel } from '../sweep/xAxis'
import type { FrequencyResponseParameters } from '../api/client'
import type { ReportElementId } from './elements'
import { renderChartPng, type ChartProvision } from './renderChart'
import { subjectLabel, type ReportSubject } from './types'
import { reportViewsForKind } from './views'

/**
 * One chart view of one (or, overlaid, several) measurements as a PNG — the app's own chart
 * component rendered headless with the subject's curves and display state, see renderChart.ts. The
 * two charts that read a shared store for their data or display state are handed the subject's
 * through a `provide` under the key they inject.
 */

const SIZE = { width: CHART_EXPORT_WIDTH_PX, height: CHART_EXPORT_HEIGHT_PX }

export interface RenderedChart {
  png: Blob
  caption: string
}

/**
 * A stored subject as the overlay entry the charts draw history from; null for the live one, which
 * is always a chart's primary curve and never an overlay (see buildReport).
 */
export function toOverlayEntry(subject: ReportSubject): OverlayEntry | null {
  if (!subject.detail || subject.resultId === null) return null
  const data = subject.data
  return {
    id: subject.resultId,
    detail: subject.detail,
    visible: true,
    sweepPoints: data.kind === 'sweep' ? data.points : undefined,
    sweepMethod: data.kind === 'sweep' ? data.method : undefined,
    sweepFamily: data.kind === 'sweep' ? data.family : undefined,
    sweepEndFreqHz: data.kind === 'sweep' ? sweepEndFreqHzFromParams(subject.paramsJson) : undefined,
    responseTraces: data.kind === 'response' || data.kind === 'impulse' || data.kind === 'crosstalk' ? data.traces : undefined,
    impulseTrace: data.kind === 'impulse' ? data.impulse : undefined,
    impedanceTraces: data.kind === 'impedance' ? data.traces : data.kind === 'outputImpedance' && data.trace ? [data.trace] : undefined,
  }
}

/** Whether the subject holds anything its charts could draw — what enables the Chart element in the dialog. */
export function subjectHasChartData(subject: ReportSubject): boolean {
  const data = subject.data
  switch (data.kind) {
    case 'response':
      return data.traces.length > 0
    case 'spectrum':
      return Object.keys(data.spectrum).length > 0
    case 'sweep':
      return data.points.length > 0
    case 'impulse':
      return data.impulse !== null || data.traces.length > 0
    case 'impedance':
      return data.traces.length > 0
    case 'crosstalk':
      return data.traces.length > 0
    case 'outputImpedance':
      return data.trace !== null
    default:
      return false
  }
}

/** Whether a view of this kind can share one chart between several measurements. */
export function viewIsCombinable(kind: string, view: string): boolean {
  return reportViewsForKind(kind).find((v) => v.id === view)?.combinable ?? false
}

function exportOptions(primaryLabel: string | null, monochrome: boolean): ChartExportOptions {
  return { ...SIZE, primaryLabel: primaryLabel ?? undefined, monochrome }
}

/**
 * The FFT chapter's two secondary charts, when their elements are chosen and the subject has them:
 * the oscilloscope (rendered headless from the subject's scope frames, like any chart) and the
 * spectrogram, which only the instance on screen can draw — its picture is the history it collected
 * frame by frame, and a fresh instance would have one row. Neither is combinable, so they are
 * per subject; a stored result has neither, and simply gets none.
 */
export async function renderExtraCharts(
  subject: ReportSubject,
  elements: readonly ReportElementId[],
  monochrome: boolean,
): Promise<RenderedChart[]> {
  const data = subject.data
  if (data.kind !== 'spectrum') return []
  const charts: RenderedChart[] = []
  const options = exportOptions(null, monochrome)

  if (elements.includes('oscilloscope') && Object.keys(data.scope).length > 0) {
    const png = await renderChartPng(
      OscilloscopeChart,
      {
        scope: data.scope,
        residual: data.scopeResidual ?? {},
        status: data.status,
        annotations: data.display.annotations.value,
        annotationNumbers: elements.includes('annotations'),
      },
      options,
    )
    charts.push({ png, caption: 'Oscilloscope' })
  }

  if (elements.includes('spectrogram') && data.spectrogram) {
    const png = await data.spectrogram.exportPng({ ...SIZE, captureZoom: false })
    charts.push({ png: monochrome ? await monochromeChartPng(png) : png, caption: 'Spectrogram' })
  }

  return charts
}

/**
 * Renders `view` of `primary`, with `overlays` (stored subjects only) drawn as history, or null when
 * the primary has nothing for that view. `primaryLabel` names the primary curve in the legend when
 * there is anything to tell it apart from.
 */
/**
 * The pole-zero fit order a result was saved with, or null for Auto — which is also what a result
 * written before the orders were stored reads as.
 */
function storedFitOrder(paramsJson: string | null): { poles: number; zeros: number } | null {
  if (!paramsJson) return null
  try {
    const stored = JSON.parse(paramsJson) as FrequencyResponseParameters
    if (stored.chartFitAutoOrder !== false) return null
    if (typeof stored.chartFitPoles !== 'number' || typeof stored.chartFitZeros !== 'number') return null
    return { poles: stored.chartFitPoles, zeros: stored.chartFitZeros }
  } catch {
    return null
  }
}

export async function renderView(
  view: string,
  primary: ReportSubject,
  overlays: readonly ReportSubject[],
  primaryLabel: string | null,
  monochrome = false,
  /**
   * Whether the ordinal badges are drawn on the labels. Off when the chapter's Annotations table was
   * not chosen: a number on the picture would tie a label to a row nobody printed. An argument
   * rather than a flag written onto the subject's display, because only the FFT's display is a bag
   * of refs that could carry one.
   */
  annotationNumbers = true,
): Promise<RenderedChart | null> {
  const data = primary.data
  const overlayEntries = overlays.map(toOverlayEntry).filter((o): o is OverlayEntry => o !== null)
  const options = exportOptions(primaryLabel, monochrome)

  switch (data.kind) {
    case 'response': {
      if (data.traces.length === 0) return null
      const { traces, display, phaseKind, rightChannelIsReference, latencySamples } = data
      const flags = responseChartFlags(display, { kind: primary.kind, phaseKind, rightChannelIsReference, hasTraces: true })
      if (view === 'Bode') {
        const png = await renderChartPng(
          FrequencyResponseChart,
          { traces, overlays: overlayEntries, ...display, ...flags, phaseKind, annotationView: 'Bode', annotationNumbers },
          options,
        )
        return { png, caption: display.view === 'Gain' ? 'Gain against frequency' : 'Input level against frequency' }
      }
      if (view === 'Nyquist' || view === 'Nichols' || view === 'PoleZero') {
        const png = await renderChartPng(
          ResponsePolarChart,
          {
            traces,
            overlays: overlayEntries,
            annotations: display.annotations,
            annotationNumbers,
            projection: view,
            radiusMode: display.nyquistRadius,
            radiusFloorDb: display.nyquistRadiusFloorDb,
            // The stored axis windows, so a chapter is the chart the screen showed rather than a
            // re-fit of it — the same reasoning as `fitOrder` below.
            nicholsAutoGain: display.nicholsAutoGain,
            nicholsTopDb: display.nicholsTopDb,
            nicholsBottomDb: display.nicholsBottomDb,
            poleZeroAutoWindow: display.poleZeroAutoWindow,
            poleZeroXMinHz: display.poleZeroXMinHz,
            poleZeroXMaxHz: display.poleZeroXMaxHz,
            poleZeroYMinHz: display.poleZeroYMinHz,
            poleZeroYMaxHz: display.poleZeroYMaxHz,
            invertSign: display.invertLoopSign,
            restoreDelay: display.restoreDelay,
            latencySamples,
            sampleRateHz: primary.sampleRateHz,
            phaseKind,
            xFromHz: display.xFromHz,
            xToHz: display.xToHz,
            thickLines: display.thickLines,
            showDataPoints: display.showDataPoints,
            showFittedModel: display.showFittedModel,
            hideOutOfBandRoots: display.hideOutOfBandRoots,
            scale: display.surfaceScale,
            // No panel exists for a report, so the chart fits its own (see effectiveFit) — at the
            // order this measurement was saved with when it pinned one, and automatically otherwise.
            fit: null,
            fitOrder: storedFitOrder(primary.paramsJson),
          },
          options,
        )
        return { png, caption: view === 'Nyquist' ? 'Nyquist plot' : view === 'Nichols' ? 'Nichols plot' : 'Poles and zeros of the fitted model' }
      }
      if (view === 'SPlane') {
        const png = await renderChartPng(
          ResponseSPlaneChart,
          {
            traces,
            overlays: overlayEntries,
            annotations: display.annotations,
            annotationNumbers,
            mode: display.surfaceMode,
            thickLines: display.thickLines,
            scale: display.surfaceScale,
            surfaceTopDb: display.surfaceTopDb,
            surfaceBottomDb: display.surfaceBottomDb,
            palette: display.surfacePalette,
            showContours: display.surfaceContours,
            hideOutOfBandRoots: display.hideOutOfBandRoots,
            invertSign: display.invertLoopSign,
            restoreDelay: display.restoreDelay,
            latencySamples,
            sampleRateHz: primary.sampleRateHz,
            phaseKind,
            xFromHz: display.xFromHz,
            xToHz: display.xToHz,
            // As above: no panel, so the chart fits its own at the stored order.
            fit: null,
            fitOrder: storedFitOrder(primary.paramsJson),
          },
          options,
        )
        return { png, caption: 'Magnitude of the fitted model over the s-plane' }
      }
      return null
    }

    case 'spectrum': {
      if (Object.keys(data.spectrum).length === 0 || view !== 'Spectrum') return null
      const provision: ChartProvision = { key: SPECTRUM_DISPLAY_KEY, value: data.display }
      const png = await renderChartPng(
        SpectrumChart,
        { spectrum: data.spectrum, meters: data.meters, multitone: data.multitone ?? {}, overlays: overlayEntries },
        options,
        [provision],
      )
      return { png, caption: 'Spectrum' }
    }

    case 'sweep': {
      if (data.points.length === 0 || view !== 'Sweep') return null
      const { display } = data
      const window = sweepChartWindow(display, primary.kind)
      const provision: ChartProvision = {
        key: SWEEP_POINTS_KEY,
        value: { points: ref(data.points), thdMethod: ref(data.method) },
      }
      const png = await renderChartPng(
        ThdSweepChart,
        {
          annotations: display.annotations,
          annotationNumbers,
          unit: display.unit,
          showNoiseFloor: display.showNoiseFloor,
          showMagnitude: display.showMagnitude,
          showThd: display.showThd,
          showD2: display.showD2,
          showD3: display.showD3,
          showD4: display.showD4,
          showD5: display.showD5,
          showD6Plus: display.showD6Plus,
          showLeft: display.showLeft,
          showRight: display.showRight,
          thickLines: display.thickLines,
          showDataPoints: display.showDataPoints,
          yMaxPercent: display.yMaxPercent,
          yMinPercent: display.yMinPercent,
          yTopDb: display.yTopDb,
          yBottomDb: display.yBottomDb,
          xFrom: window.xFrom,
          xTo: window.xTo,
          xAxis: window.xAxis,
          // Which stored runs may be drawn under this one -- its own kind's, see overlayScope.ts.
          kind: primary.kind,
          overlays: overlayEntries,
          selectedLeft: null,
          selectedRight: null,
          overlaySelections: [],
          traceFamily: data.family,
        },
        options,
        [provision],
      )
      const what = data.family === 'imd' ? 'Intermodulation distortion' : 'Distortion'
      return { png, caption: `${what} against ${sweepXLabel(window.xAxis).toLowerCase()}` }
    }

    case 'impulse': {
      const { impulse, traces, harmonics, display } = data
      switch (view) {
        case 'Impulse': {
          if (!impulse) return null
          const png = await renderChartPng(
            ImpulseResponseChart,
            {
              impulse,
              overlays: overlayEntries,
              annotations: display.annotations,
              annotationNumbers,
              normalised: display.normalised,
              thickLines: display.thickLines,
              timeFromMs: display.timeFromMs,
              timeToMs: display.timeToMs,
              yTopDb: display.yTopDb,
              yBottomDb: display.yBottomDb,
            },
            options,
          )
          return { png, caption: display.normalised ? 'Impulse response, dB below the peak' : 'Impulse response' }
        }
        case 'Response': {
          if (traces.length === 0) return null
          const png = await renderChartPng(
            FrequencyResponseChart,
            {
              traces,
              overlays: overlayEntries,
              annotations: display.annotations,
              annotationView: 'Response',
              annotationNumbers,
              view: 'Gain',
              showLeft: true,
              showRight: false,
              showPhase: display.showPhase,
              showCoherence: display.showCoherence,
              showOneDb: false,
              showThreeDb: false,
              thickLines: display.thickLines,
              showDataPoints: false,
              yTopDb: display.responseYTopDb,
              yBottomDb: display.responseYBottomDb,
              yTopDbv: display.responseYTopDb,
              yBottomDbv: display.responseYBottomDb,
              xFromHz: display.freqFromHz,
              xToHz: display.freqToHz,
              phaseTopDeg: display.phaseTopDeg,
              phaseBottomDeg: display.phaseBottomDeg,
              delayView: display.delayView,
              delayTopUs: display.delayTopUs,
              delayBottomUs: display.delayBottomUs,
              phaseKind: 'PerTrace',
            },
            options,
          )
          return { png, caption: 'Gated frequency response' }
        }
        case 'Harmonics': {
          if (harmonics.length === 0) return null
          const png = await renderChartPng(
            ImpulseHarmonicsChart,
            {
              harmonics,
              overlays: overlayEntries,
              annotations: display.annotations,
              annotationNumbers,
              thickLines: display.thickLines,
              showDataPoints: display.showDataPoints,
              freqFromHz: display.freqFromHz,
              freqToHz: display.freqToHz,
              yTopDb: display.harmonicsYTopDb,
              yBottomDb: display.harmonicsYBottomDb,
            },
            options,
          )
          return { png, caption: 'Harmonic distortion against fundamental' }
        }
        case 'Csd':
        case 'BurstDecay': {
          if (!impulse) return null
          const surface = await decaySurface(primary, view)
          const png = await renderChartPng(
            DecaySurfaceChart,
            {
              surface,
              mode: display.decayMode,
              palette: display.decayPalette,
              coloredCurves: display.decayColoredCurves,
              rangeDb: display.decayRangeDb,
              thickLines: display.thickLines,
              computing: false,
              error: null,
              emptyMessage: '',
            },
            options,
          )
          return { png, caption: view === 'Csd' ? 'Cumulative spectral decay' : 'Burst decay' }
        }
        default:
          return null
      }
    }

    case 'impedance': {
      if (data.traces.length === 0) return null
      const { traces, display } = data
      if (view === 'Bode') {
        const png = await renderChartPng(
          ImpedanceChart,
          {
            traces,
            overlays: overlayEntries,
            annotations: display.annotations,
            annotationView: 'Bode',
            annotationNumbers,
            showPhase: display.showPhase,
            showCoherence: display.showCoherence,
            showShortCal: display.showShortCal,
            thickLines: display.thickLines,
            showDataPoints: display.showDataPoints,
            zTopOhms: display.zTopOhms,
            zBottomOhms: display.zBottomOhms,
            zScale: display.zScale,
            xFromHz: display.xFromHz,
            xToHz: display.xToHz,
            phaseTopDeg: display.phaseTopDeg,
            phaseBottomDeg: display.phaseBottomDeg,
          },
          options,
        )
        return { png, caption: 'Impedance against frequency' }
      }
      if (view === 'Nyquist') {
        const png = await renderChartPng(
          ImpedancePlaneChart,
          {
            traces,
            xFromHz: display.xFromHz,
            xToHz: display.xToHz,
            thickLines: display.thickLines,
            annotations: display.annotations,
            annotationNumbers,
          },
          options,
        )
        return { png, caption: 'Impedance locus' }
      }
      return null
    }

    case 'outputImpedance': {
      if (data.trace === null || view !== 'Bode') return null
      const { trace, display } = data
      const png = await renderChartPng(
        ImpedanceChart,
        {
          traces: [trace],
          overlays: overlayEntries,
          annotations: display.annotations,
          annotationView: 'Bode',
          annotationNumbers,
          showPhase: data.rightChannelIsReference && display.showPhase,
          showCoherence: false,
          showShortCal: false,
          thickLines: display.thickLines,
          showDataPoints: display.showDataPoints,
          zTopOhms: display.zTopOhms,
          zBottomOhms: display.zBottomOhms,
          zScale: display.zScale,
          xFromHz: display.xFromHz,
          xToHz: display.xToHz,
          phaseTopDeg: display.phaseTopDeg,
          phaseBottomDeg: display.phaseBottomDeg,
          showDampingFactor: display.showDampingFactor,
          dampingFactorNominalOhms: data.nominalOhms,
        },
        options,
      )
      return { png, caption: 'Output impedance and damping factor against frequency' }
    }

    case 'crosstalk': {
      if (data.traces.length === 0 || view !== 'Crosstalk') return null
      const { traces, display } = data
      // The response chart in its Crosstalk view, with the same constants App.vue hands the windows
      // that view never reads.
      const png = await renderChartPng(
        FrequencyResponseChart,
        {
          traces,
          overlays: overlayEntries,
          annotations: display.annotations,
          annotationView: 'Crosstalk',
          annotationNumbers,
          view: 'Crosstalk',
          showLeft: true,
          showRight: true,
          showPhase: false,
          showCoherence: false,
          showOneDb: false,
          showThreeDb: false,
          thickLines: display.thickLines,
          showDataPoints: display.showDataPoints,
          yTopDb: display.yTopDb,
          yBottomDb: display.yBottomDb,
          yTopDbv: 20,
          yBottomDbv: -140,
          xFromHz: display.xFromHz,
          xToHz: display.xToHz,
          phaseTopDeg: 180,
          phaseBottomDeg: -180,
          phaseKind: 'None',
        },
        options,
      )
      return { png, caption: 'Crosstalk against frequency' }
    }

    default:
      return null
  }
}

/**
 * A decay surface for the subject: the live runner's for what is on screen, the stored result's own
 * IR for a stored one — both analysed with the subject's settings, through the same DSP.
 */
async function decaySurface(subject: ReportSubject, view: 'Csd' | 'BurstDecay'): Promise<DecaySurface> {
  if (subject.data.kind !== 'impulse') throw new Error('Not an impulse response.')
  const { display } = subject.data
  const label = subjectLabel(subject)

  try {
    if (view === 'Csd') {
      // The span is in milliseconds and the server wants a shift in samples, so it is derived
      // here exactly as the screen derives it — see csdBlockShiftSamples.
      const request: ImpulseCsdRequest = {
        fftLength: display.csd.fftLength,
        blockShiftSamples: csdBlockShiftSamples(display.csd.spanMs, display.csd.slices, subject.data.impulse?.sampleRateHz ?? 0),
        blockCount: display.csd.slices,
        startBeforePeakMs: display.csd.startBeforePeakMs,
        windowMode: display.csd.windowMode,
        riseTimeMs: display.csd.riseTimeMs,
        lowHz: display.freqFromHz,
        highHz: display.freqToHz,
        displayColumns: DECAY_DISPLAY_COLUMNS,
      }
      return subject.resultId === null
        ? await api.computeImpulseCsd(request)
        : await api.computeStoredImpulseCsd(subject.resultId, request)
    }

    const request: ImpulseBurstDecayRequest = {
      lowHz: display.freqFromHz,
      highHz: display.freqToHz,
      maxPeriods: display.burstMaxPeriods,
      pointsPerPeriod: display.burstPointsPerPeriod,
      resolution: display.burstResolution,
      frequencyColumns: BURST_FREQUENCY_COLUMNS,
    }
    return subject.resultId === null
      ? await api.computeImpulseBurstDecay(request)
      : await api.computeStoredImpulseBurstDecay(subject.resultId, request)
  } catch (e) {
    throw new Error(`${view === 'Csd' ? 'Cumulative spectral decay' : 'Burst decay'} of ${label}: ${(e as Error).message}`)
  }
}
