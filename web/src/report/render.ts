import { ref } from 'vue'
import { api, type DecaySurface, type ImpulseBurstDecayRequest, type ImpulseCsdRequest } from '../api/client'
import { CHART_EXPORT_HEIGHT_PX, CHART_EXPORT_WIDTH_PX, type ChartExportOptions } from '../charts/exportChart'
import DecaySurfaceChart from '../components/DecaySurfaceChart.vue'
import FrequencyResponseChart from '../components/FrequencyResponseChart.vue'
import ImpedanceChart from '../components/ImpedanceChart.vue'
import ImpedancePlaneChart from '../components/ImpedancePlaneChart.vue'
import ImpulseHarmonicsChart from '../components/ImpulseHarmonicsChart.vue'
import ImpulseResponseChart from '../components/ImpulseResponseChart.vue'
import OscilloscopeChart from '../components/OscilloscopeChart.vue'
import ResponsePolarChart from '../components/ResponsePolarChart.vue'
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
    impedanceTraces: data.kind === 'impedance' ? data.traces : undefined,
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
 * waterfall, which only the instance on screen can draw — its picture is the history it collected
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
    const png = await renderChartPng(OscilloscopeChart, { scope: data.scope, status: data.status }, options)
    charts.push({ png, caption: 'Oscilloscope' })
  }

  if (elements.includes('waterfall') && data.waterfall) {
    const png = await data.waterfall.exportPng({ ...SIZE, captureZoom: false })
    charts.push({ png: monochrome ? await monochromeChartPng(png) : png, caption: 'Waterfall' })
  }

  return charts
}

/**
 * Renders `view` of `primary`, with `overlays` (stored subjects only) drawn as history, or null when
 * the primary has nothing for that view. `primaryLabel` names the primary curve in the legend when
 * there is anything to tell it apart from.
 */
export async function renderView(
  view: string,
  primary: ReportSubject,
  overlays: readonly ReportSubject[],
  primaryLabel: string | null,
  monochrome = false,
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
          { traces, overlays: overlayEntries, ...display, ...flags, phaseKind },
          options,
        )
        return { png, caption: display.view === 'Gain' ? 'Gain against frequency' : 'Input level against frequency' }
      }
      if (view === 'Nyquist' || view === 'Nichols') {
        const png = await renderChartPng(
          ResponsePolarChart,
          {
            traces,
            overlays: overlayEntries,
            projection: view,
            radiusMode: display.nyquistRadius,
            radiusFloorDb: display.nyquistRadiusFloorDb,
            invertSign: display.invertLoopSign,
            restoreDelay: display.restoreDelay,
            latencySamples,
            sampleRateHz: primary.sampleRateHz,
            phaseKind,
            xFromHz: display.xFromHz,
            xToHz: display.xToHz,
            thickLines: display.thickLines,
            showDataPoints: display.showDataPoints,
            fittedModel: null,
          },
          options,
        )
        return { png, caption: view === 'Nyquist' ? 'Nyquist plot' : 'Nichols plot' }
      }
      return null
    }

    case 'spectrum': {
      if (Object.keys(data.spectrum).length === 0 || view !== 'Spectrum') return null
      const provision: ChartProvision = { key: SPECTRUM_DISPLAY_KEY, value: data.display }
      const png = await renderChartPng(
        SpectrumChart,
        { spectrum: data.spectrum, meters: data.meters, overlays: overlayEntries },
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
          { traces, xFromHz: display.xFromHz, xToHz: display.xToHz, thickLines: display.thickLines },
          options,
        )
        return { png, caption: 'Impedance locus' }
      }
      return null
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
      const request: ImpulseCsdRequest = {
        ...display.csd,
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
