<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, provide, reactive, ref, shallowRef, watch } from 'vue'
import { api, ApiError, type ResponsePhaseKindName, type SimulatedProfileRequest, type StagedMeasurementConfig, type ThdSweepPoint } from './api/client'
import { imdSpecFromStimulus, type ImdPresetId } from './imd/presets'
import { generatorLevelUnitOf } from './generatorLevel'
import { useDevice } from './composables/useDevice'
import { DEVICE_STATUS_KEY } from './simulator/hints'
import { useSpectrumStream, type FftDisplayState } from './composables/useSpectrumStream'
import { useMeasurements, type OverlayEntry } from './composables/useMeasurements'
import { curveOverlayScreen, sweepOverlayScreen, type CurveOverlayScreen } from './measurements/overlayScope'
import { sweepOverlays } from './measurements/sweepOverlayStyles'
import { spectrumAxisOverlay } from './measurements/spectrumOverlayColors'
import { useProjects } from './composables/useProjects'
import { useTrash } from './composables/useTrash'
import { useProjectSession } from './composables/useProjectSession'
import { useSpectrumConfig } from './composables/useSpectrumConfig'
import { useMetricSelection } from './composables/useMetricSelection'
import { DEFAULT_LOAD_OHMS, useLoadOhms } from './composables/useLoadOhms'
import { useScopeDisplay } from './composables/useScopeDisplay'
import { useChartDisplay } from './composables/useChartDisplay'
import { useAnnotations } from './composables/useAnnotations'
import { useUserWeighting } from './composables/useUserWeighting'
import { weightingLabel } from './weighting'
import { useGeneratorStimulus } from './composables/useGeneratorStimulus'
import { useCaptureDelay } from './composables/useCaptureDelay'
import { useExternalGain } from './composables/useExternalGain'
import { useAutosave } from './composables/useAutosave'
import { formatAveraging, stimulusSummary } from './metrics'
import { isWaveKind } from './periodicWave'
import { stopRun } from './deviceControl'
import DeviceStatusBar from './components/DeviceStatusBar.vue'
import MeasurementSidebar from './components/MeasurementSidebar.vue'
import TrashView from './components/TrashView.vue'
import ProjectMenu, { type ProjectMenuAction } from './components/ProjectMenu.vue'
import ProjectDialog from './components/ProjectDialog.vue'
import FileBrowserDialog from './components/FileBrowserDialog.vue'
import ConfirmDialog from './components/ConfirmDialog.vue'
import WelcomeScreen from './components/WelcomeScreen.vue'
import { type ProjectDialogMode, type ProjectForm } from './projects/projectDialogModel'
import GeneratorPanel from './components/GeneratorPanel.vue'
import SpectrumConfigPanel from './components/SpectrumConfigPanel.vue'
import BandwidthWeightingPanel from './components/BandwidthWeightingPanel.vue'
import SpectrumChart from './components/SpectrumChart.vue'
import SpectrogramChart from './components/SpectrogramChart.vue'
import OscilloscopeChart from './components/OscilloscopeChart.vue'
import MetricsPanel from './components/MetricsPanel.vue'
import GeneratorMetricsPanel from './components/GeneratorMetricsPanel.vue'
import HarmonicsTable from './components/HarmonicsTable.vue'
import ImdProductsTable from './components/ImdProductsTable.vue'
import MultitoneTable from './components/MultitoneTable.vue'
import OtherPeaksTable from './components/OtherPeaksTable.vue'
import MeasurementHistoryPanel from './components/MeasurementHistoryPanel.vue'
import MeasurementNotesPanel from './components/MeasurementNotesPanel.vue'
import MeasurementChecklist from './components/MeasurementChecklist.vue'
import { closeSimulationImport, simulationImportOpen } from './measurements/simulationImport'
import SimulationImportDialog from './components/SimulationImportDialog.vue'
import AcSimulationImportDialog from './components/AcSimulationImportDialog.vue'
import AcImpedanceImportDialog from './components/AcImpedanceImportDialog.vue'
import ChartDisplayPanel from './components/ChartDisplayPanel.vue'
import MeasurementLayout from './components/MeasurementLayout.vue'
import SaveMeasurementDialog from './components/SaveMeasurementDialog.vue'
import SaveAsDialog from './components/SaveAsDialog.vue'
import OverlayList from './components/OverlayList.vue'
import MetricPickerDialog from './components/MetricPickerDialog.vue'
import ConfigDialog from './components/ConfigDialog.vue'
import ProgrammableLoadDialog from './components/ProgrammableLoadDialog.vue'
import Qa403Dialog from './components/Qa403Dialog.vue'
import SoundCardDialog from './components/SoundCardDialog.vue'
import { useProgrammableLoad } from './composables/useProgrammableLoad'
import { useSoundCard } from './composables/useSoundCard'
import AboutDialog from './components/AboutDialog.vue'
import TonebenchLogo from './components/TonebenchLogo.vue'
import HeaderIcon from './components/HeaderIcon.vue'
import ManualDialog from './manual/ManualDialog.vue'
import { useManual } from './manual/useManual'
import {
  responseRunTone,
  responseRunToneByName,
  statusDetail,
  statusHeadline,
  sweepRunTone,
  type RunTone,
} from './measurements/runStatus'
import UpdateBanner from './components/UpdateBanner.vue'
import SweepConfigPanel from './components/SweepConfigPanel.vue'
import AmplitudeSweepConfigPanel from './components/AmplitudeSweepConfigPanel.vue'
import ImdSweepConfigPanel from './components/ImdSweepConfigPanel.vue'
import SweepDisplayPanel from './components/SweepDisplayPanel.vue'
import ThdSweepChart from './components/ThdSweepChart.vue'
import SweepFftPreviewChart from './components/SweepFftPreviewChart.vue'
import SweepScopePreviewChart from './components/SweepScopePreviewChart.vue'
import FrequencyResponseConfigPanel from './components/FrequencyResponseConfigPanel.vue'
import ImpedanceConfigPanel from './components/ImpedanceConfigPanel.vue'
import ImpedanceDisplayPanel from './components/ImpedanceDisplayPanel.vue'
import ImpedanceChart from './components/ImpedanceChart.vue'
import ImpedancePlaneChart from './components/ImpedancePlaneChart.vue'
import ImpedanceReadoutPanel from './components/ImpedanceReadoutPanel.vue'
import ImpedanceComparisonPanel from './components/ImpedanceComparisonPanel.vue'
import SteppedResponseConfigPanel from './components/SteppedResponseConfigPanel.vue'
import CrosstalkConfigPanel from './components/CrosstalkConfigPanel.vue'
import CrosstalkDisplayPanel from './components/CrosstalkDisplayPanel.vue'
import CrosstalkMeasurementsPanel from './components/CrosstalkMeasurementsPanel.vue'
import OutputImpedanceConfigPanel from './components/OutputImpedanceConfigPanel.vue'
import OutputImpedanceDisplayPanel from './components/OutputImpedanceDisplayPanel.vue'
import OutputImpedanceReadoutPanel from './components/OutputImpedanceReadoutPanel.vue'
import FrequencyResponseDisplayPanel from './components/FrequencyResponseDisplayPanel.vue'
import FrequencyResponseChart from './components/FrequencyResponseChart.vue'
import ImpulseResponseConfigPanel from './components/ImpulseResponseConfigPanel.vue'
import ImpulseResponseDisplayPanel from './components/ImpulseResponseDisplayPanel.vue'
import ImpulseResponseChart from './components/ImpulseResponseChart.vue'
import ImpulseHarmonicsChart from './components/ImpulseHarmonicsChart.vue'
import DecaySurfaceChart from './components/DecaySurfaceChart.vue'
import LoopbackReferenceDialog from './components/LoopbackReferenceDialog.vue'
import ResponsePolarChart from './components/ResponsePolarChart.vue'
import ResponseSPlaneChart from './components/ResponseSPlaneChart.vue'
import ResponseStabilityPanel from './components/ResponseStabilityPanel.vue'
import ResponsePoleZeroPanel from './components/ResponsePoleZeroPanel.vue'
import ResponseFormulaPanel from './components/ResponseFormulaPanel.vue'
import ResponseMeasurementsPanel from './components/ResponseMeasurementsPanel.vue'
import ReportDialog from './components/ReportDialog.vue'
import ProjectReportDialog from './components/ProjectReportDialog.vue'
import ProjectExportDialog from './components/ProjectExportDialog.vue'
import ProjectImportDialog from './components/ProjectImportDialog.vue'
import ToolbarIcon from './components/ToolbarIcon.vue'
import MeasurementStatusBar from './components/MeasurementStatusBar.vue'
import ScreenshotDialog from './components/ScreenshotDialog.vue'
import IssueReportDialog from './components/IssueReportDialog.vue'
import SplashScreen from './components/SplashScreen.vue'
import { DEFAULT_RESPONSE_DISPLAY, responseDisplayFromParams, responsePhaseKindFromParams, type ResponseDisplay } from './response/display'
import { sweepDisplayFromParams, type SweepDisplay } from './sweep/display'
import { impedanceDisplayFromParams, type ImpedanceDisplay } from './impedance/display'
import { crosstalkDisplayFromParams, crosstalkDisplayToParams, DEFAULT_CROSSTALK_DISPLAY, type CrosstalkDisplay } from './crosstalk/display'
import {
  DEFAULT_OUTPUT_IMPEDANCE_DISPLAY,
  outputImpedanceDisplayFromParams,
  outputImpedanceDisplayToParams,
  outputImpedanceRunFactsFromParams,
  type OutputImpedanceDisplay,
} from './outputImpedance/display'
import { useOutputImpedance } from './composables/useOutputImpedance'
import { formatOhms } from './impedance/equivalent'
import { DEFAULT_IMPULSE_DISPLAY, impulseDisplayFromParams, type ImpulseDisplay } from './measurements/impulseDisplay'
import { monochromeChartPng } from './charts/exportChart'
import { copyPngToClipboard, frameChartPng, screenshotFrameLayout, type ScreenshotSettings } from './screenshot'
import { useScreenshotSettings } from './composables/useScreenshotSettings'
import { useDateTimeFormat } from './composables/useDateTimeFormat'
import { downloadBlob, safeFileName } from './report/download'
import { reportHasChart } from './report/elements'
import type { ReportSubject } from './report/types'
import { useFrequencyResponse } from './composables/useFrequencyResponse'
import { isDutType, type DutType } from './impedance/component'
import { useImpedance } from './composables/useImpedance'
import { useImpulseResponse } from './composables/useImpulseResponse'
import {
  BURST_FREQUENCY_COLUMNS,
  DECAY_DISPLAY_COLUMNS,
  defaultCsdAnalysis,
} from './measurements/decay'
import { SWEEP_POINTS_KEY, useSweep } from './composables/useSweep'
import { useImdSweep } from './composables/useImdSweep'
import { nearestSweepPoint } from './sweep/points'
import { fitPreviewAxis, fitScopePreviewAxis, useSweepPreviewFit } from './sweep/previewAxis'
import { sweepXValue, type SweepXAxis } from './sweep/xAxis'
import {
  FFT_SPECTRUM_KIND,
  isAmplitudeSweepKind,
  isFrequencyResponseKind,
  isImdSweepKind,
  isSteppedResponseKind,
  isImpulseResponseKind,
  isSweepKind as isSweepKindName,
  isImpedanceKind,
  isCrosstalkKind,
  isOutputImpedanceKind,
  THD_VS_FREQUENCY_KIND,
} from './measurements/kinds'
import {
  claimLive,
  foreignLiveStores,
  liveOwnerKey,
  liveScreenStore,
  liveStoreForKind,
  releaseLive,
  releaseOwners,
  type LiveOwners,
  type LiveStore,
} from './measurements/liveOwner'

const { status, ranges, error, refreshStatus, refreshRanges } = useDevice()
const soundCard = useSoundCard()
/**
 * The programmable load. Its poll's lifetime is owned here and nowhere else: the dialog uses the
 * same composable, and starting the poll from inside it would give a second one per open. Nothing
 * is polled at all until a load has been chosen in Configuration — see useProgrammableLoad.
 */
const { status: loadStatus, startPolling: startLoadPolling } = useProgrammableLoad()
const {
  spectrum,
  spectrumOverview,
  meters,
  intermodulation,
  imdUnresolved,
  multitone,
  noise,
  scope,
  scopeResidual,
  setViewport,
  captureFftDisplay,
  restoreFftDisplay,
} = useSpectrumStream()
const { overlays, load: loadMeasurements, setOverlayWindow } = useMeasurements()
const { config: spectrumConfig, load: reloadSpectrumConfig } = useSpectrumConfig()
const { selectedMetrics, applyLoaded: applyLoadedMetrics } = useMetricSelection()
// For the report's Metrics element — the power tiles need the load the Generator pane names.
const { loadOhms } = useLoadOhms()
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
  showFftTrace: chartShowFftTrace,
  autofitOnDone: chartAutofitOnDone,
  showSpectrogram: chartShowSpectrogram,
  showOscilloscope: chartShowOscilloscope,
  showHarmonics: chartShowHarmonics,
  showOtherPeaks: chartShowOtherPeaks,
  visibleL: chartVisibleL,
  visibleR: chartVisibleR,
  spectrumUnit: chartSpectrumUnit,
  dutGainDb: chartDutGainDb,
  showWeightingCurve: chartShowWeightingCurve,
  showHarmonicMarkers: chartShowHarmonicMarkers,
  harmonicMarkerUnit: chartHarmonicMarkerUnit,
  storedHarmonicsSort: chartStoredHarmonicsSort,
  storedOtherPeaksSort: chartStoredOtherPeaksSort,
  applyLoaded: applyLoadedChartDisplay,
  requestAutofitY: requestChartAutofitY,
  requestAutofitX: requestChartAutofitX,
  requestResetPeakHold: requestChartResetPeakHold,
} = useChartDisplay()

// The notes on the open measurement's charts. One store for every kind -- see useAnnotations -- so
// the six display getters below and every screen's rail read the same state, and a measurement
// switch resets it exactly once.
//
// `liveAnnotations` is this measurement's own set, which is what a save writes to its type;
// `shownAnnotations` is whichever set is being *drawn*, which may be a stored result's instead, and
// is what every chart binds. Saving the drawn one would write a result's frozen notes onto the
// measurement that produced it.
const {
  shown: shownAnnotations,
  live: liveAnnotations,
  armed: annotationArmed,
  applyLoadedLive: applyLoadedAnnotations,
} = useAnnotations()

// The user weighting tables' names, for the chart's annunciator and the report.
const userWeighting = useUserWeighting()
void userWeighting.load()
const weightingAnnunciator = computed(() =>
  weightingLabel(spectrumConfig.value?.weighting ?? null, userWeighting.user1.value?.name, userWeighting.user2.value?.name),
)

// A weighting or unit change redraws every level on the chart, so the histories the chart keeps --
// the peak-hold maps and the spectrogram's rows -- would mix two scales. Clear them. The DUT gain only
// matters while the input-referred unit is selected.
watch(
  () => [
    spectrumConfig.value?.weighting?.standard,
    spectrumConfig.value?.weighting?.user1,
    spectrumConfig.value?.weighting?.user2,
    chartSpectrumUnit.value,
    chartSpectrumUnit.value === 'dBVPerRootHzInput' ? chartDutGainDb.value : 0,
  ],
  (next, previous) => {
    if (previous === undefined || next.every((v, i) => v === previous[i])) return
    requestChartResetPeakHold()
    spectrogramChart.value?.clearHistory()
  },
)
// Gates the fundamental/harmonic markers and tables to Sine — Multitone's comb has no single
// fundamental (the global-peak-bin search would just lock onto whichever tone happens to read
// loudest) and White noise has no discrete tone at all, so both would label meaningless content.
const {
  kind: stimulusKind,
  tones: stimulusTones,
  multitone: stimulusMultitone,
  imd: stimulusImd,
  levelUnit: stimulusLevelUnit,
} = useGeneratorStimulus()
// Square and triangle keep them: those shapes have one unambiguous fundamental (the loudest bin by
// a wide margin -- 0.91 dB of a square's rms is in it) and their harmonic series is the measurement.
// The ideal levels are 1/n for a square and 1/n^2 for a triangle, so the table reads the DUT's own
// high end directly: -9.54 dBc at H3 on a perfect square, -19.08 at H5.
const showFundamentalMarkers = computed(() => stimulusKind.value === 'Tones' || isWaveKind(stimulusKind.value))

/**
 * The layout of the FFT screen's per-channel table grids -- harmonics, IMD products, other peaks,
 * and the multitone table that replaces the first of them.
 *
 * <p>One column per channel the display rail's <b>Channels</b> checkboxes have ticked, rather than
 * two columns always: the tables used to be rendered for both channels whatever was selected, so a
 * one-channel measurement -- which is the default, and most benches' normal case -- printed an
 * empty "R" card beside every populated "L" one (Joost, 2026-10-16). A single column is held to
 * half the width and centred, so it comes out the width of the Notes box above it instead of a
 * table of four short columns stretched across the whole view.</p>
 *
 * <p>Keyed on the display rail's own checkboxes and not on the acquisition panel's L/R: those two
 * agree except where the user has unticked a channel they *are* measuring, and in that case the
 * question "which tables do I want on screen" is exactly what the display rail answers. (The
 * acquisition panel's own state forces the rail's, see ChartDisplayPanel, so a channel that is not
 * measured can never be ticked here.)</p>
 */
const peakTableGridClass = computed(() =>
  chartVisibleL.value && chartVisibleR.value
    ? 'grid grid-cols-1 gap-4 xl:grid-cols-2'
    : 'grid grid-cols-1 gap-4 xl:mx-auto xl:w-1/2',
)

// Whether the pipeline is measuring two-tone IMD right now — what the Intermodulation table
// needs to tell "no frame yet" from "nothing configured".
const imdConfigured = computed(() => (spectrumConfig.value?.intermodulation ?? null) !== null)
// The multitone figure follows the generator: the server derives its spec from the comb it plays,
// so the staged stimulus kind is what says whether a table belongs on screen (the frames say when
// there is something in it).
const multitoneConfigured = computed(() => stimulusKind.value === 'Multitone')
// The server just stops sending multitone frames when the comb goes away (there is no
// "unresolved" marker as there is for IMD), so the last figure has to be dropped here or the
// TD+N tiles keep quoting it against a sine.
watch(multitoneConfigured, (configured) => {
  if (!configured) multitone.value = {}
})

// The IMD analysis follows the staged stimulus: exactly two sine tones make a spec, anything else
// clears it (see imd/presets.ts). Pushed straight to the pipeline like FFT size — it is pure
// analysis state, nothing reaches the device — but *without* marking the type dirty here: a user
// edit already dirties it through the generator panel's own change event, and this watcher also
// fires when a type is loaded, where dirtying would put a red dot on an untouched measurement.
// Loading a type is also why the push is skipped when the server already has this spec: the
// server applied the stored one itself (MeasurementSaveService.ApplyAsync).
watch(
  () => imdSpecFromStimulus(stimulusKind.value, stimulusTones, stimulusImd),
  async (spec) => {
    const current = spectrumConfig.value?.intermodulation ?? null
    const same = spec.enabled
      ? current !== null &&
        current.toneLowHz === spec.toneLowHz &&
        current.toneHighHz === spec.toneHighHz &&
        current.method === spec.method &&
        current.maxOrder === spec.maxOrder
      : current === null
    if (!spec.enabled) intermodulation.value = {}
    if (same) return
    try {
      spectrumConfig.value = await api.setSpectrumConfig({ intermodulation: spec })
    } catch (e) {
      error.value = (e as Error).message
    }
  },
  { deep: true },
)
// Loaded at startup so the sweep panels' time estimates already include it; the value itself is
// applied server-side (see useCaptureDelay).
const { load: loadCaptureDelay } = useCaptureDelay()
void loadCaptureDelay()
// Loaded so the status bar's Ext. gain popover opens on the stored figures; the ranging that
// used to read them here is the session's now (SessionInputRanger), which takes the peak from the
// block itself — analyzer volts, before the pipeline divides this gain out of them.
const { load: loadExternalGain, set: setSharedExternalGain } = useExternalGain()
void loadExternalGain()

// Moved out of SystemMetricsPanel's Sys:* grid into its own readout between the Metrics button
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
/**
 * The FFT screen's status bar. It has no runner — the pipeline is simply on or off — so the three
 * readings come from the device status and the averager: busy while acquiring, "stopped" once there
 * is a spectrum on screen to look at, idle before that. The bar fills with the averages taken, which
 * is the only thing on that screen that progresses.
 */
const fftTone = computed<RunTone>(() => {
  if (status.value?.acquiring) return 'busy'
  return Object.keys(spectrum.value).length > 0 ? 'ok' : 'idle'
})
const fftHeadline = computed(() => {
  if (status.value?.acquiring) return singleShotArmed.value ? 'Single capture' : 'Acquiring'
  return fftTone.value === 'ok' ? 'Stopped' : 'Idle'
})
/** Averaging off is one record, so the bar is full the moment one has been analysed. */
const fftAveragesTaken = computed(() => (meters.value[0] ?? meters.value[1])?.averagesTaken ?? 0)
const fftAverageCount = computed(() =>
  spectrumConfig.value && spectrumConfig.value.averaging !== 'Off' ? spectrumConfig.value.averageCount : 0,
)

const {
  currentProject,
  sessionLoaded,
  types,
  currentProjectId,
  currentTypeId,
  loadedParams,
  dirtyTypeIds,
  hasDirty: hasAnyDirtyInProject,
  loadTypes,
  markDirty,
  setAutosaveFlush,
  setLiveStateReset,
  setRunActive,
  saveMeasurementType,
  saveMeasurementTypeAs,
  saveProject,
} = useProjects()
const projectSession = useProjectSession()

/**
 * The FFT chart's zoom window has two consumers, so the emit goes through here rather than
 * straight at one of them. `setViewport` tells the *server* which window this client is looking
 * at, so the live frames — and, while stopped, a frame rebuilt from the retained snapshot — are
 * decimated over it; `setOverlayWindow` re-fetches any saved overlay's columns at the same window.
 * Both exist for the same reason: every bin is kept, and it is only the display decimation that
 * was spending the whole column budget on the whole span.
 *
 * Already debounced and already null-at-full-span by the chart (`reportViewport`), so this is one
 * call per settled zoom rather than one per frame.
 */
function onSpectrumViewportChange(viewport: { loFreqHz: number; hiFreqHz: number } | null) {
  setViewport(viewport)
  setOverlayWindow(viewport)
}

// The trash view, which replaces <main>'s content. Selecting any measurement closes it — one
// watcher rather than a closeTrash() at each of the sidebar's two row handlers and
// NewMeasurementDialog's, which is three places to forget. Guarded on non-null because
// deleteMeasurementType sets currentTypeId to null when the open measurement goes, and that must
// not count as a selection.
const { open: trashOpen, closeTrash } = useTrash()
watch(currentTypeId, (id) => {
  if (id !== null) closeTrash()
})

// A restore puts a measurement back in the sidebar or a result back in the history, neither of
// which the trash view owns. The history panel is filtered to the open measurement, so reloading
// it is cheap and always right.
async function onTrashRestored() {
  if (currentProjectId.value !== null) await loadTypes(currentProjectId.value)
  await loadMeasurements()
}

/**
 * A permanent delete destroyed these measurements, so nothing may go on holding a run for them.
 *
 * <p>Two places hold one: the <b>server</b> parks a run per measurement in each of the eight
 * runners and a retained snapshot beside them (see RunParking), and this file keeps each
 * measurement's live FFT display in {@link fftDisplays}. Both are keyed by the owner, which is why
 * this is App.vue's job and not the trash view's — the key needs the project id, and `fftDisplays`
 * lives here.</p>
 *
 * <p>The park is the smaller half of the reason. The larger one is that a destroyed measurement
 * may still <i>own</i> a live slot, and an owned slot is left alone while an <b>unowned</b> one is
 * adopted by the next measurement to arrive: left as it was, the run of a measurement that no
 * longer exists would be handed to whichever measurement was selected next, and Save would store
 * it there. See RunParking.Forget.</p>
 *
 * <p>No reload follows, unlike a restore: these rows left the sidebar and the history when they
 * were trashed, and destroying them changes nothing either list is showing. A failure is
 * swallowed — the measurements are gone whatever this call did, and a stale park costs memory and
 * an adoption the next switch corrects, which is not worth an error banner over a delete that
 * worked.</p>
 */
async function onTrashPurged(measurementTypeIds: number[]) {
  const owners = measurementTypeIds
    .map((id) => liveOwnerKey(currentProjectId.value, id))
    .filter((key): key is string => key !== null)
  if (owners.length === 0) return

  for (const owner of owners) fftDisplays.delete(owner)
  liveOwners.value = releaseOwners(liveOwners.value, owners)

  await api.forgetRunOwners(owners).catch(() => undefined)
}
// Configuration ▸ General ▸ Autosave — see the isCurrentTypeDirty watcher further down.
const { autosaveEnabled, load: loadAutosave } = useAutosave()
void loadAutosave()

/**
 * Set by {@link swapFftDisplay}, consumed once the arriving measurement's own chart preferences
 * have been applied: the spectrum chart is put back to its From/To, dropping the drag-zoom the
 * previous measurement was left at.
 *
 * <p>It cannot be done in the swap itself, which runs before the selection moves — the panel still
 * holds the window being left, and resetting to that one pins the chart there (seen: measurement 2
 * at 10 Hz–100 kHz, switch to measurement 1 stored at 500 Hz–10 kHz, and the axis stayed at the
 * four decades while the dropdowns read the right numbers). The `[xFrom, xTo]` watcher inside the
 * chart does the work whenever the two measurements' windows differ; this covers the case they do
 * not, where that watcher never fires and the zoom would carry over. On a tick, after the applies,
 * so nothing rebuilds the plot behind it (ensurePlot restores the scale it found).</p>
 */
let fftViewNeedsReset = false

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
    // The Load field, FFT types only: `loadedParams` is the live device state for every other
    // kind (ApplyAsync's HasNoLiveConfiguration arm), so its null here would overwrite the figure
    // a sweep panel has just restored from its own params_json.
    // `types`/`currentTypeId` rather than the `currentType` computed, which is declared further
    // down the file: this watcher is immediate, so it runs during setup and would read it in its
    // temporal dead zone.
    const kind = types.value.find((t) => t.id === currentTypeId.value)?.kind
    if (params && kind === FFT_SPECTRUM_KIND) loadOhms.value = params.loadOhms ?? DEFAULT_LOAD_OHMS
    if (fftViewNeedsReset) {
      fftViewNeedsReset = false
      void nextTick(() => spectrumChart.value?.resetToPanelWindow())
    }
    // The server applied the type's spectrum options (FFT size, IMD spec, scope residual...) on
    // the way here; re-read them so every reader of the shared config sees this type's.
    if (params) void reloadSpectrumConfig()
    // The IMD template/analysis choice is staged with the tones (see useGeneratorStimulus.ts);
    // a type without one is Custom, i.e. whatever its tone list says.
    Object.assign(stimulusImd, {
      preset: (params?.imdPreset as ImdPresetId | null | undefined) ?? 'Custom',
      method: params?.imdMethod ?? 'Smpte',
      maxOrder: params?.imdMaxOrder ?? 5,
    })
    // Which unit the generator's level boxes read in — display only, staged with the tones. A type
    // saved before this existed has none, which is dBV.
    stimulusLevelUnit.value = generatorLevelUnitOf(params?.generatorLevelUnit)
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
// Importing a simulation as a trace. The flag is a module store rather than a ref here, because
// every measurement screen mounts its own MeasurementChecklist and the wiring was forgotten twice
// -- see measurements/simulationImport.ts. Which dialog it opens is still decided here, from the
// kind, since what a file means depends on the screen it lands on.

/**
 * After a Project ▸ Import…: the sidebar and the History list both have new rows to show.
 *
 * Deliberately no `selectType`. An imported measurement must not steal the selection — that would
 * throw away the live state of whatever is on screen, which is the seam the trash view follows for
 * the same reason.
 */
async function onMeasurementsImported() {
  const projectId = currentProjectId.value
  if (projectId !== null) await loadTypes(projectId)
  await loadMeasurements()
}

async function onSimulationImported() {
  // The new result has to appear in the History list before it can be checked, and the trash
  // badge is unaffected -- this adds a row rather than removing one.
  await loadMeasurements()
}
// Reports — see report/buildReport.ts. One dialog serves the measurement on screen and a stored
// result alike; `reportSubject` is which.
const reportOpen = ref(false)
// shallowRef: a subject holds whole traces and, for a spectrum, a display source of refs — neither
// should be made deeply reactive. Snapshotted when a dialog opens, so a pass arriving mid-report is
// not half in it.
const reportLiveSubject = shallowRef<ReportSubject | null>(null)
const reportPreselectResultId = ref<number | null>(null)
const projectReportOpen = ref(false)
// Project ▸ Export… / Import…: measurements and results between two project files. Separate refs
// rather than one mode, because the two dialogs ask different questions of different files.
const projectExportOpen = ref(false)
const projectImportOpen = ref(false)
const saveTypeAsOpen = ref(false)
// The Project menu's dialogs: one form for New…/Save As…/Properties… (its mode says which), and
// the file dialog for Open…. See useProjectSession for what each action does once confirmed.
const projectDialogOpen = ref(false)
const projectDialogMode = ref<ProjectDialogMode>('new')
const openProjectDialogOpen = ref(false)
const metricPickerOpen = ref(false)
// The FFT screen's metrics block, handed to SpectrumChart so its height fit leaves room for it.
const fftAbove = ref<HTMLDivElement | null>(null)
const configDialogOpen = ref(false)
const soundCardDialogOpen = ref(false)
const qa403DialogOpen = ref(false)
const loadDialogOpen = ref(false)
const aboutDialogOpen = ref(false)
const issueReportDialogOpen = ref(false)
// The splash screen, up from the first frame and gone three seconds later (SplashScreen.vue, and
// splash.ts for the durations). An overlay rather than a gate: everything below mounts, opens the
// last project and connects the analyzer behind it, so those seconds cover that work instead of
// being spent before it starts.
const splashShowing = ref(true)
// The manual's own state is a module-level singleton (see useManual.ts) rather than a ref here,
// because the header's button and the per-measurement help button have to drive one dialog.
const { openManual, openManualForKind } = useManual()
const currentType = computed(() => types.value.find((t) => t.id === currentTypeId.value) ?? null)
// ---------------------------------------------------------------------------
// THD vs frequency
// ---------------------------------------------------------------------------
const {
  running: thdSweepRunning,
  phase: thdSweepPhase,
  index: thdSweepIndex,
  total: thdSweepTotal,
  progressLabel: thdSweepProgressLabel,
  generatorAmpDbv: sweepGeneratorAmpDbv,
  error: thdSweepError,
  stopReason: thdSweepStopReason,
  points: thdSweepPoints,
  thdMethod: thdSweepMethod,
  viewAxis: sweepViewAxis,
  start: startSweep,
  startAmplitude: startAmplitudeSweep,
  cancel: cancelSweepRun,
  clear: clearSweepRun,
  refresh: refreshSweep,
  apply: applySweepStatus,
} = useSweep()

// The IMD sweep: its own runner and store, drawn by the same sweep screen. Everything the screen
// reads about "the sweep" — running, points, progress, error, method — comes through the
// kind-switched computeds below, so the chart, the toolbar and the report see whichever runner
// the open type belongs to and never the other one's leftovers.
const imdSweep = useImdSweep()
const isImdSweep = computed(() => isImdSweepKind(currentType.value?.kind))
const sweepRunning = computed(() => (isImdSweep.value ? imdSweep.running.value : thdSweepRunning.value))
const sweepProgressLabel = computed(() => (isImdSweep.value ? imdSweep.progressLabel.value : thdSweepProgressLabel.value))
const sweepError = computed(() => (isImdSweep.value ? imdSweep.error.value : thdSweepError.value))
/** Why a finished sweep ended early, from whichever runner the open kind belongs to — both have a stop threshold. */
const sweepStopReason = computed(() => (isImdSweep.value ? imdSweep.stopReason.value : thdSweepStopReason.value))
const sweepPoints = computed(() => (isImdSweep.value ? imdSweep.points.value : thdSweepPoints.value))
const sweepThdMethod = computed<import('./api/client').ThdMethodName>(() => (isImdSweep.value ? 'Conventional' : thdSweepMethod.value))
/** THD/D2.. or IMD/IM2.. on the chart, the display panel and the report — see sweep/points.ts. */
const sweepTraceFamily = computed<import('./sweep/points').SweepTraceFamily>(() => (isImdSweep.value ? 'imd' : 'thd'))

// What the sweep screen's status bar shows. The phase, the counts and the progress string all come
// from whichever of the two runners the open kind belongs to — the same switch every other sweep
// computed above makes — so the bar can never show the THD runner's leftovers on an IMD screen.
const sweepPhase = computed(() => (isImdSweep.value ? imdSweep.phase.value : thdSweepPhase.value))
const sweepIndex = computed(() => (isImdSweep.value ? imdSweep.index.value : thdSweepIndex.value))
const sweepTotal = computed(() => (isImdSweep.value ? imdSweep.total.value : thdSweepTotal.value))
const sweepTone = computed(() => sweepRunTone(sweepPhase.value))
const sweepStatusHeadline = computed(() => statusHeadline(sweepTone.value, sweepProgressLabel.value))
const sweepStatusDetail = computed(() => statusDetail(sweepTone.value, sweepProgressLabel.value))
// ThdSweepChart falls back to useSweep() without this; providing the switched source is what
// puts the IMD runner's points on the chart. A report's headless render provides its own.
provide(SWEEP_POINTS_KEY, { points: sweepPoints, thdMethod: sweepThdMethod })
// The one polled device status, for the simulator hint every MeasurementLayout carries.
provide(DEVICE_STATUS_KEY, status)

const {
  running: responseRunning,
  phase: responsePhase,
  averagesTaken: responseAveragesTaken,
  averageCount: responseAverageCount,
  pointsMeasured: responsePointsMeasured,
  pointCount: responsePointCount,
  traces: responseTraces,
  previews: responsePreviews,
  error: responseError,
  progressLabel: responseProgressLabel,
  generatorAmpDbv: responseGeneratorAmpDbv,
  binHz: responseBinHz,
  latencySamples: responseLatencySamples,
  sampleRateHz: responseSampleRateHz,
  start: startResponse,
  startStepped: startSteppedResponse,
  startCrosstalk,
  cancel: cancelResponseRun,
  clear: clearResponseRun,
  clearTarget: clearResponseTarget,
  refresh: refreshResponse,
  refreshTarget: refreshResponseTarget,
  switchOwner: switchResponseOwner,
  setTarget: setResponseTarget,
  phaseKind: responsePhaseKind,
  rightChannelIsReference: responseRightChannelIsReference,
  phaseAvailable: responsePhaseAvailable,
} = useFrequencyResponse()

// What the frequency-response screen's status bar shows. One runner serves the chirp, the stepped
// sweep and crosstalk, and the counts it reports mean different things on each: a chirp counts the
// records it has averaged, the other two count points. `responseCaption` is what reads them in the
// units the open measurement is actually in — "Record 1 of 1" against "Point 12 of 50" — which is
// the caption row of the mockup's bar.
const responseTone = computed(() => responseRunTone(responsePhase.value))
const responseStatusHeadline = computed(() => statusHeadline(responseTone.value, responseProgressLabel.value))
const responseStatusDetail = computed(() => statusDetail(responseTone.value, responseProgressLabel.value))
const responseCountsPoints = computed(() => isSteppedResponse.value || isCrosstalk.value)
const responseDone = computed(() => (responseCountsPoints.value ? responsePointsMeasured.value : responseAveragesTaken.value))
const responseTotal = computed(() => (responseCountsPoints.value ? responsePointCount.value : responseAverageCount.value))
const responseCaption = computed(() => {
  const noun = responseCountsPoints.value ? 'Point' : 'Record'
  if (responseTotal.value <= 0) return responseStatusDetail.value
  return `${noun} ${Math.min(Math.max(responseDone.value, responseTone.value === 'ok' ? responseTotal.value : 1), responseTotal.value)} of ${responseTotal.value}`
})
/** The right-hand caption: the resolution the curve was measured at, which is the one figure the bar can
 *  always produce — the band and the record length live in the settings panel's own form. */
const responseCaptionRight = computed(() => {
  if (!responseBinHz.value) return responseStatusDetail.value
  return `${responseBinHz.value.toFixed(2)} Hz/bin · ${(1 / responseBinHz.value).toFixed(2)} s/record`
})

// The other four screens' bars. Each is the same three readings — which of five states, how far
// through, and what the runner has to say — off whichever store owns that screen; they are computed
// here rather than inside MeasurementStatusBar because every runner counts something different.
const impedanceTone = computed(() => responseRunTone(impedancePhase.value))
const impedanceStatusHeadline = computed(() => statusHeadline(impedanceTone.value, impedanceProgressLabel.value))
const impedanceStatusDetail = computed(() => statusDetail(impedanceTone.value, impedanceProgressLabel.value))

const outputImpedanceTone = computed(() => responseRunTone(outputImpedancePhase.value))
const outputImpedanceStatusHeadline = computed(() =>
  statusHeadline(outputImpedanceTone.value, outputImpedanceProgressLabel.value),
)
const outputImpedanceStatusDetail = computed(() =>
  statusDetail(outputImpedanceTone.value, outputImpedanceProgressLabel.value),
)

// The impulse runner is polled over REST rather than streamed, so its phase arrives as a name.
const impulseTone = computed(() => responseRunToneByName(impulsePhase.value))
const impulseStatusHeadline = computed(() => statusHeadline(impulseTone.value, impulseProgressLabel.value))
const impulseStatusDetail = computed(() => statusDetail(impulseTone.value, impulseProgressLabel.value))

const {
  running: impedanceRunning,
  phase: impedancePhase,
  pointsMeasured: impedancePointsMeasured,
  pointCount: impedancePointCount,
  traces: impedanceTraces,
  error: impedanceError,
  warning: impedanceWarning,
  progressLabel: impedanceProgressLabel,
  generatorAmpDbv: impedanceGeneratorAmpDbv,
  binHz: impedanceBinHz,
  referenceOhms: impedanceReferenceOhms,
  medianOhms: impedanceMedianOhms,
  driveCurrentAmps: impedanceDriveCurrentAmps,
  selectedStimulus: impedanceStimulus,
  start: startImpedance,
  cancel: cancelImpedanceRun,
  clear: clearImpedanceRun,
  refresh: refreshImpedance,
  switchOwner: switchImpedanceOwner,
} = useImpedance()

const {
  status: impulseStatus,
  phase: impulsePhase,
  progressLabel: impulseProgressLabel,
  averagesTaken: impulseAveragesTaken,
  averageCount: impulseAverageCount,
  generatorAmpDbv: impulseGeneratorAmpDbv,
  binHz: impulseBinHz,
  running: impulseRunning,
  traces: impulseTraces,
  impulse: impulseTrace,
  harmonics: impulseHarmonics,
  gateResolutionHz: impulseGateResolutionHz,
  refresh: refreshImpulse,
  start: startImpulse,
  cancel: cancelImpulse,
  clear: clearImpulse,
  switchOwner: switchImpulseOwner,
  refreshReferences: refreshLoopbackReferences,
  csdSurface: impulseCsdSurface,
  burstSurface: impulseBurstSurface,
  decayError: impulseDecayError,
  decayComputing: impulseDecayComputing,
  computeCsd: computeImpulseCsd,
  computeBurstDecay: computeImpulseBurstDecay,
  computeStoredCsd: computeStoredImpulseCsd,
  computeStoredBurstDecay: computeStoredImpulseBurstDecay,
} = useImpulseResponse()

// Which screen the main pane shows. App.vue used to render the FFT view for any selected type,
// which was fine while FFTSpectrum was the only kind. The two sweep kinds share one screen —
// identical layout, identical chart, identical history and preview rail — and differ only in which
// config panel is mounted and what the chart's x axis plots.
const isSweepKind = computed(() => isSweepKindName(currentType.value?.kind))
const isAmplitudeSweep = computed(() => isAmplitudeSweepKind(currentType.value?.kind))

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

/**
 * The response chart's pinned frequency — a click freezes the cursor readout, and on the stepped
 * screen the two preview charts in the rail with it (see FrequencyResponseChart's `cursorPin`).
 *
 * <p>Owned here rather than in the chart for the reason the sweep screen's is: the whole point of a
 * pin is that it outlives the pointer leaving the plot, which is exactly what reaching for a
 * preview's expand button does. Joost, 2026-10-11.</p>
 *
 * <p>Dropped when the measurement changes — a frequency from another run's grid would freeze this
 * one's rail on an arbitrary point — and when the curves are cleared. Kept across a re-run, so the
 * same frequency can be watched before and after a change to the DUT.</p>
 */
const pinnedResponseHz = ref<number | null>(null)
function onResponseCursorPin(freqHz: number | null) {
  pinnedResponseHz.value = freqHz
}

watch(currentTypeId, () => {
  pinnedResponseHz.value = null
})

/**
 * The point each input's preview charts draw: the one being measured while a stepped run is going,
 * the pinned one once it is pinned, and otherwise the last point measured.
 *
 * <p>No hover case, unlike the sweep screen's: the response chart keeps its hover to itself (it
 * draws its own readout from it), and a rail that followed the pointer as well as the pin would
 * need that hover routed out through a second event for no gain — the pin is what this screen was
 * asked for.</p>
 */
function selectedResponsePreview(channel: number) {
  const onChannel = responsePreviews.value.filter((p) => p.channel === channel)
  if (onChannel.length === 0) return null

  if (responseRunning.value) return onChannel[onChannel.length - 1]

  const target = pinnedResponseHz.value ?? onChannel[onChannel.length - 1].freqHz
  return nearestSweepPoint(responsePreviews.value, channel, target, (p) => p.freqHz)
}

const responseSelectedLeft = computed(() => selectedResponsePreview(0))
const responseSelectedRight = computed(() => selectedResponsePreview(1))

/**
 * The checked history runs whose previews this screen can draw: visible, of the stepped kind, and
 * actually carrying captures — a stepped result saved before they were recorded comes back with
 * none, and an empty pair on screen says nothing.
 */
const visibleResponsePreviewOverlays = computed(() =>
  overlays.value.filter((o) => o.visible && o.responsePreviews?.length && isSteppedResponseKind(o.detail.kind)),
)

/** selectedResponsePreview's counterpart for a stored run — never "running", so it always follows the pin. */
function selectedResponsePreviewForOverlay(overlay: OverlayEntry, channel: number) {
  const points = overlay.responsePreviews ?? []
  const onChannel = points.filter((p) => p.channel === channel)
  if (onChannel.length === 0) return null

  const target = pinnedResponseHz.value ?? onChannel[onChannel.length - 1].freqHz
  return nearestSweepPoint(points, channel, target, (p) => p.freqHz)
}

/**
 * The y window the stepped screen's spectrum previews share while their Fit-all toggle is on: every
 * point of the live run and of every checked history run, so reading one stacked preview against
 * another is a comparison on one axis — the sweep screen's rule, for its reason.
 */
const responsePreviewFitRange = computed(() =>
  fitPreviewAxis([
    ...responsePreviews.value.map((p) => p.previewFft),
    ...visibleResponsePreviewOverlays.value.flatMap((o) => (o.responsePreviews ?? []).map((p) => p.previewFft)),
  ]),
)

/** The same, in volts, for the oscilloscope previews underneath them. */
const responseScopeFitRange = computed(() =>
  fitScopePreviewAxis([
    ...responsePreviews.value.map((p) => p.previewScope),
    ...visibleResponsePreviewOverlays.value.flatMap((o) => (o.responsePreviews ?? []).map((p) => p.previewScope)),
  ]),
)

// A fourth screen. It shares the chirp with the response screen and even redraws its gated magnitude
// with the same chart, but the impulse itself, the gate and the harmonic curves have no counterpart
// there, and the two run through different endpoints — so it keys off its own flag rather than
// becoming a third arm of isFrequencyResponse.
const isImpulseResponse = computed(() => isImpulseResponseKind(currentType.value?.kind))

// A fifth screen. It shares the chirp and the storage rows with the response screen, but it reports
// ohms rather than a gain and it has a fixture calibration of its own, so like the impulse screen it
// keys off its own flag rather than becoming a third arm of isFrequencyResponse.
const isImpedance = computed(() => isImpedanceKind(currentType.value?.kind))

// A sixth screen. It runs the stepped sine's mechanics with one generator channel off and draws its
// curves with the response chart, but it reports a channel ratio rather than a gain, has no phase,
// no reference and no bandwidth, and runs through its own endpoints — so, like impedance, it keys
// off its own flag rather than becoming a third arm of isFrequencyResponse.
const isCrosstalk = computed(() => isCrosstalkKind(currentType.value?.kind))

// A seventh screen. Its Zout curve is an impedance curve drawn by the impedance chart, but the
// runner, the two-pass flow with its pause, the readings and the settings are its own — so, like
// impedance and crosstalk, it keys off its own flag.
const isOutputImpedance = computed(() => isOutputImpedanceKind(currentType.value?.kind))

const {
  running: outputImpedanceRunning,
  phase: outputImpedancePhase,
  pointsMeasured: outputImpedancePointsMeasured,
  pointCount: outputImpedancePointCount,
  waiting: outputImpedanceWaiting,
  trace: outputImpedanceTrace,
  traces: outputImpedanceTraces,
  error: outputImpedanceError,
  warning: outputImpedanceWarning,
  generatorAmpDbv: outputImpedanceGeneratorAmpDbv,
  inputRangeDbv: outputImpedanceInputRangeDbv,
  sampleRateHz: outputImpedanceSampleRateHz,
  loadOhms: outputImpedanceLoadOhms,
  secondLoadOhms: outputImpedanceSecondLoadOhms,
  nominalOhms: outputImpedanceNominalOhms,
  rightChannelIsReference: outputImpedanceReferenced,
  firstGainAt1kDb: outputImpedanceFirstGainAt1kDb,
  secondGainAt1kDb: outputImpedanceSecondGainAt1kDb,
  progressLabel: outputImpedanceProgressLabel,
  start: startOutputImpedance,
  continueRun: continueOutputImpedanceRun,
  cancel: cancelOutputImpedanceRun,
  clear: clearOutputImpedanceRun,
  refresh: refreshOutputImpedance,
  switchOwner: switchOutputImpedanceOwner,
} = useOutputImpedance()

/**
 * A stepped-sine impedance run is single-shot, so the continuous RUN button goes away and the
 * remaining one carries the whole width — the stepped-sine response screen's model, and for its
 * reason: a run that walks a grid of tones is minutes long, and repeating it until stopped is not a
 * mode anyone wants. The server rejects it too, so leaving the button visible would only offer a
 * 400.
 */
const impedanceIsStepped = computed(() => isImpedance.value && impedanceStimulus.value === 'SteppedSine')

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

/**
 * The pinned point, which beats the hover everywhere below — a click on the sweep chart freezes the
 * two preview charts and the metrics rows on one measurement until it is released (see
 * ThdSweepChart's `cursorPin`).
 *
 * <p>It lives here rather than in the chart because the whole point of it is to outlive the pointer
 * leaving the plot: reaching for the expand button on one of the previews is exactly that, and it
 * used to reset them to the last point measured just as the user went to look closer. Joost,
 * 2026-10-10.</p>
 */
const pinnedSweepX = ref<number | null>(null)
function onSweepCursorPin(x: number | null) {
  pinnedSweepX.value = x
}

/** The x accessor the chart is currently plotting with — what "nearest to the cursor" is measured along. */
function sweepXOf(point: ThdSweepPoint): number {
  return sweepXValue(point, sweepChartXAxis.value)
}

function selectedSweepPointFor(channel: number) {
  const onChannel = sweepPoints.value.filter((p) => p.channel === channel)
  if (onChannel.length === 0) return null

  if (sweepRunning.value) return onChannel[onChannel.length - 1]

  const target = pinnedSweepX.value ?? hoveredSweepX.value ?? sweepXOf(onChannel[onChannel.length - 1])
  return nearestSweepPoint(sweepPoints.value, channel, target, sweepXOf)
}

const sweepSelectedLeft = computed(() => selectedSweepPointFor(0))
const sweepSelectedRight = computed(() => selectedSweepPointFor(1))

/**
 * The Spectrum/Oscilloscope preview pair drawn underneath the live one for each checked history
 * item — literally the same call ThdSweepChart's own activeOverlays makes, so the previews, the
 * cursor readings and the chart cannot disagree about which runs are on screen. An FFT result
 * checked on here has no points and is skipped; a sweep of another kind is skipped too, which is
 * the whole of overlayScope.ts's `sweepOverlayScreen`.
 */
const visibleSweepOverlays = computed(() =>
  sweepOverlays(overlays.value, sweepOverlayScreen(currentType.value?.kind)),
)

/**
 * The one y window the spectrum previews share while their Fit-all toggle is on: every point of
 * every sweep the main chart is drawing — the live run's and each checked overlay's — so hovering
 * from point to point, and reading one stacked preview against another, are both comparisons of
 * curves on the same axis. Computed whether or not the toggle is on, so switching it costs no
 * rescan and the charts never render a frame against a window that has not arrived yet; it is a
 * computed over the same arrays the chart itself watches, so it only ever recomputes when a point
 * or an overlay does.
 */
const sweepPreviewFitRange = computed(() =>
  fitPreviewAxis([
    ...sweepPoints.value.map((p) => p.previewFft),
    ...visibleSweepOverlays.value.flatMap((o) => (o.sweepPoints ?? []).map((p) => p.previewFft)),
  ]),
)

/** The same, in volts, for the oscilloscope previews underneath them. */
const sweepScopeFitRange = computed(() =>
  fitScopePreviewAxis([
    ...sweepPoints.value.map((p) => p.previewScope),
    ...visibleSweepOverlays.value.flatMap((o) => (o.sweepPoints ?? []).map((p) => p.previewScope)),
  ]),
)

// The two Fit-all toggles themselves live in the module store the preview charts read (one window
// shared by the live pair and every overlay's), but they are this screen's display state like the
// sweep* refs below: saved into the measurement type's params_json, restored with it, and dirtying
// it when changed.
const { fitAllSpectra: sweepFitPreviewSpectrum, fitAllScopes: sweepFitPreviewScope } = useSweepPreviewFit()

/**
 * The checked history items one curve screen may draw: visible, carrying curves, and of a kind that
 * belongs on that screen — see measurements/overlayScope.ts for why the last clause is not optional.
 */
function curveOverlaysFor(screen: CurveOverlayScreen): OverlayEntry[] {
  return overlays.value.filter(
    (o) =>
      o.visible &&
      ((o.responseTraces?.length ?? 0) > 0 || (o.impedanceTraces?.length ?? 0) > 0) &&
      curveOverlayScreen(o.detail.kind) === screen,
  )
}

/**
 * The same filter for the response screens — and split by **kind**, for the reason the impedance
 * one below is.
 *
 * <p>Three kinds store their curves as `response_trace` rows and read back through one reader into
 * `responseTraces`: a frequency response, an impulse response (its gated magnitude and phase) and a
 * crosstalk run. Two of them even use the same trace kinds — an impulse result's gated response is
 * a `Left` and a `Gain` curve, exactly what the chirp screen draws — so a filter on the field alone
 * cannot tell them apart. Overlays deliberately stay loaded when you move between measurements, so
 * an impulse result checked on for its gated response and its group delay then appeared on the next
 * frequency-response chart, legend and all, with no row in that screen's History to uncheck it from.
 * Reported 2026-09-25.</p>
 *
 * <p>The chirp and the stepped sine share one list on purpose: they are two ways of measuring the
 * same curve, stored identically, and drawing one under the other is a comparison worth having.</p>
 */
const visibleResponseOverlays = computed(() => curveOverlaysFor('Response'))

/** The same for the impulse screen's gated-response view: its own kind's runs, and never a chirp's. */
const visibleImpulseResponseOverlays = computed(() => curveOverlaysFor('ImpulseResponse'))

/**
 * The same for the crosstalk screen. Its chart asks for the two crosstalk trace kinds, which no
 * other kind stores, so this was already safe in the drawing — but stating the kind keeps all three
 * response screens answering the same question in the same place.
 */
const visibleCrosstalkOverlays = computed(() => curveOverlaysFor('Crosstalk'))

/**
 * Checked history items carrying an ohms curve — and split by **kind**, not by shape alone.
 *
 * <p>An output-impedance result's `zout` row is an `ImpedanceTrace` like any other (that is what lets
 * one chart draw both), so a filter on `impedanceTraces` alone matches it from the impedance screen
 * too. Overlays deliberately stay loaded when you move between measurements, so after a damping-factor
 * run its curve appeared on the Jamo's impedance chart, legend and all, with no row in that screen's
 * History to uncheck it from — an amplifier's source impedance drawn as a loudspeaker's. Reported
 * 2026-09-25.</p>
 *
 * <p>These two are different quantities that share a unit, so unlike the response screens — where a
 * crosstalk overlay is skipped because the chart asks for a `Gain` trace it does not have — nothing
 * downstream can tell them apart. The kind is the only thing that can.</p>
 */
const visibleImpedanceOverlays = computed(() => curveOverlaysFor('Impedance'))

/** The same for the damping-factor screen: its own kind's runs, and never a one-port impedance. */
const visibleOutputImpedanceOverlays = computed(() => curveOverlaysFor('OutputImpedance'))

/**
 * The same filter for the impulse waveform. Gated on the trace actually being there, so a result of
 * another kind checked on while this screen is open — or an impulse result that stored none — is
 * skipped rather than drawn empty.
 */
const visibleImpulseOverlays = computed(() => overlays.value.filter((o) => o.visible && o.impulseTrace))

/** The same for the harmonic curves, which a stored impulse result carries separately from its waveform. */
const visibleHarmonicOverlays = computed(() => overlays.value.filter((o) => o.visible && o.harmonicCurves?.length))

/**
 * selectedSweepPointFor's counterpart for a stored overlay: never "running" (it's a saved result),
 * so it always follows the chart cursor, falling back to the channel's last point when nothing is
 * hovered — same fallback selectedSweepPointFor uses once a live sweep is done.
 */
function selectedSweepPointForOverlay(overlay: OverlayEntry, channel: number) {
  const onChannel = (overlay.sweepPoints ?? []).filter((p) => p.channel === channel)
  if (onChannel.length === 0) return null

  const target = pinnedSweepX.value ?? hoveredSweepX.value ?? sweepXOf(onChannel[onChannel.length - 1])
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
const sweepShowThdN = ref(false)
const sweepShowSnr = ref(false)
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

/**
 * A pin is an x value on a particular sweep's grid, so it has to be dropped whenever that grid
 * stops being what is on screen: a different measurement, or the amplitude screen's axis dropdown
 * (a figure in volts means nothing in hertz, and a pin left over would freeze the previews on an
 * arbitrary point of the new axis). Clearing drops it too — see clearSweep. A re-run deliberately
 * does *not*: re-running the same sweep to watch one point is a real bench action, the previews
 * follow the running sweep regardless (see selectedSweepPointFor), and the chart header says a pin
 * is up the whole time. Unlike the hover, nothing drops it on its own.
 */
watch([currentTypeId, sweepChartXAxis], () => {
  pinnedSweepX.value = null
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
    sweepShowD4, sweepShowD5, sweepShowD6Plus, sweepShowThdN, sweepShowSnr,
    sweepShowLeft, sweepShowRight, sweepThickLines,
    sweepShowDataPoints, sweepYMaxPercent, sweepYMinPercent, sweepXFromHz, sweepXToHz, sweepYTopDb,
    sweepYBottomDb, sweepAutofitOnDone, sweepAmplitudeXAxis, sweepAmplitudeBounds,
    sweepFitPreviewSpectrum, sweepFitPreviewScope,
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
/** The sweep chart's display state as the refs above hold it — what a live report renders the chart with. */
function currentSweepDisplay(): SweepDisplay {
  return {
    // The one display field that is not a ref of this screen's own: annotations live in the shared
    // useAnnotations singleton, because a measurement keeps one list across all of its charts. Here
    // so a note placed or edited marks the measurement dirty like every other display change, and so
    // a live report renders the chart with what is on it.
    annotations: liveAnnotations.value,
    unit: sweepUnit.value,
    showNoiseFloor: sweepShowNoiseFloor.value,
    showMagnitude: sweepShowMagnitude.value,
    showThd: sweepShowThd.value,
    showD2: sweepShowD2.value,
    showD3: sweepShowD3.value,
    showD4: sweepShowD4.value,
    showD5: sweepShowD5.value,
    showD6Plus: sweepShowD6Plus.value,
    showThdN: sweepShowThdN.value,
    showSnr: sweepShowSnr.value,
    showLeft: sweepShowLeft.value,
    showRight: sweepShowRight.value,
    thickLines: sweepThickLines.value,
    showDataPoints: sweepShowDataPoints.value,
    yMaxPercent: sweepYMaxPercent.value,
    yMinPercent: sweepYMinPercent.value,
    yTopDb: sweepYTopDb.value,
    yBottomDb: sweepYBottomDb.value,
    xFromHz: sweepXFromHz.value,
    xToHz: sweepXToHz.value,
    amplitudeXAxis: sweepAmplitudeXAxis.value,
    amplitudeBounds: Object.fromEntries(Object.entries(sweepAmplitudeBounds).map(([k, v]) => [k, { ...v }])),
  }
}

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
  // The chart's own fields go through the shared restore (sweep/display.ts), which the PDF report
  // also uses to render a stored sweep — so a type restores the same on screen and on paper.
  const display = sweepDisplayFromParams(paramsJson, currentSweepDisplay())
  // The notes come back with the rest of the display state, and are applied even when the
  // list is empty -- a measurement that has never carried one must not be left showing the
  // previous measurement's, which is what every other field here deliberately does.
  applyLoadedAnnotations(display.annotations)
  sweepAmplitudeXAxis.value = display.amplitudeXAxis
  for (const [axis, bounds] of Object.entries(display.amplitudeBounds)) sweepAmplitudeBounds[axis] = { ...bounds }
  sweepUnit.value = display.unit
  sweepShowNoiseFloor.value = display.showNoiseFloor
  sweepShowMagnitude.value = display.showMagnitude
  sweepShowThd.value = display.showThd
  sweepShowD2.value = display.showD2
  sweepShowD3.value = display.showD3
  sweepShowD4.value = display.showD4
  sweepShowD5.value = display.showD5
  sweepShowD6Plus.value = display.showD6Plus
  sweepShowThdN.value = display.showThdN
  sweepShowSnr.value = display.showSnr
  sweepShowLeft.value = display.showLeft
  sweepShowRight.value = display.showRight
  sweepThickLines.value = display.thickLines
  sweepShowDataPoints.value = display.showDataPoints
  sweepYMaxPercent.value = display.yMaxPercent
  sweepYMinPercent.value = display.yMinPercent
  sweepXFromHz.value = display.xFromHz
  sweepXToHz.value = display.xToHz
  sweepYTopDb.value = display.yTopDb
  sweepYBottomDb.value = display.yBottomDb
  if (typeof stored.chartAutofitOnDone === 'boolean') sweepAutofitOnDone.value = stored.chartAutofitOnDone
  // Absent on a type saved before the previews had a Fit all button, which now reads as *on* --
  // the default these toggles took on 2026-10-17 (see sweep/previewAxis.ts). Only an explicit
  // false survives, which is the toggle a type was deliberately saved with. Written straight
  // rather than through sweepDisplayFromParams: these belong to the rail's previews, which no
  // report draws, so SweepDisplay has nothing to say about them.
  sweepFitPreviewSpectrum.value = stored.chartFitPreviewSpectrum !== false
  sweepFitPreviewScope.value = stored.chartFitPreviewScope !== false

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
const impedanceConfigPanel = ref<InstanceType<typeof ImpedanceConfigPanel> | null>(null)
const steppedResponseConfigPanel = ref<InstanceType<typeof SteppedResponseConfigPanel> | null>(null)
const crosstalkConfigPanel = ref<InstanceType<typeof CrosstalkConfigPanel> | null>(null)
const outputImpedanceConfigPanel = ref<InstanceType<typeof OutputImpedanceConfigPanel> | null>(null)
const sweepConfigPanel = ref<InstanceType<typeof SweepConfigPanel> | null>(null)
const amplitudeSweepConfigPanel = ref<InstanceType<typeof AmplitudeSweepConfigPanel> | null>(null)
const imdSweepConfigPanel = ref<InstanceType<typeof ImdSweepConfigPanel> | null>(null)
const activeSweepPanel = computed(() =>
  isImdSweep.value ? imdSweepConfigPanel.value : isAmplitudeSweep.value ? amplitudeSweepConfigPanel.value : sweepConfigPanel.value,
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
const responseView = ref<import('./api/client').ResponseView>(DEFAULT_RESPONSE_DISPLAY.view)
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
// The delay curve on the Bode chart's own delay axis: none by default, and a +/-1 ms window in
// microseconds -- the unit the chart, the panel and params_json all carry it in.
const responseDelayView = ref<import('./api/client').ResponseDelayView>(DEFAULT_RESPONSE_DISPLAY.delayView)
const responseDelayTopUs = ref(DEFAULT_RESPONSE_DISPLAY.delayTopUs)
const responseDelayBottomUs = ref(DEFAULT_RESPONSE_DISPLAY.delayBottomUs)
// Off by default so nothing changes for a type saved before this existed.
const responseAutofitOnDone = ref(false)

// Which projection of the response is on screen. A separate axis of state from responseView, which
// still means only "which y quantity the Bode chart shows" -- see ResponseProjection's own remarks.
const responseProjection = ref<import('./api/client').ResponseProjection>('Bode')
// Linear is the textbook Nyquist plot and the only one whose distances mean what they look like;
// the dB map trades that away to keep 60 dB of range on screen.
const responseNyquistRadius = ref<import('./api/client').NyquistRadiusName>('Linear')
const responseNyquistRadiusFloorDb = ref(-40)
// Nichols' gain axis: fitted by default, which is what every build before the control did. There is
// no phase pair beside it on purpose -- that axis carries unwrapped degrees, so its span belongs to
// the DUT rather than to a preference (response/projection.ts's nicholsViewport says why).
const responseNicholsAutoGain = ref(DEFAULT_RESPONSE_DISPLAY.nicholsAutoGain)
const responseNicholsTopDb = ref(DEFAULT_RESPONSE_DISPLAY.nicholsTopDb)
const responseNicholsBottomDb = ref(DEFAULT_RESPONSE_DISPLAY.nicholsBottomDb)
// The pole-zero map's window, in hertz on both axes. Fitted by default, and square while it is --
// damping reads as an angle from the origin there.
const responsePoleZeroAutoWindow = ref(DEFAULT_RESPONSE_DISPLAY.poleZeroAutoWindow)
const responsePoleZeroXMinHz = ref(DEFAULT_RESPONSE_DISPLAY.poleZeroXMinHz)
const responsePoleZeroXMaxHz = ref(DEFAULT_RESPONSE_DISPLAY.poleZeroXMaxHz)
const responsePoleZeroYMinHz = ref(DEFAULT_RESPONSE_DISPLAY.poleZeroYMinHz)
const responsePoleZeroYMaxHz = ref(DEFAULT_RESPONSE_DISPLAY.poleZeroYMaxHz)
const responseInvertLoopSign = ref(false)
const responseRestoreDelay = ref(false)
const responseShowFittedModel = ref(DEFAULT_RESPONSE_DISPLAY.showFittedModel)
const responseHideOutOfBandRoots = ref(DEFAULT_RESPONSE_DISPLAY.hideOutOfBandRoots)
// Linear stays the default s-plane for the same reason Linear is the default Nyquist radius: it is
// the plane a reader expects, and switching the scale must change where a root is drawn and nothing
// about what it is.
const responseSurfaceScale = ref<import('./api/client').SPlaneScaleName>(DEFAULT_RESPONSE_DISPLAY.surfaceScale)
const responseSurfaceMode = ref<import('./api/client').SurfaceModeName>(DEFAULT_RESPONSE_DISPLAY.surfaceMode)
const responseSurfaceTopDb = ref(DEFAULT_RESPONSE_DISPLAY.surfaceTopDb)
const responseSurfaceBottomDb = ref(DEFAULT_RESPONSE_DISPLAY.surfaceBottomDb)
const responseSurfacePalette = ref<import('./api/client').DecayPalette>(DEFAULT_RESPONSE_DISPLAY.surfacePalette)
const responseSurfaceContours = ref(DEFAULT_RESPONSE_DISPLAY.surfaceContours)

/**
 * Whether the measurement on screen has a complex response at all.
 *
 * Not `responsePhaseAvailable`, which is the looser gate the Bode phase series uses. A
 * ChannelDifference run measures its gain against the commanded generator level and its phase
 * against the right input — two different ratios — so 10^(dB/20)·e^(jφ) built from the pair is a
 * complex number describing nothing. It plots, and it looks entirely plausible, which is why it is
 * excluded by construction rather than by a warning.
 */
/**
 * The phase kind the screen should be read with: the live run's when there is one, and otherwise the
 * first checked history item's, taken from its own stored settings exactly as a report takes it.
 *
 * <p>Without the second half the projection buttons did nothing whenever a stored result was the
 * only thing on screen. The gate asked the <i>runner</i>, and an idle stepped runner reports
 * <c>None</c> — so `activeResponseProjection` clamped every choice back to Bode, silently, while the
 * buttons went on looking clickable. Reported 2026-09-24 against a saved stepped run whose own
 * settings say DutResponse, i.e. one the polar views can represent perfectly well.</p>
 */
const effectiveResponsePhaseKind = computed<ResponsePhaseKindName>(() => {
  if (responseTraces.value.length > 0) return responsePhaseKind.value
  const first = visibleResponseOverlays.value[0]
  if (!first) return responsePhaseKind.value
  return responsePhaseKindFromParams(first.detail.kind, first.detail.paramsJson)
})

const responseHasComplexResponse = computed(
  () => effectiveResponsePhaseKind.value === 'DutResponse' || effectiveResponsePhaseKind.value === 'PerTrace',
)

// Falls back to the Bode chart whenever the run cannot support a projection, so selecting a
// stored polar preference on a type that no longer qualifies never leaves a blank screen.
const activeResponseProjection = computed(() =>
  responseProjection.value !== 'Bode' && responseHasComplexResponse.value ? responseProjection.value : 'Bode',
)

const responsePolarChart = ref<InstanceType<typeof ResponsePolarChart> | null>(null)
const responseSPlaneChart = ref<InstanceType<typeof ResponseSPlaneChart> | null>(null)
const responsePoleZeroPanel = ref<InstanceType<typeof ResponsePoleZeroPanel> | null>(null)

/**
 * The s-plane chart on screen, whichever of the two it is.
 *
 * `ResponsePoleZeroPanel` takes its curve from the mounted chart rather than from a store, so this is
 * what keeps the table describing the same points the picture draws — see `useResponseComplexCurve`.
 */
const responseComplexChart = computed(() => responsePolarChart.value ?? responseSPlaneChart.value)

// Fit settings. Persisted into params_json since 2026-09-24, at Joost's request. They were left out
// originally on the grounds that they describe an *analysis* rather than how the screen is set up,
// and that a stored order could be applied to a different DUT's curve on load — but params_json
// belongs to one measurement type, i.e. one DUT and one setup, so that case does not arise; and the
// order is a judgement reached by reading the residual, which should survive closing the app. Auto
// is the default, so a type that never touched them still fits adaptively.
const responseFitAutoOrder = ref(true)
const responseFitOrder = ref(2)
const responseFitZeroOrder = ref(2)

// Same guard as sweepDisplayRestoring: applying a type's stored display state is the opposite of an
// unsaved edit, and marking it dirty there would light Save up on every selection.
let responseDisplayRestoring = false

/** The Bode chart's display state as the refs above hold it — what a live report renders the chart with. */
function currentResponseDisplay(): ResponseDisplay {
  return {
    // The one display field that is not a ref of this screen's own: annotations live in the shared
    // useAnnotations singleton, because a measurement keeps one list across all of its charts. Here
    // so a note placed or edited marks the measurement dirty like every other display change, and so
    // a live report renders the chart with what is on it.
    annotations: liveAnnotations.value,
    view: responseView.value,
    showLeft: responseShowLeft.value,
    showRight: responseShowRight.value,
    showPhase: responseShowPhase.value,
    showCoherence: responseShowCoherence.value,
    showOneDb: responseShowOneDb.value,
    showThreeDb: responseShowThreeDb.value,
    thickLines: responseThickLines.value,
    showDataPoints: responseShowDataPoints.value,
    yTopDb: responseYTopDb.value,
    yBottomDb: responseYBottomDb.value,
    yTopDbv: responseYTopDbv.value,
    yBottomDbv: responseYBottomDbv.value,
    xFromHz: responseXFromHz.value,
    xToHz: responseXToHz.value,
    phaseTopDeg: responsePhaseTopDeg.value,
    phaseBottomDeg: responsePhaseBottomDeg.value,
    delayView: responseDelayView.value,
    delayTopUs: responseDelayTopUs.value,
    delayBottomUs: responseDelayBottomUs.value,
    nyquistRadius: responseNyquistRadius.value,
    nyquistRadiusFloorDb: responseNyquistRadiusFloorDb.value,
    nicholsAutoGain: responseNicholsAutoGain.value,
    nicholsTopDb: responseNicholsTopDb.value,
    nicholsBottomDb: responseNicholsBottomDb.value,
    poleZeroAutoWindow: responsePoleZeroAutoWindow.value,
    poleZeroXMinHz: responsePoleZeroXMinHz.value,
    poleZeroXMaxHz: responsePoleZeroXMaxHz.value,
    poleZeroYMinHz: responsePoleZeroYMinHz.value,
    poleZeroYMaxHz: responsePoleZeroYMaxHz.value,
    invertLoopSign: responseInvertLoopSign.value,
    restoreDelay: responseRestoreDelay.value,
    showFittedModel: responseShowFittedModel.value,
    hideOutOfBandRoots: responseHideOutOfBandRoots.value,
    surfaceScale: responseSurfaceScale.value,
    surfaceMode: responseSurfaceMode.value,
    surfaceTopDb: responseSurfaceTopDb.value,
    surfaceBottomDb: responseSurfaceBottomDb.value,
    surfacePalette: responseSurfacePalette.value,
    surfaceContours: responseSurfaceContours.value,
  }
}

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
  // The Bode chart's own fields go through the shared restore (response/display.ts), which the PDF
  // report also uses to render a stored measurement — so a type restores the same on screen and on
  // paper. What follows is the screen's own state: the polar views, autofit and the fit order.
  const display = responseDisplayFromParams(paramsJson, currentResponseDisplay())
  // The notes come back with the rest of the display state, and are applied even when the
  // list is empty -- a measurement that has never carried one must not be left showing the
  // previous measurement's, which is what every other field here deliberately does.
  applyLoadedAnnotations(display.annotations)
  responseView.value = display.view
  responseShowLeft.value = display.showLeft
  responseShowRight.value = display.showRight
  responseShowPhase.value = display.showPhase
  responseShowCoherence.value = display.showCoherence
  responseShowOneDb.value = display.showOneDb
  responseShowThreeDb.value = display.showThreeDb
  responseThickLines.value = display.thickLines
  responseShowDataPoints.value = display.showDataPoints
  responseYTopDb.value = display.yTopDb
  responseYBottomDb.value = display.yBottomDb
  responseYTopDbv.value = display.yTopDbv
  responseYBottomDbv.value = display.yBottomDbv
  responseXFromHz.value = display.xFromHz
  responseXToHz.value = display.xToHz
  responsePhaseTopDeg.value = display.phaseTopDeg
  responsePhaseBottomDeg.value = display.phaseBottomDeg
  responseDelayView.value = display.delayView
  responseDelayTopUs.value = display.delayTopUs
  responseDelayBottomUs.value = display.delayBottomUs
  responseNyquistRadius.value = display.nyquistRadius
  responseNyquistRadiusFloorDb.value = display.nyquistRadiusFloorDb
  responseNicholsAutoGain.value = display.nicholsAutoGain
  responseNicholsTopDb.value = display.nicholsTopDb
  responseNicholsBottomDb.value = display.nicholsBottomDb
  responsePoleZeroAutoWindow.value = display.poleZeroAutoWindow
  responsePoleZeroXMinHz.value = display.poleZeroXMinHz
  responsePoleZeroXMaxHz.value = display.poleZeroXMaxHz
  responsePoleZeroYMinHz.value = display.poleZeroYMinHz
  responsePoleZeroYMaxHz.value = display.poleZeroYMaxHz
  responseShowFittedModel.value = display.showFittedModel
  responseHideOutOfBandRoots.value = display.hideOutOfBandRoots
  responseSurfaceScale.value = display.surfaceScale
  responseSurfaceMode.value = display.surfaceMode
  responseSurfaceTopDb.value = display.surfaceTopDb
  responseSurfaceBottomDb.value = display.surfaceBottomDb
  responseSurfacePalette.value = display.surfacePalette
  responseSurfaceContours.value = display.surfaceContours
  responseInvertLoopSign.value = display.invertLoopSign
  responseRestoreDelay.value = display.restoreDelay
  if (typeof stored.chartAutofitOnDone === 'boolean') responseAutofitOnDone.value = stored.chartAutofitOnDone
  // Unconditional, with Bode as the fallback: assigning only when the type stores one leaves the
  // previous measurement's projection on screen for a type that has never been saved, so a fresh
  // Frequency response could open on Nyquist because the last one was looked at that way.
  responseProjection.value = stored.chartProjection ?? 'Bode'
  // Absent on a type written before the orders were stored: Auto, which is what those builds did.
  responseFitAutoOrder.value = typeof stored.chartFitAutoOrder === 'boolean' ? stored.chartFitAutoOrder : true
  if (typeof stored.chartFitPoles === 'number') responseFitOrder.value = stored.chartFitPoles
  if (typeof stored.chartFitZeros === 'number') responseFitZeroOrder.value = stored.chartFitZeros

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
    responseDelayView, responseDelayTopUs, responseDelayBottomUs,
    responseProjection, responseNyquistRadius, responseNyquistRadiusFloorDb,
    responseNicholsAutoGain, responseNicholsTopDb, responseNicholsBottomDb,
    responsePoleZeroAutoWindow, responsePoleZeroXMinHz, responsePoleZeroXMaxHz,
    responsePoleZeroYMinHz, responsePoleZeroYMaxHz,
    responseInvertLoopSign, responseRestoreDelay,
    responseFitAutoOrder, responseFitOrder, responseFitZeroOrder,
    // The fit overlay and the six s-plane surface controls. Stored and restored like everything
    // above, and missing from this list until 2026-10-16: with autosave off, changing the surface
    // palette or unticking the fitted model left the Save button hidden, and the choice was
    // discarded on the next measurement switch without a word. The same class of gap as the FFT
    // screen's Load field -- a control bound to a ref that nothing watches.
    responseShowFittedModel, responseHideOutOfBandRoots,
    responseSurfaceScale, responseSurfaceMode, responseSurfaceTopDb, responseSurfaceBottomDb,
    responseSurfacePalette, responseSurfaceContours,
  ],
  () => {
    if (!responseDisplayRestoring && isFrequencyResponse.value) markDirty()
  },
)

// ---------------------------------------------------------------------------
// Impedance
// ---------------------------------------------------------------------------

const impedanceView = ref<import('./api/client').ImpedanceView>('Bode')
const impedanceShowPhase = ref(true)
const impedanceShowCoherence = ref(false)
const impedanceShowShortCal = ref(false)
const impedanceThickLines = ref(false)
const impedanceShowDataPoints = ref(false)
const impedanceZTopOhms = ref(100)
const impedanceZBottomOhms = ref(1)
// Linear by default: it is how an impedance curve is usually drawn, and it reads a flat load as flat.
const impedanceZScale = ref<import('./api/client').ImpedanceZScale>('Linear')
const impedanceXFromHz = ref(10)
const impedanceXToHz = ref(20000)
const impedancePhaseTopDeg = ref(90)
const impedancePhaseBottomDeg = ref(-90)
// The one screen that keeps it on, and deliberately: its control is "Autofit each measurement",
// not the others' "Autofit when measurement done", and it also fits once as soon as a run produces
// a curve -- an ohm axis has no sensible default window, so a window left over from another DUT
// shows nothing at all for the minutes a stepped sweep takes, which is indistinguishable from a
// run that has hung. See ImpedanceDisplayPanel.vue's own watcher.
const impedanceAutofitOnDone = ref(true)

/**
 * What the readout interprets the impedance curve as. None by default: an impedance sweep is a
 * general measurement, and volunteering Thiele-Small parameters for whatever happens to be on the
 * fixture is how a choke's self-resonance gets reported as a driver's fs.
 */
const impedanceDutType = ref<DutType>('None')

/**
 * The frequency the Equivalent circuit block reads, owned here rather than by the readout panel: the
 * comparison table under the chart reads every checked history curve at the same frequency, and the
 * report's Equivalent circuit block prints it. Persisted with the rest of the screen's display state.
 */
const impedanceEquivalentHz = ref(1000)

let impedanceDisplayRestoring = false

/**
 * Restores an impedance type's stored chart state. Every field is optional and guarded, so a type
 * saved before one of them existed simply keeps whatever is showing.
 */
/** The impedance chart's display state as the refs above hold it — what a live report renders the chart with. */
function currentImpedanceDisplay(): ImpedanceDisplay {
  return {
    // The one display field that is not a ref of this screen's own: annotations live in the shared
    // useAnnotations singleton, because a measurement keeps one list across all of its charts. Here
    // so a note placed or edited marks the measurement dirty like every other display change, and so
    // a live report renders the chart with what is on it.
    annotations: liveAnnotations.value,
    view: impedanceView.value,
    showPhase: impedanceShowPhase.value,
    showCoherence: impedanceShowCoherence.value,
    showShortCal: impedanceShowShortCal.value,
    thickLines: impedanceThickLines.value,
    showDataPoints: impedanceShowDataPoints.value,
    zScale: impedanceZScale.value,
    zTopOhms: impedanceZTopOhms.value,
    zBottomOhms: impedanceZBottomOhms.value,
    xFromHz: impedanceXFromHz.value,
    xToHz: impedanceXToHz.value,
    phaseTopDeg: impedancePhaseTopDeg.value,
    phaseBottomDeg: impedancePhaseBottomDeg.value,
  }
}

function applyStoredImpedanceDisplay(paramsJson: string) {
  type StoredDisplay = Partial<import('./api/client').ImpedanceParameters>

  let stored: StoredDisplay
  try {
    stored = JSON.parse(paramsJson) as StoredDisplay
  } catch {
    return
  }

  impedanceDisplayRestoring = true
  // The chart's own fields go through the shared restore (impedance/display.ts), which the PDF
  // report also uses to render a stored impedance — so a type restores the same on screen and on paper.
  const display = impedanceDisplayFromParams(paramsJson, currentImpedanceDisplay())
  // The notes come back with the rest of the display state, and are applied even when the
  // list is empty -- a measurement that has never carried one must not be left showing the
  // previous measurement's, which is what every other field here deliberately does.
  applyLoadedAnnotations(display.annotations)
  impedanceView.value = display.view
  impedanceShowPhase.value = display.showPhase
  impedanceShowCoherence.value = display.showCoherence
  impedanceShowShortCal.value = display.showShortCal
  impedanceThickLines.value = display.thickLines
  impedanceShowDataPoints.value = display.showDataPoints
  impedanceZScale.value = display.zScale
  impedanceZTopOhms.value = display.zTopOhms
  impedanceZBottomOhms.value = display.zBottomOhms
  impedanceXFromHz.value = display.xFromHz
  impedanceXToHz.value = display.xToHz
  impedancePhaseTopDeg.value = display.phaseTopDeg
  impedancePhaseBottomDeg.value = display.phaseBottomDeg
  if (typeof stored.chartAutofitOnDone === 'boolean') impedanceAutofitOnDone.value = stored.chartAutofitOnDone
  impedanceDutType.value = isDutType(stored.chartDutType) ? stored.chartDutType : 'None'
  impedanceEquivalentHz.value =
    typeof stored.chartEquivalentHz === 'number' && stored.chartEquivalentHz > 0 ? stored.chartEquivalentHz : 1000

  void nextTick(() => {
    impedanceDisplayRestoring = false
  })
}

// Kind-gated for the same reason the response watcher is: another kind's params_json parses fine,
// and reading chart fields out of it would apply that screen's state to this one.
watch(
  () => (isImpedance.value ? currentType.value?.paramsJson : undefined),
  (paramsJson) => {
    if (paramsJson) applyStoredImpedanceDisplay(paramsJson)
  },
  { immediate: true },
)

watch(
  [
    impedanceView, impedanceShowPhase, impedanceShowCoherence, impedanceShowShortCal, impedanceThickLines,
    impedanceShowDataPoints, impedanceZTopOhms, impedanceZBottomOhms, impedanceZScale, impedanceXFromHz,
    impedanceXToHz, impedancePhaseTopDeg, impedancePhaseBottomDeg, impedanceAutofitOnDone,
    impedanceDutType, impedanceEquivalentHz,
  ],
  () => {
    if (!impedanceDisplayRestoring && isImpedance.value) markDirty()
  },
)

// A measurement runs server-side, so a reload (or a second tab) mid-run has to re-attach rather than
// start from an empty chart.
watch(isImpedance, (selected) => {
  if (selected) void refreshImpedance().catch(() => undefined)
}, { immediate: true })

// Crosstalk chart display preferences — the same arrangement as the impedance screen's, through
// crosstalk/display.ts so the report restores a stored type the way the screen does.
const crosstalkThickLines = ref(DEFAULT_CROSSTALK_DISPLAY.thickLines)
const crosstalkShowDataPoints = ref(DEFAULT_CROSSTALK_DISPLAY.showDataPoints)
const crosstalkYTopDb = ref(DEFAULT_CROSSTALK_DISPLAY.yTopDb)
const crosstalkYBottomDb = ref(DEFAULT_CROSSTALK_DISPLAY.yBottomDb)
const crosstalkXFromHz = ref(DEFAULT_CROSSTALK_DISPLAY.xFromHz)
const crosstalkXToHz = ref(DEFAULT_CROSSTALK_DISPLAY.xToHz)
const crosstalkAutofitOnDone = ref(DEFAULT_CROSSTALK_DISPLAY.autofitOnDone)

let crosstalkDisplayRestoring = false

function currentCrosstalkDisplay(): CrosstalkDisplay {
  return {
    // The one display field that is not a ref of this screen's own: annotations live in the shared
    // useAnnotations singleton, because a measurement keeps one list across all of its charts. Here
    // so a note placed or edited marks the measurement dirty like every other display change, and so
    // a live report renders the chart with what is on it.
    annotations: liveAnnotations.value,
    thickLines: crosstalkThickLines.value,
    showDataPoints: crosstalkShowDataPoints.value,
    yTopDb: crosstalkYTopDb.value,
    yBottomDb: crosstalkYBottomDb.value,
    xFromHz: crosstalkXFromHz.value,
    xToHz: crosstalkXToHz.value,
    autofitOnDone: crosstalkAutofitOnDone.value,
  }
}

function applyStoredCrosstalkDisplay(paramsJson: string) {
  crosstalkDisplayRestoring = true
  const display = crosstalkDisplayFromParams(paramsJson, currentCrosstalkDisplay())
  // The notes come back with the rest of the display state, and are applied even when the
  // list is empty -- a measurement that has never carried one must not be left showing the
  // previous measurement's, which is what every other field here deliberately does.
  applyLoadedAnnotations(display.annotations)
  crosstalkThickLines.value = display.thickLines
  crosstalkShowDataPoints.value = display.showDataPoints
  crosstalkYTopDb.value = display.yTopDb
  crosstalkYBottomDb.value = display.yBottomDb
  crosstalkXFromHz.value = display.xFromHz
  crosstalkXToHz.value = display.xToHz
  crosstalkAutofitOnDone.value = display.autofitOnDone
  void nextTick(() => {
    crosstalkDisplayRestoring = false
  })
}

// Kind-gated like the impedance watcher above, and for the same reason.
watch(
  () => (isCrosstalk.value ? currentType.value?.paramsJson : undefined),
  (paramsJson) => {
    if (paramsJson) applyStoredCrosstalkDisplay(paramsJson)
  },
  { immediate: true },
)

watch(
  [crosstalkThickLines, crosstalkShowDataPoints, crosstalkYTopDb, crosstalkYBottomDb, crosstalkXFromHz, crosstalkXToHz, crosstalkAutofitOnDone],
  () => {
    if (!crosstalkDisplayRestoring && isCrosstalk.value) markDirty()
  },
)

// Output-impedance chart display preferences — the crosstalk arrangement, through
// outputImpedance/display.ts so the report restores a stored type the way the screen does.
const outputImpedanceZTopOhms = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.zTopOhms)
const outputImpedanceZBottomOhms = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.zBottomOhms)
const outputImpedanceZScale = ref<import('./api/client').ImpedanceZScale>(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.zScale)
/**
 * Whether the right channel was the reference, and the nominal load the damping factor is quoted
 * against — for the run actually on screen. The live one when there is one, otherwise the first
 * checked history item, read from its own params.
 *
 * <p>Both live only on the runner. Asking it while idle returned `false` and `0`, so a saved result
 * checked on from History drew its Zout curve and then offered no damping factor (the axis needs
 * `nominal / |Z|`, which zero cannot form) with the Phase box greyed out — even for a run whose own
 * settings say it measured one. Reported 2026-09-24 against "DF 8-4 ohm, right channel as
 * reference".</p>
 */
const outputImpedanceRunFacts = computed(() => {
  const live = {
    rightChannelIsReference: outputImpedanceReferenced.value,
    nominalOhms: outputImpedanceNominalOhms.value,
  }
  if (outputImpedanceTraces.value.length > 0) return live

  const stored = visibleOutputImpedanceOverlays.value[0]
  if (!stored) return live

  const facts = outputImpedanceRunFactsFromParams(stored.detail.paramsJson)
  return {
    rightChannelIsReference: facts.rightChannelIsReference ?? live.rightChannelIsReference,
    nominalOhms: facts.nominalOhms ?? live.nominalOhms,
  }
})

const outputImpedanceShowDampingFactor = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.showDampingFactor)
const outputImpedanceShowPhase = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.showPhase)
const outputImpedanceXFromHz = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.xFromHz)
const outputImpedanceXToHz = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.xToHz)
const outputImpedancePhaseTopDeg = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.phaseTopDeg)
const outputImpedancePhaseBottomDeg = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.phaseBottomDeg)
const outputImpedanceThickLines = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.thickLines)
const outputImpedanceShowDataPoints = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.showDataPoints)
const outputImpedanceAutofitOnDone = ref(DEFAULT_OUTPUT_IMPEDANCE_DISPLAY.autofitOnDone)

let outputImpedanceDisplayRestoring = false

function currentOutputImpedanceDisplay(): OutputImpedanceDisplay {
  return {
    // The one display field that is not a ref of this screen's own: annotations live in the shared
    // useAnnotations singleton, because a measurement keeps one list across all of its charts. Here
    // so a note placed or edited marks the measurement dirty like every other display change, and so
    // a live report renders the chart with what is on it.
    annotations: liveAnnotations.value,
    zTopOhms: outputImpedanceZTopOhms.value,
    zBottomOhms: outputImpedanceZBottomOhms.value,
    zScale: outputImpedanceZScale.value,
    showDampingFactor: outputImpedanceShowDampingFactor.value,
    showPhase: outputImpedanceShowPhase.value,
    xFromHz: outputImpedanceXFromHz.value,
    xToHz: outputImpedanceXToHz.value,
    phaseTopDeg: outputImpedancePhaseTopDeg.value,
    phaseBottomDeg: outputImpedancePhaseBottomDeg.value,
    thickLines: outputImpedanceThickLines.value,
    showDataPoints: outputImpedanceShowDataPoints.value,
    autofitOnDone: outputImpedanceAutofitOnDone.value,
  }
}

function applyStoredOutputImpedanceDisplay(paramsJson: string) {
  outputImpedanceDisplayRestoring = true
  const display = outputImpedanceDisplayFromParams(paramsJson, currentOutputImpedanceDisplay())
  // The notes come back with the rest of the display state, and are applied even when the
  // list is empty -- a measurement that has never carried one must not be left showing the
  // previous measurement's, which is what every other field here deliberately does.
  applyLoadedAnnotations(display.annotations)
  outputImpedanceZTopOhms.value = display.zTopOhms
  outputImpedanceZBottomOhms.value = display.zBottomOhms
  outputImpedanceZScale.value = display.zScale
  outputImpedanceShowDampingFactor.value = display.showDampingFactor
  outputImpedanceShowPhase.value = display.showPhase
  outputImpedanceXFromHz.value = display.xFromHz
  outputImpedanceXToHz.value = display.xToHz
  outputImpedancePhaseTopDeg.value = display.phaseTopDeg
  outputImpedancePhaseBottomDeg.value = display.phaseBottomDeg
  outputImpedanceThickLines.value = display.thickLines
  outputImpedanceShowDataPoints.value = display.showDataPoints
  outputImpedanceAutofitOnDone.value = display.autofitOnDone
  void nextTick(() => {
    outputImpedanceDisplayRestoring = false
  })
}

watch(
  () => (isOutputImpedance.value ? currentType.value?.paramsJson : undefined),
  (paramsJson) => {
    if (paramsJson) applyStoredOutputImpedanceDisplay(paramsJson)
  },
  { immediate: true },
)

watch(
  [
    outputImpedanceZTopOhms, outputImpedanceZBottomOhms, outputImpedanceZScale, outputImpedanceShowDampingFactor,
    outputImpedanceShowPhase, outputImpedanceXFromHz, outputImpedanceXToHz, outputImpedancePhaseTopDeg,
    outputImpedancePhaseBottomDeg, outputImpedanceThickLines, outputImpedanceShowDataPoints, outputImpedanceAutofitOnDone,
  ],
  () => {
    if (!outputImpedanceDisplayRestoring && isOutputImpedance.value) markDirty()
  },
)

// A run — or its pause — survives a reload, so arriving on the screen re-attaches.
watch(isOutputImpedance, (selected) => {
  if (selected) void refreshOutputImpedance().catch(() => undefined)
}, { immediate: true })

function runImpedance(continuous: boolean) {
  const request = impedanceConfigPanel.value?.buildRequest(continuous)
  if (!request) return

  return withErrorReporting(async () => {
    await startImpedance(request)
  })
}

/**
 * Captures one calibration standard. The same runner and the same request shape as a measurement —
 * only `calTarget` differs — so this goes through the panel's own builder rather than assembling a
 * second request here.
 */
function calibrateImpedance(target: import('./api/client').ImpedanceCalTargetName) {
  const request = impedanceConfigPanel.value?.buildRequest(false, target)
  if (!request) return

  return withErrorReporting(async () => {
    await startImpedance(request)
  })
}

function cancelImpedance() {
  return withErrorReporting(async () => {
    await cancelImpedanceRun()
  })
}

function clearImpedance() {
  return withErrorReporting(async () => {
    await clearImpedanceRun()
  })
}

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
    // With the curves: the pin names a frequency on a grid that no longer exists, and the rail it
    // was holding has just gone with it.
    pinnedResponseHz.value = null
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

/** The output-impedance run: RUN SWEEP starts the first load's pass; CONTINUE, at the pause, the second's. */
function runOutputImpedance(request: import('./api/client').OutputImpedanceStartRequest) {
  return withErrorReporting(async () => {
    await startOutputImpedance(request)
  })
}

function continueOutputImpedance() {
  return withErrorReporting(() => continueOutputImpedanceRun())
}

function cancelOutputImpedance() {
  return withErrorReporting(() => cancelOutputImpedanceRun())
}

function clearOutputImpedance() {
  return withErrorReporting(() => clearOutputImpedanceRun())
}

/**
 * The crosstalk sweep: the stepped sine's single RUN SWEEP model, from the panel's own `run` emit.
 */
function runCrosstalk(request: import('./api/client').CrosstalkStartRequest) {
  return withErrorReporting(async () => {
    await startCrosstalk(request)
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
  [isSteppedResponse, isCrosstalk],
  ([stepped, crosstalk]) => {
    setResponseTarget(crosstalk ? 'Crosstalk' : stepped ? 'Stepped' : 'Chirp')
    if (isFrequencyResponse.value || crosstalk) void refreshResponse().catch(() => undefined)
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

function runImdSweep(request: import('./api/client').ImdSweepStartRequest) {
  sweepShowLeft.value = request.processLeft
  sweepShowRight.value = request.processRight
  return withErrorReporting(async () => {
    await imdSweep.start(request)
  })
}

function cancelSweep() {
  return withErrorReporting(async () => {
    if (isImdSweep.value) await imdSweep.cancel()
    else await cancelSweepRun()
  })
}

// The sweep screen's counterpart of clearMeasurement. The hovered frequency has to go with the
// points: it is a frequency from a sweep that no longer exists, and leaving it set would keep the
// FFT/scope preview charts pointing at the last thing hovered on the chart that was just emptied.
function clearSweep() {
  return withErrorReporting(async () => {
    if (isImdSweep.value) await imdSweep.clear()
    else await clearSweepRun()
    hoveredSweepX.value = null
    pinnedSweepX.value = null
  })
}

// A sweep runs server-side, so a reload (or opening a second tab) mid-sweep has to re-attach
// rather than start from an empty chart. Both runners are refreshed: which one the screen shows
// is the open type's, and switching between the two sweep kinds does not pass through false.
watch(isSweepKind, (isSweep) => {
  if (isSweep) {
    void refreshSweep().catch(() => undefined)
    void imdSweep.refresh().catch(() => undefined)
  }
})

const isCurrentTypeDirty = computed(() => currentTypeId.value !== null && dirtyTypeIds.value.has(currentTypeId.value))

/**
 * Autosave: the dirty flag going up is the trigger, and the save is the Save button's own
 * (saveCurrentType). Debounced a little rather than immediate, because a run of edits — typing a
 * frequency digit by digit, dragging a zoom — dirties the type on the first one and the save reads
 * the live state when it fires, so one write covers the whole run. The flag stays up until that
 * write lands, so nothing edited in between is missed: the next edit after a save raises it again.
 *
 * Only while the type is still dirty when the timer fires: a switch in between has already saved
 * (through the flush below) or discarded, and saving a clean type would just rewrite it.
 */
const AUTOSAVE_DEBOUNCE_MS = 750
let autosaveTimer: ReturnType<typeof setTimeout> | null = null

function cancelPendingAutosave() {
  if (autosaveTimer !== null) clearTimeout(autosaveTimer)
  autosaveTimer = null
}

async function autosaveNow() {
  cancelPendingAutosave()
  if (!autosaveEnabled.value || !isCurrentTypeDirty.value) return
  await saveCurrentType()
}

watch([isCurrentTypeDirty, autosaveEnabled], ([dirty, enabled]) => {
  cancelPendingAutosave()
  if (!dirty || !enabled) return
  autosaveTimer = setTimeout(() => void autosaveNow(), AUTOSAVE_DEBOUNCE_MS)
})

// Navigating away flushes instead of asking — see useProjects.confirmDiscardCurrentIfDirty.
setAutosaveFlush(autosaveNow)

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

/**
 * Swaps the analyzer between the QA403, the simulator and a sound card. The server tears the old
 * one down and stands the new one up, so a failure here is a refusal (a measurement is running)
 * rather than a half-switched session — the error line is the whole response.
 *
 * The range ladder is re-read afterwards. Every analyzer has its own: a sound card reports one
 * fixed rung where the QA403 has eight, and `ranges` is otherwise fetched once at mount, so
 * without this the browser would keep the previous analyzer's ladder for the rest of the session —
 * which is also what the live ranging watcher falls back to.
 */
function setDeviceKind(kind: import('./api/client').DeviceKindName) {
  return withErrorReporting(async () => {
    status.value = await api.setDeviceKind(kind)
    await refreshRanges()
  })
}

/**
 * Closing the sound-card dialog re-reads the range ladder: a calibration accepted in there moves the
 * card's single input rung, and the ladder is otherwise only fetched at mount.
 */
function closeSoundCardDialog() {
  soundCardDialogOpen.value = false
  void refreshRanges()
}

/**
 * And so does closing the QA403's, for the same reason: the experimental 384 kHz checkbox moves the
 * analyzer's reported sample-rate list, which `ranges` is otherwise only fetched at mount — so
 * without this the box appears to do nothing until the page is reloaded. (It used to hang off
 * Configuration, which is where that checkbox was until 2026-10-05.) The device itself needs no
 * reconnect; it reads the setting through a Func, so the open connection already reports the new
 * list (see Qa40xUsbDevice.Allow384kHz).
 */
function closeQa403Dialog() {
  qa403DialogOpen.value = false
  void refreshRanges()
}

/**
 * The QA403 dialog's footer link to the full Configuration — the two overlap (ranging, the capture
 * delay), and the dialog's own three settings are a shortcut rather than a separate place. One
 * dialog at a time, so it closes on the way, which also means the ranges are re-read.
 */
function openConfigurationFromQa403() {
  closeQa403Dialog()
  configDialogOpen.value = true
}

/**
 * The load dialog's link to Configuration, where which load this bench has is chosen — and turned
 * off again. One dialog at a time, like the QA403's. No ranges to re-read: nothing about the load
 * moves the analyzer's ladders.
 */
function openConfigurationFromLoad() {
  loadDialogOpen.value = false
  configDialogOpen.value = true
}

function closeConfigDialog() {
  configDialogOpen.value = false
}

/**
 * The status bar's Ext. gain popover, for every analyzer.
 *
 * <p><b>This is the routing, and it is the whole reason the dialog does not own it.</b> Each bench
 * stores its own front-end gain -- the sound card in its configuration document, the QA403 and the
 * simulator in `acquisition.externalGain` -- and `DeviceSessionService` applies whichever analyzer
 * is live. Writing to the wrong one is not an error: it stores the number, returns 200, and applies
 * nothing, which is exactly what the Configuration dialog's field used to do while the sound card
 * was selected. Sending both edits through one handler that keys on the live kind is what makes
 * that unrepresentable.</p>
 */
function setExternalGain(gain: { leftDb: number; rightDb: number; unit: 'Db' | 'Times' }) {
  return withErrorReporting(async () => {
    if (status.value?.deviceKind === 'SoundCard')
      await soundCard.setFrontEndGain(gain.leftDb, gain.rightDb, gain.unit)
    else
      await setSharedExternalGain({ leftDb: gain.leftDb, rightDb: gain.rightDb, unit: gain.unit })

    status.value = await api.getDevice()
  })
}

// The status bar's Ranging control. All three write through the server and take the status it
// returns rather than setting anything locally: the input range in particular is the *device's*
// answer, and a range it refuses or a mode change it declines has to show up as the dropdown
// snapping back rather than as a UI that believes something the hardware never did.
function setRangingMode(mode: import('./api/client').RangingModeName) {
  return withErrorReporting(async () => {
    status.value = await api.setRangingMode(mode)
  })
}

function setInputRangeManually(dbv: number) {
  return withErrorReporting(async () => {
    status.value = await api.setInputRange(dbv)
  })
}

function setOutputRangeManually(dbv: number) {
  return withErrorReporting(async () => {
    status.value = await api.setOutputRange(dbv)
  })
}

// The status bar's Connect button: the app starts with no device rather than refusing to start, so
// this is how a QA403 plugged in afterwards gets picked up. Failure is just an error line in the
// status bar — nothing else about the session changes, and pressing it again is safe. The line is
// the server's own text rather than the usual "POST /api/device/connect failed: 503 …": the
// endpoint writes it for the person at the bench (see DeviceEndpoints), and the request line in
// front of it is what made "no QA403 found" read as a fault in the app.
const connecting = ref(false)

async function connectDevice() {
  connecting.value = true
  try {
    status.value = await api.connectDevice()
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiError && e.status === 503 ? e.body : (e as Error).message
  } finally {
    connecting.value = false
  }
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
//
// singleShotArmed: whether the Single button armed the capture this screen is now waiting on — see
// the autoStopOnceHeld watcher below for why merely seeing `held` is not enough.
const singleShotArmed = ref(false)
function runMeasurement(runConfig: RunConfig, singleShot = false) {
  return withErrorReporting(async () => {
    const { sampleRateHz, channelCount } = runConfig

    // Drop whatever the previous run last displayed, most importantly any `held` flag from an
    // earlier Single capture — otherwise the auto-stop watcher below could see stale held=true
    // meters left over from before this run even produces its first fresh frame.
    restoreFftDisplay(null)

    // Armed before the start request goes out, not after it returns: a held meters frame can
    // land before the response does (a 4096-point record is 21 ms at 192 kHz), and a held channel
    // sends nothing further for the watcher to react to. A plain Run disarms a pending Single.
    singleShotArmed.value = singleShot

    // No auto-ranging request here any more. RUN used to force the device's ranger back on every
    // time, which was harmless while nothing could ever turn it off — and is exactly what would
    // undo the status bar's Manual pin on the first press of the button the user pinned it for.
    // The ranging mode is now the only thing that writes AutoRangeEnabled (see RangingMode), and
    // it is applied when the mode is set and again on every connect.

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

      // No wait for the generator to settle here: the Configuration dialog's "Capture delay" is
      // applied server-side, as a discard at the front of the analysis window when acquisition
      // starts with the generator on -- see DeviceSessionService.StartAcquisitionAsync. It used to
      // be a setTimeout between these two calls, which covered this one path and no sweep point.
    }

    // Single rides on the start itself (see AcquisitionStartRequest.singleShot): it used to be a
    // separate arming call after this one, whose bare flush threw that capture delay away again.
    const newStatus = await api.startAcquisition({ sampleRateHz, channelCount, singleShot })
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
  return runMeasurement(currentRunConfig())
}

function currentRunConfig(): RunConfig {
  return {
    sampleRateHz: spectrumConfigPanel.value?.getSampleRateHz() ?? 192000,
    channelCount: ranges.value?.maxChannelCount ?? 2,
  }
}

// The Stop button's counterpart — same "stop everything" used automatically when switching
// measurements (see useProjects.ts's selectType), just with a snappy refresh here since this one
// has a button sitting right there waiting on it, unlike the switch-measurement case.
function stopMeasurement() {
  return withErrorReporting(async () => {
    singleShotArmed.value = false
    await stopRun()
    await refreshStatus()
  })
}

// The Single button: the same start as Run with SpectrumPipelineOptions.SingleShot asked for on
// it, so the capture settles to the configured average count and holds instead of free-running —
// the autoStopOnceHeld watcher below then stops it automatically, same as pressing Stop, once
// every processed channel's meters frame comes back flagged Held. Not a saved config value — just
// another way to capture data, alongside Run/Stop. One request rather than start-then-arm on
// purpose: the arming call's own flush used to cancel the capture delay the start had booked, so
// Single measured the DUT still settling where Run did not.
async function singleShotCapture() {
  await runMeasurement(currentRunConfig(), true)
  if (!status.value?.acquiring)
    singleShotArmed.value = false
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
//
// Also re-evaluated when `acquiring` flips, not only on a meters frame: a held frame that arrives
// before the start response has set status is otherwise dropped for good, since a held channel
// sends nothing further.
let autoStopping = false
watch([meters, () => status.value?.acquiring], ([m, acquiring]) => {
  if (!singleShotArmed.value || !acquiring || autoStopping) return

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

// Which runners hold the session. Read by the Measurements panel (`runner-active`), and the
// reason a few controls stand down while a measurement is up: every runner ranges for itself,
// and the server refuses an input-range write under one with a 409.
//
// It used to gate a ranging watcher here as well. That watcher is gone: the input range of an
// analyzer with no ranger of its own is now chosen in the session, block by block
// (`SessionInputRanger`), because a decision made per browser tab is a decision two open windows
// can disagree about — and each disagreement is a range write, which flushes the analysis window
// and restarts averaging. Measured on the simulator 2026-10-09: two attached clients wrote 18 and
// 12 dBV alternately about once a second, and a 15-average FFT restarted every third record,
// three being this file's own RANGE_STABILITY_FRAMES.
const runnerActive = computed(
  () =>
    responseRunning.value ||
    sweepRunning.value ||
    impedanceRunning.value ||
    impulseRunning.value ||
    outputImpedanceRunning.value ||
    soundCard.active.value,
)

function reportError(message: string) {
  error.value = message
}

function openSaveDialog() {
  saveDialogOpen.value = true
}

// Every screen's charts, for the Screenshot button — each exposes exportPng. Only the chart of the
// view that is mounted is non-null, which is how screenshotChart picks the one on screen.
const responseChart = ref<InstanceType<typeof FrequencyResponseChart> | null>(null)
const spectrumChart = ref<InstanceType<typeof SpectrumChart> | null>(null)
const sweepChart = ref<InstanceType<typeof ThdSweepChart> | null>(null)
const impulseChart = ref<InstanceType<typeof ImpulseResponseChart> | null>(null)
const impulseResponseChart = ref<InstanceType<typeof FrequencyResponseChart> | null>(null)
const impulseHarmonicsChart = ref<InstanceType<typeof ImpulseHarmonicsChart> | null>(null)
const impulseDecayChart = ref<InstanceType<typeof DecaySurfaceChart> | null>(null)
const impedanceChart = ref<InstanceType<typeof ImpedanceChart> | null>(null)
const outputImpedanceChart = ref<InstanceType<typeof ImpedanceChart> | null>(null)
const impedancePlaneChart = ref<InstanceType<typeof ImpedancePlaneChart> | null>(null)
// The FFT screen's spectrogram, for the report's Spectrogram element (not the Screenshot, which takes
// the main chart): mounted only while the display panel shows it, null otherwise.
const spectrogramChart = ref<InstanceType<typeof SpectrogramChart> | null>(null)

/** The chart on screen — whichever view or projection the screen is showing. */
function chartOnScreen() {
  if (isFrequencyResponse.value) {
    if (activeResponseProjection.value === 'Bode') return responseChart.value
    return activeResponseProjection.value === 'SPlane' ? responseSPlaneChart.value : responsePolarChart.value
  }
  if (isCrosstalk.value) return responseChart.value
  if (isOutputImpedance.value) return outputImpedanceChart.value
  if (isSweepKind.value) return sweepChart.value
  if (isImpulseResponse.value) {
    switch (impulseView.value) {
      case 'Impulse':
        return impulseChart.value
      case 'Response':
        return impulseResponseChart.value
      case 'Harmonics':
        return impulseHarmonicsChart.value
      default:
        return impulseDecayChart.value
    }
  }
  if (isImpedance.value) return impedanceView.value === 'Bode' ? impedanceChart.value : impedancePlaneChart.value
  return spectrumChart.value
}

/**
 * Whether the chart on screen has something to draw.
 *
 * <p>**A checked history item counts**, not only the live measurement. Every arm used to ask about
 * the live state alone, so pressing <strong>Clear</strong> with an overlay still ticked disabled
 * the Screenshot button over a chart that was plainly showing a curve — reported 2026-10-08, and
 * the obvious thing to want a picture of: a stored run on its own, with nothing live drawn over
 * it. Each arm therefore ORs in the same per-screen visible-overlay list the chart is handed, so
 * the button is enabled exactly when the chart has a trace, whichever of the two put it there.</p>
 *
 * <p>The two decay surfaces are the deliberate exception: they are computed from the live impulse
 * response and no overlay contributes one, so there is nothing of an overlay's to photograph.</p>
 */
const screenshotAvailable = computed(() => {
  if (isFrequencyResponse.value) return responseTraces.value.length > 0 || visibleResponseOverlays.value.length > 0
  if (isCrosstalk.value) return responseTraces.value.length > 0 || visibleCrosstalkOverlays.value.length > 0
  if (isOutputImpedance.value)
    return outputImpedanceTrace.value !== null || visibleOutputImpedanceOverlays.value.length > 0
  if (isSweepKind.value) return sweepPoints.value.length > 0 || visibleSweepOverlays.value.length > 0
  if (isImpulseResponse.value) {
    switch (impulseView.value) {
      case 'Impulse':
        return impulseTrace.value !== null || visibleImpulseOverlays.value.length > 0
      case 'Response':
        return impulseTraces.value.length > 0 || visibleImpulseResponseOverlays.value.length > 0
      case 'Harmonics':
        return impulseHarmonics.value.length > 0 || visibleHarmonicOverlays.value.length > 0
      case 'Csd':
        return impulseCsdSurface.value !== null
      default:
        return impulseBurstSurface.value !== null
    }
  }
  if (isImpedance.value) return impedanceTraces.value.length > 0 || visibleImpedanceOverlays.value.length > 0
  // spectrumAxisOverlay is the same test the chart itself makes for "can I draw from an overlay
  // with no live frame" — a visible FFT overlay that actually carries a channel.
  return Object.keys(spectrum.value).length > 0 || spectrumAxisOverlay(overlays.value) !== null
})

/**
 * The chart as a PNG, in the light palette, at the zoom on screen — at the size, ratio and colours
 * the Screenshot dialog asks, inside a frame carrying its title and date, to the clipboard, a file,
 * or both. The chart draws itself (see FrequencyResponseChart.exportPng and its siblings) at the
 * frame's chart area, so what is in the file is what is on screen; the frame and the greys are
 * post-processes (screenshot.ts, monochromeChartPng). The answers are remembered by
 * useScreenshotSettings for the next screenshot.
 */
const screenshotDialogOpen = ref(false)
const { remember: rememberScreenshotSettings } = useScreenshotSettings()
const { format: formatDateTimePreference, load: loadDateTimeFormat } = useDateTimeFormat()
void loadDateTimeFormat()

async function screenshotChart(settings: ScreenshotSettings) {
  screenshotDialogOpen.value = false
  void rememberScreenshotSettings(settings)
  const chart = chartOnScreen()
  if (!chart) return
  try {
    const layout = screenshotFrameLayout(settings.widthPx, settings.aspectRatio, settings.title, settings.addDate)
    const chartPng = await chart.exportPng({ width: layout.chartWidth, height: layout.chartHeight, captureZoom: true })
    const framed = await frameChartPng({
      chart: chartPng,
      layout,
      title: settings.title,
      date: settings.addDate ? formatDateTimePreference(new Date()) : '',
    })
    const png = settings.monochrome ? await monochromeChartPng(framed) : framed

    if (settings.destination !== 'clipboard') {
      downloadBlob(png, `${safeFileName(currentType.value?.name ?? 'chart')}.png`)
    }
    if (settings.destination !== 'file') {
      try {
        await copyPngToClipboard(png)
      } catch (e) {
        // The browser's own sentence ("Write permission denied") says nothing about what was
        // asked or what still happened; a file already saved is worth saying.
        const saved = settings.destination === 'both' ? ' The file was saved.' : ''
        throw new Error(`Could not copy the screenshot to the clipboard: ${(e as Error).message}${saved}`)
      }
    }
  } catch (e) {
    reportError((e as Error).message)
  }
}

/**
 * The chart on screen as a PNG for an issue report, or null when this screen has no chart or has
 * nothing drawn on it yet.
 *
 * <p>A fixed 1200 x 800 in the light palette, with the zoom captured — the Screenshot dialog's size
 * and ratio questions are for a document, and a report wants a picture that is legible rather than
 * one anybody chose a size for. No frame, no title, no black and white: the report's own
 * <code>report.md</code> says what measurement it was.</p>
 *
 * <p>Base64 rather than a Blob because it rides inside the JSON request, and <code>FileReader</code>
 * is what turns one into the other without a loop over a megabyte of bytes.</p>
 */
async function issueReportScreenshot(): Promise<string | null> {
  const chart = chartOnScreen()
  if (!chart || !screenshotAvailable.value) return null

  try {
    const png = await chart.exportPng({ width: 1200, height: 800, captureZoom: true })
    return await blobToBase64(png)
  } catch {
    // A chart that will not export is a smaller problem than the one being reported.
    return null
  }
}

function blobToBase64(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onerror = () => reject(reader.error ?? new Error('Could not read the chart image.'))
    reader.onload = () => {
      // readAsDataURL gives "data:image/png;base64,AAAA" and the server wants the payload alone.
      const text = String(reader.result)
      const comma = text.indexOf(',')
      resolve(comma >= 0 ? text.slice(comma + 1) : text)
    }
    reader.readAsDataURL(blob)
  })
}

/**
 * Everything the browser knows that the server cannot, for the report's <code>client</code> block.
 *
 * <p>Three kinds of thing, and each has cost a diagnosis. <b>Which browser and how big its
 * window</b>: several faults here have been rendering ones (a ResizeObserver that never fires in a
 * hidden pane, a chart that kept a 432 px canvas inside a 661 px column), and a report otherwise
 * cannot say whether it was Chrome, Edge or Firefox. <b>Which measurement was on screen</b>, by kind
 * as well as name, because a screenshot of a Bode plot does not say which of three projections drew
 * it. And <b>the staged settings</b>, which are the client's own until a run starts — the server's
 * status reports what is playing, never what is typed into the panels, so a fault in a run that was
 * never started leaves no trace anywhere else.</p>
 */
function issueReportClientState(): unknown {
  return {
    userAgent: navigator.userAgent,
    language: navigator.language,
    window: { width: window.innerWidth, height: window.innerHeight, pixelRatio: window.devicePixelRatio },
    theme: document.documentElement.classList.contains('dark') ? 'dark' : 'light',
    project: currentProject.value?.name ?? null,
    measurement: currentType.value
      ? { id: currentType.value.id, name: currentType.value.name, kind: currentType.value.kind }
      : null,
    view: {
      responseProjection: isFrequencyResponse.value ? activeResponseProjection.value : null,
      impulseView: isImpulseResponse.value ? impulseView.value : null,
      impedanceView: isImpedance.value ? impedanceView.value : null,
    },
    stagedStimulus: stimulusSummary(stimulusKind.value, stimulusTones, stimulusMultitone),
    runnersActive: {
      response: responseRunning.value,
      sweep: sweepRunning.value,
      impedance: impedanceRunning.value,
      impulse: impulseRunning.value,
      outputImpedance: outputImpedanceRunning.value,
    },
    lastError: error.value ?? null,
  }
}

/**
 * What is on screen as a report subject, or null when there is no measurement open. Assembled from
 * the live refs: the curves, the display panel's state, and the type's *current* settings — which
 * may be edited since the run, so the chapter labels them as configured rather than as run. A stored
 * result's chapter is exact. The report dialogs decide from `subjectHasChartData` whether to offer it.
 */
function buildLiveSubject(): ReportSubject | null {
  const type = currentType.value
  if (!type) return null

  const facts: { label: string; value: string }[] = []
  const subject: ReportSubject = {
    id: 'live',
    resultId: null,
    typeId: type.id,
    kind: type.kind,
    typeName: type.name,
    resultName: null,
    projectName: currentProject.value?.name ?? '',
    typeNotes: type.notes,
    resultNotes: null,
    paramsJson: buildTypeParamsJson() ?? type.paramsJson,
    timestampUtc: null,
    facts,
    sampleRateHz: 0,
    detail: null,
    data: { kind: 'none' },
  }

  if (isFrequencyResponse.value) {
    subject.sampleRateHz = responseSampleRateHz.value
    if (responseSampleRateHz.value > 0) facts.push({ label: 'Sample rate', value: `${responseSampleRateHz.value} Hz` })
    if (responseBinHz.value > 0) facts.push({ label: 'Resolution', value: `${responseBinHz.value.toFixed(3)} Hz per bin` })
    if (responseTraces.value.length > 0) facts.push({ label: 'Generator level', value: `${responseGeneratorAmpDbv.value.toFixed(2)} dBV` })
    subject.data = {
      kind: 'response',
      traces: responseTraces.value,
      display: currentResponseDisplay(),
      phaseKind: effectiveResponsePhaseKind.value,
      rightChannelIsReference: responseRightChannelIsReference.value,
      latencySamples: responseLatencySamples.value,
    }
  } else if (isCrosstalk.value) {
    subject.sampleRateHz = responseSampleRateHz.value
    if (responseSampleRateHz.value > 0) facts.push({ label: 'Sample rate', value: `${responseSampleRateHz.value} Hz` })
    if (responseTraces.value.length > 0) facts.push({ label: 'Generator level', value: `${responseGeneratorAmpDbv.value.toFixed(2)} dBV` })
    subject.data = { kind: 'crosstalk', traces: responseTraces.value, display: currentCrosstalkDisplay() }
  } else if (isOutputImpedance.value) {
    subject.sampleRateHz = outputImpedanceSampleRateHz.value
    if (outputImpedanceSampleRateHz.value > 0) facts.push({ label: 'Sample rate', value: `${outputImpedanceSampleRateHz.value} Hz` })
    facts.push({ label: 'Load', value: `${outputImpedanceLoadOhms.value} Ω` })
    if (outputImpedanceTrace.value) facts.push({ label: 'Generator level', value: `${outputImpedanceGeneratorAmpDbv.value.toFixed(2)} dBV` })
    subject.data = {
      kind: 'outputImpedance',
      trace: outputImpedanceTrace.value,
      display: currentOutputImpedanceDisplay(),
      nominalOhms: outputImpedanceNominalOhms.value,
      rightChannelIsReference: outputImpedanceReferenced.value,
    }
  } else if (isSweepKind.value) {
    if (sweepPoints.value.length > 0) facts.push({ label: 'Points', value: String(sweepPoints.value.length) })
    subject.data = {
      kind: 'sweep',
      points: sweepPoints.value,
      method: sweepThdMethod.value,
      family: sweepTraceFamily.value,
      display: currentSweepDisplay(),
    }
  } else if (isImpulseResponse.value) {
    subject.sampleRateHz = impulseStatus.value?.sampleRateHz ?? 0
    if (subject.sampleRateHz > 0) facts.push({ label: 'Sample rate', value: `${subject.sampleRateHz} Hz` })
    const impulse = impulseStatus.value
    subject.data = {
      kind: 'impulse',
      impulse: impulseTrace.value,
      traces: impulseTraces.value,
      harmonics: impulseHarmonics.value,
      display: currentImpulseDisplay(),
      run: {
        referenceMode: impulse?.referenceMode ?? 'None',
        referenceName: impulse?.referenceCalibrationName ?? null,
        latencySamples: impulse?.latencySamples ?? null,
        averagesTaken: impulse?.averagesTaken ?? null,
        averageCount: impulse?.averageCount ?? 0,
        generatorAmpDbv: impulse?.generatorAmpDbv ?? null,
      },
    }
  } else if (isImpedance.value) {
    facts.push({ label: 'Reference resistor', value: `${impedanceReferenceOhms.value} Ω` })
    if (impedanceTraces.value.length > 0) facts.push({ label: 'Generator level', value: `${impedanceGeneratorAmpDbv.value.toFixed(2)} dBV` })
    subject.data = {
      kind: 'impedance',
      traces: impedanceTraces.value,
      display: currentImpedanceDisplay(),
      dutType: impedanceDutType.value,
      equivalentHz: impedanceEquivalentHz.value,
      referenceOhms: impedanceReferenceOhms.value,
      generatorAmpDbv: impedanceTraces.value.length > 0 && Number.isFinite(impedanceGeneratorAmpDbv.value) ? impedanceGeneratorAmpDbv.value : null,
    }
  } else {
    if (spectrumConfig.value) {
      facts.push({ label: 'FFT size', value: String(spectrumConfig.value.fftSize) })
      facts.push({ label: 'Window', value: spectrumConfig.value.window })
    }
    // The screen's own display singleton: the report draws exactly what the display panel shows.
    // The scope frames and the mounted spectrogram come along for the report's two secondary charts —
    // the spectrogram's picture is the history the chart on screen collected, and only it can draw it.
    subject.data = {
      kind: 'spectrum',
      // The full-span frames, never the drawn ones: a report is of the measurement and not of the
      // view, and `renderChartPng` passes captureZoom: false so the chart is drawn at the display
      // panel's own From/To either way. Handed the zoomed frames it filled that axis with the zoom
      // window's columns and left the rest of the span blank. See `spectrumOverview`.
      spectrum: Object.keys(spectrumOverview.value).length > 0 ? spectrumOverview.value : spectrum.value,
      meters: meters.value,
      intermodulation: intermodulation.value,
      multitone: multitone.value,
      noise: noise.value,
      weightingLabel: weightingAnnunciator.value,
      display: useChartDisplay(),
      selectedMetrics: selectedMetrics.value,
      generatorTones: status.value?.generatorLeft.tones ?? [],
      // The staged stimulus, not the live one: a report is usually taken after Stop, which silences
      // DeviceStatus's tones — MetricsPanel.vue decides the same thing from the same place.
      stimulus: stimulusSummary(stimulusKind.value, stimulusTones, stimulusMultitone),
      loadOhms: loadOhms.value,
      scope: scope.value,
      scopeResidual: scopeResidual.value,
      status: status.value,
      spectrogram: spectrogramChart.value,
    }
  }

  return subject
}

/**
 * "Report…" for the open measurement: from the toolbar with what is on screen preselected, from a
 * History row with that result preselected. One dialog either way — it lists both.
 */
function openReport(preselectResultId: number | null) {
  reportLiveSubject.value = buildLiveSubject()
  reportPreselectResultId.value = preselectResultId
  reportOpen.value = true
}

function openProjectReport() {
  reportLiveSubject.value = buildLiveSubject()
  projectReportOpen.value = true
}

// The Clear button next to Save measurement result: discards the retained (stopped) spectrum both
// server-side (so Save has nothing stale left to save) and here in the client's own display state,
// same reset runMeasurement does before a fresh Run — so the chart goes straight back to "Waiting
// for data…" instead of waiting for a frame that isn't coming until Run/Single is pressed again.
// Does not touch anything already saved to History.
function clearMeasurement() {
  return withErrorReporting(async () => {
    await api.clearSpectrum()
    restoreFftDisplay(null)
  })
}

// ---------------------------------------------------------------------------
// One measurement's live state never reaches another's screen
// ---------------------------------------------------------------------------
// See measurements/liveOwner.ts for the whole reasoning. In short: every runner drives the one
// SpectrumPipeline, so every run — a stepped sweep, an impedance calibration, anything — fills the
// live FFT display and the server's retained snapshot, and selecting an unrelated FFT measurement
// then drew that record inside its own axis ranges. Reported 2026-09-27.
const liveOwners = ref<LiveOwners>({})

/** Who the live state on screen belongs to right now — project *and* type, see liveOwnerKey. */
const liveOwnerNow = computed(() => liveOwnerKey(currentProjectId.value, currentTypeId.value))

/**
 * Is anything measuring: an acquisition, or any of the six runners.
 *
 * <p>`status.acquiring` alone will not do, and the two watchers below both depend on that: the
 * status poll is 1 Hz and a short chirp run can start and finish between two polls, so the
 * acquiring transition on its own is missable. Each runner's `running` is set from its start
 * call's own response, which is not.</p>
 */
const anythingLive = computed(
  () =>
    status.value?.acquiring === true ||
    sweepRunning.value ||
    responseRunning.value ||
    impedanceRunning.value ||
    impulseRunning.value ||
    outputImpedanceRunning.value,
)

// Stamps the open measurement as the owner of whatever is now being filled -- one watcher rather
// than a line in each of the ten run functions.
watch(anythingLive, (live) => {
  if (live) liveOwners.value = claimLive(liveOwners.value, liveStoreForKind(currentType.value?.kind), liveOwnerNow.value)
})

// The sidebar's rows and its New measurement button are disabled while this is true, and
// selectType refuses a switch -- see useProjects' `runActive`. This is the one place that can see
// every runner, which is why the flag is pushed down rather than computed there. `immediate`
// because a reload lands mid-run often enough to matter: the status poll answers before the user
// can click, but the first paint should not offer a row it is about to refuse.
watch(anythingLive, setRunActive, { immediate: true })

/**
 * Discards the live state that belongs to a measurement other than the one about to be selected.
 *
 * <p>Registered with useProjects and awaited by `selectType` <b>before</b> `currentTypeId` moves —
 * see `liveStateReset` there for why a watcher would race the re-attach instead. Nothing is running
 * by this point: `selectType` has already called `stopRun`.</p>
 *
 * <p>Exactly one store is a candidate: the one the screen being switched *to* draws from (see
 * `liveScreenStore`). Whatever the screen being switched away *from* was showing is left alone —
 * that is what keeps a five-minute sweep, or a settled FFT capture, on screen when you click its own
 * measurement away and back, which is the reason this is an ownership question and not a blanket
 * clear.</p>
 */
async function discardForeignLiveState(nextTypeId: number) {
  const nextOwner = liveOwnerKey(currentProjectId.value, nextTypeId)
  const nextStore = liveScreenStore(types.value.find((t) => t.id === nextTypeId)?.kind)

  // The server's retained snapshot follows the measurement on screen whatever kind it is, and that
  // breadth is the point: a sweep selected between two FFT measurements writes that one snapshot
  // too, so leaving the owner behind while it ran would hand the first measurement the sweep's last
  // point. See SpectrumPipeline.SwitchRetentionOwner.
  const retention =
    nextOwner === liveOwnerNow.value
      ? Promise.resolve(null)
      : api.setSpectrumRetentionOwner(nextOwner).catch(() => null)

  // The live FFT display is swapped rather than discarded, and only on arrival at a screen that
  // draws it -- see swapFftDisplay.
  if (nextStore === 'fft') {
    const adopted = swapFftDisplay(nextOwner)
    const answer = await retention
    // A display nothing owned is adopted rather than cleared (a fresh page, where the snapshot the
    // server sends on connect belongs to whichever measurement was open before the reload) -- but
    // only by the measurement that snapshot is actually *of*. For any other, the server has just
    // handed over an empty slot, and the trace on screen is one whose Save would rightly refuse
    // it. Only this branch, and only on the answer: the swap path above has already put the
    // arriving measurement's own display back.
    if (!adopted && answer !== null && !answer.hasCapture) {
      restoreFftDisplay(null)
      requestChartResetPeakHold()
      spectrogramChart.value?.clearHistory()
    }
    return
  }

  // The two sweep screens are parked rather than discarded, the way the FFT display is -- except
  // that the points live on the *server* (the runner owns them, and Save sweep result reads them
  // there), so the park is the server's too. One call switches both sweep runners and answers with
  // both statuses: see ThdSweepRunner.SwitchOwner, and RunParking for the reasoning. Posted on
  // arrival at a sweep screen only, because nothing but a sweep writes those runners.
  if (nextStore === 'sweep' || nextStore === 'imdSweep') {
    const [, owned] = await Promise.all([retention, api.setSweepOwner(nextOwner).catch(() => null)])
    // A failure leaves whatever the runners hold on screen and the owner where it was -- the same
    // trade selectType makes about this whole function. Re-read rather than trusted blindly only
    // when the call itself failed; the answer is the status both stores would have fetched.
    if (owned) {
      applySweepStatus(owned.thd)
      imdSweep.apply(owned.imd)
    } else {
      await Promise.all([refreshSweep().catch(() => undefined), imdSweep.refresh().catch(() => undefined)])
    }
    // Nothing left for foreignLiveStores to discard here, and the owner map is what tells the
    // *next* switch that these stores are spoken for -- which the server now tracks itself.
    liveOwners.value = releaseLive(liveOwners.value, ['sweep', 'imdSweep'])
    return
  }

  // Every other runner is parked exactly the same way, by the same reasoning and against the same
  // six rules -- see RunParking. The six were still being *cleared* here until 2026-10-17, which is
  // the sweep report's own bug in six more places: measure a frequency response, select another
  // frequency-response measurement, come back, and the curves were gone. Each screen switches its
  // own runner and no other, because nothing but that screen writes it.
  const parked = PARKED_LIVE_STORES[nextStore]
  if (parked) {
    const [, switched] = await Promise.all([
      retention,
      parked.switch(nextOwner).then(() => true).catch(() => false),
    ])
    // A failure leaves whatever the runner holds on screen and the owner where it was -- the same
    // trade the sweep branch above makes, and the same one selectType makes about this whole
    // function. Re-read only then: a switch that went through has already applied the status it
    // answered with, so re-reading would be a second round trip for a figure in hand.
    if (!switched) await parked.reread().catch(() => undefined)
    // Nothing left for foreignLiveStores to discard, and the owner map is what tells the *next*
    // switch that this store is spoken for -- which the server now tracks itself.
    liveOwners.value = releaseLive(liveOwners.value, [nextStore])
    return
  }

  // The fallback, and nothing reaches it today: every store a measurement kind can arrive at is
  // either swapped (fft), parked by the sweep call, or parked by the table below. That is what
  // `PARKED_LIVE_STORES` being Partial is for -- a store added later with no park of its own gets
  // this, the old behaviour, rather than silently showing another measurement's run.
  const stale = foreignLiveStores(liveOwners.value, nextOwner, [nextStore])
  if (stale.length === 0) {
    await retention
    return
  }

  // Released first: a clear that fails (a runner still unwinding answers 409) must not leave the
  // owner behind for the next switch to try again on state it has already reported as wrong.
  liveOwners.value = releaseLive(liveOwners.value, stale)

  await Promise.all([retention, ...stale.map((store) => discardLiveStore(store).catch(() => undefined))])
}

/**
 * The six runners that park per measurement on arrival at their own screen: how to hand each one
 * the arriving owner, and how to re-read it if that call failed. The switch applies the status it
 * answers with itself, inside the store, the way `clear` and `cancel` already do there.
 *
 * <p>A table rather than six branches, so the set is one thing to read against `LiveStore` and a
 * screen cannot be wired to another screen's runner. `fft`, `sweep` and `imdSweep` are deliberately
 * absent and handled above — the FFT display is swapped client-side, and the two sweep runners
 * share one endpoint because they share one screen. A store missing from here is still *cleared*
 * on a switch, which is what every one of these did before 2026-10-17.</p>
 */
const PARKED_LIVE_STORES: Partial<
  Record<LiveStore, { switch: (owner: string | null) => Promise<void>; reread: () => Promise<void> }>
> = {
  chirp: {
    switch: (owner) => switchResponseOwner('Chirp', owner),
    reread: () => refreshResponseTarget('Chirp'),
  },
  stepped: {
    switch: (owner) => switchResponseOwner('Stepped', owner),
    reread: () => refreshResponseTarget('Stepped'),
  },
  crosstalk: {
    switch: (owner) => switchResponseOwner('Crosstalk', owner),
    reread: () => refreshResponseTarget('Crosstalk'),
  },
  impedance: { switch: switchImpedanceOwner, reread: refreshImpedance },
  impulse: { switch: switchImpulseOwner, reread: refreshImpulse },
  outputImpedance: { switch: switchOutputImpedanceOwner, reread: refreshOutputImpedance },
}

/**
 * Every measurement's own live FFT display, by owner key — the spectrum, the meters, the harmonics,
 * the oscilloscope and the IMD/multitone tables as {@link captureFftDisplay} takes them.
 *
 * <p>Bounded by how many FFT measurements have been looked at, at roughly 50 kB each (the drawn
 * ~1600 columns per channel plus two 2048-sample scope tails), so unlike the server's own parked
 * snapshots — which are the whole full-resolution record and are capped for it — this needs no cap.
 * It does not survive a reload, which is deliberate and the same reason `liveOwners` does not: the
 * runs belong to the server, and on a fresh page an unowned display is adopted as it always was.</p>
 */
const fftDisplays = new Map<string, FftDisplayState>()

/**
 * Hands the live FFT display over to `nextOwner`: what is on screen is kept under the measurement
 * it belongs to, and whatever that one had kept is put back.
 *
 * <p>Until 2026-10-16 this was a discard, and with two FFT measurements in a project that meant the
 * first one's trace, tiles and harmonics were gone the moment the second was selected — reported as
 * "the last measured data disappears when switching back and forth". A discard was right about one
 * thing and that part is kept: the two must not show each other's capture, which is also why the
 * <i>server's</i> snapshot is parked with it (the retention-owner call above) rather than left
 * pointing at whichever measurement ran last. Save therefore writes the capture on screen, which is
 * the whole reason a restored display is allowed to be there.</p>
 *
 * <p>Three things it deliberately does not do. It does not touch the display when nothing owns it
 * (a reload mid-run, where the server has just re-sent its snapshot to a fresh page): an unowned
 * display is adopted, exactly as `foreignLiveStores` has it. It does not keep the <b>peak-hold maps
 * or the spectrogram's rows</b> — those are histories of a continuous display rather than of a
 * capture, no frame can re-derive them, and the weighting/unit watcher already discards them for
 * the same reason. And it is only reached on arrival at a screen that <i>draws</i> the display, so
 * glancing at a sweep's settings and coming back leaves an FFT capture, peak-hold and all, exactly
 * where it was.</p>
 */
/** True when the display on screen was swapped for the arriving measurement's own, false when it was adopted as it stood. */
function swapFftDisplay(nextOwner: string | null): boolean {
  const held = liveOwners.value.fft ?? null
  if (held === nextOwner) return true
  if (held === null) {
    liveOwners.value = claimLive(liveOwners.value, null, nextOwner)
    return false
  }

  fftDisplays.set(held, captureFftDisplay())
  restoreFftDisplay(nextOwner === null ? null : fftDisplays.get(nextOwner) ?? null)
  requestChartResetPeakHold()
  spectrogramChart.value?.clearHistory()
  // Not reset here: this runs *before* the selection moves, so the panel still holds the window
  // being left and resetting to it would pin the chart there. See `fftViewNeedsReset`.
  fftViewNeedsReset = true
  liveOwners.value =
    nextOwner === null ? releaseLive(liveOwners.value, ['fft']) : claimLive(liveOwners.value, null, nextOwner)
  return true
}

/**
 * Throws one live store away. The fallback path of `discardForeignLiveState` above, which no store
 * reaches today — every one of them is swapped or parked instead. Kept whole because `LiveStore`
 * is exhaustive here: a store added later with no park gets this rather than falling through.
 */
function discardLiveStore(store: LiveStore): Promise<unknown> {
  switch (store) {
    case 'fft':
      // The FFT display is swapped rather than discarded (see swapFftDisplay).
      restoreFftDisplay(null)
      requestChartResetPeakHold()
      spectrogramChart.value?.clearHistory()
      return api.clearSpectrum()
    case 'sweep':
      return clearSweepRun()
    case 'imdSweep':
      return imdSweep.clear()
    case 'chirp':
      return clearResponseTarget('Chirp')
    case 'stepped':
      return clearResponseTarget('Stepped')
    case 'crosstalk':
      return clearResponseTarget('Crosstalk')
    case 'impedance':
      return clearImpedanceRun()
    case 'impulse':
      return clearImpulse()
    case 'outputImpedance':
      return clearOutputImpedanceRun()
  }
}

setLiveStateReset(discardForeignLiveState)

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
    chartShowFftTrace: chartShowFftTrace.value,
    chartShowHarmonicMarkers: chartShowHarmonicMarkers.value,
    chartHarmonicMarkerUnit: chartHarmonicMarkerUnit.value,
    chartAnnotations: liveAnnotations.value,
    // The Generator panel's Load field. Shared with every sweep panel (useLoadOhms), but stored
    // per measurement like every other setting on screen -- until 2026-10-16 the FFT screen was
    // the one screen that read it and never wrote it, so the figure a measurement's power tiles
    // divided by was whichever sweep had last been opened, and editing it dirtied nothing.
    loadOhms: loadOhms.value,
    chartAutofitOnDone: chartAutofitOnDone.value,
    chartShowSpectrogram: chartShowSpectrogram.value,
    chartShowOscilloscope: chartShowOscilloscope.value,
    chartShowHarmonics: chartShowHarmonics.value,
    chartShowOtherPeaks: chartShowOtherPeaks.value,
    // How the harmonics and other-peaks tables are sorted -- display only, like the toggles above.
    chartHarmonicsSort: chartStoredHarmonicsSort(),
    chartOtherPeaksSort: chartStoredOtherPeaksSort(),
    imdPreset: stimulusImd.preset,
    imdMethod: stimulusImd.method,
    imdMaxOrder: stimulusImd.maxOrder,
    dutGainDb: chartDutGainDb.value,
    chartUnit: chartSpectrumUnit.value,
    chartShowWeightingCurve: chartShowWeightingCurve.value,
    generatorLevelUnit: stimulusLevelUnit.value,
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
type ImpulseLevelView = 'Impulse' | 'Response' | 'Harmonics'

const impulseYWindows = reactive<Record<ImpulseLevelView, { top: number; bottom: number }>>({
  Impulse: { top: 0, bottom: -60 },
  Response: { top: 0, bottom: -120 },
  Harmonics: { top: 0, bottom: -120 },
})

/**
 * Which window the level fields edit. The two decay views have none — 0 dB there is fixed by the
 * surface's own normalisation and the only setting is how far below it to reach — so they fall back
 * to the impulse view's rather than indexing a record that has no key for them.
 */
const impulseLevelView = computed<ImpulseLevelView>(() =>
  impulseView.value === 'Csd' || impulseView.value === 'BurstDecay' ? 'Impulse' : impulseView.value,
)
const impulseYTopDb = computed({
  get: () => impulseYWindows[impulseLevelView.value].top,
  set: (value: number) => {
    impulseYWindows[impulseLevelView.value].top = value
  },
})
const impulseYBottomDb = computed({
  get: () => impulseYWindows[impulseLevelView.value].bottom,
  set: (value: number) => {
    impulseYWindows[impulseLevelView.value].bottom = value
  },
})
const impulsePhaseTopDeg = ref(180)
const impulsePhaseBottomDeg = ref(-180)
const impulseDelayView = ref<import('./api/client').ResponseDelayView>(DEFAULT_IMPULSE_DISPLAY.delayView)
const impulseDelayTopUs = ref(DEFAULT_IMPULSE_DISPLAY.delayTopUs)
const impulseDelayBottomUs = ref(DEFAULT_IMPULSE_DISPLAY.delayBottomUs)
const impulseTimeFromMs = ref(-1)
const impulseTimeToMs = ref(20)
const impulseFreqFromHz = ref(20)
const impulseFreqToHz = ref(20000)

// The two decay surfaces. How they are drawn is shared between them; what is analysed is not.
const impulseDecayMode = ref<import('./api/client').DecayChartMode>('Waterfall')
const impulseDecayPalette = ref<import('./api/client').DecayPalette>('Jet')
const impulseDecayColoredCurves = ref(true)
const impulseDecayRangeDb = ref(30)

const initialCsd = defaultCsdAnalysis(48000)
const impulseCsdFftLength = ref(initialCsd.fftLength)
const impulseCsdBlockShiftSamples = ref(initialCsd.blockShiftSamples)
const impulseCsdBlockCount = ref(initialCsd.blockCount)
const impulseCsdWindowMode = ref<import('./api/client').CsdWindowModeName>(initialCsd.windowMode)
const impulseCsdRiseTimeMs = ref(initialCsd.riseTimeMs)

const impulseBurstMaxPeriods = ref(30)
const impulseBurstPointsPerPeriod = ref(8)

/**
 * The lowest frequency the deepest slice can still answer for — where the burst decay's curves stop
 * short at the left. It is not a setting and not a defect: thirty periods of 50 Hz is 600 ms, and the
 * sweep gate does not hold that much. Stated under the chart the way the CSD's transform limit is,
 * because a curve that stops in mid-air with nothing said about it reads as a drawing fault. Zero
 * when every curve reaches the bottom of the band, in which case there is nothing to say.
 */
const impulseBurstFullDepthHz = computed(() => {
  const surface = impulseBurstSurface.value
  if (!surface || surface.slices.length === 0) return 0

  const deepest = surface.slices[surface.slices.length - 1]
  return deepest.firstValidColumn > 0 ? surface.freqHz[deepest.firstValidColumn] : 0
})
const impulseBurstResolution = ref<import('./api/client').BurstDecayResolutionName>('PreferFrequency')

/** The sample rate the CSD defaults above were derived for. See the watcher below. */
const impulseCsdDefaultsRate = ref(48000)

/**
 * Untouched CSD defaults follow the measurement's sample rate.
 *
 * Every one of those settings is in **samples**, and a sample is four times shorter at 192 kHz than
 * at 48 — a 256-point transform resolves 188 Hz at one rate and 750 Hz at the other. Leaving the
 * defaults fixed would mean the chart is right at one sample rate and empty at another, for no
 * reason the user could see. Settings the user has actually changed are left alone, which is what
 * comparing against the previous rate's defaults establishes.
 */
watch(
  () => impulseStatus.value?.sampleRateHz ?? 0,
  (rate) => {
    if (!(rate > 0) || rate === impulseCsdDefaultsRate.value) return

    const previous = defaultCsdAnalysis(impulseCsdDefaultsRate.value)
    const untouched =
      impulseCsdFftLength.value === previous.fftLength &&
      impulseCsdBlockShiftSamples.value === previous.blockShiftSamples &&
      impulseCsdBlockCount.value === previous.blockCount

    impulseCsdDefaultsRate.value = rate
    if (!untouched) return

    const next = defaultCsdAnalysis(rate)
    impulseCsdFftLength.value = next.fftLength
    impulseCsdBlockShiftSamples.value = next.blockShiftSamples
    impulseCsdBlockCount.value = next.blockCount
  },
)

let impulseDisplayRestoring = false

/** Restores an impulse-response type's stored chart state. Every field is optional and guarded. */
/** The impulse screen's display state as the refs above hold it — what a live report renders its charts with. */
function currentImpulseDisplay(): ImpulseDisplay {
  return {
    // The one display field that is not a ref of this screen's own: annotations live in the shared
    // useAnnotations singleton, because a measurement keeps one list across all of its charts. Here
    // so a note placed or edited marks the measurement dirty like every other display change, and so
    // a live report renders the chart with what is on it.
    annotations: liveAnnotations.value,
    normalised: impulseNormalised.value,
    thickLines: impulseThickLines.value,
    showDataPoints: impulseShowDataPoints.value,
    timeFromMs: impulseTimeFromMs.value,
    timeToMs: impulseTimeToMs.value,
    yTopDb: impulseYWindows.Impulse.top,
    yBottomDb: impulseYWindows.Impulse.bottom,
    showPhase: impulseShowPhase.value,
    showCoherence: impulseShowCoherence.value,
    responseYTopDb: impulseYWindows.Response.top,
    responseYBottomDb: impulseYWindows.Response.bottom,
    harmonicsYTopDb: impulseYWindows.Harmonics.top,
    harmonicsYBottomDb: impulseYWindows.Harmonics.bottom,
    phaseTopDeg: impulsePhaseTopDeg.value,
    phaseBottomDeg: impulsePhaseBottomDeg.value,
    delayView: impulseDelayView.value,
    delayTopUs: impulseDelayTopUs.value,
    delayBottomUs: impulseDelayBottomUs.value,
    freqFromHz: impulseFreqFromHz.value,
    freqToHz: impulseFreqToHz.value,
    decayMode: impulseDecayMode.value,
    decayPalette: impulseDecayPalette.value,
    decayColoredCurves: impulseDecayColoredCurves.value,
    decayRangeDb: impulseDecayRangeDb.value,
    csd: {
      fftLength: impulseCsdFftLength.value,
      blockShiftSamples: impulseCsdBlockShiftSamples.value,
      blockCount: impulseCsdBlockCount.value,
      windowMode: impulseCsdWindowMode.value,
      riseTimeMs: impulseCsdRiseTimeMs.value,
    },
    burstMaxPeriods: impulseBurstMaxPeriods.value,
    burstPointsPerPeriod: impulseBurstPointsPerPeriod.value,
    burstResolution: impulseBurstResolution.value,
  }
}

function applyStoredImpulseDisplay(paramsJson: string) {
  type StoredDisplay = Partial<import('./api/client').ImpulseResponseParameters>

  let stored: StoredDisplay
  try {
    stored = JSON.parse(paramsJson) as StoredDisplay
  } catch {
    return
  }

  impulseDisplayRestoring = true
  // The impulse chart's own fields go through the shared restore (measurements/impulseDisplay.ts),
  // which the PDF report also uses to render a stored impulse — so a type restores the same on
  // screen and on paper. The other views' state follows below.
  const display = impulseDisplayFromParams(paramsJson, currentImpulseDisplay())
  // The notes come back with the rest of the display state, and are applied even when the
  // list is empty -- a measurement that has never carried one must not be left showing the
  // previous measurement's, which is what every other field here deliberately does.
  applyLoadedAnnotations(display.annotations)
  impulseNormalised.value = display.normalised
  impulseThickLines.value = display.thickLines
  impulseShowDataPoints.value = display.showDataPoints
  impulseTimeFromMs.value = display.timeFromMs
  impulseTimeToMs.value = display.timeToMs
  impulseYWindows.Impulse.top = display.yTopDb
  impulseYWindows.Impulse.bottom = display.yBottomDb
  impulseShowPhase.value = display.showPhase
  impulseShowCoherence.value = display.showCoherence
  impulseYWindows.Response.top = display.responseYTopDb
  impulseYWindows.Response.bottom = display.responseYBottomDb
  impulseYWindows.Harmonics.top = display.harmonicsYTopDb
  impulseYWindows.Harmonics.bottom = display.harmonicsYBottomDb
  impulsePhaseTopDeg.value = display.phaseTopDeg
  impulsePhaseBottomDeg.value = display.phaseBottomDeg
  impulseDelayView.value = display.delayView
  impulseDelayTopUs.value = display.delayTopUs
  impulseDelayBottomUs.value = display.delayBottomUs
  impulseFreqFromHz.value = display.freqFromHz
  impulseFreqToHz.value = display.freqToHz
  impulseDecayMode.value = display.decayMode
  impulseDecayPalette.value = display.decayPalette
  impulseDecayColoredCurves.value = display.decayColoredCurves
  impulseDecayRangeDb.value = display.decayRangeDb
  // A stored analysis is a deliberate choice, so it also stops the sample-rate watcher above from
  // replacing it: the values no longer match the previous rate's defaults.
  impulseCsdFftLength.value = display.csd.fftLength
  impulseCsdBlockShiftSamples.value = display.csd.blockShiftSamples
  impulseCsdBlockCount.value = display.csd.blockCount
  impulseCsdWindowMode.value = display.csd.windowMode
  impulseCsdRiseTimeMs.value = display.csd.riseTimeMs
  impulseBurstMaxPeriods.value = display.burstMaxPeriods
  impulseBurstPointsPerPeriod.value = display.burstPointsPerPeriod
  impulseBurstResolution.value = display.burstResolution
  if (stored.chartView) impulseView.value = stored.chartView

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
    impulseDelayView, impulseDelayTopUs, impulseDelayBottomUs,
    impulseDecayMode, impulseDecayPalette, impulseDecayColoredCurves, impulseDecayRangeDb,
    impulseCsdFftLength, impulseCsdBlockShiftSamples, impulseCsdBlockCount, impulseCsdWindowMode,
    impulseCsdRiseTimeMs, impulseBurstMaxPeriods, impulseBurstPointsPerPeriod, impulseBurstResolution,
  ],
  () => {
    if (!impulseDisplayRestoring && isImpulseResponse.value) markDirty()
  },
  { deep: true },
)

/**
 * Recomputes whichever decay surface is on screen, debounced.
 *
 * <p>Debounced because every setting is a number field: typing "40" into Max periods passes through
 * 4, and a burst decay is tens of thousands of taps per analysis frequency. The composable also
 * keeps a request token, so what ends up on screen is whichever analysis was asked for last rather
 * than whichever finished last.</p>
 *
 * <p>Nothing here re-measures. That is the point of the two views: the record is already captured
 * and every one of these settings is a different question put to it.</p>
 */
let impulseDecayTimer: ReturnType<typeof setTimeout> | null = null

function requestImpulseDecay() {
  if (impulseDecayTimer) clearTimeout(impulseDecayTimer)
  if (!isImpulseResponse.value) return
  if (impulseView.value !== 'Csd' && impulseView.value !== 'BurstDecay') return

  // A surface is a heatmap and cannot overlay, so there is one subject: the live measurement when
  // there is one, otherwise the first checked history item. Without this second case a checked item
  // drew its waveform and its gated response and then left both decay views blank, which reads as
  // the analysis being broken rather than as having no input.
  const live = (impulseStatus.value?.passesCompleted ?? 0) > 0
  const storedId = live ? null : (visibleImpulseOverlays.value[0]?.id ?? null)
  if (!live && storedId === null) return

  const isCsd = impulseView.value === 'Csd'
  impulseDecayTimer = setTimeout(() => {
    impulseDecayTimer = null
    if (isCsd) {
      const csdRequest = {
        fftLength: impulseCsdFftLength.value,
        blockShiftSamples: impulseCsdBlockShiftSamples.value,
        blockCount: impulseCsdBlockCount.value,
        windowMode: impulseCsdWindowMode.value,
        riseTimeMs: impulseCsdRiseTimeMs.value,
        lowHz: impulseFreqFromHz.value,
        highHz: impulseFreqToHz.value,
        displayColumns: DECAY_DISPLAY_COLUMNS,
      }
      void (storedId === null ? computeImpulseCsd(csdRequest) : computeStoredImpulseCsd(storedId, csdRequest))
    } else {
      const burstRequest = {
        lowHz: impulseFreqFromHz.value,
        highHz: impulseFreqToHz.value,
        maxPeriods: impulseBurstMaxPeriods.value,
        pointsPerPeriod: impulseBurstPointsPerPeriod.value,
        resolution: impulseBurstResolution.value,
        frequencyColumns: BURST_FREQUENCY_COLUMNS,
      }
      void (storedId === null
        ? computeImpulseBurstDecay(burstRequest)
        : computeStoredImpulseBurstDecay(storedId, burstRequest))
    }
  }, 250)
}

// Only the settings that change the *analysis*. The palette, the chart mode and the dB range are
// redraws of a surface already computed, and asking the server again for them would put a
// half-second pause on a colour change.
watch(
  () => [
    impulseView.value,
    impulseFreqFromHz.value,
    impulseFreqToHz.value,
    impulseCsdFftLength.value,
    impulseCsdBlockShiftSamples.value,
    impulseCsdBlockCount.value,
    impulseCsdWindowMode.value,
    impulseCsdRiseTimeMs.value,
    impulseBurstMaxPeriods.value,
    impulseBurstPointsPerPeriod.value,
    impulseBurstResolution.value,
    impulseStatus.value?.passesCompleted ?? 0,
    // Checking a history item on or off changes which run the surface is of when nothing is live.
    visibleImpulseOverlays.value.map((o) => o.id).join(','),
  ],
  () => requestImpulseDecay(),
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

/**
 * A non-FFT measurement type's `params_json`, with its annotations.
 *
 * <p>Spliced here, in <b>one</b> place, rather than added to each of the six branches below. Those
 * branches hand-build their documents field by field, and a field missing from one of them is not
 * an error -- it is a default, so the type would save and simply lose its notes (the
 * `ChartStartHz` trap, written up in CLAUDE.md). One seam cannot be forgotten by the seventh kind.
 *
 * <p>The FFT screen is not covered and does not need to be: it returns undefined from here and goes
 * through `buildStagedConfig`, which carries the same field explicitly.</p>
 */
function buildTypeParamsJson(): string | undefined {
  const json = buildTypeParamsJsonCore()
  if (json === undefined) return undefined
  return JSON.stringify({ ...JSON.parse(json), chartAnnotations: liveAnnotations.value })
}

function buildTypeParamsJsonCore(): string | undefined {
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
      chartDelayView: impulseDelayView.value,
      chartDelayTopUs: impulseDelayTopUs.value,
      chartDelayBottomUs: impulseDelayBottomUs.value,
      chartTimeFromMs: impulseTimeFromMs.value,
      chartTimeToMs: impulseTimeToMs.value,
      chartFreqFromHz: impulseFreqFromHz.value,
      chartFreqToHz: impulseFreqToHz.value,
      chartDecayMode: impulseDecayMode.value,
      chartDecayPalette: impulseDecayPalette.value,
      chartDecayColoredCurves: impulseDecayColoredCurves.value,
      chartDecayRangeDb: impulseDecayRangeDb.value,
      chartCsdFftLength: impulseCsdFftLength.value,
      chartCsdBlockShiftSamples: impulseCsdBlockShiftSamples.value,
      chartCsdBlockCount: impulseCsdBlockCount.value,
      chartCsdWindowMode: impulseCsdWindowMode.value,
      chartCsdRiseTimeMs: impulseCsdRiseTimeMs.value,
      chartBurstMaxPeriods: impulseBurstMaxPeriods.value,
      chartBurstPointsPerPeriod: impulseBurstPointsPerPeriod.value,
      chartBurstResolution: impulseBurstResolution.value,
    })
  }

  if (isImpedance.value) {
    const base = impedanceConfigPanel.value?.buildParamsJson()
    if (!base) return undefined
    return JSON.stringify({
      ...JSON.parse(base),
      chartView: impedanceView.value,
      chartShowPhase: impedanceShowPhase.value,
      chartShowCoherence: impedanceShowCoherence.value,
      chartShowShortCal: impedanceShowShortCal.value,
      chartThickLines: impedanceThickLines.value,
      chartShowDataPoints: impedanceShowDataPoints.value,
      chartZTopOhms: impedanceZTopOhms.value,
      chartZBottomOhms: impedanceZBottomOhms.value,
      chartZScale: impedanceZScale.value,
      chartXFromHz: impedanceXFromHz.value,
      chartXToHz: impedanceXToHz.value,
      chartPhaseTopDeg: impedancePhaseTopDeg.value,
      chartPhaseBottomDeg: impedancePhaseBottomDeg.value,
      chartAutofitOnDone: impedanceAutofitOnDone.value,
      chartDutType: impedanceDutType.value,
      chartEquivalentHz: impedanceEquivalentHz.value,
    })
  }

  if (isOutputImpedance.value) {
    const base = outputImpedanceConfigPanel.value?.buildParamsJson()
    if (!base) return undefined
    return JSON.stringify({ ...JSON.parse(base), ...outputImpedanceDisplayToParams(currentOutputImpedanceDisplay()) })
  }

  if (isCrosstalk.value) {
    const base = crosstalkConfigPanel.value?.buildParamsJson()
    if (!base) return undefined
    return JSON.stringify({ ...JSON.parse(base), ...crosstalkDisplayToParams(currentCrosstalkDisplay()) })
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
      chartNicholsAutoGain: responseNicholsAutoGain.value,
      chartNicholsTopDb: responseNicholsTopDb.value,
      chartNicholsBottomDb: responseNicholsBottomDb.value,
      chartPoleZeroAutoWindow: responsePoleZeroAutoWindow.value,
      chartPoleZeroXMinHz: responsePoleZeroXMinHz.value,
      chartPoleZeroXMaxHz: responsePoleZeroXMaxHz.value,
      chartPoleZeroYMinHz: responsePoleZeroYMinHz.value,
      chartPoleZeroYMaxHz: responsePoleZeroYMaxHz.value,
      chartInvertLoopSign: responseInvertLoopSign.value,
      chartRestoreDelay: responseRestoreDelay.value,
      chartShowFittedModel: responseShowFittedModel.value,
      chartHideOutOfBandRoots: responseHideOutOfBandRoots.value,
      chartSurfaceScale: responseSurfaceScale.value,
      chartSurfaceMode: responseSurfaceMode.value,
      chartSurfaceTopDb: responseSurfaceTopDb.value,
      chartSurfaceBottomDb: responseSurfaceBottomDb.value,
      chartSurfacePalette: responseSurfacePalette.value,
      chartSurfaceContours: responseSurfaceContours.value,
      chartFitAutoOrder: responseFitAutoOrder.value,
      chartFitPoles: responseFitOrder.value,
      chartFitZeros: responseFitZeroOrder.value,
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
      chartDelayView: responseDelayView.value,
      chartDelayTopUs: responseDelayTopUs.value,
      chartDelayBottomUs: responseDelayBottomUs.value,
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
    chartShowThdN: sweepShowThdN.value,
    chartShowSnr: sweepShowSnr.value,
    chartShowLeft: sweepShowLeft.value,
    chartShowRight: sweepShowRight.value,
    chartThickLines: sweepThickLines.value,
    chartShowDataPoints: sweepShowDataPoints.value,
    chartYMaxPercent: sweepYMaxPercent.value,
    chartYMinPercent: sweepYMinPercent.value,
    chartYTopDb: sweepYTopDb.value,
    chartYBottomDb: sweepYBottomDb.value,
    chartAutofitOnDone: sweepAutofitOnDone.value,
    chartFitPreviewSpectrum: sweepFitPreviewSpectrum.value,
    chartFitPreviewScope: sweepFitPreviewScope.value,
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

// ---------------------------------------------------------------------------
// The Project menu
// ---------------------------------------------------------------------------
function onProjectAction(action: ProjectMenuAction) {
  switch (action) {
    case 'new':
    case 'saveAs':
    case 'properties':
      projectDialogMode.value = action
      projectDialogOpen.value = true
      break
    case 'open':
      openProjectDialogOpen.value = true
      break
    case 'save':
      void saveCurrentProject()
      break
    case 'report':
      openProjectReport()
      break
    case 'export':
      projectExportOpen.value = true
      break
    case 'import':
      projectImportOpen.value = true
      break
    case 'delete':
      void withErrorReporting(async () => {
        await projectSession.remove()
      })
      break
    case 'close':
      void withErrorReporting(async () => {
        await projectSession.close()
      })
      break
  }
}

/** The New…/Save As…/Properties… form, confirmed. The dialog stays open on failure so the input is not lost. */
function confirmProjectDialog(form: ProjectForm) {
  return withErrorReporting(async () => {
    const notes = form.notes.length > 0 ? form.notes : null
    let done = false
    switch (projectDialogMode.value) {
      case 'new':
        done = await projectSession.create({ name: form.name, notes, path: form.location })
        break
      case 'saveAs':
        done = await projectSession.saveAs(
          { name: form.name, notes, path: form.location, includeResults: form.includeResults },
          async () => {
            if (currentProjectId.value !== null) await saveProject(currentProjectId.value, buildStagedConfig(), buildTypeParamsJson())
          },
        )
        break
      case 'properties':
        done = await projectSession.updateProperties({ name: form.name, notes })
        break
    }
    if (done) projectDialogOpen.value = false
  })
}

function openProjectFile(path: string) {
  return withErrorReporting(async () => {
    if (await projectSession.open(path)) openProjectDialogOpen.value = false
  })
}

// Ctrl+S is Project ▸ Save. Ctrl+N and Ctrl+O are left to the browser: it owns them and a page
// can only sometimes take them over, which is worse than never.
function onWindowKeydown(event: KeyboardEvent) {
  if ((event.ctrlKey || event.metaKey) && !event.shiftKey && !event.altKey && event.key.toLowerCase() === 's') {
    event.preventDefault()
    if (currentProjectId.value !== null && hasAnyDirtyInProject.value) void saveCurrentProject()
  }
}
onMounted(() => window.addEventListener('keydown', onWindowKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', onWindowKeydown))

// The programmable load's poll, started once from here and stopped with the app. It reads the status
// once and then only keeps polling if a load is actually selected, so a bench without one pays a
// single request for the whole session.
let stopLoadPolling: (() => void) | undefined
onMounted(() => {
  stopLoadPolling = startLoadPolling()
})
onBeforeUnmount(() => stopLoadPolling?.())
</script>

<template>
  <div class="flex h-screen flex-col bg-[var(--surface-ground)] text-neutral-900 dark:text-neutral-100">
    <!-- First in the tree and `fixed`, so it is painted over everything without taking part in the
         layout underneath -- see SplashScreen.vue. It unmounts itself through @done once its own
         fade has finished, rather than on the hold's timer, or it would disappear instead of
         fading. -->
    <SplashScreen v-if="splashShowing" @done="splashShowing = false" />

    <UpdateBanner />
    <header class="flex items-center gap-3 border-b border-neutral-200 bg-[var(--surface-panel)] px-4 py-2 dark:border-neutral-800">
      <!-- The mark and the name are one unit, so they sit in their own flex box: the header's own
           gap-3 is the spacing between the *controls* on the bar, and applied between these two it
           reads as an icon that belongs to nothing in particular.

           `glyph` and not `tile`: the tile's own ground (#1f2937..#0b1220) is within a few counts of
           the dark theme's panel, so at 20 px it reads as a muddy box around a comb that the tile's
           own inset has already shrunk — and the glyph, whose structure is `currentColor`, picks up
           the header's ink in both themes instead. The tile belongs where the mark stands alone.

           `aria-hidden` because the name is right beside it: it overrides the component's own
           role/aria-label, which exist for the about dialog, where the mark is on its own. -->
      <div class="flex items-center gap-2">
        <TonebenchLogo :size="20" variant="glyph" class="shrink-0" aria-hidden="true" />
        <span class="text-sm font-semibold">Tonebench</span>
      </div>
      <ProjectMenu
        :project-name="currentProject?.name ?? null"
        :dirty="hasAnyDirtyInProject"
        :busy="projectSession.busy.value"
        @action="onProjectAction"
      />
      <span class="flex-1"></span>
      <!-- The whole manual, from its index. Each measurement screen has its own "?" further down
           that jumps straight to that measurement's chapter. -->
      <button
        type="button"
        class="flex h-10 w-10 items-center justify-center rounded-lg hover:bg-neutral-100 dark:hover:bg-neutral-800"
        aria-label="Manual"
        title="Manual"
        @click="openManual()"
      >
        <HeaderIcon name="manual" />
      </button>
      <!-- About: the version, the credits and the open-source libraries. Beside the manual rather
           than inside Configuration — it answers questions rather than changing anything. -->
      <button
        type="button"
        class="flex h-10 w-10 items-center justify-center rounded-lg hover:bg-neutral-100 dark:hover:bg-neutral-800"
        aria-label="About"
        title="About"
        @click="aboutDialogOpen = true"
      >
        <HeaderIcon name="about" />
      </button>
      <!-- Report an issue: six questions and a zip holding the answers, the chart on screen, the
           log and every calibration of this bench. Here rather than inside Configuration, and
           beside About for the same reason it is — it is reached when something is wrong, which is
           not a moment to go looking through settings pages. -->
      <button
        type="button"
        class="flex h-10 w-10 items-center justify-center rounded-lg hover:bg-neutral-100 dark:hover:bg-neutral-800"
        aria-label="Report an issue"
        title="Report an issue — builds one zip to send, holding your description, this screen, the log and your settings"
        @click="issueReportDialogOpen = true"
      >
        <HeaderIcon name="bug" />
      </button>
      <button
        type="button"
        class="flex h-10 w-10 items-center justify-center rounded-lg hover:bg-neutral-100 dark:hover:bg-neutral-800"
        aria-label="Configuration"
        title="Configuration"
        @click="configDialogOpen = true"
      >
        <HeaderIcon name="config" />
      </button>
    </header>

    <!-- No project open is the welcome screen; the header (Project menu, manual, configuration)
         and the status bar stay, so the device can be connected and a database problem read
         without one. `sessionLoaded` keeps a reload of an open project from flashing the welcome
         screen while GET /api/project is in flight. -->
    <WelcomeScreen
      v-if="sessionLoaded && currentProject === null"
      @new="onProjectAction('new')"
      @open="onProjectAction('open')"
      @open-recent="openProjectFile"
    />

    <div v-else-if="currentProject !== null" class="flex flex-1 overflow-hidden">
      <MeasurementSidebar />

      <!-- `[&>*]:shrink-0` is load-bearing: this is a flex column that scrolls, and in a flex
           column the children shrink before the container scrolls. A child whose own automatic
           minimum size is its content survives that, but `CollapsibleSection`'s root carries
           `overflow-hidden` (its header has to clip), which resolves that minimum to *zero* — so
           History details, the last child on the FFT screen, was crushed to a 2px sliver at the
           bottom of the page instead of its 86px. It read as the panel having gone missing
           (Joost, 2026-10-14), and it appeared the moment the Notes box moved into the chart
           column and made that column a hundred pixels taller. On the element rather than on each
           child, so a sibling added later cannot forget it — the same rule the scrolling dialog
           bodies already carry one `shrink-0` at a time. -->
      <main class="flex flex-1 flex-col gap-4 overflow-y-auto p-4 [&>*]:shrink-0">
        <!-- The trash takes over <main> and leaves the sidebar alone, unlike WelcomeScreen, which
             replaces both. It deliberately does *not* change the selection: no selectType, so none
             of the live-state machinery below runs, and Close lands back on the same measurement
             with its curves and staged settings intact. -->
        <TrashView
          v-if="trashOpen"
          @close="closeTrash"
          @restored="onTrashRestored"
          @purged="onTrashPurged"
        />

        <template v-else-if="currentTypeId !== null">
          <!-- THE MEASUREMENT BAR: the name of what is selected, everything that can be done to it,
               and the help button — the one row every screen shares. A bar rather than a row of
               loose buttons on the page ground (Joost, 2026-09-30): it bleeds out to the edges of
               <main> (the negative margins cancel that element's own p-4, which is then re-applied
               as px-4), carries the panel surface with a rule under it, and sticks to the top while
               a long screen scrolls, so Save never scrolls away from the settings being changed.
               `-top-4` and not `top-0`: a sticky element parks against its scroll container's
               *padding* box, which would leave the bar 16 px down with content sliding through the
               gap above it. The buttons inside are flat — the bar is the frame now, and sixteen
               outlined boxes inside another box read as a fence. z-10 sits under the sidebar's
               floating menu (z-30), an expanded chart (z-40) and the dialogs (z-50). -->
          <div
            class="panel-surface sticky -top-4 z-10 -mx-4 -mt-4 flex min-h-[52px] flex-wrap items-center gap-0.5 border-b border-neutral-200 px-4 py-1.5 text-sm dark:border-neutral-800"
          >
            <span class="mr-2 min-w-0 max-w-xs truncate text-base font-semibold" :title="currentType?.name">{{ currentType?.name }}</span>
            <span
              v-if="isCurrentTypeDirty"
              class="h-2 w-2 shrink-0 rounded-full bg-red-500"
              title="Unsaved changes"
            ></span>
            <!-- Separates what is selected from what can be done to it. Purely a rule: `aria-hidden`
                 so it is not read out as a control. -->
            <span class="mx-2 h-5 w-px shrink-0 bg-neutral-200 dark:bg-neutral-800" aria-hidden="true"></span>
            <button
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="!isCurrentTypeDirty"
              title="Save the current settings into this measurement."
              @click="saveCurrentType"
            >
              <ToolbarIcon name="save" />
              Save
            </button>
            <button
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 dark:text-neutral-100 dark:hover:bg-neutral-800"
              title="Save the current settings as a new measurement in this project; this one reverts to its own last-saved settings."
              @click="saveTypeAsOpen = true"
            >
              <ToolbarIcon name="save-as" tone="blue" />
              Save as…
            </button>
            <button
              v-if="!isSweepKind && !isFrequencyResponse && !isImpulseResponse && !isImpedance && !isCrosstalk && !isOutputImpedance"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="status?.acquiring"
              title="Saves the currently displayed (stopped) spectrum as a new measurement result — stop acquisition first."
              @click="openSaveDialog"
            >
              <ToolbarIcon name="save-result" tone="green" />
              Save measurement result
            </button>
            <button
              v-if="!isSweepKind && !isFrequencyResponse && !isImpulseResponse && !isImpedance && !isCrosstalk && !isOutputImpedance"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="status?.acquiring"
              title="Clears the currently displayed spectrum, oscilloscope trace and meters — stop acquisition first. Does not delete anything saved to History."
              @click="clearMeasurement"
            >
              <ToolbarIcon name="clear" tone="red" />
              Clear
            </button>
            <button
              v-if="isSweepKind"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="sweepRunning || sweepPoints.length === 0"
              title="Saves the sweep on screen as a new measurement result — one stored point per frequency. A cancelled sweep can be saved too."
              @click="openSaveDialog"
            >
              <ToolbarIcon name="save-result" tone="green" />
              Save sweep result
            </button>
            <button
              v-if="isFrequencyResponse || isCrosstalk"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="responseRunning || responseTraces.length === 0"
              :title="isCrosstalk ? 'Saves the crosstalk curves on screen as a new measurement result.' : 'Saves the curves on screen as a new measurement result, with their bandwidth readings.'"
              @click="openSaveDialog"
            >
              <ToolbarIcon name="save-result" tone="green" />
              {{ isCrosstalk ? 'Save crosstalk result' : 'Save response result' }}
            </button>
            <button
              v-if="isImpedance"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="impedanceRunning || impedanceTraces.length === 0"
              title="Saves the impedance curve on screen as a new measurement result, together with the fixture calibration that produced it."
              @click="openSaveDialog"
            >
              <ToolbarIcon name="save-result" tone="green" />
              Save impedance result
            </button>
            <button
              v-if="isImpedance"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="impedanceRunning || impedanceTraces.length === 0"
              title="Clears the curve on screen — cancel a running measurement first. Does not delete anything saved to History, and leaves the settings and the calibration alone."
              @click="clearImpedance"
            >
              <ToolbarIcon name="clear" tone="red" />
              Clear
            </button>
            <button
              v-if="isOutputImpedance"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="outputImpedanceRunning || !outputImpedanceTrace"
              title="Saves the output-impedance curve on screen as a new measurement result, with the two gain passes it was derived from."
              @click="openSaveDialog"
            >
              <ToolbarIcon name="save-result" tone="green" />
              Save output impedance result
            </button>
            <button
              v-if="isOutputImpedance"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="outputImpedanceRunning || !outputImpedanceTrace"
              title="Clears the curve on screen — cancel a running measurement first. Does not delete anything saved to History, and leaves the settings alone."
              @click="clearOutputImpedance"
            >
              <ToolbarIcon name="clear" tone="red" />
              Clear
            </button>
            <button
              v-if="isImpulseResponse"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="impulseRunning || impulseTraces.length === 0"
              title="Saves the impulse response on screen as a new measurement result, with its gated curves and harmonic distortion."
              @click="openSaveDialog"
            >
              <ToolbarIcon name="save-result" tone="green" />
              Save impulse result
            </button>
            <button
              v-if="isImpulseResponse"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="impulseRunning || impulseTraces.length === 0"
              title="Clears what is on screen — cancel a running measurement first. Does not delete anything saved to History, and leaves the settings alone."
              @click="clearImpulseResponse"
            >
              <ToolbarIcon name="clear" tone="red" />
              Clear
            </button>
            <button
              v-if="isFrequencyResponse || isCrosstalk"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="responseRunning || responseTraces.length === 0"
              title="Clears the curves on screen — cancel a running measurement first. Does not delete anything saved to History, and leaves the settings alone."
              @click="clearFrequencyResponse"
            >
              <ToolbarIcon name="clear" tone="red" />
              Clear
            </button>
            <!-- The sweep screen's Clear, in the same slot and the same shape as the FFT screen's
                 above. Disabled while a sweep runs for the same reason CANCEL exists separately:
                 stopping a sweep and discarding its points are different intentions, and a sweep
                 that took two minutes should not be thrown away by one stray click. -->
            <button
              v-if="isSweepKind"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="sweepRunning || sweepPoints.length === 0"
              title="Clears the sweep on screen and its preview traces — cancel a running sweep first. Does not delete anything saved to History, and leaves the sweep settings alone."
              @click="clearSweep"
            >
              <ToolbarIcon name="clear" tone="red" />
              Clear
            </button>
            <!-- The two exports, on every screen whose chart can produce them (the Screenshot) or
                 on every screen at all (the Report — a kind without a chart builder yet still gets
                 its description and settings). Both read what is on screen, so a running
                 measurement disables them the way Save does. -->
            <button
              v-if="reportHasChart(currentType?.kind)"
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="!screenshotAvailable"
              title="The chart on screen — whichever view or projection is showing — as a PNG in the light colour scheme, at the zoom shown: to the clipboard or a file, at a chosen size, with a title and the date if wanted."
              @click="screenshotDialogOpen = true"
            >
              <ToolbarIcon name="screenshot" tone="violet" />
              Screenshot
            </button>
            <button
              type="button"
              class="inline-flex h-9 items-center gap-1.5 rounded-lg px-2.5 text-[13px] text-neutral-800 hover:bg-neutral-100 disabled:opacity-40 disabled:hover:bg-transparent dark:text-neutral-100 dark:hover:bg-neutral-800"
              :disabled="!currentType || responseRunning || sweepRunning || impedanceRunning || impulseRunning || outputImpedanceRunning"
              title="A PDF report of what is on screen: chart, notes, settings and readings, as chosen. Stored results have their own Report button in History details."
              @click="openReport(null)"
            >
              <ToolbarIcon name="report" tone="amber" />
              Report…
            </button>
            <!-- Shortcut into the manual chapter for whichever kind is selected. It sits in this
                 toolbar rather than in each screen's own template because the toolbar is the one
                 row every screen shares, so the button is in the same place everywhere: the far right
                 of the row, clear of the save buttons. -->
            <button
              type="button"
              class="ml-auto flex h-9 w-9 shrink-0 items-center justify-center rounded-full border border-neutral-200 text-[13px] font-semibold text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:border-neutral-800 dark:text-neutral-400 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
              :aria-label="`Help for ${currentType?.kind ? currentType.name : 'this measurement'}`"
              title="Open this measurement's chapter of the manual."
              @click="openManualForKind(currentType?.kind)"
            >
              ?
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
            <MeasurementLayout>
              <template #rail>
                <div class="flex gap-3">
                  <button
                    type="button"
                    class="btn-run-go"
                    :disabled="impulseRunning || !!impulseConfigPanel?.invalid"
                    :title="impulseConfigPanel?.invalid ?? 'Sweeps a chirp and re-measures the impulse response until you press Stop.'"
                    @click="runImpulseResponse(true)"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    Run
                  </button>
                  <button
                    type="button"
                    class="btn-run-single"
                    :disabled="impulseRunning || !!impulseConfigPanel?.invalid"
                    :title="impulseConfigPanel?.invalid ?? 'Measures the impulse response once and stops.'"
                    @click="runImpulseResponse(false)"
                  >
                    Single
                  </button>
                  <button
                    type="button"
                    class="btn-run-stop"
                    :disabled="!impulseRunning"
                    title="Stops the measurement and silences the generator. The last completed pass stays on screen."
                    @click="cancelImpulseResponse"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                    Stop
                  </button>
                </div>

                <ImpulseResponseConfigPanel
                  ref="impulseConfigPanel"
                  :running="impulseRunning"
                  @capture-reference="loopbackDialogOpen = true"
                />
              </template>

              <template #status>
                <MeasurementStatusBar
                  :tone="impulseTone"
                  :label="impulseStatusHeadline"
                  :done="impulseAveragesTaken"
                  :total="impulseAverageCount"
                  :caption="impulseAverageCount > 0 ? `Record ${Math.min(Math.max(impulseAveragesTaken, 1), impulseAverageCount)} of ${impulseAverageCount}` : impulseStatusDetail"
                  :caption-right="impulseBinHz ? `${impulseBinHz.toFixed(2)} Hz/bin` : ''"
                >
                  <span v-if="Number.isFinite(impulseGeneratorAmpDbv) && impulseTraces.length">
                    gen <span class="font-mono text-neutral-700 dark:text-neutral-200">{{ impulseGeneratorAmpDbv.toFixed(2) }} dBV</span>
                  </span>
                </MeasurementStatusBar>
              </template>

              <template #default>
                <ImpulseResponseChart
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  v-if="impulseView === 'Impulse'"
                  ref="impulseChart"
                  :impulse="impulseTrace"
                  :overlays="visibleImpulseOverlays"
                  :normalised="impulseNormalised"
                  :thick-lines="impulseThickLines"
                  :time-from-ms="impulseTimeFromMs"
                  :time-to-ms="impulseTimeToMs"
                  :y-top-db="impulseYTopDb"
                  :y-bottom-db="impulseYBottomDb"
                />
                <!-- The gated magnitude and phase are the same curves a frequency response produces,
                     so they are drawn by the same chart rather than a second one that would have to
                     be kept in step with it.

                     It takes the history overlays for the same reason: a saved impulse result stores
                     its gated magnitude and phase as response_trace rows, and addOverlay already
                     reads them into responseTraces for exactly this chart. Passing [] here is what
                     made the History checkbox on an impulse type do nothing visible anywhere. The
                     time-domain view above still has none — the impulse itself is not a
                     response_trace row and is never loaded for an overlay. -->
                <FrequencyResponseChart
                  v-else-if="impulseView === 'Response'"
                  ref="impulseResponseChart"
                  run-label="Run"
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  :annotation-view="'Response'"
                  :traces="impulseTraces"
                  :overlays="visibleImpulseResponseOverlays"
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
                  :delay-view="impulseDelayView"
                  :delay-top-us="impulseDelayTopUs"
                  :delay-bottom-us="impulseDelayBottomUs"
                  phase-kind="PerTrace"
                  :axis-titles="true"
                />
                <ImpulseHarmonicsChart
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  v-else-if="impulseView === 'Harmonics'"
                  ref="impulseHarmonicsChart"
                  :harmonics="impulseHarmonics"
                  :overlays="visibleHarmonicOverlays"
                  :thick-lines="impulseThickLines"
                  :show-data-points="impulseShowDataPoints"
                  :freq-from-hz="impulseFreqFromHz"
                  :freq-to-hz="impulseFreqToHz"
                  :y-top-db="impulseYTopDb"
                  :y-bottom-db="impulseYBottomDb"
                />
                <!-- Both decay surfaces, drawn by one chart: they differ in what the depth axis
                     counts and in nothing about how it is drawn. -->
                <DecaySurfaceChart
                  v-else
                  ref="impulseDecayChart"
                  :surface="impulseView === 'Csd' ? impulseCsdSurface : impulseBurstSurface"
                  :mode="impulseDecayMode"
                  :palette="impulseDecayPalette"
                  :colored-curves="impulseDecayColoredCurves"
                  :range-db="impulseDecayRangeDb"
                  :thick-lines="impulseThickLines"
                  :computing="impulseDecayComputing"
                  :error="impulseDecayError"
                  empty-message="Measure an impulse response, and this draws its decay."
                />

                <!-- What 0 dB means here, restated under the chart, because it is not the same
                     thing in the two measurements and the difference decides how the picture is
                     read. -->
                <p
                  v-if="impulseView === 'Csd' && impulseCsdSurface"
                  class="text-[11px] text-neutral-500 dark:text-neutral-400"
                >
                  0 dB is the loudest point of the whole surface, so the front slice still carries the DUT's
                  frequency response. Nothing below ≈{{ impulseCsdSurface.resolutionLimitHz.toFixed(0) }} Hz is
                  resolved by a {{ impulseCsdFftLength }}-point transform.
                </p>
                <p
                  v-else-if="impulseView === 'BurstDecay' && impulseBurstSurface"
                  class="text-[11px] text-neutral-500 dark:text-neutral-400"
                >
                  Every frequency is normalised to its own maximum, so this says how long each one rings and
                  nothing about how loud it is. The first period or two is the analysis opening up, not the DUT.
                  <template v-if="impulseBurstFullDepthHz > 0">
                    Curves stop short at the left because the record does: only above
                    ≈{{ impulseBurstFullDepthHz.toFixed(0) }} Hz does it hold
                    {{ impulseBurstMaxPeriods }} whole periods plus the analysis window, so the lower ones end
                    early. A finer resolution lengthens the record.
                  </template>
                </p>

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

                <MeasurementNotesPanel />
                <MeasurementHistoryPanel @error="reportError" @report="openReport" />
              </template>

              <template #aside>
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
                  v-model:delay-view="impulseDelayView"
                  v-model:delay-top-us="impulseDelayTopUs"
                  v-model:delay-bottom-us="impulseDelayBottomUs"
                  v-model:decay-mode="impulseDecayMode"
                  v-model:decay-palette="impulseDecayPalette"
                  v-model:decay-colored-curves="impulseDecayColoredCurves"
                  v-model:decay-range-db="impulseDecayRangeDb"
                  v-model:csd-fft-length="impulseCsdFftLength"
                  v-model:csd-block-shift-samples="impulseCsdBlockShiftSamples"
                  v-model:csd-block-count="impulseCsdBlockCount"
                  v-model:csd-window-mode="impulseCsdWindowMode"
                  v-model:csd-rise-time-ms="impulseCsdRiseTimeMs"
                  v-model:burst-max-periods="impulseBurstMaxPeriods"
                  v-model:burst-points-per-period="impulseBurstPointsPerPeriod"
                  v-model:burst-resolution="impulseBurstResolution"
                />

                <MeasurementChecklist @error="reportError" />
              </template>
            </MeasurementLayout>

            <LoopbackReferenceDialog
              :open="loopbackDialogOpen"
              :defaults="impulseConfigPanel?.referenceCaptureDefaults() ?? { frequencySpanHz: 96000, resolutionMultiplier: 1, chirpStartHz: 2 }"
              @close="loopbackDialogOpen = false"
              @captured="onLoopbackCaptured"
            />
          </template>

          <template v-else-if="isFrequencyResponse">
            <!-- The one screen with a wide settings rail: its generator block carries sentences
                 ("at 5-10 Hz (bandwidth reference)", the stepped sweep's point and time estimate)
                 that wrap to three lines at the default 16rem. -->
            <MeasurementLayout>
              <template #rail>
                <!-- RUN / SINGLE / STOP, the same trio and the same shape as the FFT screen's, so
                     the button that starts a measurement sits in one place whichever screen you are
                     on. RUN repeats until stopped; SINGLE measures once and stops itself. -->
                <div class="flex gap-3">
                  <button
                    v-if="isChirpResponse"
                    type="button"
                    class="btn-run-go"
                    :disabled="responseRunning || !!responseConfigPanel?.invalid"
                    :title="
                      responseConfigPanel?.invalid ??
                      'Sweeps a chirp across the band and re-measures until you press Stop.'
                    "
                    @click="runFrequencyResponse(true)"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    Run
                  </button>
                  <button
                    v-if="isChirpResponse"
                    type="button"
                    class="btn-run-single"
                    :disabled="responseRunning || !!responseConfigPanel?.invalid"
                    :title="
                      responseConfigPanel?.invalid ??
                      'Measures the frequency response once and stops.'
                    "
                    @click="runFrequencyResponse(false)"
                  >
                    Single
                  </button>
                  <!-- One button rather than the chirp's RUN/SINGLE pair: a stepped run is minutes
                       long, so repeating it until stopped is not a mode anyone wants. Same shape and
                       same slot as the sweep screen's RUN SWEEP. -->
                  <button
                    v-if="isSteppedResponse"
                    type="button"
                    class="btn-run-go"
                    :disabled="responseRunning || !!steppedResponseConfigPanel?.invalid"
                    :title="
                      steppedResponseConfigPanel?.invalid ??
                      'Steps a tone across the band, measuring level and phase at each point.'
                    "
                    @click="steppedResponseConfigPanel?.run()"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    Run sweep
                  </button>
                  <button
                    type="button"
                    class="btn-run-stop"
                    :disabled="!responseRunning"
                    title="Stops the measurement and silences the generator. The curves from the last completed pass stay on screen."
                    @click="cancelFrequencyResponse"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                    Stop
                  </button>
                </div>

                <FrequencyResponseConfigPanel
                  v-if="isChirpResponse"
                  ref="responseConfigPanel"
                  :running="responseRunning"
                  :min-stimulus-frequency-hz="ranges?.minStimulusFrequencyHz ?? 0"
                  :sample-rates-hz="ranges?.sampleRatesHz"
                />
                <SteppedResponseConfigPanel
                  v-else
                  ref="steppedResponseConfigPanel"
                  :running="responseRunning"
                  @run="runSteppedResponse"
                />

                <!-- The spectrum and waveform behind the point being measured (while a sweep runs)
                     or the pinned one (once it is pinned) — the sweep screen's pair, fed this
                     screen's points. Only the stepped sine has points to look inside: a chirp
                     excites the whole band at once, so there is no "the capture at 1 kHz" to show.
                     Hidden entirely until something has been measured, so Clear makes them
                     disappear rather than go blank. -->
                <template v-if="isSteppedResponse && responsePreviews.length > 0">
                  <SweepFftPreviewChart
                    :point-left="responseSelectedLeft"
                    :point-right="responseSelectedRight"
                    :fit-range="responsePreviewFitRange"
                  />
                  <SweepScopePreviewChart
                    :point-left="responseSelectedLeft"
                    :point-right="responseSelectedRight"
                    :fit-range="responseScopeFitRange"
                  />
                </template>

                <!-- One more pair per checked history run, underneath the live one and fed that
                     run's own points at the same pinned frequency — the sweep screen's arrangement. -->
                <template v-for="overlay in (isSteppedResponse ? visibleResponsePreviewOverlays : [])" :key="overlay.id">
                  <SweepFftPreviewChart
                    :label="overlay.detail.name"
                    :point-left="selectedResponsePreviewForOverlay(overlay, 0)"
                    :point-right="selectedResponsePreviewForOverlay(overlay, 1)"
                    :fit-range="responsePreviewFitRange"
                  />
                  <SweepScopePreviewChart
                    :label="overlay.detail.name"
                    :point-left="selectedResponsePreviewForOverlay(overlay, 0)"
                    :point-right="selectedResponsePreviewForOverlay(overlay, 1)"
                    :fit-range="responseScopeFitRange"
                  />
                </template>

                <!-- Margins belong to the contour views; the pole–zero map has no −1 point to measure from. -->
                <ResponseStabilityPanel
                  v-if="(activeResponseProjection === 'Nyquist' || activeResponseProjection === 'Nichols') && responsePolarChart"
                  :margins="responsePolarChart.margins"
                  :radius-mode="responseNyquistRadius"
                  :projection="activeResponseProjection"
                  v-model:invert-sign="responseInvertLoopSign"
                />

              </template>

              <template #status>
                <!-- The status bar, `h-11` like the RUN row across the gutter so the settings panel under
                     the one and the chart card under the other start at the same height. It carries the
                     caption row of the mockup: what is being counted on the left, what it is being
                     measured at on the right, and the track under both. -->
                <MeasurementStatusBar
                  :tone="responseTone"
                  :label="responseStatusHeadline"
                  :done="responseDone"
                  :total="responseTotal"
                  :caption="responseCaption"
                  :caption-right="responseCaptionRight"
                >
                  <!-- Number.isFinite, not truthiness: 0 dBV is a perfectly ordinary drive level
                       and a plain v-if hides it, which is exactly what a loopback measurement shows. -->
                  <span v-if="Number.isFinite(responseGeneratorAmpDbv) && responseTraces.length">
                    gen <span class="font-mono text-neutral-700 dark:text-neutral-200">{{ responseGeneratorAmpDbv.toFixed(2) }} dBV</span>
                  </span>
                  <!-- Only shown when the right input was the reference: that is the one case
                       where the recovered rotation is the DUT's delay rather than the record's
                       arbitrary start point in a periodic stimulus. -->
                  <span v-if="responseLatencySamples !== null">
                    DUT delay
                    <span class="font-mono text-neutral-700 dark:text-neutral-200">{{ responseLatencySamples.toFixed(1) }}</span>
                    samples
                  </span>
                </MeasurementStatusBar>
              </template>

              <template #default>
                <p v-if="responseError" class="text-xs text-red-600 dark:text-red-400">{{ responseError }}</p>

                <!-- show-coherence is forced off for a stepped run whatever the stored preference
                     says: its coherence is 1.0 by construction, so drawing it would be a flat line
                     claiming a measurement nobody made. -->
                <FrequencyResponseChart
                  v-if="activeResponseProjection === 'Bode'"
                  ref="responseChart"
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  :annotation-view="'Bode'"
                  :run-label="isSteppedResponse ? 'Run sweep' : 'Run'"
                  :traces="responseTraces"
                  :overlays="visibleResponseOverlays"
                  :view="responseView"
                  :show-left="responseShowLeft"
                  :show-right="!responseRightChannelIsReference && responseShowRight"
                  :show-phase="responsePhaseAvailable && responseShowPhase"
                  :phase-kind="effectiveResponsePhaseKind"
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
                  :delay-view="responsePhaseAvailable ? responseDelayView : 'None'"
                  :delay-top-us="responseDelayTopUs"
                  :delay-bottom-us="responseDelayBottomUs"
                  :restore-delay="responseRestoreDelay"
                  :latency-samples="responseLatencySamples"
                  :sample-rate-hz="responseSampleRateHz"
                  :axis-titles="true"
                  :pinned-x="pinnedResponseHz"
                  @cursor-pin="onResponseCursorPin"
                />
                <ResponseSPlaneChart
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  v-else-if="activeResponseProjection === 'SPlane'"
                  ref="responseSPlaneChart"
                  :run-label="isSteppedResponse ? 'Run sweep' : 'Run'"
                  :traces="responseTraces"
                  :overlays="visibleResponseOverlays"
                  :fit="responsePoleZeroPanel?.fit ?? null"
                  :mode="responseSurfaceMode"
                  :thick-lines="responseThickLines"
                  :scale="responseSurfaceScale"
                  :surface-top-db="responseSurfaceTopDb"
                  :surface-bottom-db="responseSurfaceBottomDb"
                  :palette="responseSurfacePalette"
                  :show-contours="responseSurfaceContours"
                  :hide-out-of-band-roots="responseHideOutOfBandRoots"
                  :invert-sign="responseInvertLoopSign"
                  :restore-delay="responseRestoreDelay"
                  :latency-samples="responseLatencySamples"
                  :sample-rate-hz="responseSampleRateHz"
                  :phase-kind="effectiveResponsePhaseKind"
                  :x-from-hz="responseXFromHz"
                  :x-to-hz="responseXToHz"
                />
                <ResponsePolarChart
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  v-else
                  ref="responsePolarChart"
                  :run-label="isSteppedResponse ? 'Run sweep' : 'Run'"
                  :traces="responseTraces"
                  :overlays="visibleResponseOverlays"
                  :projection="activeResponseProjection"
                  :scale="responseSurfaceScale"
                  :radius-mode="responseNyquistRadius"
                  :radius-floor-db="responseNyquistRadiusFloorDb"
                  :nichols-auto-gain="responseNicholsAutoGain"
                  :nichols-top-db="responseNicholsTopDb"
                  :nichols-bottom-db="responseNicholsBottomDb"
                  :pole-zero-auto-window="responsePoleZeroAutoWindow"
                  :pole-zero-x-min-hz="responsePoleZeroXMinHz"
                  :pole-zero-x-max-hz="responsePoleZeroXMaxHz"
                  :pole-zero-y-min-hz="responsePoleZeroYMinHz"
                  :pole-zero-y-max-hz="responsePoleZeroYMaxHz"
                  :invert-sign="responseInvertLoopSign"
                  :restore-delay="responseRestoreDelay"
                  :latency-samples="responseLatencySamples"
                  :sample-rate-hz="responseSampleRateHz"
                  :phase-kind="effectiveResponsePhaseKind"
                  :x-from-hz="responseXFromHz"
                  :x-to-hz="responseXToHz"
                  :thick-lines="responseThickLines"
                  :show-data-points="responseShowDataPoints"
                  :fit="responsePoleZeroPanel?.fit ?? null"
                  :show-fitted-model="responseShowFittedModel"
                  :hide-out-of-band-roots="responseHideOutOfBandRoots"
                />

                <!-- Measurements, then the roots, then the formula — all in the chart column, where
                     a table of readings and a factored H(s) have the width they need. Joost's request,
                     2026-09-27; the rail keeps only what is genuinely a display choice. -->
                <ResponseMeasurementsPanel
                  v-model:show-one-db="responseShowOneDb"
                  v-model:show-three-db="responseShowThreeDb"
                  :traces="responseTraces"
                  :view="responseView"
                  :show-left="responseShowLeft"
                  :phase-kind="effectiveResponsePhaseKind"
                  :stepped="!isChirpResponse"
                />
                <ResponsePoleZeroPanel
                  v-if="activeResponseProjection !== 'Bode' && responseComplexChart"
                  ref="responsePoleZeroPanel"
                  :curve="responseComplexChart.curve"
                  :restore-delay="responseRestoreDelay"
                  :latency-samples="responseLatencySamples"
                  v-model:order="responseFitOrder"
                  v-model:zero-order="responseFitZeroOrder"
                  v-model:auto-order="responseFitAutoOrder"
                />
                <!-- In the chart column rather than the rail: the factored formula of a fifth-order
                     fit is far wider than a 16rem rail, and wrapping it made it unreadable.
                     Joost's request, 2026-09-27 — provisional, may move back. -->
                <ResponseFormulaPanel
                  v-if="activeResponseProjection !== 'Bode' && responseComplexChart"
                  :curve="responseComplexChart.curve"
                  :order="responseFitOrder"
                  :zero-order="responseFitZeroOrder"
                  :auto-order="responseFitAutoOrder"
                />

                <MeasurementNotesPanel />
                <MeasurementHistoryPanel @error="reportError" @report="openReport" />
              </template>

              <template #aside>
                <FrequencyResponseDisplayPanel
                  :allow-coherence="isChirpResponse"
                  v-model:view="responseView"
                  v-model:projection="responseProjection"
                  v-model:nyquist-radius="responseNyquistRadius"
                  v-model:nyquist-radius-floor-db="responseNyquistRadiusFloorDb"
                  v-model:nichols-auto-gain="responseNicholsAutoGain"
                  v-model:nichols-top-db="responseNicholsTopDb"
                  v-model:nichols-bottom-db="responseNicholsBottomDb"
                  v-model:pole-zero-auto-window="responsePoleZeroAutoWindow"
                  v-model:pole-zero-x-min-hz="responsePoleZeroXMinHz"
                  v-model:pole-zero-x-max-hz="responsePoleZeroXMaxHz"
                  v-model:pole-zero-y-min-hz="responsePoleZeroYMinHz"
                  v-model:pole-zero-y-max-hz="responsePoleZeroYMaxHz"
                  :auto-window="responsePolarChart?.autoWindow ?? null"
                  v-model:show-fitted-model="responseShowFittedModel"
                  v-model:hide-out-of-band-roots="responseHideOutOfBandRoots"
                  v-model:surface-scale="responseSurfaceScale"
                  v-model:surface-mode="responseSurfaceMode"
                  v-model:surface-top-db="responseSurfaceTopDb"
                  v-model:surface-bottom-db="responseSurfaceBottomDb"
                  v-model:surface-palette="responseSurfacePalette"
                  v-model:surface-contours="responseSurfaceContours"
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
                  v-model:delay-view="responseDelayView"
                  v-model:delay-top-us="responseDelayTopUs"
                  v-model:delay-bottom-us="responseDelayBottomUs"
                  v-model:autofit-on-done="responseAutofitOnDone"
                />

                <MeasurementChecklist @error="reportError" />
              </template>
            </MeasurementLayout>
          </template>

          <template v-else-if="isCrosstalk">
            <MeasurementLayout>
              <template #rail>
                <!-- RUN SWEEP / STOP — the stepped-sine response's pair, since this is the same kind
                     of run: minutes long, single-shot. -->
                <div class="flex gap-3">
                  <button
                    type="button"
                    class="btn-run-go"
                    :disabled="responseRunning || !!crosstalkConfigPanel?.invalid"
                    :title="
                      crosstalkConfigPanel?.invalid ??
                      'Drives one channel with a tone stepped across the band and reads what leaks into the other.'
                    "
                    @click="crosstalkConfigPanel?.run()"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    Run sweep
                  </button>
                  <button
                    type="button"
                    class="btn-run-stop"
                    :disabled="!responseRunning"
                    title="Stops the measurement and silences the generator. Every point measured so far stays on screen."
                    @click="cancelFrequencyResponse"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                    Stop
                  </button>
                </div>

                <CrosstalkConfigPanel
                  ref="crosstalkConfigPanel"
                  :running="responseRunning"
                  @run="runCrosstalk"
                />

                <CrosstalkMeasurementsPanel
                  :traces="responseTraces"
                  :generator-amp-dbv="responseGeneratorAmpDbv"
                />
              </template>

              <template #status>
                <MeasurementStatusBar
                  :tone="responseTone"
                  :label="responseStatusHeadline"
                  :done="responseDone"
                  :total="responseTotal"
                  :caption="responseCaption"
                  :caption-right="responseCaptionRight"
                >
                  <span v-if="Number.isFinite(responseGeneratorAmpDbv) && responseTraces.length">
                    gen <span class="font-mono text-neutral-700 dark:text-neutral-200">{{ responseGeneratorAmpDbv.toFixed(2) }} dBV</span>
                  </span>
                </MeasurementStatusBar>
              </template>

              <template #default>
                <p v-if="responseError" class="text-xs text-red-600 dark:text-red-400">{{ responseError }}</p>

                <!-- The response chart in its Crosstalk view: only the crosstalk traces, no phase,
                     no coherence, no bandwidth markers. The dBV window and the phase window are
                     handed constants because that view never reads them. -->
                <FrequencyResponseChart
                  ref="responseChart"
                  run-label="Run sweep"
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  :annotation-view="'Crosstalk'"
                  :traces="responseTraces"
                  :overlays="visibleCrosstalkOverlays"
                  view="Crosstalk"
                  :show-left="true"
                  :show-right="true"
                  :show-phase="false"
                  phase-kind="None"
                  :show-coherence="false"
                  :show-one-db="false"
                  :show-three-db="false"
                  :thick-lines="crosstalkThickLines"
                  :show-data-points="crosstalkShowDataPoints"
                  :y-top-db="crosstalkYTopDb"
                  :y-bottom-db="crosstalkYBottomDb"
                  :y-top-dbv="20"
                  :y-bottom-dbv="-140"
                  :x-from-hz="crosstalkXFromHz"
                  :x-to-hz="crosstalkXToHz"
                  :phase-top-deg="180"
                  :phase-bottom-deg="-180"
                  :axis-titles="true"
                  :pinned-x="pinnedResponseHz"
                  @cursor-pin="onResponseCursorPin"
                />

                <MeasurementNotesPanel />
                <MeasurementHistoryPanel @error="reportError" @report="openReport" />
              </template>

              <template #aside>
                <CrosstalkDisplayPanel
                  v-model:y-top-db="crosstalkYTopDb"
                  v-model:y-bottom-db="crosstalkYBottomDb"
                  v-model:x-from-hz="crosstalkXFromHz"
                  v-model:x-to-hz="crosstalkXToHz"
                  v-model:thick-lines="crosstalkThickLines"
                  v-model:show-data-points="crosstalkShowDataPoints"
                  v-model:autofit-on-done="crosstalkAutofitOnDone"
                />

                <MeasurementChecklist @error="reportError" />
              </template>
            </MeasurementLayout>
          </template>

          <template v-else-if="isOutputImpedance">
            <MeasurementLayout>
              <template #rail>
                <!-- RUN SWEEP / CONTINUE / STOP. CONTINUE exists only while the run is paused for the
                     load change, and takes RUN SWEEP's slot then — one green button at a time. -->
                <div class="flex gap-3">
                  <button
                    v-if="!outputImpedanceWaiting"
                    type="button"
                    class="btn-run-go"
                    :disabled="outputImpedanceRunning || !!outputImpedanceConfigPanel?.invalid"
                    :title="
                      outputImpedanceConfigPanel?.invalid ??
                      'Measures the gain with the load connected, pauses for you to remove it, then measures again.'
                    "
                    @click="outputImpedanceConfigPanel?.run()"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    Run sweep
                  </button>
                  <button
                    v-else
                    type="button"
                    class="btn-run-go animate-pulse"
                    title="The first pass is done and the generator is silent. Change the load — leave the amplifier on — then press to measure the second pass."
                    @click="continueOutputImpedance"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M5 4l10 8-10 8V4zM17 4h2v16h-2z" /></svg>
                    Continue
                  </button>
                  <button
                    type="button"
                    class="btn-run-stop"
                    :disabled="!outputImpedanceRunning"
                    title="Stops the measurement and silences the generator. A run stopped before the second pass has no Zout to keep; one stopped during it keeps the pairs measured so far."
                    @click="cancelOutputImpedance"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                    Stop
                  </button>
                </div>

                <OutputImpedanceConfigPanel
                  ref="outputImpedanceConfigPanel"
                  :running="outputImpedanceRunning"
                  @run="runOutputImpedance"
                />

                <OutputImpedanceReadoutPanel
                  :trace="outputImpedanceTrace"
                  :nominal-ohms="outputImpedanceNominalOhms"
                  :load-ohms="outputImpedanceLoadOhms"
                  :second-load-ohms="outputImpedanceSecondLoadOhms"
                  :right-channel-is-reference="outputImpedanceReferenced"
                  :generator-amp-dbv="outputImpedanceGeneratorAmpDbv"
                  :input-range-dbv="outputImpedanceInputRangeDbv"
                  :first-gain-at1k-db="outputImpedanceFirstGainAt1kDb"
                  :second-gain-at1k-db="outputImpedanceSecondGainAt1kDb"
                  :warning="outputImpedanceWarning"
                />
              </template>

              <template #status>
                <MeasurementStatusBar
                  :tone="outputImpedanceTone"
                  :label="outputImpedanceStatusHeadline"
                  :done="outputImpedancePointsMeasured"
                  :total="outputImpedancePointCount"
                >
                  <span v-if="outputImpedancePointCount > 0" class="font-mono text-neutral-700 dark:text-neutral-200">
                    {{ Math.min(outputImpedancePointsMeasured, outputImpedancePointCount) }} /
                    {{ outputImpedancePointCount }}
                  </span>
                  <span v-if="outputImpedanceStatusDetail">{{ outputImpedanceStatusDetail }}</span>
                  <span v-if="Number.isFinite(outputImpedanceGeneratorAmpDbv) && outputImpedanceTrace">
                    gen
                    <span class="font-mono text-neutral-700 dark:text-neutral-200">{{ outputImpedanceGeneratorAmpDbv.toFixed(2) }} dBV</span>
                  </span>
                </MeasurementStatusBar>
              </template>

              <template #default>
                <p v-if="outputImpedanceError" class="text-xs text-red-600 dark:text-red-400">{{ outputImpedanceError }}</p>

                <!-- The pause, made impossible to miss: the run is holding the bench and nothing
                     happens until the load has been changed and CONTINUE pressed. -->
                <div
                  v-if="outputImpedanceWaiting"
                  class="rounded-lg border border-amber-400 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-600 dark:bg-amber-950/40 dark:text-amber-200"
                  data-output-impedance-waiting
                >
                  <strong>First pass done.</strong> The generator is silent.
                  <template v-if="outputImpedanceSecondLoadOhms === null">
                    Disconnect the {{ formatOhms(outputImpedanceLoadOhms) }} load from the amplifier's output
                  </template>
                  <template v-else>
                    Replace the {{ formatOhms(outputImpedanceLoadOhms) }} load on the amplifier's output with the
                    {{ formatOhms(outputImpedanceSecondLoadOhms) }} one
                  </template>
                  — leave the amplifier on and everything else as it is — then press <strong>Continue</strong> for the second pass.
                </div>

                <!-- The impedance chart with its damping-factor axis: Zout is an impedance curve in
                     every respect, so the ohms axis, the log scale and the phase axis are its. -->
                <ImpedanceChart
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  annotation-view="Bode"
                  ref="outputImpedanceChart"
                  :traces="outputImpedanceTraces"
                  :overlays="visibleOutputImpedanceOverlays"
                  :show-phase="outputImpedanceRunFacts.rightChannelIsReference && outputImpedanceShowPhase"
                  :show-coherence="false"
                  :show-short-cal="false"
                  :thick-lines="outputImpedanceThickLines"
                  :show-data-points="outputImpedanceShowDataPoints"
                  :z-top-ohms="outputImpedanceZTopOhms"
                  :z-bottom-ohms="outputImpedanceZBottomOhms"
                  :z-scale="outputImpedanceZScale"
                  :x-from-hz="outputImpedanceXFromHz"
                  :x-to-hz="outputImpedanceXToHz"
                  :phase-top-deg="outputImpedancePhaseTopDeg"
                  :phase-bottom-deg="outputImpedancePhaseBottomDeg"
                  :show-damping-factor="outputImpedanceShowDampingFactor"
                  :damping-factor-nominal-ohms="outputImpedanceRunFacts.nominalOhms"
                />

                <MeasurementNotesPanel />
                <MeasurementHistoryPanel @error="reportError" @report="openReport" />
              </template>

              <template #aside>
                <OutputImpedanceDisplayPanel
                  :traces="outputImpedanceTraces"
                  :phase-available="outputImpedanceRunFacts.rightChannelIsReference"
                  v-model:z-top-ohms="outputImpedanceZTopOhms"
                  v-model:z-bottom-ohms="outputImpedanceZBottomOhms"
                  v-model:z-scale="outputImpedanceZScale"
                  v-model:show-damping-factor="outputImpedanceShowDampingFactor"
                  v-model:show-phase="outputImpedanceShowPhase"
                  v-model:x-from-hz="outputImpedanceXFromHz"
                  v-model:x-to-hz="outputImpedanceXToHz"
                  v-model:phase-top-deg="outputImpedancePhaseTopDeg"
                  v-model:phase-bottom-deg="outputImpedancePhaseBottomDeg"
                  v-model:thick-lines="outputImpedanceThickLines"
                  v-model:show-data-points="outputImpedanceShowDataPoints"
                  v-model:autofit-on-done="outputImpedanceAutofitOnDone"
                />

                <MeasurementChecklist @error="reportError" />
              </template>
            </MeasurementLayout>
          </template>

          <template v-else-if="isImpedance">
            <MeasurementLayout>
              <template #rail>
                <!-- RUN / SINGLE / STOP, the same trio and the same slot as every other screen. -->
                <div class="flex gap-3">
                  <button
                    v-if="!impedanceIsStepped"
                    type="button"
                    class="btn-run-go"
                    :disabled="impedanceRunning || !!impedanceConfigPanel?.invalid"
                    :title="
                      impedanceConfigPanel?.invalid ??
                      'Sweeps a chirp through the reference resistor and re-measures until you press Stop.'
                    "
                    @click="runImpedance(true)"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    Run
                  </button>
                  <button
                    type="button"
                    :class="impedanceIsStepped ? 'btn-run-go' : 'btn-run-single'"
                    :disabled="impedanceRunning || !!impedanceConfigPanel?.invalid"
                    :title="impedanceConfigPanel?.invalid ?? 'Measures the impedance once and stops.'"
                    @click="runImpedance(false)"
                  >
                    <svg
                      v-if="impedanceIsStepped"
                      width="18"
                      height="18"
                      viewBox="0 0 24 24"
                      fill="currentColor"
                      aria-hidden="true"
                    >
                      <path d="M7 4l14 8-14 8V4z" />
                    </svg>
                    {{ impedanceIsStepped ? 'Run sweep' : 'Single' }}
                  </button>
                  <button
                    type="button"
                    class="btn-run-stop"
                    :disabled="!impedanceRunning"
                    title="Stops the measurement and silences the generator. The curve from the last completed pass stays on screen."
                    @click="cancelImpedance"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                    Stop
                  </button>
                </div>

                <ImpedanceConfigPanel
                  ref="impedanceConfigPanel"
                  :running="impedanceRunning"
                  @calibrate="calibrateImpedance"
                />

                <ImpedanceReadoutPanel
                  v-model:dut-type="impedanceDutType"
                  v-model:equivalent-hz="impedanceEquivalentHz"
                  :traces="impedanceTraces"
                  :median-ohms="impedanceMedianOhms"
                  :drive-current-amps="impedanceDriveCurrentAmps"
                  :reference-ohms="impedanceReferenceOhms"
                />
              </template>

              <template #status>
                <MeasurementStatusBar
                  :tone="impedanceTone"
                  :label="impedanceStatusHeadline"
                  :done="impedancePointsMeasured ?? 0"
                  :total="impedancePointCount ?? 0"
                >
                  <span v-if="(impedancePointCount ?? 0) > 0" class="font-mono text-neutral-700 dark:text-neutral-200">
                    {{ Math.min(impedancePointsMeasured ?? 0, impedancePointCount ?? 0) }} / {{ impedancePointCount }}
                  </span>
                  <span v-if="impedanceStatusDetail">{{ impedanceStatusDetail }}</span>
                  <!-- Number.isFinite, not truthiness: 0 dBV is an ordinary drive level. -->
                  <span v-if="Number.isFinite(impedanceGeneratorAmpDbv) && impedanceTraces.length">
                    gen <span class="font-mono text-neutral-700 dark:text-neutral-200">{{ impedanceGeneratorAmpDbv.toFixed(2) }} dBV</span>
                  </span>
                  <span v-if="impedanceBinHz">
                    <span class="font-mono text-neutral-700 dark:text-neutral-200">{{ impedanceBinHz.toFixed(2) }}</span> Hz/bin
                  </span>
                </MeasurementStatusBar>
              </template>

              <template #default>
                <p v-if="impedanceError" class="text-xs text-red-600 dark:text-red-400">{{ impedanceError }}</p>
                <!-- Amber, not red: the run succeeded, the curve is drawn — it is the setup that is wrong. -->
                <p v-if="impedanceWarning" class="text-xs text-amber-700 dark:text-amber-400">{{ impedanceWarning }}</p>

                <ImpedanceChart
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  annotation-view="Bode"
                  v-if="impedanceView === 'Bode'"
                  ref="impedanceChart"
                  :traces="impedanceTraces"
                  :overlays="visibleImpedanceOverlays"
                  :show-phase="impedanceShowPhase"
                  :show-coherence="impedanceShowCoherence"
                  :show-short-cal="impedanceShowShortCal"
                  :thick-lines="impedanceThickLines"
                  :show-data-points="impedanceShowDataPoints"
                  :z-top-ohms="impedanceZTopOhms"
                  :z-bottom-ohms="impedanceZBottomOhms"
                  :z-scale="impedanceZScale"
                  :x-from-hz="impedanceXFromHz"
                  :x-to-hz="impedanceXToHz"
                  :phase-top-deg="impedancePhaseTopDeg"
                  :phase-bottom-deg="impedancePhaseBottomDeg"
                />
                <ImpedancePlaneChart
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  v-else
                  ref="impedancePlaneChart"
                  :traces="impedanceTraces"
                  :x-from-hz="impedanceXFromHz"
                  :x-to-hz="impedanceXToHz"
                  :thick-lines="impedanceThickLines"
                />

                <ImpedanceComparisonPanel
                  :traces="impedanceTraces"
                  :overlays="visibleImpedanceOverlays"
                  :dut-type="impedanceDutType"
                  :reference-ohms="impedanceReferenceOhms"
                  :generator-amp-dbv="impedanceTraces.length > 0 && Number.isFinite(impedanceGeneratorAmpDbv) ? impedanceGeneratorAmpDbv : null"
                  :equivalent-hz="impedanceEquivalentHz"
                />

                <MeasurementNotesPanel />
                <MeasurementHistoryPanel @error="reportError" @report="openReport" />
              </template>

              <template #aside>
                <ImpedanceDisplayPanel
                  :traces="impedanceTraces"
                  :running="impedanceRunning"
                  v-model:view="impedanceView"
                  v-model:show-phase="impedanceShowPhase"
                  v-model:show-coherence="impedanceShowCoherence"
                  v-model:show-short-cal="impedanceShowShortCal"
                  v-model:thick-lines="impedanceThickLines"
                  v-model:show-data-points="impedanceShowDataPoints"
                  v-model:z-top-ohms="impedanceZTopOhms"
                  v-model:z-bottom-ohms="impedanceZBottomOhms"
                  v-model:z-scale="impedanceZScale"
                  v-model:x-from-hz="impedanceXFromHz"
                  v-model:x-to-hz="impedanceXToHz"
                  v-model:phase-top-deg="impedancePhaseTopDeg"
                  v-model:phase-bottom-deg="impedancePhaseBottomDeg"
                  v-model:autofit-on-done="impedanceAutofitOnDone"
                />

                <MeasurementChecklist @error="reportError" />
              </template>
            </MeasurementLayout>
          </template>

          <template v-else-if="isSweepKind">
            <MeasurementLayout>
              <template #rail>
                <div class="flex gap-3">
                  <button
                    v-if="!sweepRunning"
                    type="button"
                    class="btn-run-go"
                    :disabled="!!activeSweepPanel?.invalid"
                    :title="
                      activeSweepPanel?.invalid ??
                      (isImdSweep
                        ? 'Plays the two-tone pair at each level of the configured range and records the intermodulation products at each point.'
                        : isAmplitudeSweep
                          ? 'Steps the generator across the configured voltage range at one frequency and records the harmonics at each point.'
                          : 'Steps the generator across the configured frequency range and records the harmonics at each point.')
                    "
                    @click="activeSweepPanel?.run()"
                  >
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                    Run sweep
                  </button>
                  <button
                    v-else
                    type="button"
                    class="btn-run-stop"
                    title="Stops the sweep and silences the generator. The points already measured are kept."
                    @click="cancelSweep"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                    Cancel
                  </button>
                </div>

                <ImdSweepConfigPanel v-if="isImdSweep" ref="imdSweepConfigPanel" :running="sweepRunning" @run="runImdSweep" />
                <AmplitudeSweepConfigPanel
                  v-else-if="isAmplitudeSweep"
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
                  <SweepFftPreviewChart
                    :point-left="sweepSelectedLeft"
                    :point-right="sweepSelectedRight"
                    :fit-range="sweepPreviewFitRange"
                  />
                  <SweepScopePreviewChart
                    :point-left="sweepSelectedLeft"
                    :point-right="sweepSelectedRight"
                    :fit-range="sweepScopeFitRange"
                  />
                </template>

                <!-- One more Spectrum/Oscilloscope pair per history item checked on in the
                     checklist, underneath the live pair above — same components, same left-blue/
                     right-red convention, just fed that result's own selected point. -->
                <template v-for="overlay in visibleSweepOverlays" :key="overlay.id">
                  <SweepFftPreviewChart
                    :label="overlay.detail.name"
                    :point-left="selectedSweepPointForOverlay(overlay, 0)"
                    :point-right="selectedSweepPointForOverlay(overlay, 1)"
                    :fit-range="sweepPreviewFitRange"
                  />
                  <SweepScopePreviewChart
                    :label="overlay.detail.name"
                    :point-left="selectedSweepPointForOverlay(overlay, 0)"
                    :point-right="selectedSweepPointForOverlay(overlay, 1)"
                    :fit-range="sweepScopeFitRange"
                  />
                </template>
              </template>

              <template #status>
                <!-- The status bar, `h-11` like the RUN row across the gutter so the Sweep panel under the one
                     and the chart card under the other start at the same height. -->
                <MeasurementStatusBar
                  :tone="sweepTone"
                  :label="sweepStatusHeadline"
                  :done="sweepIndex"
                  :total="sweepTotal"
                >
                  <span v-if="sweepTotal > 0" class="font-mono text-neutral-700 dark:text-neutral-200">
                    {{ Math.min(sweepIndex, sweepTotal) }} / {{ sweepTotal }}
                  </span>
                  <span v-else-if="sweepPoints.length" class="font-mono text-neutral-700 dark:text-neutral-200">
                    {{ sweepPoints.length }} readings
                  </span>
                  <span v-if="sweepStatusDetail">{{ sweepStatusDetail }}</span>
                  <!-- `v-if` on the value itself hid the readout at exactly 0.00 dBV, which is a level
                       anyone sweeps at; and the IMD runner has no level of its own (its x axis IS the
                       level), so without the kind test this showed the THD runner's leftover figure. -->
                  <span v-if="!isImdSweep && (sweepRunning || sweepPoints.length) && Number.isFinite(sweepGeneratorAmpDbv)">
                    gen <span class="font-mono text-neutral-700 dark:text-neutral-200">{{ sweepGeneratorAmpDbv.toFixed(2) }} dBV</span>
                  </span>
                </MeasurementStatusBar>
              </template>

              <template #default>
                <p v-if="sweepError" class="text-xs text-red-600 dark:text-red-400">{{ sweepError }}</p>
                <p v-else-if="sweepStopReason" class="text-xs text-amber-600 dark:text-amber-400">
                  {{ sweepStopReason }}
                </p>

                <ThdSweepChart
                  v-model:annotations="shownAnnotations"
                  v-model:annotation-armed="annotationArmed"
                  ref="sweepChart"
                  :unit="sweepUnit"
                  :show-noise-floor="sweepShowNoiseFloor"
                  :show-magnitude="sweepShowMagnitude"
                  :show-thd="sweepShowThd"
                  :show-d2="sweepShowD2"
                  :show-d3="sweepShowD3"
                  :show-d4="sweepShowD4"
                  :show-d5="sweepShowD5"
                  :show-d6-plus="sweepShowD6Plus"
                  :show-thd-n="sweepShowThdN"
                  :show-snr="sweepShowSnr"
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
                  :kind="currentType?.kind ?? ''"
                  :overlays="overlays"
                  :selected-left="sweepSelectedLeft"
                  :selected-right="sweepSelectedRight"
                  :pinned-x="pinnedSweepX"
                  :overlay-selections="sweepOverlaySelections"
                  :trace-family="sweepTraceFamily"
                  @cursor-x="onSweepCursorX"
                  @cursor-pin="onSweepCursorPin"
                />

                <MeasurementNotesPanel />
                <MeasurementHistoryPanel @error="reportError" @report="openReport" />
              </template>

              <template #aside>
                <SweepDisplayPanel
                  :trace-family="sweepTraceFamily"
                  v-model:unit="sweepUnit"
                  v-model:show-noise-floor="sweepShowNoiseFloor"
                  v-model:show-magnitude="sweepShowMagnitude"
                  v-model:show-thd="sweepShowThd"
                  v-model:show-d2="sweepShowD2"
                  v-model:show-d3="sweepShowD3"
                  v-model:show-d4="sweepShowD4"
                  v-model:show-d5="sweepShowD5"
                  v-model:show-d6-plus="sweepShowD6Plus"
                  v-model:show-thd-n="sweepShowThdN"
                  v-model:show-snr="sweepShowSnr"
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
              </template>
            </MeasurementLayout>
          </template>

          <!-- FFT Spectrum screen, matching the wireframe: settings rail on the left, chart in the
               center, display options + history checklist on the right. -->
          <template v-else>
          <MeasurementLayout roomy-rail>
            <template #rail>
              <div class="flex gap-3">
                <button
                  type="button"
                  class="btn-run-go"
                  :disabled="status?.acquiring"
                  title="Starts the generator(s) and acquisition using whatever is currently configured in the Generator and FFT settings panels."
                  @click="runFromCurrentSettings"
                >
                  <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4l14 8-14 8V4z" /></svg>
                  Run
                </button>
                <button
                  type="button"
                  class="btn-run-single"
                  :disabled="status?.acquiring"
                  title="Same as Run, but captures one settled spectrum (the configured average count, or one FFT if averaging is off) and stops automatically — no need to press Stop."
                  @click="singleShotCapture"
                >
                  Single
                </button>
                <button
                  type="button"
                  class="btn-run-stop"
                  :disabled="!status?.acquiring && !status?.generatorLeft.enabled && !status?.generatorRight.enabled"
                  title="Stops acquisition and silences both generator channels."
                  @click="stopMeasurement"
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="5" y="5" width="14" height="14" /></svg>
                  Stop
                </button>
              </div>

              <GeneratorPanel ref="generatorPanel" :ranges="ranges" @change="applyGeneratorLive" />
              <SpectrumConfigPanel
                ref="spectrumConfigPanel"
                :ranges="ranges"
                @error="reportError"
                @sample-rate-change="onSampleRateChange"
              />
              <BandwidthWeightingPanel @error="reportError" />
            </template>

            <template #status>
              <MeasurementStatusBar :tone="fftTone" :label="fftHeadline" :done="fftAveragesTaken" :total="fftAverageCount">
                <span v-if="averagesLabel">
                  Averages <span class="font-mono text-neutral-700 dark:text-neutral-200">{{ averagesLabel }}</span>
                </span>
                <span v-if="spectrumConfig">
                  <span class="font-mono text-neutral-700 dark:text-neutral-200">{{ spectrumConfig.fftSize }}</span> pts
                </span>
              </MeasurementStatusBar>
            </template>

            <div class="min-w-0 flex-1">
              <!-- The metrics row and the averages line are one block for SpectrumChart's fit-above,
                   which reserves one line of tiles of it — see oneLineOfMetrics there. The tiles
                   flow in the inner row, which is the one it measures. -->
              <div ref="fftAbove">
              <div class="mb-3 flex flex-wrap items-start gap-3">
                <div data-metrics-row class="flex flex-1 flex-wrap gap-3">
                  <GeneratorMetricsPanel />
                  <MetricsPanel
                    v-if="spectrumConfig?.processLeft ?? true"
                    channel-label="L"
                    :meters="meters[0] ?? null"
                    :generator-tones="status?.generatorLeft.tones ?? []"
                    :imd="intermodulation[0] ?? null"
                    :imd-unresolved="imdUnresolved[0] ?? false"
                    :noise="noise[0] ?? null"
                    :multitone="multitone[0] ?? null"
                  />
                  <MetricsPanel
                    v-if="spectrumConfig?.processRight ?? true"
                    channel-label="R"
                    :meters="meters[1] ?? null"
                    :generator-tones="status?.generatorRight.tones ?? []"
                    :imd="intermodulation[1] ?? null"
                    :imd-unresolved="imdUnresolved[1] ?? false"
                    :noise="noise[1] ?? null"
                    :multitone="multitone[1] ?? null"
                  />
                </div>
                <button
                  type="button"
                  class="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-neutral-300 px-3 py-1.5 text-xs hover:bg-neutral-50 dark:border-neutral-700 dark:hover:bg-neutral-800"
                  title="Choose which metrics appear above the graph."
                  @click="metricPickerOpen = true"
                >
                  <ToolbarIcon name="metrics" />
                  Metrics
                </button>
              </div>

              </div>

              <SpectrumChart
                ref="spectrumChart"
                :fit-above="fftAbove"
                :spectrum="spectrum"
                :meters="meters"
                :multitone="multitone"
                :overlays="overlays"
                :weighting-label="weightingAnnunciator"
                @viewport-change="onSpectrumViewportChange"
              />
              <!-- v-show, not v-if: the spectrogram's history is collected frame by frame by the
                   mounted instance (it is what the report exports, see SpectrogramChart.exportPng),
                   and the oscilloscope's period count and channel are its own state. Unchecking
                   the panel hides it; unmounting it would throw all of that away. Both charts
                   skip a zero-width resize while hidden and refit when shown again. -->
              <SpectrogramChart
                v-show="chartShowSpectrogram"
                ref="spectrogramChart"
                v-model:annotations="shownAnnotations"
                v-model:annotation-armed="annotationArmed"
                class="mt-3"
                :spectrum="spectrum"
              />
              <OscilloscopeChart
                v-show="chartShowOscilloscope"
                v-model:annotations="shownAnnotations"
                v-model:annotation-armed="annotationArmed"
                class="mt-3"
                :scope="scope"
                :residual="scopeResidual"
                :status="status"
              />

              <!-- Notes sits here, under the charts, and is the one screen where it is NOT directly
                   above History details. On the other six screens the two are adjacent because both
                   are in the chart column; here History spans the whole view at the foot of the page,
                   below the overlay list and the harmonics/other-peaks grids, and a Notes box that
                   followed it down was three screens of scrolling away — reported as missing
                   (Joost, 2026-10-14), having last been visible at the foot of the settings rail
                   before it moved to History's width on 2026-09-19. -->
              <MeasurementNotesPanel class="mt-3" />
            </div>

            <template #aside>
              <ChartDisplayPanel>
                <MeasurementChecklist @error="reportError" />
              </ChartDisplayPanel>
            </template>
          </MeasurementLayout>

          <!-- Everything below isn't part of the new FFT Spectrum design — kept here for now, to be
               repositioned later. -->
          <OverlayList />

          <!-- The multitone table takes the harmonics table's place while a comb plays: it is the
               comb's own distortion reading, and the harmonics grid below is gated off for it.
               Its own container rather than a widening of that gate, which is there for a reason.
               Its columns follow the display rail's Channels checkboxes exactly as the harmonics
               grid below does -- see peakTableGridClass. -->
          <div v-if="multitoneConfigured && chartShowHarmonics" :class="peakTableGridClass">
            <MultitoneTable v-if="chartVisibleL" :multitone="multitone" :channel="0" channel-label="L" />
            <MultitoneTable v-if="chartVisibleR" :multitone="multitone" :channel="1" channel-label="R" />
          </div>

          <!-- Harmonics/other-peaks only mean something relative to a single fundamental tone —
               Multitone's comb and White noise have none (see showFundamentalMarkers above). The
               Display panel's own two checkboxes are on top of that, and can hide either table on a
               stimulus that does have a fundamental; its Channels checkboxes decide which columns
               exist at all (peakTableGridClass). -->
          <div
            v-if="showFundamentalMarkers && (chartShowHarmonics || chartShowOtherPeaks) && (chartVisibleL || chartVisibleR)"
            :class="peakTableGridClass"
          >
            <div v-if="chartVisibleL" class="flex flex-col gap-4">
              <HarmonicsTable v-if="chartShowHarmonics" :meters="meters" :channel="0" channel-label="L" />
              <ImdProductsTable v-if="chartShowHarmonics && imdConfigured" :imd="intermodulation" :channel="0" channel-label="L" :configured="imdConfigured" :unresolved="imdUnresolved[0] ?? false" />
              <OtherPeaksTable v-if="chartShowOtherPeaks" :meters="meters" :channel="0" channel-label="L" />
            </div>
            <div v-if="chartVisibleR" class="flex flex-col gap-4">
              <HarmonicsTable v-if="chartShowHarmonics" :meters="meters" :channel="1" channel-label="R" />
              <ImdProductsTable v-if="chartShowHarmonics && imdConfigured" :imd="intermodulation" :channel="1" channel-label="R" :configured="imdConfigured" :unresolved="imdUnresolved[1] ?? false" />
              <OtherPeaksTable v-if="chartShowOtherPeaks" :meters="meters" :channel="1" channel-label="R" />
            </div>
          </div>

          <!-- History details sits at the bottom, spanning the whole measurement view rather than
               the centre column it used to share with the charts: its rows are a name, a timestamp,
               per-channel headlines and three buttons, which a third of the width could only wrap.
               Notes does NOT follow it down here, unlike on the other six screens — see the chart
               column above for why. -->
          <MeasurementHistoryPanel @error="reportError" @report="openReport" />
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
      :ranges="ranges"
      :runner-active="runnerActive"
      :load="loadStatus"
      @set-device-kind="setDeviceKind"
      @set-simulated-profile="setSimulatedProfile"
      @set-ranging-mode="setRangingMode"
      @set-input-range="setInputRangeManually"
      @open-sound-card="soundCardDialogOpen = true"
      @open-qa403="qa403DialogOpen = true"
      @open-load="loadDialogOpen = true"
      @set-external-gain="setExternalGain"
      @set-output-range="setOutputRangeManually"
      @connect="connectDevice"
    />

    <SaveMeasurementDialog :open="saveDialogOpen" @close="saveDialogOpen = false" />
    <!--
      One flag, three dialogs, chosen by the open measurement's kind -- the same thing the Import
      button's own tooltip says it will read. The transient one is the `else`, so a kind that grew
      an Import button without a dialog of its own lands somewhere rather than nowhere; today
      MeasurementChecklist's canImport never raises the flag on one.
    -->
    <AcSimulationImportDialog
      :open="simulationImportOpen && isFrequencyResponseKind(currentType?.kind)"
      :measurement-type-id="currentTypeId"
      @close="closeSimulationImport()"
      @imported="onSimulationImported"
    />

    <AcImpedanceImportDialog
      :open="simulationImportOpen && isImpedanceKind(currentType?.kind)"
      :measurement-type-id="currentTypeId"
      @close="closeSimulationImport()"
      @imported="onSimulationImported"
    />

    <SimulationImportDialog
      :open="
        simulationImportOpen &&
        !isFrequencyResponseKind(currentType?.kind) &&
        !isImpedanceKind(currentType?.kind)
      "
      :measurement-type-id="currentTypeId"
      :sweep-target="currentType?.kind === THD_VS_FREQUENCY_KIND"
      @close="closeSimulationImport()"
      @imported="onSimulationImported"
    />
    <SaveAsDialog
      :open="saveTypeAsOpen"
      title="Save measurement as…"
      label="New measurement name"
      @close="saveTypeAsOpen = false"
      @confirm="confirmSaveTypeAs"
    />
    <ProjectDialog
      :open="projectDialogOpen"
      :mode="projectDialogMode"
      :current="currentProject"
      @close="projectDialogOpen = false"
      @confirm="confirmProjectDialog"
    />
    <FileBrowserDialog
      :open="openProjectDialogOpen"
      mode="open"
      remember-as="project"
      @close="openProjectDialogOpen = false"
      @select="openProjectFile"
    />
    <ConfirmDialog />
    <MetricPickerDialog :open="metricPickerOpen" @close="metricPickerOpen = false" />
    <ReportDialog
      :open="reportOpen"
      :live-subject="reportLiveSubject"
      :preselect-result-id="reportPreselectResultId"
      @close="reportOpen = false"
    />
    <!-- The open measurement's name is the title the box opens with unless Save text is keeping
         one -- see ScreenshotDialog's own remarks. -->
    <ScreenshotDialog
      :open="screenshotDialogOpen"
      :default-title="currentType?.name ?? ''"
      @close="screenshotDialogOpen = false"
      @confirm="screenshotChart"
    />
    <ProjectReportDialog
      :open="projectReportOpen"
      :project="currentProject"
      :types="types"
      :live-subject="reportLiveSubject"
      @close="projectReportOpen = false"
    />
    <ProjectExportDialog
      :open="projectExportOpen"
      :project="currentProject"
      @close="projectExportOpen = false"
    />
    <ProjectImportDialog
      :open="projectImportOpen"
      :types="types"
      @close="projectImportOpen = false"
      @imported="onMeasurementsImported"
    />
    <ConfigDialog :open="configDialogOpen" @close="closeConfigDialog" />
    <!-- `screenshot` and `clientState` are callbacks rather than props: nothing is rendered or
         serialized until the dialog actually asks, so the button costs nothing until it is pressed.
         The PNG is the Screenshot button's own export at a fixed size -- a report wants a picture
         that is legible rather than one the user has chosen a size for. -->
    <IssueReportDialog
      :open="issueReportDialogOpen"
      :screenshot="issueReportScreenshot"
      :client-state="issueReportClientState"
      @close="issueReportDialogOpen = false"
    />
    <SoundCardDialog :open="soundCardDialogOpen" @close="closeSoundCardDialog" />
    <!-- The status's own poll feeds this rather than a second one of its own: the step-down delay is
         read back off the device, and ConfigDialog's draft-string bug was a dialog that polled. -->
    <Qa403Dialog
      :open="qa403DialogOpen"
      :status="status"
      @close="closeQa403Dialog"
      @configuration="openConfigurationFromQa403"
    />
    <!-- Always mounted, like the others, and fed by the app's own load poll — never one of its own.
         Harmless on a bench with no load: the box that opens it does not exist there. -->
    <ProgrammableLoadDialog
      :open="loadDialogOpen"
      :runner-active="runnerActive"
      @close="loadDialogOpen = false"
      @configuration="openConfigurationFromLoad"
    />
    <AboutDialog :open="aboutDialogOpen" @close="aboutDialogOpen = false" />
    <ManualDialog />
  </div>
</template>
