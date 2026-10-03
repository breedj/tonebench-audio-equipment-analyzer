import { nextTick, ref, watch, type InjectionKey, type Ref } from 'vue'
import { DEFAULT_SPECTRUM_UNIT, parseSpectrumUnit, type SpectrumUnit } from '../spectrumUnits'
import { useProjects } from './useProjects'

// Module-level singleton, same pattern as useMeasurements.ts / useSpectrumConfig.ts — the chart
// itself and the display-options column (a separate sibling in the layout, not nested inside the
// chart's own card) both need to see the same state.
const DEFAULT_Y_TOP = 20
const DEFAULT_Y_BOTTOM = -150
const DEFAULT_X_FROM = 20
const DEFAULT_X_TO = 20000

const yTop = ref(DEFAULT_Y_TOP)
const yBottom = ref(DEFAULT_Y_BOTTOM)
const xFrom = ref(DEFAULT_X_FROM)
const xTo = ref(DEFAULT_X_TO)
const thickLines = ref(false)
const showDataPoints = ref(false)
const visibleL = ref(true)
const visibleR = ref(true)
const showMinMax = ref(true)
const showPeakHold = ref(false)
// The live spectrum line itself (per channel: the max of each decimated column). On by default, and
// the one trace here that is normally the whole point of the screen -- it is a checkbox because
// peak-hold is readable on its own: with the live capture off, the peak-hold trace is a still
// picture of the worst case so far, with nothing redrawn over the top of it twenty times a second.
// It takes the min/max band with it (see the watcher below); only peak-hold is left.
const showFftTrace = ref(true)
// Off by default so nothing changes for a type saved before this existed -- the SINGLE/STOP
// running->not-running transition that drives it is watched in App.vue, which already holds
// device status; see requestAutofitY/requestAutofitX below, which it calls into.
const autofitOnDone = ref(false)

// Which of the FFT screen's secondary panels are drawn at all. Every one of them is fed by the same
// live stream as the spectrum chart, so hiding one is purely about how much of the screen a
// measurement is allowed to take -- nothing here reaches the pipeline.
//
// The Oscilloscope and Harmonics default to on; the Waterfall and Other peaks default to *off*
// (Joost, 2026-09-29) -- they are the two a bench measurement least often wants, and between them
// they push the harmonics table off the bottom of the screen. Note what a default means here: a
// stored type carries an explicit boolean per panel, so only a row saved before these fields existed
// (null) picks the default up, and those two now come back hidden rather than shown.
const showWaterfall = ref(false)
const showOscilloscope = ref(true)
const showHarmonics = ref(true)
const showOtherPeaks = ref(false)

// The chart's level unit (see spectrumUnits.ts) and the DUT gain the input-referred unit and the
// "Noise in" tile divide by. Both are edited in ChartDisplayPanel's Level range section, the gain
// directly under the unit dropdown and only while it does something (see showDutGain there), and both
// are per-type settings with exactly this composable's lifecycle: restored with the type, marking it
// dirty when edited. Both default to what a type saved
// before they existed means -- dBV, and a DUT of unity gain.
const spectrumUnit = ref<SpectrumUnit>(DEFAULT_SPECTRUM_UNIT)
const dutGainDb = ref(0)
// Draw the weighting in effect as a dashed curve over the spectrum -- shape only, hung from a fixed
// level (see SpectrumChart's weightingCurvePlugin). Off by default: it is a teaching aid, and a
// second line on a busy chart otherwise.
const showWeightingCurve = ref(false)
// The vertical lines at H2, H3 ... on the spectrum, each labelled with its dBc. On by default --
// they are how a harmonic is found by eye -- but at the low end of a log axis the first few orders
// land within a handful of pixels of one another, and over a dense trace the lines hide more than
// they point at. The harmonics *table* is the separate `showHarmonics` flag above and reads the
// same numbers either way.
const showHarmonicMarkers = ref(true)

// Suppresses the dirty watcher below while applyLoaded writes these refs from a loaded type's
// stored params — loading is the opposite of an unsaved edit. Same pattern as
// SweepConfigPanel.vue's own `restoring` flag.
let restoring = false

// These eight are the ones persisted (see FftSpectrumParameters.ChartYTopDbv etc); activeXRange/
// the trigger counters are transient chart-owned state, never saved. No setter functions to route
// through (ChartDisplayPanel.vue binds straight to the refs via v-model), so this watches the
// values themselves instead — same effect as useSpectrumConfig.ts's apply() marking dirty, just
// triggered by the ref changing rather than a function call.
watch([
  yTop, yBottom, xFrom, xTo, thickLines, showDataPoints, showMinMax, showPeakHold, showFftTrace,
  autofitOnDone,
  showWaterfall, showOscilloscope, showHarmonics, showOtherPeaks, spectrumUnit, dutGainDb, showWeightingCurve,
  showHarmonicMarkers,
], () => {
  if (!restoring) useProjects().markDirty()
})

// Unchecking `FFT data` takes Min/max with it, and re-checking gives it back (Joost, 2026-09-30).
// The band IS that trace's own data -- the *minimum* of each decimated column, where the line above
// it is the maximum -- so leaving it drawn is not "hiding the FFT", and what is wanted in that state
// is the peak-hold trace on its own. The remembered value is put back only if Min/max is still off
// when the trace returns: a user who ticked the band back on while the line was hidden has made the
// newer choice, and restoring over it would undo something they just did. `restoring` is the same
// guard the dirty watcher uses -- applyLoaded writes both refs itself, and a stored type's Min/max
// must not be overwritten by the order those two writes happen in.
let minMaxBeforeTraceHidden: boolean | null = null
watch(showFftTrace, (shown) => {
  if (restoring) return
  if (!shown) {
    minMaxBeforeTraceHidden = showMinMax.value
    showMinMax.value = false
    return
  }
  if (minMaxBeforeTraceHidden !== null && !showMinMax.value) showMinMax.value = minMaxBeforeTraceHidden
  minMaxBeforeTraceHidden = null
})

// The frequency span the spectrum chart is actually showing right now — the net result of the
// From/To dropdowns above, a drag-zoom on the chart, and the double-click reset, all of which end
// up in the same setScale('x'). Published by SpectrumChart.vue rather than derived here, because
// only the chart knows the full span (it comes off the streamed frames) and therefore what a zoom
// is relative to. Read by WaterfallChart.vue, which is stacked directly beneath it and has to
// share the exact same axis for the two to be readable as one picture. Null until the first frame
// establishes a span.
const activeXRange = ref<{ min: number; max: number } | null>(null)

// SpectrumChart.vue owns the actual per-bin peak data (module-level there, same reasoning as
// autofit below); this is just the button's "clear it" signal.
const resetPeakHoldTrigger = ref(0)

// "Autofit" needs the currently-plotted data (highest/lowest dBV point, current sample rate) to
// compute a real answer — data this composable doesn't have. The buttons just bump these counters;
// SpectrumChart.vue (which owns the data) watches them and does the actual computation, falling
// back to a plain default reset when nothing's plotted yet.
const autofitYTrigger = ref(0)
const autofitXTrigger = ref(0)

/**
 * The stored chart preferences of an FFT measurement type — the `chart*` half of
 * FftSpectrumParameters. Every field optional: a type saved before one existed falls back to that
 * field's default.
 */
export interface StoredSpectrumDisplay {
  chartYTopDbv?: number | null
  chartYBottomDbv?: number | null
  chartXFromHz?: number | null
  chartXToHz?: number | null
  chartThickLines?: boolean | null
  chartShowDataPoints?: boolean | null
  chartShowMinMax?: boolean | null
  chartShowPeakHold?: boolean | null
  chartShowFftTrace?: boolean | null
  chartAutofitOnDone?: boolean | null
  chartShowWaterfall?: boolean | null
  chartShowOscilloscope?: boolean | null
  chartShowHarmonics?: boolean | null
  chartShowOtherPeaks?: boolean | null
  chartUnit?: string | null
  dutGainDb?: number | null
  chartShowWeightingCurve?: boolean | null
  chartShowHarmonicMarkers?: boolean | null
}

/**
 * What SpectrumChart reads of the display state — the subset of this composable it draws from. The
 * screen's chart takes the singleton below; a headless render for a report (report/renderChart.ts)
 * provides a stored measurement type's own state under SPECTRUM_DISPLAY_KEY, so a report of a stored
 * FFT result is drawn with that type's window and not whichever screen happens to be open.
 */
export interface SpectrumDisplaySource {
  yTop: Ref<number>
  yBottom: Ref<number>
  xFrom: Ref<number>
  xTo: Ref<number>
  thickLines: Ref<boolean>
  showDataPoints: Ref<boolean>
  visibleL: Ref<boolean>
  visibleR: Ref<boolean>
  showMinMax: Ref<boolean>
  showPeakHold: Ref<boolean>
  /** Whether the live spectrum line itself is drawn — the band and peak-hold have their own flags. */
  showFftTrace: Ref<boolean>
  /** The level unit the chart draws in, and the DUT gain its input-referred unit subtracts -- see spectrumUnits.ts. */
  spectrumUnit: Ref<SpectrumUnit>
  dutGainDb: Ref<number>
  showWeightingCurve: Ref<boolean>
  /** Whether the harmonic markers are drawn over the spectrum -- the harmonics table is separate. */
  showHarmonicMarkers: Ref<boolean>
  activeXRange: Ref<{ min: number; max: number } | null>
  resetPeakHoldTrigger: Ref<number>
  autofitYTrigger: Ref<number>
  autofitXTrigger: Ref<number>
  resetYDefaults(): void
  resetXDefaults(): void
  /**
   * Whether a channel is being processed at all — the acquisition panel's L/R checkboxes on screen.
   * A stored result answers from which channels it holds.
   */
  channelEnabled?: (channel: number) => boolean
}

export const SPECTRUM_DISPLAY_KEY: InjectionKey<SpectrumDisplaySource> = Symbol('spectrumDisplay')

/**
 * A display source of its own, seeded from a stored type's preferences, that shares nothing with
 * the screen's singleton — for rendering a stored FFT result headless. Same defaults as applyLoaded.
 */
export function createSpectrumDisplaySource(
  loaded: StoredSpectrumDisplay | null | undefined,
  channels: readonly number[],
): SpectrumDisplaySource {
  const yTopRef = ref(loaded?.chartYTopDbv ?? DEFAULT_Y_TOP)
  const yBottomRef = ref(loaded?.chartYBottomDbv ?? DEFAULT_Y_BOTTOM)
  const xFromRef = ref(loaded?.chartXFromHz ?? DEFAULT_X_FROM)
  const xToRef = ref(loaded?.chartXToHz ?? DEFAULT_X_TO)
  return {
    yTop: yTopRef,
    yBottom: yBottomRef,
    xFrom: xFromRef,
    xTo: xToRef,
    thickLines: ref(loaded?.chartThickLines ?? false),
    showDataPoints: ref(loaded?.chartShowDataPoints ?? false),
    visibleL: ref(true),
    visibleR: ref(true),
    showMinMax: ref(loaded?.chartShowMinMax ?? true),
    // Never for a stored result: the peak-hold maps are the screen's own running state.
    showPeakHold: ref(false),
    showFftTrace: ref(loaded?.chartShowFftTrace ?? true),
    spectrumUnit: ref(parseSpectrumUnit(loaded?.chartUnit)),
    dutGainDb: ref(loaded?.dutGainDb ?? 0),
    // Never for a stored result: the curve is fetched for the *live* weighting, which need not be
    // the one the result was captured under.
    showWeightingCurve: ref(false),
    showHarmonicMarkers: ref(loaded?.chartShowHarmonicMarkers ?? true),
    activeXRange: ref(null),
    resetPeakHoldTrigger: ref(0),
    autofitYTrigger: ref(0),
    autofitXTrigger: ref(0),
    resetYDefaults() {
      yTopRef.value = DEFAULT_Y_TOP
      yBottomRef.value = DEFAULT_Y_BOTTOM
    },
    resetXDefaults() {
      xFromRef.value = DEFAULT_X_FROM
      xToRef.value = DEFAULT_X_TO
    },
    channelEnabled: (channel) => channels.includes(channel),
  }
}

export function useChartDisplay() {
  function resetYDefaults() {
    yTop.value = DEFAULT_Y_TOP
    yBottom.value = DEFAULT_Y_BOTTOM
  }

  function resetXDefaults() {
    xFrom.value = DEFAULT_X_FROM
    xTo.value = DEFAULT_X_TO
  }

  /**
   * Syncs from a loaded measurement type's stored chart preferences (see App.vue's watch on
   * loadedParams). Not a user edit, so it never marks dirty. Null/undefined per field — a type
   * saved before this feature existed, or a freshly created one — falls back to that field's own
   * default, same as resetYDefaults/resetXDefaults.
   */
  function applyLoaded(loaded: StoredSpectrumDisplay | null | undefined) {
    restoring = true
    yTop.value = loaded?.chartYTopDbv ?? DEFAULT_Y_TOP
    yBottom.value = loaded?.chartYBottomDbv ?? DEFAULT_Y_BOTTOM
    xFrom.value = loaded?.chartXFromHz ?? DEFAULT_X_FROM
    xTo.value = loaded?.chartXToHz ?? DEFAULT_X_TO
    thickLines.value = loaded?.chartThickLines ?? false
    showDataPoints.value = loaded?.chartShowDataPoints ?? false
    showMinMax.value = loaded?.chartShowMinMax ?? true
    showPeakHold.value = loaded?.chartShowPeakHold ?? false
    showFftTrace.value = loaded?.chartShowFftTrace ?? true
    autofitOnDone.value = loaded?.chartAutofitOnDone ?? false
    showWaterfall.value = loaded?.chartShowWaterfall ?? false
    showOscilloscope.value = loaded?.chartShowOscilloscope ?? true
    showHarmonics.value = loaded?.chartShowHarmonics ?? true
    showOtherPeaks.value = loaded?.chartShowOtherPeaks ?? false
    spectrumUnit.value = parseSpectrumUnit(loaded?.chartUnit)
    dutGainDb.value = loaded?.dutGainDb ?? 0
    showWeightingCurve.value = loaded?.chartShowWeightingCurve ?? false
    showHarmonicMarkers.value = loaded?.chartShowHarmonicMarkers ?? true
    void nextTick(() => {
      restoring = false
    })
  }

  function requestAutofitY() {
    autofitYTrigger.value++
  }

  function requestAutofitX() {
    autofitXTrigger.value++
  }

  function requestResetPeakHold() {
    resetPeakHoldTrigger.value++
  }

  return {
    yTop,
    yBottom,
    xFrom,
    xTo,
    thickLines,
    showDataPoints,
    visibleL,
    visibleR,
    showMinMax,
    showPeakHold,
    showFftTrace,
    autofitOnDone,
    showWaterfall,
    showOscilloscope,
    showHarmonics,
    showOtherPeaks,
    spectrumUnit,
    dutGainDb,
    showWeightingCurve,
    showHarmonicMarkers,
    activeXRange,
    resetPeakHoldTrigger,
    autofitYTrigger,
    autofitXTrigger,
    requestAutofitY,
    requestAutofitX,
    requestResetPeakHold,
    resetYDefaults,
    resetXDefaults,
    applyLoaded,
  }
}
