<script setup lang="ts">
import { computed, nextTick, reactive, ref, watch } from 'vue'
import { api, type SimulatedProfileRequest, type StagedMeasurementConfig, type ThdSweepPoint } from './api/client'
import { useDevice } from './composables/useDevice'
import { useSpectrumStream } from './composables/useSpectrumStream'
import { useMeasurements, type OverlayEntry } from './composables/useMeasurements'
import { useProjects } from './composables/useProjects'
import { useSpectrumConfig } from './composables/useSpectrumConfig'
import { useMetricSelection } from './composables/useMetricSelection'
import { useScopeDisplay } from './composables/useScopeDisplay'
import { useChartDisplay } from './composables/useChartDisplay'
import { useGeneratorStimulus } from './composables/useGeneratorStimulus'
import { useCaptureDelay } from './composables/useCaptureDelay'
import { determineAttenuation, nearestInputRange } from './attenuation'
import { formatAveraging } from './metrics'
import { stopRun } from './deviceControl'
import DeviceStatusBar from './components/DeviceStatusBar.vue'
import MeasurementSidebar from './components/MeasurementSidebar.vue'
import ProjectPanel from './components/ProjectPanel.vue'
import GeneratorPanel from './components/GeneratorPanel.vue'
import SpectrumConfigPanel from './components/SpectrumConfigPanel.vue'
import SpectrumChart from './components/SpectrumChart.vue'
import WaterfallChart from './components/WaterfallChart.vue'
import OscilloscopeChart from './components/OscilloscopeChart.vue'
import MetricsPanel from './components/MetricsPanel.vue'
import GeneratorMetricsPanel from './components/GeneratorMetricsPanel.vue'
import HarmonicsTable from './components/HarmonicsTable.vue'
import OtherPeaksTable from './components/OtherPeaksTable.vue'
import MeasurementHistoryPanel from './components/MeasurementHistoryPanel.vue'
import MeasurementChecklist from './components/MeasurementChecklist.vue'
import ChartDisplayPanel from './components/ChartDisplayPanel.vue'
import SaveMeasurementDialog from './components/SaveMeasurementDialog.vue'
import SaveAsDialog from './components/SaveAsDialog.vue'
import OverlayList from './components/OverlayList.vue'
import MetricPickerDialog from './components/MetricPickerDialog.vue'
import ConfigDialog from './components/ConfigDialog.vue'
import ManualDialog from './manual/ManualDialog.vue'
import { useManual } from './manual/useManual'
import UpdateBanner from './components/UpdateBanner.vue'
import SweepConfigPanel from './components/SweepConfigPanel.vue'
import AmplitudeSweepConfigPanel from './components/AmplitudeSweepConfigPanel.vue'
import SweepDisplayPanel from './components/SweepDisplayPanel.vue'
import ThdSweepChart from './components/ThdSweepChart.vue'
import SweepFftPreviewChart from './components/SweepFftPreviewChart.vue'
import SweepScopePreviewChart from './components/SweepScopePreviewChart.vue'
import FrequencyResponseConfigPanel from './components/FrequencyResponseConfigPanel.vue'
import SteppedResponseConfigPanel from './components/SteppedResponseConfigPanel.vue'
import FrequencyResponseDisplayPanel from './components/FrequencyResponseDisplayPanel.vue'
import FrequencyResponseChart from './components/FrequencyResponseChart.vue'
import ImpulseResponseConfigPanel from './components/ImpulseResponseConfigPanel.vue'
import ImpulseResponseDisplayPanel from './components/ImpulseResponseDisplayPanel.vue'
import ImpulseResponseChart from './components/ImpulseResponseChart.vue'
import ImpulseHarmonicsChart from './components/ImpulseHarmonicsChart.vue'
import LoopbackReferenceDialog from './components/LoopbackReferenceDialog.vue'
import ResponsePolarChart from './components/ResponsePolarChart.vue'
import ResponseStabilityPanel from './components/ResponseStabilityPanel.vue'
import ResponsePoleZeroPanel from './components/ResponsePoleZeroPanel.vue'
import ResponseMeasurementsPanel from './components/ResponseMeasurementsPanel.vue'
import { useFrequencyResponse } from './composables/useFrequencyResponse'
import { useImpulseResponse } from './composables/useImpulseResponse'
import { useSweep } from './composables/useSweep'
import { nearestSweepPoint } from './sweep/points'
import { sweepXValue, type SweepXAxis } from './sweep/xAxis'
import {
  THD_VS_AMPLITUDE_KIND,
  isFrequencyResponseKind,
  isSteppedResponseKind,
  isImpulseResponseKind,
  isSweepKind as isSweepKindName,
} from './measurements/kinds'

const { status, ranges, error, refreshStatus } = useDevice()
const { spectrum, meters, scope, setViewport, clearScope } = useSpectrumStream()
const { overlays } = useMeasurements()
const { config: spectrumConfig } = useSpectrumConfig()
const { selectedMetrics, applyLoaded: applyLoadedMetrics } = useMetricSelection()
const { scopePeriods, applyLoaded: applyLoadedScopePeriods } = useScopeDisplay()
const {
  yTop: chartYTop,
  yBottom: chartYBottom,
  xFrom: chartXFrom,
  xTo: chartXTo,
  thickLines: chartThickLines,
  showDataPoints: chartShowDataPoints,
  showMinMax: chartShowMinMax,
  showPeakHold: chartShowPeakHold,
  autofitOnDone: chartAutofitOnDone,
  showWaterfall: chartShowWaterfall,
  showOscilloscope: chartShowOscilloscope,
  showHarmonics: chartShowHarmonics,
  showOtherPeaks: chartShowOtherPeaks,
  applyLoaded: applyLoadedChartDisplay,
  requestAutofitY: requestChartAutofitY,
  requestAutofitX: requestChartAutofitX,
} = useChartDisplay()
// Gates the fundamental/harmonic markers and tables to Sine — Multitone's comb has no single
// fundamental (the global-peak-bin search would just lock onto whichever tone happens to read
// loudest) and White noise has no discrete tone at all, so both would label meaningless content.
const { kind: stimulusKind } = useGeneratorStimulus()
const showFundamentalMarkers = computed(() => stimulusKind.value === 'Tones')
const { captureDelayMs } = useCaptureDelay()


// Moved out of SystemMetricsPanel's Sys:* grid into its own readout between the +Metrics button
// and the chart — same formatAveraging formula, just relocated. Either channel's averagesTaken is
// fine (both average together, see SystemMetricsPanel's own meters prop).
const averagesLabel = computed(() => {
  if (!spectrumConfig.value) return ''
  return formatAveraging(
    spectrumConfig.value.averaging,
    spectrumConfig.value.averageCount,
    (meters.value[0] ?? meters.value[1])?.averagesTaken,
  )
})
const {
  projects,
  types,
  currentProjectId,
  currentTypeId,
  loadedParams,
  dirtyTypeIds,
  markDirty,
  saveMeasurementType,
  saveMeasurementTypeAs,
  saveProject,
  saveProjectAs,
} = useProjects()

// Syncs the display-only settings — the metrics-picker selection, the oscilloscope's period count,
// and the spectrum chart's own axis bounds/line-style toggles — whenever a measurement type is
// (re)loaded. None of these reach the device or the pipeline, so unlike the generator/acquisition
// fields they apply straight away rather than waiting for Run. See each composable's applyLoaded
// for the null-means-default handling.
watch(
  loadedParams,
  (params) => {
    applyLoadedMetrics(params?.selectedMetrics)
    applyLoadedScopePeriods(params?.scopePeriods)
    applyLoadedChartDisplay(params)
  },
  { immediate: true },
)

interface RunConfig {
  sampleRateHz: number
  channelCount: number
}

const generatorPanel = ref<InstanceType<typeof GeneratorPanel> | null>(null)
const spectrumConfigPanel = ref<InstanceType<typeof SpectrumConfigPanel> | null>(null)
const saveDialogOpen = ref(false)
const saveTypeAsOpen = ref(false)
const saveProjectAsOpen = ref(false)
const metricPickerOpen = ref(false)
const configDialogOpen = ref(false)
// The manual's own state is a module-level singleton (see useManual.ts) rather than a ref here,
// because the header's button and the per-measurement help button have to drive one dialog.
const { openManual, openManualForKind } = useManual()
// Placeholder for the real "Projects" dialog — for now the button just expands the existing
// project list/create/delete panel in place, until that gets its own proper dialog.
const projectPanelOpen = ref(false)

const currentProject = computed(() => projects.value.find((p) => p.id === currentProjectId.value) ?? null)
const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)
// ---------------------------------------------------------------------------
// THD vs frequency
// ---------------------------------------------------------------------------
const {
  running: sweepRunning,
  progressLabel: sweepProgressLabel,
  generatorAmpDbv: sweepGeneratorAmpDbv,
  error: sweepError,
  points: sweepPoints,
  viewAxis: sweepViewAxis,
  start: startSweep,
  startAmplitude: startAmplitudeSweep,
  cancel: cancelSweepRun,
  clear: clearSweepRun,
  refresh: refreshSweep,
} = useSweep()

const {
  running: responseRunning,
  traces: responseTraces,
  error: responseError,
  progressLabel: responseProgressLabel,
  generatorAmpDbv: responseGeneratorAmpDbv,
  binHz: responseBinHz,
  latencySamples: responseLatencySamples,
  sampleRateHz: responseSampleRateHz,
  start: startResponse,
  startStepped: startSteppedResponse,
  cancel: cancelResponseRun,
  clear: clearResponseRun,
  refresh: refreshResponse,
  setTarget: setResponseTarget,
  phaseKind: responsePhaseKind,
  rightChannelIsReference: responseRightChannelIsReference,
  phaseAvailable: responsePhaseAvailable,
} = useFrequencyResponse()

const {
  status: impulseStatus,
  running: impulseRunning,
  traces: impulseTraces,
  impulse: impulseTrace,
  harmonics: impulseHarmonics,
  gateResolutionHz: impulseGateResolutionHz,
  refresh: refreshImpulse,
  start: startImpulse,
  cancel: cancelImpulse,
  clear: clearImpulse,
  refreshReferences: refreshLoopbackReferences,
} = useImpulseResponse()

// Which screen the main pane shows. App.vue used to render the FFT view for any selected type,
// which was fine while FFTSpectrum was the only kind. The two sweep kinds share one screen —
// identical layout, identical chart, identical history and preview rail — and differ only in which
// config panel is mounted and what the chart's x axis plots.
const isSweepKind = computed(() => isSweepKindName(currentType.value?.kind))
const isAmplitudeSweep = computed(() => currentType.value?.kind === THD_VS_AMPLITUDE_KIND)

// The frequency response is a third screen rather than a variant of the sweep one: it shares the
// three-column layout but nothing else — its own runner, its own endpoints, and a readout table the
// sweeps have no equivalent of.
//
// Two kinds land on it. A chirp and a stepped sine are acquired completely differently but produce
// the same three curves, so they share the chart, the readout, the display rail, the save path and
// the history overlay — everything below keys off this. Only three things have to tell them apart:
// which config panel is mounted, whether RUN can repeat, and whether coherence means anything.
const isFrequencyResponse = computed(() => isFrequencyResponseKind(currentType.value?.kind))
const isSteppedResponse = computed(() => isSteppedResponseKind(currentType.value?.kind))
const isChirpResponse = computed(() => isFrequencyResponse.value && !isSteppedResponse.value)

// A fourth screen. It shares the chirp with the response screen and even redraws its gated magnitude
// with the same chart, but the impulse itself, the gate and the harmonic curves have no counterpart
// there, and the two run through different endpoints — so it keys off its own flag rather than
// becoming a third arm of isFrequencyResponse.
const isImpulseResponse = computed(() => isImpulseResponseKind(currentType.value?.kind))

// One runner holds one set of points, and the two screens cannot read each other's: see useSweep's
// own `viewAxis`. Kept in step with the selected type rather than set on navigation, so a reload
// straight onto either screen is right on the first frame.
watch(
  isAmplitudeSweep,
  (amplitude) => {
    sweepViewAxis.value = amplitude ? 'Amplitude' : 'Frequency'
  },
  { immediate: true },
)

// The small FFT/oscilloscope preview charts below the sweep rail: while a sweep is running they
// track the point each channel just measured (points arrive frequency-sorted and a sweep always
// steps upward, so the last one on a channel is the most recent); once it's done they follow
// ThdSweepChart's cursor instead, falling back to each channel's own last point until something
// is hovered. Both channels are computed independently and always passed to both charts — a
// channel with no points (never part of this sweep, i.e. disabled in the generator) resolves to
// null and the charts simply draw nothing for it, rather than this deciding up front which one
// channel to show.
const hoveredSweepX = ref<number | null>(null)
function onSweepCursorX(x: number | null) {
  hoveredSweepX.value = x
}

/** The x accessor the chart is currently plotting with — what "nearest to the cursor" is measured along. */
function sweepXOf(point: ThdSweepPoint): number {
  return sweepXValue(point, sweepChartXAxis.value)
}

function selectedSweepPointFor(channel: number) {
  const onChannel = sweepPoints.value.filter((p) => p.channel === channel)
  if (onChannel.length === 0) return null

  if (sweepRunning.value) return onChannel[onChannel.length - 1]

  const target = hoveredSweepX.value ?? sweepXOf(onChannel[onChannel.length - 1])
  return nearestSweepPoint(sweepPoints.value, channel, target, sweepXOf)
}

const sweepSelectedLeft = computed(() => selectedSweepPointFor(0))
const sweepSelectedRight = computed(() => selectedSweepPointFor(1))

/**
 * The Spectrum/Oscilloscope preview pair drawn underneath the live one for each checked history
 * item — same filter ThdSweepChart's own activeOverlays uses (visible, and actually carrying
 * sweep points), so an FFT-kind result checked on while this screen is open is silently skipped
 * rather than drawn empty.
 */
const visibleSweepOverlays = computed(() => overlays.value.filter((o) => o.visible && o.sweepPoints?.length))

/**
 * The same filter for the response screen. Gated on the curves actually being there, so a result of
 * another kind that happens to be checked on is skipped rather than drawn empty.
 */
const visibleResponseOverlays = computed(() =>
  overlays.value.filter((o) => o.visible && o.responseTraces?.length),
)

/**
 * selectedSweepPointFor's counterpart for a stored overlay: never "running" (it's a saved result),
 * so it always follows the chart cursor, falling back to the channel's last point when nothing is
 * hovered — same fallback selectedSweepPointFor uses once a live sweep is done.
 */
function selectedSweepPointForOverlay(overlay: OverlayEntry, channel: number) {
  const onChannel = (overlay.sweepPoints ?? []).filter((p) => p.channel === channel)
  if (onChannel.length === 0) return null

  const target = hoveredSweepX.value ?? sweepXOf(onChannel[onChannel.length - 1])
  return nearestSweepPoint(overlay.sweepPoints ?? [], channel, target, sweepXOf)
}

/**
 * The same per-channel selection for every checked history result, for ThdSweepChart's metrics
 * table — the stored counterpart of sweepSelectedLeft/Right, resolved here rather than in the
 * chart so one function decides what "the selected point" means for the metrics, the preview
 * charts and the live readout alike.
 */
const sweepOverlaySelections = computed(() =>
  visibleSweepOverlays.value.map((overlay) => ({
    id: overlay.id,
    name: overlay.detail.name,
    left: selectedSweepPointForOverlay(overlay, 0),
    right: selectedSweepPointForOverlay(overlay, 1),
  })),
)

// Chart display preferences. Local view state, persisted into the measurement type's params_json
// alongside the sweep's own run settings — see applyStoredSweepDisplay/buildTypeParamsJson below.
const sweepUnit = ref<'percent' | 'db'>('percent')
const sweepShowNoiseFloor = ref(false)

// Per-trace visibility. THD/D2/D3 on by default; D4/D5/D6+ and noise floor start off, per Joost.
// Magnitude only appears on the dB axis and starts off.
const sweepShowMagnitude = ref(false)
const sweepShowThd = ref(true)
const sweepShowD2 = ref(true)
const sweepShowD3 = ref(true)
const sweepShowD4 = ref(false)
const sweepShowD5 = ref(false)
const sweepShowD6Plus = ref(false)
const sweepShowLeft = ref(true)
const sweepShowRight = ref(false)
const sweepThickLines = ref(true)
const sweepShowDataPoints = ref(false)

// Axis bounds, in percent for y and Hz for x. 10% down to 0.00001% covers everything from a
// clipping tube stage to the analyzer's own residual without touching the dropdowns.
const sweepYMaxPercent = ref(10)
const sweepYMinPercent = ref(0.00001)
const sweepXFromHz = ref(10)
const sweepXToHz = ref(20000)

/**
 * Which quantity the amplitude screen's x axis plots. Output voltage by default, matching the
 * previous app. Ignored entirely on the frequency screen, where the axis is not a choice —
 * `sweepChartXAxis` below is what the chart and the display panel actually read.
 */
const sweepAmplitudeXAxis = ref<SweepXAxis>('outputVolt')

/**
 * X bounds for the amplitude screen, one pair *per axis choice* rather than one pair shared.
 * The three axes are in two different units and three different ranges — a window of 1 mV to 10 V
 * means nothing in watts — so a single pair would leave the chart empty every time the axis was
 * switched, and re-Autofitting would be mandatory rather than a convenience.
 */
const sweepAmplitudeBounds = reactive<Record<string, { from: number; to: number }>>({
  outputVolt: { from: 0.001, to: 10 },
  outputWatt: { from: 0.000001, to: 100 },
  inputVolt: { from: 0.001, to: 10 },
})

/** The axis the chart is on right now: the amplitude screen's choice, or plain frequency. */
const sweepChartXAxis = computed<SweepXAxis>({
  get: () => (isAmplitudeSweep.value ? sweepAmplitudeXAxis.value : 'frequency'),
  set: (value) => {
    // The frequency screen hides the selector, so this can only ever be written from the amplitude
    // one; guarded anyway so a stray write cannot leave that screen on a volt axis.
    if (isAmplitudeSweep.value && value !== 'frequency') sweepAmplitudeXAxis.value = value
  },
})

/** The x bounds for whichever axis is showing — the Hz pair, or the current amplitude axis' own pair. */
const sweepXFrom = computed<number>({
  get: () => (isAmplitudeSweep.value ? sweepAmplitudeBounds[sweepAmplitudeXAxis.value].from : sweepXFromHz.value),
  set: (value) => {
    if (isAmplitudeSweep.value) sweepAmplitudeBounds[sweepAmplitudeXAxis.value].from = value
    else sweepXFromHz.value = value
  },
})

const sweepXTo = computed<number>({
  get: () => (isAmplitudeSweep.value ? sweepAmplitudeBounds[sweepAmplitudeXAxis.value].to : sweepXToHz.value),
  set: (value) => {
    if (isAmplitudeSweep.value) sweepAmplitudeBounds[sweepAmplitudeXAxis.value].to = value
    else sweepXToHz.value = value
  },
})

// The dB axis keeps its own bounds rather than reusing the percent ones: it has to reach above
// 0 dB for the Magnitude trace, and no percentage expresses that.
const sweepYTopDb = ref(20)
const sweepYBottomDb = ref(-160)

// Off by default so nothing changes for a type saved before this existed. Shared by both sweep
// kinds, same as every other ref on this screen.
const sweepAutofitOnDone = ref(false)

// Suppresses the dirty watcher below while applyStoredSweepDisplay writes these refs from a loaded
// type's stored params — loading is the opposite of an unsaved edit. Same pattern as
// SweepConfigPanel.vue's own `restoring` flag; kept separate because these refs live here, not in
// that component.
let sweepDisplayRestoring = false

watch(
  [
    sweepUnit, sweepShowNoiseFloor, sweepShowMagnitude, sweepShowThd, sweepShowD2, sweepShowD3,
    sweepShowD4, sweepShowD5, sweepShowD6Plus, sweepShowLeft, sweepShowRight, sweepThickLines,
    sweepShowDataPoints, sweepYMaxPercent, sweepYMinPercent, sweepXFromHz, sweepXToHz, sweepYTopDb,
    sweepYBottomDb, sweepAutofitOnDone, sweepAmplitudeXAxis, sweepAmplitudeBounds,
  ],
  () => {
    if (!sweepDisplayRestoring) markDirty()
  },
  // Deep, for sweepAmplitudeBounds: its per-axis pairs are mutated in place by the computed setters
  // above, which a shallow watcher on the reactive object would not see.
  { deep: true },
)

/**
 * Restores the sweep chart's display preferences from a loaded type's stored params_json — the
 * counterpart of SweepConfigPanel.vue's own applyStoredParams, just for the fields that live here
 * instead of in that panel's form. Guarded the same way: a parse failure (an FFT type's params, or
 * a sweep saved before these fields existed) just keeps whatever's already showing.
 */
function applyStoredSweepDisplay(paramsJson: string) {
  // The two records share every chart field but the x pair, so one loose shape reads both: the
  // frequency sweep stores chartXFromHz/chartXToHz in hertz, the amplitude one chartXFrom/chartXTo
  // in the selected axis' own unit, and each is applied to the state that owns it.
  type StoredDisplay = Partial<import('./api/client').ThdVsFrequencyParameters> &
    Partial<import('./api/client').ThdVsAmplitudeParameters>

  let stored: StoredDisplay
  try {
    stored = JSON.parse(paramsJson) as StoredDisplay
  } catch {
    return
  }

  sweepDisplayRestoring = true
  if (stored.chartXAxis) sweepAmplitudeXAxis.value = stored.chartXAxis
  if (typeof stored.chartXFrom === 'number' && stored.chartXFrom > 0)
    sweepAmplitudeBounds[sweepAmplitudeXAxis.value].from = stored.chartXFrom
  if (typeof stored.chartXTo === 'number' && stored.chartXTo > 0)
    sweepAmplitudeBounds[sweepAmplitudeXAxis.value].to = stored.chartXTo
  if (stored.chartUnit) sweepUnit.value = stored.chartUnit
  if (typeof stored.chartShowNoiseFloor === 'boolean') sweepShowNoiseFloor.value = stored.chartShowNoiseFloor
  if (typeof stored.chartShowMagnitude === 'boolean') sweepShowMagnitude.value = stored.chartShowMagnitude
  if (typeof stored.chartShowThd === 'boolean') sweepShowThd.value = stored.chartShowThd
  if (typeof stored.chartShowD2 === 'boolean') sweepShowD2.value = stored.chartShowD2
  if (typeof stored.chartShowD3 === 'boolean') sweepShowD3.value = stored.chartShowD3
  if (typeof stored.chartShowD4 === 'boolean') sweepShowD4.value = stored.chartShowD4
  if (typeof stored.chartShowD5 === 'boolean') sweepShowD5.value = stored.chartShowD5
  if (typeof stored.chartShowD6Plus === 'boolean') sweepShowD6Plus.value = stored.chartShowD6Plus
  if (typeof stored.chartShowLeft === 'boolean') sweepShowLeft.value = stored.chartShowLeft
  if (typeof stored.chartShowRight === 'boolean') sweepShowRight.value = stored.chartShowRight
  if (typeof stored.chartThickLines === 'boolean') sweepThickLines.value = stored.chartThickLines
  if (typeof stored.chartShowDataPoints === 'boolean') sweepShowDataPoints.value = stored.chartShowDataPoints
  if (typeof stored.chartYMaxPercent === 'number') sweepYMaxPercent.value = stored.chartYMaxPercent
  if (typeof stored.chartYMinPercent === 'number') sweepYMinPercent.value = stored.chartYMinPercent
  if (typeof stored.chartXFromHz === 'number') sweepXFromHz.value = stored.chartXFromHz
  if (typeof stored.chartXToHz === 'number') sweepXToHz.value = stored.chartXToHz
  if (typeof stored.chartYTopDb === 'number') sweepYTopDb.value = stored.chartYTopDb
  if (typeof stored.chartYBottomDb === 'number') sweepYBottomDb.value = stored.chartYBottomDb
  if (typeof stored.chartAutofitOnDone === 'boolean') sweepAutofitOnDone.value = stored.chartAutofitOnDone

  void nextTick(() => {
    sweepDisplayRestoring = false
  })
}

// Gated on kind, not just "paramsJson is present": a handful of these field names (chartThickLines,
// chartShowDataPoints, chartXFromHz, chartXToHz) are shared with FftSpectrumParameters' own chart
// fields, so parsing an FFT type's params here would silently borrow its chart settings for the
// sweep display refs instead of leaving them alone.
watch(
  () => (isSweepKindName(currentType.value?.kind) ? currentType.value?.paramsJson ?? null : null),
  (paramsJson) => {
    if (paramsJson) applyStoredSweepDisplay(paramsJson)
  },
  { immediate: true },
)

// Drives RUN SWEEP, which lives above the rail while the form it validates stays in the panel. One
// ref per panel because only one of the two is mounted at a time; activeSweepPanel is what the
// button and the save path actually read, and both panels expose the same { run, invalid,
// buildParamsJson } contract.
const responseConfigPanel = ref<InstanceType<typeof FrequencyResponseConfigPanel> | null>(null)
const steppedResponseConfigPanel = ref<InstanceType<typeof SteppedResponseConfigPanel> | null>(null)
const sweepConfigPanel = ref<InstanceType<typeof SweepConfigPanel> | null>(null)
const amplitudeSweepConfigPanel = ref<InstanceType<typeof AmplitudeSweepConfigPanel> | null>(null)
const activeSweepPanel = computed(() =>
  isAmplitudeSweep.value ? amplitudeSweepConfigPanel.value : sweepConfigPanel.value,
)

// The same arrangement on the response screen: one ref per panel because only one of the two is
// ever mounted, and this is what the RUN button and the save path read. Both expose the same
// { run, invalid, buildParamsJson } contract, so neither of those has to know which is up.
const activeResponsePanel = computed(() =>
  isSteppedResponse.value ? steppedResponseConfigPanel.value : responseConfigPanel.value,
)

// Frequency-response chart display preferences. Local view state, persisted into the measurement
// type's params_json alongside the run settings — the same arrangement as the sweep screen's, and
// for the same reason: none of it is server state the host could snapshot on its own.
const responseView = ref<import('./api/client').ResponseView>('Gain')
const responseShowLeft = ref(true)
const responseShowRight = ref(false)
const responseShowPhase = ref(false)
const responseShowCoherence = ref(false)
const responseShowOneDb = ref(true)
const responseShowThreeDb = ref(true)
const responseThickLines = ref(true)
const responseShowDataPoints = ref(false)
// The Gain (dB) view's own window.
const responseYTopDb = ref(10)
const responseYBottomDb = ref(-10)
// The FR (dBV) view's own window -- independent of the pair above so switching views never
// clobbers the other one's range. Same default (10/-10) as the Gain pair always used, so first
// load behaves exactly as before the two were split.
const responseYTopDbv = ref(10)
const responseYBottomDbv = ref(-10)
const responseXFromHz = ref(10)
const responseXToHz = ref(100000)
// Independent of the dB window because unwrapped phase can run past +/-180 deg on a DUT with real
// excess phase, so the two axes cannot share one pair of bounds. +/-180 is the useful default: it
// is the widest range a wrapped reading could ever need, and Autofit takes over from there.
const responsePhaseTopDeg = ref(180)
const responsePhaseBottomDeg = ref(-180)
// Off by default so nothing changes for a type saved before this existed.
const responseAutofitOnDone = ref(false)

// Which projection of the response is on screen. A separate axis of state from responseView, which
// still means only "which y quantity the Bode chart shows" -- see ResponseProjection's own remarks.
const responseProjection = ref<import('./api/client').ResponseProjection>('Bode')
// Linear is the textbook Nyquist plot and the only one whose distances mean what they look like;
// the dB map trades that away to keep 60 dB of range on screen.
const responseNyquistRadius = ref<import('./api/client').NyquistRadiusName>('Linear')
const responseNyquistRadiusFloorDb = ref(-40)
const responseInvertLoopSign = ref(false)
const responseRestoreDelay = ref(false)

/**
 * Whether the measurement on screen has a complex response at all.
 *
 * Not `responsePhaseAvailable`, which is the looser gate the Bode phase series uses. A
 * ChannelDifference run measures its gain against the commanded generator level and its phase
 * against the right input — two different ratios — so 10^(dB/20)·e^(jφ) built from the pair is a
 * complex number describing nothing. It plots, and it looks entirely plausible, which is why it is
 * excluded by construction rather than by a warning.
 */
const responseHasComplexResponse = computed(
  () => responsePhaseKind.value === 'DutResponse' || responsePhaseKind.value === 'PerTrace',
)

// Falls back to the Bode chart whenever the run cannot support a projection, so selecting a
// stored polar preference on a type that no longer qualifies never leaves a blank screen.
const activeResponseProjection = computed(() =>
  responseProjection.value !== 'Bode' && responseHasComplexResponse.value ? responseProjection.value : 'Bode',
)

const responsePolarChart = ref<InstanceType<typeof ResponsePolarChart> | null>(null)
const responsePoleZeroPanel = ref<InstanceType<typeof ResponsePoleZeroPanel> | null>(null)

// Fit settings. Not persisted into params_json: unlike the projection or the radius map these
// describe an analysis of one measurement rather than how the screen is set up, and a stored order
// would silently be applied to a different DUT's curve on load.
const responseFitAutoOrder = ref(true)
const responseFitOrder = ref(2)

// Same guard as sweepDisplayRestoring: applying a type's stored display state is the opposite of an
// unsaved edit, and marking it dirty there would light Save up on every selection.
let responseDisplayRestoring = false

/**
 * Restores a frequency-response type's stored chart state. Every field is optional and guarded, so
 * a type saved before one of them existed simply keeps whatever is showing.
 */
function applyStoredResponseDisplay(paramsJson: string) {
  type StoredDisplay = Partial<import('./api/client').FrequencyResponseParameters>

  let stored: StoredDisplay
  try {
    stored = JSON.parse(paramsJson) as StoredDisplay
  } catch {
    return
  }

  responseDisplayRestoring = true
  if (stored.chartView) responseView.value = stored.chartView
  if (typeof stored.chartShowLeft === 'boolean') responseShowLeft.value = stored.chartShowLeft
  if (typeof stored.chartShowRight === 'boolean') responseShowRight.value = stored.chartShowRight
  if (typeof stored.chartShowPhase === 'boolean') responseShowPhase.value = stored.chartShowPhase
  if (typeof stored.chartShowCoherence === 'boolean') responseShowCoherence.value = stored.chartShowCoherence
  if (typeof stored.chartShowOneDb === 'boolean') responseShowOneDb.value = stored.chartShowOneDb
  if (typeof stored.chartShowThreeDb === 'boolean') responseShowThreeDb.value = stored.chartShowThreeDb
  if (typeof stored.chartThickLines === 'boolean') responseThickLines.value = stored.chartThickLines
  if (typeof stored.chartShowDataPoints === 'boolean') responseShowDataPoints.value = stored.chartShowDataPoints
  if (typeof stored.chartYTopDb === 'number') responseYTopDb.value = stored.chartYTopDb
  if (typeof stored.chartYBottomDb === 'number') responseYBottomDb.value = stored.chartYBottomDb
  if (typeof stored.chartYTopDbv === 'number') responseYTopDbv.value = stored.chartYTopDbv
  if (typeof stored.chartYBottomDbv === 'number') responseYBottomDbv.value = stored.chartYBottomDbv
  if (typeof stored.chartXFromHz === 'number' && stored.chartXFromHz > 0) responseXFromHz.value = stored.chartXFromHz
  if (typeof stored.chartXToHz === 'number' && stored.chartXToHz > 0) responseXToHz.value = stored.chartXToHz
  if (typeof stored.chartPhaseTopDeg === 'number') responsePhaseTopDeg.value = stored.chartPhaseTopDeg
  if (typeof stored.chartPhaseBottomDeg === 'number') responsePhaseBottomDeg.value = stored.chartPhaseBottomDeg
  if (typeof stored.chartAutofitOnDone === 'boolean') responseAutofitOnDone.value = stored.chartAutofitOnDone
  if (stored.chartProjection) responseProjection.value = stored.chartProjection
  if (stored.chartNyquistRadius) responseNyquistRadius.value = stored.chartNyquistRadius
  if (typeof stored.chartNyquistRadiusFloorDb === 'number' && stored.chartNyquistRadiusFloorDb < 0)
    responseNyquistRadiusFloorDb.value = stored.chartNyquistRadiusFloorDb
  if (typeof stored.chartInvertLoopSign === 'boolean') responseInvertLoopSign.value = stored.chartInvertLoopSign
  if (typeof stored.chartRestoreDelay === 'boolean') responseRestoreDelay.value = stored.chartRestoreDelay

  void nextTick(() => {
    responseDisplayRestoring = false
  })
}

// Kind-gated for the same reason the sweep watcher is: an FFT type's params_json parses fine, and
// reading chart fields out of it would apply another screen's state to this one.
watch(
  () => (isFrequencyResponse.value ? currentType.value?.paramsJson : undefined),
  (paramsJson) => {
    if (paramsJson) applyStoredResponseDisplay(paramsJson)
  },
  { immediate: true },
)

watch(
  [
    responseView, responseShowLeft, responseShowRight, responseShowPhase, responseShowCoherence,
    responseShowOneDb, responseShowThreeDb, responseThickLines, responseShowDataPoints,
    responseYTopDb, responseYBottomDb, responseYTopDbv, responseYBottomDbv, responseXFromHz, responseXToHz,
    responsePhaseTopDeg, responsePhaseBottomDeg, responseAutofitOnDone,
    responseProjection, responseNyquistRadius, responseNyquistRadiusFloorDb,
    responseInvertLoopSign, responseRestoreDelay,
  ],
  () => {
    if (!responseDisplayRestoring && isFrequencyResponse.value) markDirty()
  },
)

/**
 * RUN keeps measuring until stopped; SINGLE measures once. The panel builds the request and this
 * decides which of the two it is — the panel has no reason to know, and putting the choice in one
 * place is what keeps the two buttons from drifting apart.
 */
function runFrequencyResponse(continuous: boolean) {
  const request = responseConfigPanel.value?.buildRequest(continuous)
  if (!request) return

  // Without a reference channel the right input is a trace in its own right and worth showing; with
  // one it is the yardstick, and the previous app hid it for exactly that reason.
  responseShowRight.value = !request.rightChannelIsReference
  return withErrorReporting(async () => {
    await startResponse(request)
  })
}

function cancelFrequencyResponse() {
  return withErrorReporting(async () => {
    await cancelResponseRun()
  })
}

function clearFrequencyResponse() {
  return withErrorReporting(async () => {
    await clearResponseRun()
  })
}

/**
 * The stepped-sine Bode plot. Single-shot only — a hundred-point run is minutes long, so repeating
 * it is the sweep screen's model (one RUN SWEEP button) rather than the chirp's RUN/SINGLE pair.
 *
 * Driven from the panel's own `run` emit, exactly as the two sweep panels are: the button calls
 * `run()` and the validated form arrives here, which keeps the form and its validation in one place.
 */
function runSteppedResponse(request: import('./api/client').SteppedResponseStartRequest) {
  // Without a reference channel the right input is a trace in its own right and worth showing; with
  // one it is the yardstick, and the previous app hid it for exactly that reason.
  responseShowRight.value = !request.rightChannelIsReference
  return withErrorReporting(async () => {
    await startSteppedResponse(request)
  })
}

// The store keeps one state per measurement and this is what says which of them the screen is
// showing — set from the selected kind rather than at call time, and before the re-attach below,
// since every verb reads it.
//
// The re-poll is here rather than only on the isFrequencyResponse watcher below because that flag is
// true for *both* response kinds, so switching from one to the other never changes it and never
// fired. That is what left the previous kind's curves on screen.
watch(
  isSteppedResponse,
  (stepped) => {
    setResponseTarget(stepped ? 'Stepped' : 'Chirp')
    if (isFrequencyResponse.value) void refreshResponse().catch(() => undefined)
  },
  { immediate: true },
)

// A measurement runs server-side, so a reload (or a second tab) mid-run has to re-attach rather
// than start from an empty chart. Covers arriving from a non-response kind, which the watcher above
// does not see.
watch(isFrequencyResponse, (isResponse) => {
  if (isResponse) void refreshResponse().catch(() => undefined)
})

function runSweep(request: import('./api/client').ThdSweepStartRequest) {
  // Mirror the channel selection onto the chart so a stereo sweep actually draws both traces.
  sweepShowLeft.value = request.processLeft
  sweepShowRight.value = request.processRight
  return withErrorReporting(async () => {
    await startSweep(request)
  })
}

function runAmplitudeSweep(request: import('./api/client').ThdAmplitudeSweepStartRequest) {
  sweepShowLeft.value = request.processLeft
  sweepShowRight.value = request.processRight
  return withErrorReporting(async () => {
    await startAmplitudeSweep(request)
  })
}

function cancelSweep() {
  return withErrorReporting(async () => {
    await cancelSweepRun()
  })
}

// The sweep screen's counterpart of clearMeasurement. The hovered frequency has to go with the
// points: it is a frequency from a sweep that no longer exists, and leaving it set would keep the
// FFT/scope preview charts pointing at the last thing hovered on the chart that was just emptied.
function clearSweep() {
  return withErrorReporting(async () => {
    await clearSweepRun()
    hoveredSweepX.value = null
  })
}

// A sweep runs server-side, so a reload (or opening a second tab) mid-sweep has to re-attach
// rather than start from an empty chart.
watch(isSweepKind, (isSweep) => {
  if (isSweep) void refreshSweep().catch(() => undefined)
})

const isCurrentTypeDirty = computed(() => currentTypeId.value !== null && dirtyTypeIds.value.has(currentTypeId.value))
const hasAnyDirtyInProject = computed(() =>
  types.value.some((t) => t.projectId === currentProjectId.value && dirtyTypeIds.value.has(t.id)),
)

// Every handler below talks to the backend and must not fail silently — an uncaught rejection
// from a template @click handler is swallowed by Vue (logged to console, nothing shown), which
// reads to a user as "the button did nothing."
async function withErrorReporting(action: () => Promise<void>) {
  try {
    await action()
    error.value = null
  } catch (e) {
    error.value = (e as Error).message
  }
}

function setSimulatedProfile(patch: SimulatedProfileRequest) {
  return withErrorReporting(async () => {
    status.value = await api.setSimulatedProfile(patch)
  })
}

// The status bar's Connect button: the app starts with no device rather than refusing to start, so
// this is how a QA403 plugged in afterwards gets picked up. Failure is just an error line in the
// status bar — nothing else about the session changes, and pressing it again is safe.
const connecting = ref(false)

function connectDevice() {
  return withErrorReporting(async () => {
    connecting.value = true
    try {
      status.value = await api.connectDevice()
    } finally {
      connecting.value = false
    }
  })
}

// The Run button's whole job: push everything staged in the FFT settings and Generator panels'
// forms — auto-ranging, generator config, then start acquisition — in one explicit action.
// Nothing in either panel touches the device on its own before this: on real hardware, setting
// auto-range steps the attenuator relay, which is audible the instant it happens, not just when
// the generator plays, so even that has to wait for Run. Input range itself is never sent as a
// specific value here — auto-ranging (always on, where the device supports it) decides it, either
// the device's own hardware ranger or the live monitor below for devices that don't (the
// simulator) — see DeviceStatusBar's read-only display.
// Block size isn't user-configurable — omitted from both calls below, the server defaults it to 4096.
function runMeasurement(runConfig: RunConfig) {
  return withErrorReporting(async () => {
    const { sampleRateHz, channelCount } = runConfig

    // Drop whatever the previous run last displayed, most importantly any `held` flag from an
    // earlier Single capture — otherwise the auto-stop watcher below could see stale held=true
    // meters left over from before this run even produces its first fresh frame.
    meters.value = {}
    spectrum.value = {}
    clearScope()

    // Gated on the *device* actually having auto-ranging (status.autoRangeEnabled null means it
    // doesn't — the simulator, currently) rather than unconditionally requesting it: the server
    // 400s a device with no such capability, same reasoning as the live monitor's own
    // hardwareAutoActive check below.
    if (status.value?.autoRangeEnabled !== null && status.value?.autoRangeEnabled !== undefined)
      status.value = await api.setAutoRange(true)

    const generator = generatorPanel.value?.buildRequest()
    if (generator) {
      // sampleRateHz/channelCount are a hint the backend only uses if it needs to bring up a
      // generator-only stream on its own — see DeviceSessionService.ConfigureGeneratorAsync.
      status.value = await api.setGenerator({
        left: generator.left,
        right: generator.right,
        sampleRateHz,
        channelCount,
        snapToBin: generator.snapToBin,
      })
      generatorPanel.value?.resyncTones(status.value)

      // Lets the generator settle (through external gear, DUT, etc.) before capture starts — see
      // ConfigDialog's "Capture delay" field. Only meaningful once the generator's actually been
      // enabled above, not on every Run (e.g. a generator-less measurement).
      if (captureDelayMs.value > 0) await new Promise((resolve) => setTimeout(resolve, captureDelayMs.value))
    }

    const newStatus = await api.startAcquisition({ sampleRateHz, channelCount })
    status.value = newStatus
    // A sample-rate change re-snaps the generator server-side (see DeviceSessionService.StartAcquisitionAsync);
    // newStatus already carries the new frequencies, this just pulls them into the form fields.
    generatorPanel.value?.resyncTones(newStatus)
  })
}

// Shared by the RUN button and any live setting that needs a restart (currently just FFT
// settings' sample rate) — builds the RunConfig fresh from whatever's currently staged each time,
// rather than the button owning a fixed one.
function runFromCurrentSettings() {
  return runMeasurement({
    sampleRateHz: spectrumConfigPanel.value?.getSampleRateHz() ?? 192000,
    channelCount: ranges.value?.maxChannelCount ?? 2,
  })
}

// The Stop button's counterpart — same "stop everything" used automatically when switching
// measurements (see useProjects.ts's selectType), just with a snappy refresh here since this one
// has a button sitting right there waiting on it, unlike the switch-measurement case.
// Whether the Single button armed the capture this screen is now waiting on — see the
// autoStopOnceHeld watcher below for why merely seeing `held` is not enough.
const singleShotArmed = ref(false)

function stopMeasurement() {
  return withErrorReporting(async () => {
    singleShotArmed.value = false
    await stopRun()
    await refreshStatus()
  })
}

// The Single button: same as Run, except it arms SpectrumPipelineOptions.SingleShot right after
// starting, so the capture settles to the configured average count and holds instead of
// free-running — the autoStopOnceHeld watcher below then stops it automatically, same as pressing
// Stop, once every processed channel's meters frame comes back flagged Held. Not a saved config
// value (see useSpectrumConfig().arm) — just another way to capture data, alongside Run/Stop.
const { arm: armSingleShotCapture } = useSpectrumConfig()
function singleShotCapture() {
  return withErrorReporting(async () => {
    await runFromCurrentSettings()
    if (status.value?.acquiring) {
      singleShotArmed.value = true
      await armSingleShotCapture()
    }
  })
}

// Companion to singleShotCapture: stops automatically once every enabled/processed channel's
// meters frame comes back Held, so a Single capture is a genuinely one-shot action — no separate
// "now click Stop" step.
//
// Gated on this screen having armed the capture itself, NOT merely on `held` arriving. A Held
// meters frame only means "some single-shot capture completed", and the THD-vs-frequency sweep
// turns SpectrumPipelineOptions.SingleShot on for every one of its points — so without this flag
// the FFT screen stopped the *sweep's* acquisition the moment its first point completed. Everything
// after that ran with acquisition stopped: ConfigureGeneratorAsync no longer flushed (it only does
// so while acquiring), the pipeline stayed held, and every remaining point reported the first
// point's spectrum. Only reproducible with a browser tab open, which is why the API-level tests
// never saw it.
let autoStopping = false
watch(meters, (m) => {
  if (!singleShotArmed.value || !status.value?.acquiring || autoStopping) return

  const config = spectrumConfig.value
  if (!config || (!config.processLeft && !config.processRight)) return

  const leftDone = !config.processLeft || m[0]?.held === true
  const rightDone = !config.processRight || m[1]?.held === true
  if (!leftDone || !rightDone) return

  autoStopping = true
  singleShotArmed.value = false
  stopMeasurement().finally(() => {
    autoStopping = false
  })
})

// "Autofit when measurement done" for the FFT/Spectrum screen: fires on the acquiring->not
// transition, whether that's a SINGLE capture auto-stopping (above) or a manual STOP -- there is
// no other "done" signal for a screen that otherwise streams continuously. requestChartAutofitY/X
// are the same trigger functions ChartDisplayPanel.vue's own Autofit buttons call.
watch(
  () => status.value?.acquiring,
  (isAcquiring, wasAcquiring) => {
    if (wasAcquiring && !isAcquiring && chartAutofitOnDone.value) {
      requestChartAutofitY()
      requestChartAutofitX()
    }
  },
)

// Generator field edits (frequency, level, kind, enabled, snap-to-bin — see GeneratorPanel's
// `change` emit) only need to reach the device at all if something's already live: pushing while
// idle would just be pointless traffic, since the values are already staged locally and Run reads
// them fresh anyway. `ConfigureGeneratorAsync` applies live changes to an already-running stream on
// its own (no restart), so unlike Run this never touches acquisition — only the generator.
function applyGeneratorLive() {
  return withErrorReporting(async () => {
    markDirty()
    const live = status.value?.acquiring || status.value?.generatorLeft.enabled || status.value?.generatorRight.enabled
    if (!live) return

    const generator = generatorPanel.value?.buildRequest()
    if (!generator) return
    status.value = await api.setGenerator({ left: generator.left, right: generator.right, snapToBin: generator.snapToBin })
    generatorPanel.value?.resyncTones(status.value)
  })
}

// Sample rate (see SpectrumConfigPanel's `sample-rate-change` emit) changes the actual acquisition
// stream, so unlike FFT size/window/etc. (which apply live on their own via useSpectrumConfig) it
// needs a full restart — same "no-op while idle, since Run pushes it anyway" reasoning as
// applyGeneratorLive.
function onSampleRateChange() {
  if (status.value?.acquiring) return runFromCurrentSettings()
}

// Live input-range monitoring: while acquiring, continuously reads the peak level off whichever
// channel(s) are enabled and runs it through determineAttenuation() (ported from the previous
// app's QaLibrary — see attenuation.ts) to keep the input range matched to the actual signal.
// Deliberately stays out of the way of real hardware's own auto-ranger rather than fighting it —
// this is what gives the simulator equivalent auto-ranging, since it has no hardware ranger of its
// own. Never marks the measurement dirty: this is the software tracking the live signal, not the
// user changing a setting they'd want saved — same as hardware auto-ranging never marking it dirty
// either (that stepping happens entirely server-side, invisible to the client).
//
// Requires the same determined target 3 frames running before acting — this is a coarse (6 dB step)
// decision already, but a peak sitting right on a step boundary could still ping-pong frame to
// frame; on real hardware every step is an audible relay click, so this is not just "avoid churn."
// Qa40xAutoRanger.cs uses an analogous under-range hysteresis for the real hardware ranger, but
// time-based (StepDownDelayMs, configurable in the Configuration dialog) rather than frame-count
// based — this frame count and that delay are not the same figure and don't need to match.
const RANGE_STABILITY_FRAMES = 3
let pendingRangeTarget: number | null = null
let pendingRangeCount = 0

watch(meters, (m) => {
  const hardwareAutoActive = status.value?.autoRangeEnabled !== null && status.value?.autoRangeEnabled !== undefined
  if (!status.value?.acquiring || hardwareAutoActive) {
    pendingRangeTarget = null
    pendingRangeCount = 0
    return
  }

  const peaks: number[] = []
  if ((spectrumConfig.value?.processLeft ?? true) && m[0]) peaks.push(m[0].peakDbv)
  if ((spectrumConfig.value?.processRight ?? true) && m[1]) peaks.push(m[1].peakDbv)
  if (peaks.length === 0) return

  const options = ranges.value?.inputRangesDbv ?? [0, 6, 12, 18, 24, 30, 36, 42]
  const target = nearestInputRange(determineAttenuation(Math.max(...peaks)), options)

  if (target === pendingRangeTarget) pendingRangeCount++
  else {
    pendingRangeTarget = target
    pendingRangeCount = 1
  }

  const currentRangeDbv = status.value?.inputRangeDbv
  if (pendingRangeCount >= RANGE_STABILITY_FRAMES && currentRangeDbv != null && target !== currentRangeDbv) {
    pendingRangeCount = 0
    withErrorReporting(async () => {
      status.value = await api.setInputRange(target)
    })
  }
})

function reportError(message: string) {
  error.value = message
}

function openSaveDialog() {
  saveDialogOpen.value = true
}

// The Clear button next to Save measurement result: discards the retained (stopped) spectrum both
// server-side (so Save has nothing stale left to save) and here in the client's own display state,
// same reset runMeasurement does before a fresh Run — so the chart goes straight back to "Waiting
// for data…" instead of waiting for a frame that isn't coming until Run/Single is pressed again.
// Does not touch anything already saved to History.
function clearMeasurement() {
  return withErrorReporting(async () => {
    await api.clearSpectrum()
    meters.value = {}
    spectrum.value = {}
    clearScope()
  })
}

// The FFT settings/Generator panels' current on-screen values, whether or not they've ever reached
// the device — same values runMeasurement uses to gather what Run would send, just packaged for a
// measurement-type save (params_json) instead of pushed live. Undefined when nothing's loaded yet,
// in which case the server falls back to whatever the device last actually did. inputRangeDbv/
// autoRangeEnabled are no longer user-staged values — auto-ranging is always on (see
// runFromCurrentSettings), so 0/true are just the placeholder Run itself uses.
function buildStagedConfig(): StagedMeasurementConfig | undefined {
  const sampleRateHz = spectrumConfigPanel.value?.getSampleRateHz()
  const generator = generatorPanel.value?.buildRequest()
  if (sampleRateHz === undefined || !generator) return undefined
  return {
    sampleRateHz,
    channelCount: ranges.value?.maxChannelCount ?? 2,
    inputRangeDbv: 0,
    autoRangeEnabled: true,
    generatorLeft: generator.left,
    generatorRight: generator.right,
    snapToBin: generator.snapToBin,
    selectedMetrics: selectedMetrics.value,
    scopePeriods: scopePeriods.value,
    chartYTopDbv: chartYTop.value,
    chartYBottomDbv: chartYBottom.value,
    chartXFromHz: chartXFrom.value,
    chartXToHz: chartXTo.value,
    chartThickLines: chartThickLines.value,
    chartShowDataPoints: chartShowDataPoints.value,
    chartShowMinMax: chartShowMinMax.value,
    chartShowPeakHold: chartShowPeakHold.value,
    chartAutofitOnDone: chartAutofitOnDone.value,
    chartShowWaterfall: chartShowWaterfall.value,
    chartShowOscilloscope: chartShowOscilloscope.value,
    chartShowHarmonics: chartShowHarmonics.value,
    chartShowOtherPeaks: chartShowOtherPeaks.value,
  }
}

/**
 * A sweep type's settings are the sweep panel's own form — the server cannot snapshot them the way
 * it snapshots the live pipeline for an FFT type, because nothing about a configured sweep is live
 * device state. So the sweep screen sends its params_json explicitly; every other kind keeps
 * omitting it and lets the server build one. See ProjectsEndpoints' PUT handler.
 *
 * The chart display preferences above (sweepUnit, sweepShow*, axis bounds) live in App.vue rather
 * than SweepConfigPanel's form, so they are merged into that panel's JSON here rather than passed
 * into it — round-tripping through parse/stringify is simpler than threading nineteen more props
 * through a component whose own job is the run configuration, not the chart.
 */

// Impulse-response screen. Its own state rather than a variant of the response screen's: it shares
// the chirp and the gated magnitude/phase curves, but the impulse itself, the gate and the harmonic
// curves have no counterpart there, and the two run through different endpoints.
const impulseConfigPanel = ref<InstanceType<typeof ImpulseResponseConfigPanel> | null>(null)
const loopbackDialogOpen = ref(false)

const impulseView = ref<import('./api/client').ImpulseView>('Impulse')
const impulseNormalised = ref(false)
const impulseShowPhase = ref(false)
const impulseShowCoherence = ref(false)
const impulseThickLines = ref(true)
const impulseShowDataPoints = ref(false)
/**
 * A level window per view, not one shared pair. The three views read completely different
 * quantities — dB below the impulse's own peak, gain in dB, and distortion in dBc — so a single
 * window means switching view lands on a range that belongs to the previous one. The gated response
 * defaults to -120 dB because that is where a response's noise floor lives, and the harmonics to the
 * same, because on a clean DUT every curve sits below -80 dBc and a -60 dB floor shows an empty plot.
 */
const impulseYWindows = reactive<Record<import('./api/client').ImpulseView, { top: number; bottom: number }>>({
  Impulse: { top: 0, bottom: -60 },
  Response: { top: 0, bottom: -120 },
  Harmonics: { top: 0, bottom: -120 },
})
const impulseYTopDb = computed({
  get: () => impulseYWindows[impulseView.value].top,
  set: (value: number) => {
    impulseYWindows[impulseView.value].top = value
  },
})
const impulseYBottomDb = computed({
  get: () => impulseYWindows[impulseView.value].bottom,
  set: (value: number) => {
    impulseYWindows[impulseView.value].bottom = value
  },
})
const impulsePhaseTopDeg = ref(180)
const impulsePhaseBottomDeg = ref(-180)
const impulseTimeFromMs = ref(-1)
const impulseTimeToMs = ref(20)
const impulseFreqFromHz = ref(20)
const impulseFreqToHz = ref(20000)

let impulseDisplayRestoring = false

/** Restores an impulse-response type's stored chart state. Every field is optional and guarded. */
function applyStoredImpulseDisplay(paramsJson: string) {
  type StoredDisplay = Partial<import('./api/client').ImpulseResponseParameters>

  let stored: StoredDisplay
  try {
    stored = JSON.parse(paramsJson) as StoredDisplay
  } catch {
    return
  }

  impulseDisplayRestoring = true
  if (stored.chartView) impulseView.value = stored.chartView
  if (typeof stored.chartNormalisedImpulse === 'boolean') impulseNormalised.value = stored.chartNormalisedImpulse
  if (typeof stored.chartShowPhase === 'boolean') impulseShowPhase.value = stored.chartShowPhase
  if (typeof stored.chartShowCoherence === 'boolean') impulseShowCoherence.value = stored.chartShowCoherence
  if (typeof stored.chartThickLines === 'boolean') impulseThickLines.value = stored.chartThickLines
  if (typeof stored.chartShowDataPoints === 'boolean') impulseShowDataPoints.value = stored.chartShowDataPoints
  if (typeof stored.chartYTopDb === 'number') impulseYWindows.Impulse.top = stored.chartYTopDb
  if (typeof stored.chartYBottomDb === 'number') impulseYWindows.Impulse.bottom = stored.chartYBottomDb
  if (typeof stored.chartResponseYTopDb === 'number') impulseYWindows.Response.top = stored.chartResponseYTopDb
  if (typeof stored.chartResponseYBottomDb === 'number') impulseYWindows.Response.bottom = stored.chartResponseYBottomDb
  if (typeof stored.chartHarmonicsYTopDb === 'number') impulseYWindows.Harmonics.top = stored.chartHarmonicsYTopDb
  if (typeof stored.chartHarmonicsYBottomDb === 'number') impulseYWindows.Harmonics.bottom = stored.chartHarmonicsYBottomDb
  if (typeof stored.chartPhaseTopDeg === 'number') impulsePhaseTopDeg.value = stored.chartPhaseTopDeg
  if (typeof stored.chartPhaseBottomDeg === 'number') impulsePhaseBottomDeg.value = stored.chartPhaseBottomDeg
  if (typeof stored.chartTimeFromMs === 'number') impulseTimeFromMs.value = stored.chartTimeFromMs
  if (typeof stored.chartTimeToMs === 'number') impulseTimeToMs.value = stored.chartTimeToMs
  if (typeof stored.chartFreqFromHz === 'number' && stored.chartFreqFromHz > 0)
    impulseFreqFromHz.value = stored.chartFreqFromHz
  if (typeof stored.chartFreqToHz === 'number' && stored.chartFreqToHz > 0) impulseFreqToHz.value = stored.chartFreqToHz

  void nextTick(() => {
    impulseDisplayRestoring = false
  })
}

// Kind-gated for the same reason the response watcher is: another kind's params_json parses fine,
// and reading chart fields out of it would apply a different screen's state to this one.
watch(
  () => (isImpulseResponse.value ? currentType.value?.paramsJson : undefined),
  (paramsJson) => {
    if (paramsJson) applyStoredImpulseDisplay(paramsJson)
  },
  { immediate: true },
)

watch(
  [
    impulseView, impulseNormalised, impulseShowPhase, impulseShowCoherence, impulseThickLines,
    impulseShowDataPoints, impulseYWindows, impulseTimeFromMs, impulseTimeToMs,
    impulseFreqFromHz, impulseFreqToHz, impulsePhaseTopDeg, impulsePhaseBottomDeg,
  ],
  () => {
    if (!impulseDisplayRestoring && isImpulseResponse.value) markDirty()
  },
  { deep: true },
)

function runImpulseResponse(continuous: boolean) {
  const request = impulseConfigPanel.value?.buildRequest(continuous)
  if (!request) return

  return withErrorReporting(async () => {
    await startImpulse(request)
  })
}

function cancelImpulseResponse() {
  return withErrorReporting(async () => {
    await cancelImpulse()
  })
}

function clearImpulseResponse() {
  return withErrorReporting(async () => {
    await clearImpulse()
  })
}

/** A run lives server-side, so arriving on this screen mid-run has to re-attach rather than show an empty chart. */
watch(
  isImpulseResponse,
  (isImpulse) => {
    if (!isImpulse) return
    void refreshImpulse().catch(() => undefined)
    // The calibration list is what the reference picker offers; fetched on arrival rather than on
    // first open so the dropdown is never briefly empty.
    void refreshLoopbackReferences().catch(() => undefined)
  },
  { immediate: true },
)

/** The calibration just captured becomes the selected one — capturing it is what the user wanted it for. */
function onLoopbackCaptured(id: number) {
  const panel = impulseConfigPanel.value
  if (panel) panel.selectReference(id)
}

function buildTypeParamsJson(): string | undefined {
  if (isImpulseResponse.value) {
    const base = impulseConfigPanel.value?.buildParamsJson()
    if (!base) return undefined
    return JSON.stringify({
      ...JSON.parse(base),
      chartView: impulseView.value,
      chartNormalisedImpulse: impulseNormalised.value,
      chartShowPhase: impulseShowPhase.value,
      chartShowCoherence: impulseShowCoherence.value,
      chartThickLines: impulseThickLines.value,
      chartShowDataPoints: impulseShowDataPoints.value,
      chartYTopDb: impulseYWindows.Impulse.top,
      chartYBottomDb: impulseYWindows.Impulse.bottom,
      chartResponseYTopDb: impulseYWindows.Response.top,
      chartResponseYBottomDb: impulseYWindows.Response.bottom,
      chartHarmonicsYTopDb: impulseYWindows.Harmonics.top,
      chartHarmonicsYBottomDb: impulseYWindows.Harmonics.bottom,
      chartPhaseTopDeg: impulsePhaseTopDeg.value,
      chartPhaseBottomDeg: impulsePhaseBottomDeg.value,
      chartTimeFromMs: impulseTimeFromMs.value,
      chartTimeToMs: impulseTimeToMs.value,
      chartFreqFromHz: impulseFreqFromHz.value,
      chartFreqToHz: impulseFreqToHz.value,
    })
  }

  if (isFrequencyResponse.value) {
    const base = activeResponsePanel.value?.buildParamsJson()
    if (!base) return undefined
    return JSON.stringify({
      ...JSON.parse(base),
      chartView: responseView.value,
      chartProjection: responseProjection.value,
      chartNyquistRadius: responseNyquistRadius.value,
      chartNyquistRadiusFloorDb: responseNyquistRadiusFloorDb.value,
      chartInvertLoopSign: responseInvertLoopSign.value,
      chartRestoreDelay: responseRestoreDelay.value,
      chartShowLeft: responseShowLeft.value,
      chartShowRight: responseShowRight.value,
      chartShowPhase: responseShowPhase.value,
      chartShowCoherence: responseShowCoherence.value,
      chartShowOneDb: responseShowOneDb.value,
      chartShowThreeDb: responseShowThreeDb.value,
      chartThickLines: responseThickLines.value,
      chartShowDataPoints: responseShowDataPoints.value,
      chartYTopDb: responseYTopDb.value,
      chartYBottomDb: responseYBottomDb.value,
      chartYTopDbv: responseYTopDbv.value,
      chartYBottomDbv: responseYBottomDbv.value,
      chartXFromHz: responseXFromHz.value,
      chartXToHz: responseXToHz.value,
      chartPhaseTopDeg: responsePhaseTopDeg.value,
      chartPhaseBottomDeg: responsePhaseBottomDeg.value,
      chartAutofitOnDone: responseAutofitOnDone.value,
    })
  }

  if (!isSweepKind.value) return undefined
  const base = activeSweepPanel.value?.buildParamsJson()
  if (!base) return undefined
  // The x pair goes into whichever field name that kind's record declares — hertz bounds for a
  // frequency sweep, the selected axis and its own bounds for an amplitude one. Writing both would
  // put fields into a record that does not declare them, which the server's deserializer rejects.
  const xFields = isAmplitudeSweep.value
    ? { chartXAxis: sweepAmplitudeXAxis.value, chartXFrom: sweepXFrom.value, chartXTo: sweepXTo.value }
    : { chartXFromHz: sweepXFromHz.value, chartXToHz: sweepXToHz.value }
  const withDisplay = {
    ...JSON.parse(base),
    ...xFields,
    chartUnit: sweepUnit.value,
    chartShowNoiseFloor: sweepShowNoiseFloor.value,
    chartShowMagnitude: sweepShowMagnitude.value,
    chartShowThd: sweepShowThd.value,
    chartShowD2: sweepShowD2.value,
    chartShowD3: sweepShowD3.value,
    chartShowD4: sweepShowD4.value,
    chartShowD5: sweepShowD5.value,
    chartShowD6Plus: sweepShowD6Plus.value,
    chartShowLeft: sweepShowLeft.value,
    chartShowRight: sweepShowRight.value,
    chartThickLines: sweepThickLines.value,
    chartShowDataPoints: sweepShowDataPoints.value,
    chartYMaxPercent: sweepYMaxPercent.value,
    chartYMinPercent: sweepYMinPercent.value,
    chartYTopDb: sweepYTopDb.value,
    chartYBottomDb: sweepYBottomDb.value,
    chartAutofitOnDone: sweepAutofitOnDone.value,
  }
  return JSON.stringify(withDisplay)
}

function saveCurrentType() {
  return withErrorReporting(async () => {
    if (currentTypeId.value === null) return
    await saveMeasurementType(currentTypeId.value, buildStagedConfig(), buildTypeParamsJson())
  })
}

function confirmSaveTypeAs(name: string) {
  return withErrorReporting(async () => {
    if (currentTypeId.value === null) return
    await saveMeasurementTypeAs(currentTypeId.value, name, buildStagedConfig(), buildTypeParamsJson())
    saveTypeAsOpen.value = false
  })
}

function saveCurrentProject() {
  return withErrorReporting(async () => {
    if (currentProjectId.value === null) return
    await saveProject(currentProjectId.value, buildStagedConfig(), buildTypeParamsJson())
  })
}

function confirmSaveProjectAs(name: string) {
  return withErrorReporting(async () => {
    await saveProjectAs(name)
    saveProjectAsOpen.value = false
  })
}
</script>

<template>
  <div class="flex h-screen flex-col bg-neutral-50 text-neutral-900 dark:bg-neutral-950 dark:text-neutral-100">
    <UpdateBanner />
    <header class="flex items-center gap-3 border-b border-neutral-200 px-4 py-2 dark:border-neutral-800">
      <span class="text-sm font-semibold">QA403 audio analyzer</span>
      <button
        type="button"
        class="flex items-center gap-1.5 rounded border border-neutral-300 px-2 py-1 text-xs text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-800"
        @click="projectPanelOpen = !projectPanelOpen"
      >
        {{ currentProject?.name ?? 'Select project' }}
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
          <path d="m6 9 6 6 6-6" />
        </svg>
      </button>
      <button
        v-if="currentProjectId !== null"
        type="button"
        class="rounded border border-neutral-300 px-2 py-1 text-xs text-neutral-600 hover:bg-neutral-100 disabled:opacity-40 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-800"
        :disabled="!hasAnyDirtyInProject"
        title="Save every changed measurement in this project at once."
        @click="saveCurrentProject"
      >
        Save project
      </button>
      <button
        v-if="currentProjectId !== null"
        type="button"
        class="rounded border border-neutral-300 px-2 py-1 text-xs text-neutral-600 hover:bg-neutral-100 dark:border-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-800"
        title="Save this project and its measurements as a new project, leaving this one untouched."
        @click="saveProjectAsOpen = true"
      >
        Save project as…
      </button>
      <span class="flex-1"></span>
      <!-- The whole manual, from its index. Each measurement screen has its own "?" further down
           that jumps straight to that measurement's chapter. -->
      <button
        type="button"
        class="text-neutral-500 hover:text-neutral-900 dark:text-neutral-400 dark:hover:text-neutral-100"
        aria-label="Manual"
        title="Manual"
        @click="openManual()"
      >
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
          <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
          <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2Z" />
        </svg>
      </button>
      <button
        type="button"
        class="text-neutral-500 hover:text-neutral-900 dark:text-neutral-400 dark:hover:text-neutral-100"
        aria-label="Configuration"
        title="Configuration"
        @click="configDialogOpen = true"
      >
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
          <circle cx="12" cy="12" r="3" />
          <path
            d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z"
          />
        </svg>
      </button>
    </header>

    <div v-if="projectPanelOpen" class="border-b border-neutral-200 p-4 dark:border-neutral-800">
      <ProjectPanel />
    </div>

    <div class="flex flex-1 overflow-hidden">
      <MeasurementSidebar />

      <main class="flex flex-1 flex-col gap-4 overflow-y-auto p-4">
        <template v-if="currentTypeId !== null">
          <div class="flex flex-wrap items-center gap-2 text-sm">
            <span class="min-w-0 max-w-xs truncate font-medium" :title="currentType?.name">{{ currentType?.name }}</span>
            <span
              v-if="isCurrentTypeDirty"
              class="h-2 w-2 shrink-0 rounded-full bg-red-500"
              title="Unsaved changes"
            ></span>
            <!-- Shortcut into the manual chapter for whichever kind is selected. It sits in this
                 toolbar rather than in each screen's own template because the toolbar is the one
                 row all three screens share, so the button is in the same place everywhere. -->
            <button
              type="button"
              class="flex h-6 w-6 shrink-0 items-center justify-center rounded-full border border-neutral-300 text-xs font-semibold text-neutral-500 hover:bg-neutral-50 hover:text-neutral-900 dark:border-neutral-700 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
              :aria-label="`Help for ${currentType?.kind ? currentType.name : 'this measurement'}`"
              title="Open this measurement's chapter of the manual."
              @click="openManualForKind(currentType?.kind)"
            >
              ?
            </button>
            <button
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="!isCurrentTypeDirty"
              title="Save the current settings into this measurement."
              @click="saveCurrentType"
            >
              Save
            </button>
            <button
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
              title="Save the current settings as a new measurement in this project; this one reverts to its own last-saved settings."
              @click="saveTypeAsOpen = true"
            >
              Save as…
            </button>
            <button
              v-if="!isSweepKind && !isFrequencyResponse && !isImpulseResponse"
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="status?.acquiring"
              title="Saves the currently displayed (stopped) spectrum as a new measurement result — stop acquisition first."
              @click="openSaveDialog"
            >
              Save measurement result
            </button>
            <button
              v-if="!isSweepKind && !isFrequencyResponse && !isImpulseResponse"
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="status?.acquiring"
              title="Clears the currently displayed spectrum, oscilloscope trace and meters — stop acquisition first. Does not delete anything saved to History."
              @click="clearMeasurement"
            >
              Clear
            </button>
            <button
              v-if="isSweepKind"
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="sweepRunning || sweepPoints.length === 0"
              title="Saves the sweep on screen as a new measurement result — one stored point per frequency. A cancelled sweep can be saved too."
              @click="openSaveDialog"
            >
              Save sweep result
            </button>
            <button
              v-if="isFrequencyResponse"
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="responseRunning || responseTraces.length === 0"
              title="Saves the curves on screen as a new measurement result, with their bandwidth readings."
              @click="openSaveDialog"
            >
              Save response result
            </button>
            <button
              v-if="isImpulseResponse"
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="impulseRunning || impulseTraces.length === 0"
              title="Saves the impulse response on screen as a new measurement result, with its gated curves and harmonic distortion."
              @click="openSaveDialog"
            >
              Save impulse result
            </button>
            <button
              v-if="isImpulseResponse"
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="impulseRunning || impulseTraces.length === 0"
              title="Clears what is on screen — cancel a running measurement first. Does not delete anything saved to History, and leaves the settings alone."
              @click="clearImpulseResponse"
            >
              Clear
            </button>
            <button
              v-if="isFrequencyResponse"
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="responseRunning || responseTraces.length === 0"
              title="Clears the curves on screen — cancel a running measurement first. Does not delete anything saved to History, and leaves the settings alone."
              @click="clearFrequencyResponse"
            >
              Clear
            </button>
            <!-- The sweep screen's Clear, in the same slot and the same shape as the FFT screen's
                 above. Disabled while a sweep runs for the same reason CANCEL exists separately:
                 stopping a sweep and discarding its points are different intentions, and a sweep
                 that took two minutes should not be thrown away by one stray click. -->
            <button
              v-if="isSweepKind"
              type="button"
              class="rounded border border-neutral-300 px-2 py-1 text-xs hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
              :disabled="sweepRunning || sweepPoints.length === 0"
              title="Clears the sweep on screen and its preview traces — cancel a running sweep first. Does not delete anything saved to History, and leaves the sweep settings alone."
              @click="clearSweep"
            >
              Clear
            </button>
          </div>

          <!-- THD vs frequency screen: sweep settings rail on the left, the result chart in the
               center, display options on the right. RUN SWEEP sits at the top of the rail, in the
               same slot and the same shape as the FFT screen's RUN, so the button that starts a
               measurement is in one place whichever screen you are on. It drives SweepConfigPanel
               through its exposed run(), which keeps the form and its validation in the panel. -->
          <!-- Frequency response (chirp): the same three-column shape as the sweep screen, so RUN,
               the history list and the display rail all sit where they do everywhere else. What
               differs is the middle — one continuous curve rather than stepped points — and the
               Measurements readout under the settings, which the sweeps have no equivalent of. -->
          <!-- Impulse response: the same three-column shape as the response and sweep screens, so
               RUN, the history list and the display rail sit where they do everywhere else. What
               differs is the middle, which shows one of three things the same measurement produced —
               the impulse itself on a linear time axis, the gated magnitude and phase off it, or the
               harmonic distortion pulled out of the stretch of record the frequency-response
               measurement discards. -->
          <template v-if="isImpulseResponse">
            <div class="flex flex-col gap-4 lg:flex-row lg:items-start">
              <div class="flex w-full flex-col gap-3 lg:w-72 lg:flex-none">
                <div class="flex gap-3">
                  <button
                    type="button"
                    class="flex flex-1 items-center justify-center gap-2 rounded-lg bg-green-600 py-4 text-base font-bold tracking-wide text-white ring-2 ring-green-400 ring-offset-2 transition hover:bg-green-500 disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50 disabled:ring-0 dark:ring-offset-neutral-950"
                    :disabled="impulseRunning || !!impulseConfigPanel?.invalid"
                    :title="impulseConfigPanel?.invalid ?? 'Sweeps a chirp and re-measures the impulse response until you press Stop.'"
                    @click="runImpulseResponse(true)"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    RUN
                  </button>
                  <button
                    type="button"
                    class="flex flex-1 items-center justify-center gap-2 rounded-lg border border-neutral-300 py-4 text-sm font-bold tracking-wide transition hover:bg-neutral-50 disabled:cursor-not-allowed disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
                    :disabled="impulseRunning || !!impulseConfigPanel?.invalid"
                    :title="impulseConfigPanel?.invalid ?? 'Measures the impulse response once and stops.'"
                    @click="runImpulseResponse(false)"
                  >
                    SINGLE
                  </button>
                  <button
                    type="button"
                    class="flex flex-1 items-center justify-center gap-2 rounded-lg bg-red-600 py-4 text-base font-bold tracking-wide text-white transition hover:bg-red-500 disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50"
                    :class="impulseRunning ? 'animate-pulse ring-2 ring-red-400 ring-offset-2 dark:ring-offset-neutral-950' : ''"
                    :disabled="!impulseRunning"
                    title="Stops the measurement and silences the generator. The last completed pass stays on screen."
                    @click="cancelImpulseResponse"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                    STOP
                  </button>
                </div>

                <ImpulseResponseConfigPanel
                  ref="impulseConfigPanel"
                  :running="impulseRunning"
                  @capture-reference="loopbackDialogOpen = true"
                />
              </div>

              <div class="flex min-w-0 flex-1 flex-col gap-3">
                <ImpulseResponseChart
                  v-if="impulseView === 'Impulse'"
                  :impulse="impulseTrace"
                  :normalised="impulseNormalised"
                  :thick-lines="impulseThickLines"
                  :time-from-ms="impulseTimeFromMs"
                  :time-to-ms="impulseTimeToMs"
                  :y-top-db="impulseYTopDb"
                  :y-bottom-db="impulseYBottomDb"
                />
                <!-- The gated magnitude and phase are the same curves a frequency response produces,
                     so they are drawn by the same chart rather than a second one that would have to
                     be kept in step with it. -->
                <FrequencyResponseChart
                  v-else-if="impulseView === 'Response'"
                  run-label="RUN"
                  :traces="impulseTraces"
                  :overlays="[]"
                  view="Gain"
                  :show-left="true"
                  :show-right="false"
                  :show-phase="impulseShowPhase"
                  :show-coherence="impulseShowCoherence"
                  :show-one-db="false"
                  :show-three-db="false"
                  :thick-lines="impulseThickLines"
                  :show-data-points="false"
                  :y-top-db="impulseYTopDb"
                  :y-bottom-db="impulseYBottomDb"
                  :y-top-dbv="impulseYTopDb"
                  :y-bottom-dbv="impulseYBottomDb"
                  :x-from-hz="impulseFreqFromHz"
                  :x-to-hz="impulseFreqToHz"
                  :phase-top-deg="impulsePhaseTopDeg"
                  :phase-bottom-deg="impulsePhaseBottomDeg"
                  phase-kind="PerTrace"
                />
                <ImpulseHarmonicsChart
                  v-else
                  :harmonics="impulseHarmonics"
                  :thick-lines="impulseThickLines"
                  :show-data-points="impulseShowDataPoints"
                  :freq-from-hz="impulseFreqFromHz"
                  :freq-to-hz="impulseFreqToHz"
                  :y-top-db="impulseYTopDb"
                  :y-bottom-db="impulseYBottomDb"
                />

                <!-- The gate's resolution limit, restated under the chart it applies to. It is the
                     one number that decides whether the left-hand end of a gated response means
                     anything, and a reader looking at the curve is not looking at the settings rail. -->
                <p
                  v-if="impulseView === 'Response' && impulseGateResolutionHz > 0"
                  class="text-[11px] text-amber-700 dark:text-amber-500"
                >
                  Gated to {{ impulseStatus?.gatePreMs }}/{{ impulseStatus?.gatePostMs }} ms — nothing below
                  ≈{{ impulseGateResolutionHz.toFixed(0) }} Hz is resolved, however convincing the curve looks there.
                </p>

                <MeasurementHistoryPanel @error="reportError" />
              </div>

              <div class="flex w-full flex-col gap-3 lg:w-64 lg:flex-none">
                <ImpulseResponseDisplayPanel
                  v-model:view="impulseView"
                  v-model:normalised-impulse="impulseNormalised"
                  v-model:show-phase="impulseShowPhase"
                  v-model:show-coherence="impulseShowCoherence"
                  v-model:thick-lines="impulseThickLines"
                  v-model:show-data-points="impulseShowDataPoints"
                  v-model:y-top-db="impulseYTopDb"
                  v-model:y-bottom-db="impulseYBottomDb"
                  v-model:time-from-ms="impulseTimeFromMs"
                  v-model:time-to-ms="impulseTimeToMs"
                  v-model:freq-from-hz="impulseFreqFromHz"
                  v-model:freq-to-hz="impulseFreqToHz"
                  v-model:phase-top-deg="impulsePhaseTopDeg"
                  v-model:phase-bottom-deg="impulsePhaseBottomDeg"
                />

                <MeasurementChecklist @error="reportError" />
              </div>
            </div>

            <LoopbackReferenceDialog
              :open="loopbackDialogOpen"
              :defaults="impulseConfigPanel?.referenceCaptureDefaults() ?? { frequencySpanHz: 96000, resolutionMultiplier: 1, chirpStartHz: 2 }"
              @close="loopbackDialogOpen = false"
              @captured="onLoopbackCaptured"
            />
          </template>

          <template v-else-if="isFrequencyResponse">
            <div class="flex flex-col gap-4 lg:flex-row lg:items-start">
              <div class="flex w-full flex-col gap-3 lg:w-72 lg:flex-none">
                <!-- RUN / SINGLE / STOP, the same trio and the same shape as the FFT screen's, so
                     the button that starts a measurement sits in one place whichever screen you are
                     on. RUN repeats until stopped; SINGLE measures once and stops itself. -->
                <div class="flex gap-3">
                  <button
                    v-if="isChirpResponse"
                    type="button"
                    class="flex flex-1 items-center justify-center gap-2 rounded-lg bg-green-600 py-4 text-base font-bold tracking-wide text-white ring-2 ring-green-400 ring-offset-2 transition hover:bg-green-500 disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50 disabled:ring-0 dark:ring-offset-neutral-950"
                    :disabled="responseRunning || !!responseConfigPanel?.invalid"
                    :title="
                      responseConfigPanel?.invalid ??
                      'Sweeps a chirp across the band and re-measures until you press Stop.'
                    "
                    @click="runFrequencyResponse(true)"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    RUN
                  </button>
                  <button
                    v-if="isChirpResponse"
                    type="button"
                    class="flex flex-1 items-center justify-center gap-2 rounded-lg border border-neutral-300 py-4 text-sm font-bold tracking-wide transition hover:bg-neutral-50 disabled:cursor-not-allowed disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
                    :disabled="responseRunning || !!responseConfigPanel?.invalid"
                    :title="
                      responseConfigPanel?.invalid ??
                      'Measures the frequency response once and stops.'
                    "
                    @click="runFrequencyResponse(false)"
                  >
                    SINGLE
                  </button>
                  <!-- One button rather than the chirp's RUN/SINGLE pair: a stepped run is minutes
                       long, so repeating it until stopped is not a mode anyone wants. Same shape and
                       same slot as the sweep screen's RUN SWEEP. -->
                  <button
                    v-if="isSteppedResponse"
                    type="button"
                    class="flex flex-1 items-center justify-center gap-2 rounded-lg bg-green-600 py-4 text-base font-bold tracking-wide text-white ring-2 ring-green-400 ring-offset-2 transition hover:bg-green-500 disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50 disabled:ring-0 dark:ring-offset-neutral-950"
                    :disabled="responseRunning || !!steppedResponseConfigPanel?.invalid"
                    :title="
                      steppedResponseConfigPanel?.invalid ??
                      'Steps a tone across the band, measuring level and phase at each point.'
                    "
                    @click="steppedResponseConfigPanel?.run()"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    RUN SWEEP
                  </button>
                  <button
                    type="button"
                    class="flex flex-1 items-center justify-center gap-2 rounded-lg bg-red-600 py-4 text-base font-bold tracking-wide text-white transition hover:bg-red-500 disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50"
                    :class="responseRunning ? 'animate-pulse ring-2 ring-red-400 ring-offset-2 dark:ring-offset-neutral-950' : ''"
                    :disabled="!responseRunning"
                    title="Stops the measurement and silences the generator. The curves from the last completed pass stay on screen."
                    @click="cancelFrequencyResponse"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                    STOP
                  </button>
                </div>

                <FrequencyResponseConfigPanel
                  v-if="isChirpResponse"
                  ref="responseConfigPanel"
                  :running="responseRunning"
                />
                <SteppedResponseConfigPanel
                  v-else
                  ref="steppedResponseConfigPanel"
                  :running="responseRunning"
                  @run="runSteppedResponse"
                />

                <ResponseStabilityPanel
                  v-if="activeResponseProjection !== 'Bode' && responsePolarChart"
                  :margins="responsePolarChart.margins"
                  :radius-mode="responseNyquistRadius"
                  :projection="activeResponseProjection"
                  v-model:invert-sign="responseInvertLoopSign"
                />

                <ResponsePoleZeroPanel
                  v-if="activeResponseProjection !== 'Bode' && responsePolarChart"
                  ref="responsePoleZeroPanel"
                  :curve="responsePolarChart.curve"
                  v-model:order="responseFitOrder"
                  v-model:auto-order="responseFitAutoOrder"
                />

                <ResponseMeasurementsPanel
                  v-model:show-one-db="responseShowOneDb"
                  v-model:show-three-db="responseShowThreeDb"
                  :traces="responseTraces"
                  :view="responseView"
                  :show-left="responseShowLeft"
                />
              </div>

              <div class="flex min-w-0 flex-1 flex-col gap-3">
                <div class="flex items-center gap-3 font-mono text-xs text-neutral-500 dark:text-neutral-400">
                  <span>{{ responseProgressLabel || 'Idle' }}</span>
                  <!-- Number.isFinite, not truthiness: 0 dBV is a perfectly ordinary drive level
                       and a plain v-if hides it, which is exactly what a loopback measurement shows. -->
                  <span v-if="Number.isFinite(responseGeneratorAmpDbv) && responseTraces.length">
                    · gen {{ responseGeneratorAmpDbv.toFixed(2) }} dBV
                  </span>
                  <span v-if="responseBinHz">· {{ responseBinHz.toFixed(2) }} Hz/bin</span>
                  <!-- Only shown when the right input was the reference: that is the one case
                       where the recovered rotation is the DUT's delay rather than the record's
                       arbitrary start point in a periodic stimulus. -->
                  <span v-if="responseLatencySamples !== null">
                    · DUT delay {{ responseLatencySamples.toFixed(1) }} samples
                  </span>
                </div>
                <p v-if="responseError" class="text-xs text-red-600 dark:text-red-400">{{ responseError }}</p>

                <!-- show-coherence is forced off for a stepped run whatever the stored preference
                     says: its coherence is 1.0 by construction, so drawing it would be a flat line
                     claiming a measurement nobody made. -->
                <FrequencyResponseChart
                  v-if="activeResponseProjection === 'Bode'"
                  :run-label="isSteppedResponse ? 'RUN SWEEP' : 'RUN'"
                  :traces="responseTraces"
                  :overlays="visibleResponseOverlays"
                  :view="responseView"
                  :show-left="responseShowLeft"
                  :show-right="!responseRightChannelIsReference && responseShowRight"
                  :show-phase="responsePhaseAvailable && responseShowPhase"
                  :phase-kind="responsePhaseKind"
                  :show-coherence="isChirpResponse && responseShowCoherence"
                  :show-one-db="responseShowOneDb"
                  :show-three-db="responseShowThreeDb"
                  :thick-lines="responseThickLines"
                  :show-data-points="responseShowDataPoints"
                  :y-top-db="responseYTopDb"
                  :y-bottom-db="responseYBottomDb"
                  :y-top-dbv="responseYTopDbv"
                  :y-bottom-dbv="responseYBottomDbv"
                  :x-from-hz="responseXFromHz"
                  :x-to-hz="responseXToHz"
                  :phase-top-deg="responsePhaseTopDeg"
                  :phase-bottom-deg="responsePhaseBottomDeg"
                />
                <ResponsePolarChart
                  v-else
                  ref="responsePolarChart"
                  :run-label="isSteppedResponse ? 'RUN SWEEP' : 'RUN'"
                  :traces="responseTraces"
                  :overlays="visibleResponseOverlays"
                  :projection="activeResponseProjection"
                  :radius-mode="responseNyquistRadius"
                  :radius-floor-db="responseNyquistRadiusFloorDb"
                  :invert-sign="responseInvertLoopSign"
                  :restore-delay="responseRestoreDelay"
                  :latency-samples="responseLatencySamples"
                  :sample-rate-hz="responseSampleRateHz"
                  :phase-kind="responsePhaseKind"
                  :x-from-hz="responseXFromHz"
                  :x-to-hz="responseXToHz"
                  :thick-lines="responseThickLines"
                  :show-data-points="responseShowDataPoints"
                  :fitted-model="responsePoleZeroPanel?.fit?.model ?? null"
                />

                <MeasurementHistoryPanel @error="reportError" />
              </div>

              <div class="flex w-full flex-col gap-3 lg:w-64 lg:flex-none">
                <FrequencyResponseDisplayPanel
                  :allow-coherence="isChirpResponse"
                  v-model:view="responseView"
                  v-model:projection="responseProjection"
                  v-model:nyquist-radius="responseNyquistRadius"
                  v-model:nyquist-radius-floor-db="responseNyquistRadiusFloorDb"
                  v-model:restore-delay="responseRestoreDelay"
                  v-model:show-left="responseShowLeft"
                  v-model:show-right="responseShowRight"
                  v-model:show-phase="responseShowPhase"
                  v-model:show-coherence="responseShowCoherence"
                  v-model:show-one-db="responseShowOneDb"
                  v-model:show-three-db="responseShowThreeDb"
                  v-model:thick-lines="responseThickLines"
                  v-model:show-data-points="responseShowDataPoints"
                  v-model:y-top-db="responseYTopDb"
                  v-model:y-bottom-db="responseYBottomDb"
                  v-model:y-top-dbv="responseYTopDbv"
                  v-model:y-bottom-dbv="responseYBottomDbv"
                  v-model:x-from-hz="responseXFromHz"
                  v-model:x-to-hz="responseXToHz"
                  v-model:phase-top-deg="responsePhaseTopDeg"
                  v-model:phase-bottom-deg="responsePhaseBottomDeg"
                  v-model:autofit-on-done="responseAutofitOnDone"
                />

                <MeasurementChecklist @error="reportError" />
              </div>
            </div>
          </template>

          <template v-else-if="isSweepKind">
            <div class="flex flex-col gap-4 lg:flex-row lg:items-start">
              <div class="flex w-full flex-col gap-3 lg:w-72 lg:flex-none">
                <div class="flex gap-3">
                  <button
                    v-if="!sweepRunning"
                    type="button"
                    class="flex flex-1 items-center justify-center gap-2 rounded-lg bg-green-600 py-4 text-base font-bold tracking-wide text-white ring-2 ring-green-400 ring-offset-2 transition hover:bg-green-500 disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50 disabled:ring-0 dark:ring-offset-neutral-950"
                    :disabled="!!activeSweepPanel?.invalid"
                    :title="
                      activeSweepPanel?.invalid ??
                      (isAmplitudeSweep
                        ? 'Steps the generator across the configured voltage range at one frequency and records the harmonics at each point.'
                        : 'Steps the generator across the configured frequency range and records the harmonics at each point.')
                    "
                    @click="activeSweepPanel?.run()"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    RUN SWEEP
                  </button>
                  <button
                    v-else
                    type="button"
                    class="flex flex-1 animate-pulse items-center justify-center gap-2 rounded-lg bg-red-600 py-4 text-base font-bold tracking-wide text-white ring-2 ring-red-400 ring-offset-2 transition hover:bg-red-500 dark:ring-offset-neutral-950"
                    title="Stops the sweep and silences the generator. The points already measured are kept."
                    @click="cancelSweep"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                    CANCEL
                  </button>
                </div>

                <AmplitudeSweepConfigPanel
                  v-if="isAmplitudeSweep"
                  ref="amplitudeSweepConfigPanel"
                  :running="sweepRunning"
                  @run="runAmplitudeSweep"
                />
                <SweepConfigPanel v-else ref="sweepConfigPanel" :running="sweepRunning" @run="runSweep" />

                <!-- Each channel's point currently measuring (while running) or nearest the chart
                     cursor (once done) — see selectedSweepPointFor. Hidden entirely once the sweep
                     is cleared (or before the first run) rather than showing an empty "No data"
                     chart — Clear should make these disappear, not just go blank. -->
                <template v-if="sweepPoints.length > 0">
                  <SweepFftPreviewChart :point-left="sweepSelectedLeft" :point-right="sweepSelectedRight" />
                  <SweepScopePreviewChart :point-left="sweepSelectedLeft" :point-right="sweepSelectedRight" />
                </template>

                <!-- One more Spectrum/Oscilloscope pair per history item checked on in the
                     checklist, underneath the live pair above — same components, same left-blue/
                     right-red convention, just fed that result's own selected point. -->
                <template v-for="overlay in visibleSweepOverlays" :key="overlay.id">
                  <SweepFftPreviewChart
                    :label="overlay.detail.name"
                    :point-left="selectedSweepPointForOverlay(overlay, 0)"
                    :point-right="selectedSweepPointForOverlay(overlay, 1)"
                  />
                  <SweepScopePreviewChart
                    :label="overlay.detail.name"
                    :point-left="selectedSweepPointForOverlay(overlay, 0)"
                    :point-right="selectedSweepPointForOverlay(overlay, 1)"
                  />
                </template>
              </div>

              <div class="flex min-w-0 flex-1 flex-col gap-3">
                <div class="flex items-center gap-3 font-mono text-xs text-neutral-500 dark:text-neutral-400">
                  <span>{{ sweepProgressLabel || 'Idle' }}</span>
                  <span v-if="sweepGeneratorAmpDbv">· gen {{ sweepGeneratorAmpDbv.toFixed(2) }} dBV</span>
                  <span v-if="sweepPoints.length">· {{ sweepPoints.length }} readings</span>
                </div>
                <p v-if="sweepError" class="text-xs text-red-600 dark:text-red-400">{{ sweepError }}</p>

                <ThdSweepChart
                  :unit="sweepUnit"
                  :show-noise-floor="sweepShowNoiseFloor"
                  :show-magnitude="sweepShowMagnitude"
                  :show-thd="sweepShowThd"
                  :show-d2="sweepShowD2"
                  :show-d3="sweepShowD3"
                  :show-d4="sweepShowD4"
                  :show-d5="sweepShowD5"
                  :show-d6-plus="sweepShowD6Plus"
                  :show-left="sweepShowLeft"
                  :show-right="sweepShowRight"
                  :thick-lines="sweepThickLines"
                  :show-data-points="sweepShowDataPoints"
                  :y-max-percent="sweepYMaxPercent"
                  :y-min-percent="sweepYMinPercent"
                  :y-top-db="sweepYTopDb"
                  :y-bottom-db="sweepYBottomDb"
                  :x-from="sweepXFrom"
                  :x-to="sweepXTo"
                  :x-axis="sweepChartXAxis"
                  :overlays="overlays"
                  :selected-left="sweepSelectedLeft"
                  :selected-right="sweepSelectedRight"
                  :overlay-selections="sweepOverlaySelections"
                  @cursor-x="onSweepCursorX"
                />

                <MeasurementHistoryPanel @error="reportError" />
              </div>

              <div class="flex w-full flex-col gap-3 lg:w-64 lg:flex-none">
                <SweepDisplayPanel
                  v-model:unit="sweepUnit"
                  v-model:show-noise-floor="sweepShowNoiseFloor"
                  v-model:show-magnitude="sweepShowMagnitude"
                  v-model:show-thd="sweepShowThd"
                  v-model:show-d2="sweepShowD2"
                  v-model:show-d3="sweepShowD3"
                  v-model:show-d4="sweepShowD4"
                  v-model:show-d5="sweepShowD5"
                  v-model:show-d6-plus="sweepShowD6Plus"
                  v-model:show-left="sweepShowLeft"
                  v-model:show-right="sweepShowRight"
                  v-model:thick-lines="sweepThickLines"
                  v-model:show-data-points="sweepShowDataPoints"
                  v-model:y-max-percent="sweepYMaxPercent"
                  v-model:y-min-percent="sweepYMinPercent"
                  v-model:y-top-db="sweepYTopDb"
                  v-model:y-bottom-db="sweepYBottomDb"
                  v-model:x-from="sweepXFrom"
                  v-model:x-to="sweepXTo"
                  v-model:x-axis="sweepChartXAxis"
                  v-model:autofit-on-done="sweepAutofitOnDone"
                />

                <MeasurementChecklist @error="reportError" />
              </div>
            </div>
          </template>

          <!-- FFT Spectrum screen, matching the wireframe: settings rail on the left, chart in the
               center, display options + history checklist on the right. -->
          <template v-else>
          <div class="flex flex-col gap-4 lg:flex-row lg:items-start">
            <div class="flex w-full flex-col gap-3 lg:w-72 lg:flex-none">
              <div class="flex gap-3">
                <button
                  type="button"
                  class="flex flex-1 items-center justify-center gap-2 rounded-lg py-4 text-base font-bold tracking-wide text-white transition disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50 disabled:ring-0"
                  :class="status?.acquiring
                    ? 'bg-green-800'
                    : 'bg-green-600 hover:bg-green-500 ring-2 ring-green-400 ring-offset-2 dark:ring-offset-neutral-950'"
                  :disabled="status?.acquiring"
                  title="Starts the generator(s) and acquisition using whatever is currently configured in the Generator and FFT settings panels."
                  @click="runFromCurrentSettings"
                >
                  <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                  RUN
                </button>
                <button
                  type="button"
                  class="flex flex-1 items-center justify-center gap-2 rounded-lg py-4 text-base font-bold tracking-wide text-white transition disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50 disabled:ring-0"
                  :class="status?.acquiring
                    ? 'bg-blue-800'
                    : 'bg-blue-600 hover:bg-blue-500 ring-2 ring-blue-400 ring-offset-2 dark:ring-offset-neutral-950'"
                  :disabled="status?.acquiring"
                  title="Same as Run, but captures one settled spectrum (the configured average count, or one FFT if averaging is off) and stops automatically — no need to press Stop."
                  @click="singleShotCapture"
                >
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><circle cx="12" cy="12" r="9" /></svg>
                  SINGLE
                </button>
                <button
                  type="button"
                  class="flex flex-1 items-center justify-center gap-2 rounded-lg py-4 text-base font-bold tracking-wide text-white transition disabled:cursor-not-allowed disabled:bg-neutral-400 disabled:opacity-50 disabled:ring-0"
                  :class="status?.acquiring
                    ? 'bg-red-600 hover:bg-red-500 ring-2 ring-red-400 ring-offset-2 animate-pulse dark:ring-offset-neutral-950'
                    : 'bg-red-800'"
                  :disabled="!status?.acquiring && !status?.generatorLeft.enabled && !status?.generatorRight.enabled"
                  title="Stops acquisition and silences both generator channels."
                  @click="stopMeasurement"
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                  STOP
                </button>
              </div>

              <GeneratorPanel ref="generatorPanel" @change="applyGeneratorLive" />
              <SpectrumConfigPanel
                ref="spectrumConfigPanel"
                :ranges="ranges"
                @error="reportError"
                @sample-rate-change="onSampleRateChange"
              />
            </div>

            <div class="min-w-0 flex-1">
              <div class="mb-3 flex flex-wrap items-start gap-3">
                <div class="flex flex-1 flex-wrap gap-3">
                  <GeneratorMetricsPanel />
                  <MetricsPanel
                    v-if="spectrumConfig?.processLeft ?? true"
                    channel-label="L"
                    :meters="meters[0] ?? null"
                    :generator-tones="status?.generatorLeft.tones ?? []"
                  />
                  <MetricsPanel
                    v-if="spectrumConfig?.processRight ?? true"
                    channel-label="R"
                    :meters="meters[1] ?? null"
                    :generator-tones="status?.generatorRight.tones ?? []"
                  />
                </div>
                <button
                  type="button"
                  class="shrink-0 rounded-lg border border-neutral-300 px-3 py-1.5 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
                  title="Choose which metrics appear above the graph."
                  @click="metricPickerOpen = true"
                >
                  + Metrics
                </button>
              </div>

              <div v-if="averagesLabel" class="mb-2 font-mono text-xs text-neutral-500 dark:text-neutral-400">
                Averages: {{ averagesLabel }}
              </div>

              <SpectrumChart
                :spectrum="spectrum"
                :meters="meters"
                :overlays="overlays"
                @viewport-change="setViewport"
              />
              <WaterfallChart v-if="chartShowWaterfall" class="mt-3" :spectrum="spectrum" />
              <OscilloscopeChart v-if="chartShowOscilloscope" class="mt-3" :scope="scope" :status="status" />
            </div>

            <ChartDisplayPanel>
              <MeasurementChecklist @error="reportError" />
            </ChartDisplayPanel>
          </div>

          <!-- Everything below isn't part of the new FFT Spectrum design — kept here for now, to be
               repositioned later. -->
          <OverlayList />

          <!-- Harmonics/other-peaks only mean something relative to a single fundamental tone —
               Multitone's comb and White noise have none (see showFundamentalMarkers above). The
               Display panel's own two checkboxes are on top of that, and can hide either table on a
               stimulus that does have a fundamental. -->
          <div
            v-if="showFundamentalMarkers && (chartShowHarmonics || chartShowOtherPeaks)"
            class="grid grid-cols-1 gap-4 xl:grid-cols-2"
          >
            <div class="flex flex-col gap-4">
              <HarmonicsTable v-if="chartShowHarmonics" :meters="meters" :channel="0" channel-label="L" />
              <OtherPeaksTable v-if="chartShowOtherPeaks" :meters="meters" :channel="0" channel-label="L" />
            </div>
            <div class="flex flex-col gap-4">
              <HarmonicsTable v-if="chartShowHarmonics" :meters="meters" :channel="1" channel-label="R" />
              <OtherPeaksTable v-if="chartShowOtherPeaks" :meters="meters" :channel="1" channel-label="R" />
            </div>
          </div>

          <!-- History details sits at the bottom, spanning the whole measurement view rather than
               the centre column it used to share with the charts: its rows are a name, a timestamp,
               per-channel headlines and three buttons, which a third of the width could only wrap. -->
          <MeasurementHistoryPanel @error="reportError" />
          </template>
        </template>

        <p v-else class="text-sm text-neutral-400 dark:text-neutral-600">
          Select a measurement from the sidebar, or add a new one to get started.
        </p>
      </main>
    </div>

    <DeviceStatusBar
      :status="status"
      :error="error"
      :connecting="connecting"
      @set-simulated-profile="setSimulatedProfile"
      @connect="connectDevice"
    />

    <SaveMeasurementDialog :open="saveDialogOpen" @close="saveDialogOpen = false" />
    <SaveAsDialog
      :open="saveTypeAsOpen"
      title="Save measurement as…"
      label="New measurement name"
      @close="saveTypeAsOpen = false"
      @confirm="confirmSaveTypeAs"
    />
    <SaveAsDialog
      :open="saveProjectAsOpen"
      title="Save project as…"
      label="New project name"
      @close="saveProjectAsOpen = false"
      @confirm="confirmSaveProjectAs"
    />
    <MetricPickerDialog :open="metricPickerOpen" @close="metricPickerOpen = false" />
    <ConfigDialog :open="configDialogOpen" @close="configDialogOpen = false" />
    <ManualDialog />
  </div>
</template>
