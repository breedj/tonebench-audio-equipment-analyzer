<script setup lang="ts">
import { computed, nextTick, watch } from 'vue'
import type {
  DecayPalette,
  NyquistRadiusName,
  ResponseDelayView,
  ResponseProjection,
  ResponseView,
  SPlaneScaleName,
  SurfaceModeName,
} from '../api/client'
import { PALETTE_NAMES } from '../measurements/decay'
import { useFrequencyResponse, type ResponseTrace } from '../composables/useFrequencyResponse'
import { useMeasurements, type OverlayEntry } from '../composables/useMeasurements'
import { autofitRange } from '../measurements/autofitRange'
import { RESPONSE_X_FROM_OPTIONS, RESPONSE_X_TO_OPTIONS, snapResponseXWindow } from '../response/display'
import {
  DELAY_MAX_US,
  DELAY_MIN_US,
  chooseDelayUnit,
  delayFitStepUs,
  delayLabel,
  delayValuesOf,
  unitToUs,
  usToUnit,
} from '../response/delayAxis'
import { seedNicholsGain, seedPoleZeroWindow, type ResponseAutoWindow } from '../response/autoWindow'
import AnnotationsPanel from './AnnotationsPanel.vue'
import CollapsibleSection from './CollapsibleSection.vue'
import SegmentedToggle from './SegmentedToggle.vue'

/**
 * Whether coherence is a meaningful thing to offer. A chirp measures it from the cross-spectra of
 * several records; a stepped sine has one deterministic capture per point and nothing to compare it
 * with, so the toggle is hidden there rather than offering a control that always reads 1.
 */
const props = withDefaults(
  defineProps<{
    allowCoherence?: boolean
    /**
     * The window the mounted complex-plane chart fitted for itself, in these boxes' own units — what
     * seeds them when **Auto** is turned off, and what Autofit writes into them. Null when no such
     * chart is on screen (Bode, |H(s)|) or when it has nothing to fit; the boxes then keep what they
     * hold rather than being reset to a default that has nothing to do with the DUT.
     */
    autoWindow?: ResponseAutoWindow | null
  }>(),
  { allowCoherence: true, autoWindow: null },
)

const view = defineModel<ResponseView>('view', { required: true })
const projection = defineModel<ResponseProjection>('projection', { required: true })
const nyquistRadius = defineModel<NyquistRadiusName>('nyquistRadius', { required: true })
const nyquistRadiusFloorDb = defineModel<number>('nyquistRadiusFloorDb', { required: true })
const nicholsAutoGain = defineModel<boolean>('nicholsAutoGain', { required: true })
const nicholsTopDb = defineModel<number>('nicholsTopDb', { required: true })
const nicholsBottomDb = defineModel<number>('nicholsBottomDb', { required: true })
const poleZeroAutoWindow = defineModel<boolean>('poleZeroAutoWindow', { required: true })
const poleZeroXMinHz = defineModel<number>('poleZeroXMinHz', { required: true })
const poleZeroXMaxHz = defineModel<number>('poleZeroXMaxHz', { required: true })
const poleZeroYMinHz = defineModel<number>('poleZeroYMinHz', { required: true })
const poleZeroYMaxHz = defineModel<number>('poleZeroYMaxHz', { required: true })
const showFittedModel = defineModel<boolean>('showFittedModel', { required: true })
const hideOutOfBandRoots = defineModel<boolean>('hideOutOfBandRoots', { required: true })
const surfaceScale = defineModel<SPlaneScaleName>('surfaceScale', { required: true })
const surfaceMode = defineModel<SurfaceModeName>('surfaceMode', { required: true })
const surfaceTopDb = defineModel<number>('surfaceTopDb', { required: true })
const surfaceBottomDb = defineModel<number>('surfaceBottomDb', { required: true })
const surfacePalette = defineModel<DecayPalette>('surfacePalette', { required: true })
const surfaceContours = defineModel<boolean>('surfaceContours', { required: true })
const restoreDelay = defineModel<boolean>('restoreDelay', { required: true })
const showLeft = defineModel<boolean>('showLeft', { required: true })
const showRight = defineModel<boolean>('showRight', { required: true })
const showPhase = defineModel<boolean>('showPhase', { required: true })
const showCoherence = defineModel<boolean>('showCoherence', { required: true })
// No showOneDb / showThreeDb here: the two bandwidth checkboxes live in the Measurements panel's
// own PLOT column, beside the corners they plot, and this rail carried a second copy of them until
// 2026-10-23. Two controls for one setting is one too many — and the panel's copy is the one that
// can be read against the figures it acts on.
const thickLines = defineModel<boolean>('thickLines', { required: true })
const showDataPoints = defineModel<boolean>('showDataPoints', { required: true })
// Model names stay yTopDb/yBottomDb for backward compat (App.vue/params_json), but they now name
// the Gain (dB) view's own window only -- see the yTopDb/yBottomDb computed proxies below, which
// pick this pair or yTopDbv/yBottomDbv depending on which view is showing.
const yTopDbGain = defineModel<number>('yTopDb', { required: true })
const yBottomDbGain = defineModel<number>('yBottomDb', { required: true })
const yTopDbv = defineModel<number>('yTopDbv', { required: true })
const yBottomDbv = defineModel<number>('yBottomDbv', { required: true })
const xFromHz = defineModel<number>('xFromHz', { required: true })
const xToHz = defineModel<number>('xToHz', { required: true })
const delayView = defineModel<ResponseDelayView>('delayView', { required: true })
const delayTopUs = defineModel<number>('delayTopUs', { required: true })
const delayBottomUs = defineModel<number>('delayBottomUs', { required: true })
const autofitOnDone = defineModel<boolean>('autofitOnDone', { required: true })

const { traces, averageCount, phaseKind, rightChannelIsReference, phaseAvailable, running, latencySamples } =
  useFrequencyResponse()

/**
 * A complex response needs a magnitude and a phase measured against the *same* reference.
 * `ChannelDifference` does not have that -- its gain is against the commanded generator level and
 * its phase against the right input -- so the projections are refused rather than offered with a
 * caveat. Empty is exempt for the same reason `phaseAvailable` exempts it: a type that has run
 * nothing has no phase yet, and hiding the control there suggests it never has one.
 */
const complexAvailable = computed(
  () => phaseKind.value === 'DutResponse' || phaseKind.value === 'PerTrace' || traces.value.length === 0,
)

const projectionHint = computed(() =>
  complexAvailable.value
    ? 'Bode is magnitude and phase against frequency; Nyquist and Nichols are the same data as one complex curve; Poles/zeros is the fitted model’s roots in the s-plane, and |H(s)| is that same model’s magnitude over the whole plane.'
    : phaseKind.value === 'ChannelDifference'
      ? 'This run measures gain against the generator but phase against the right input, so it has no single complex response. Make the right channel the reference.'
      : 'This run measured no phase. Turn the right-channel generator on and make it the reference.',
)

/** Bode-only controls: the dB window, the bandwidth markers, the channel checkboxes and the value-axis half of Autofit. */
const bodeShowing = computed(() => projection.value === 'Bode' || !complexAvailable.value)

const PROJECTIONS: { value: ResponseProjection; label: string }[] = [
  { value: 'Bode', label: 'Bode' },
  { value: 'Nyquist', label: 'Nyquist' },
  { value: 'Nichols', label: 'Nichols' },
  { value: 'PoleZero', label: 'Poles/zeros' },
  { value: 'SPlane', label: '|H(s)|' },
]

/** The two s-plane views, which share the radius scale. */
const sPlaneShowing = computed(() => projection.value === 'PoleZero' || projection.value === 'SPlane')
/** The surface's own controls. */
const surfaceShowing = computed(() => projection.value === 'SPlane')
/** The two projections with an axis window of their own. */
const nicholsShowing = computed(() => !bodeShowing.value && projection.value === 'Nichols')
const poleZeroShowing = computed(() => !bodeShowing.value && projection.value === 'PoleZero')

/**
 * Projection and s-plane radius as the toggle wants them: the span the Projection grid needs per
 * option, and whether the complex views are reachable at all. Built here rather than inline in the
 * template because the span rule ("three over two") is the thing a later reader has to understand,
 * and it is easier to read beside the table it applies to.
 */
const projectionOptions = computed(() =>
  PROJECTIONS.map((option, index) => ({
    value: option.value,
    label: option.label,
    span: index < 3 ? 2 : 3,
    disabled: option.value !== 'Bode' && !complexAvailable.value,
  })),
)

const S_PLANE_SCALES: { value: SPlaneScaleName; label: string; title: string }[] = [
  {
    value: 'Linear',
    label: 'Linear',
    title:
      'The plane as a textbook draws it. A DUT whose roots span decades puts its lowest one inside the origin marker.',
  },
  {
    value: 'LogModulus',
    label: 'Log',
    title:
      'Scales |s| logarithmically and leaves the angle alone, so every decade of roots is visible and ζ still reads as an angle. The plane is distorted; nothing about a root changes.',
  },
]
const sPlaneScaleOptions = S_PLANE_SCALES

const SURFACE_MODES: { value: SurfaceModeName; label: string; title: string }[] = [
  {
    value: 'Heatmap',
    label: 'Map',
    title: 'The plane seen from above, as colour with iso-dB contours. Levels are readable off the key.',
  },
  {
    value: 'Relief',
    label: 'Relief',
    title: 'The same field as an oblique surface, where a pole is a spike. Evocative rather than readable — the map is the one to take a number off.',
  },
  {
    value: 'Slice',
    label: 'Slice',
    title: 'The jω axis of the same surface: |H| against frequency on linear axes, with the measurement drawn over it. The one line where the model and the measurement are the same thing, so it is where a bad fit shows.',
  },
]
const { overlays } = useMeasurements()

/** The y window actually in effect -- FR (dBV) and Gain (dB) are independent so switching views never clobbers the other one's range. */
const yTopDb = computed({
  get: () => (view.value === 'Dbv' ? yTopDbv.value : yTopDbGain.value),
  set: (v: number) => {
    if (view.value === 'Dbv') yTopDbv.value = v
    else yTopDbGain.value = v
  },
})
const yBottomDb = computed({
  get: () => (view.value === 'Dbv' ? yBottomDbv.value : yBottomDbGain.value),
  set: (v: number) => {
    if (view.value === 'Dbv') yBottomDbv.value = v
    else yBottomDbGain.value = v
  },
})

const DB_MIN = -200
const DB_MAX = 200
const DB_STEP = 2
const DB_MIN_SPAN = 4

// The dropdowns' own values and the outward snap both live in response/display.ts: this panel and
// the crosstalk one offer the same window, and "Set axis" has to land on a value a select can show.
const X_FROM_OPTIONS = RESPONSE_X_FROM_OPTIONS
const X_TO_OPTIONS = RESPONSE_X_TO_OPTIONS

const DELAY_VIEWS: { value: ResponseDelayView; label: string }[] = [
  { value: 'None', label: 'None' },
  { value: 'Group', label: 'Group delay' },
  { value: 'Phase', label: 'Phase delay' },
]

/**
 * The delay window is held in microseconds (that is what the chart, App.vue and params_json carry)
 * and edited in whichever unit the chart's axis is reading in — the same rule as the chart, so the
 * numbers in these boxes are the numbers on the axis. The step and minimum span scale with the unit.
 */
const delayUnit = computed(() => chooseDelayUnit(delayTopUs.value, delayBottomUs.value))
const delayStepUs = computed(() => (delayUnit.value === 'ms' ? 100 : 10))
const delayMinSpanUs = computed(() => delayStepUs.value * 2)
const delayHint = computed(() =>
  phaseKind.value === 'ChannelDifference'
    ? 'Derived from the phase difference between the two inputs, so a difference in delay between two measured paths rather than the DUT\'s. Group delay is −dφ/df; phase delay is −φ/(360·f). Own axis at the right; range set below.'
    : phaseKind.value === 'PerTrace'
      ? 'Group delay is −dφ/df, phase delay −φ/(360·f), on their own axis at the right. Against the generated sweep the bulk delay went out with the record\'s arbitrary start point, so these are the excess delay only; with the right input as the reference the measured delay is put back. Range set below.'
      : 'Group delay is −dφ/df, phase delay −φ/(360·f), from the phase of the left input against the right — the DUT\'s physical delay — on their own axis at the right. Range set below.',
)

/** Visible overlays that cannot draw a delay curve because they were saved before it was recorded. */
const overlaysWithoutDelay = computed(() =>
  activeOverlayResponseTraces()
    .filter((o) => o.responseTraces!.every((t) => t.groupDelayS === null))
    .map((o) => o.detail.name),
)

/**
 * Clamping through `:value` + `@change` rather than `defineModel` + `@change`. Two reasons, both
 * learned the hard way on the sweep panel: a `defineModel` write goes out as an emit and comes back
 * as a prop, so a handler in the same tick still reads the *previous* value and clamps against it,
 * writing the old bound back over what was just typed. And `change` fires on blur, Enter and each
 * spinner click but not per keystroke, so the axis does not thrash while someone types "-160".
 *
 * Shared by the dB and the phase range boxes below — both are a bounded number pinned against its
 * own partner field, just with different limits.
 */
function commitBoundedNumber(event: Event, current: number, lo: number, hi: number): number {
  const el = event.target as HTMLInputElement
  const typed = Number(el.value)
  const next = Number.isFinite(typed) && el.value.trim() !== '' ? Math.min(hi, Math.max(lo, typed)) : current

  el.value = String(next)
  return next
}

function onTopDbChange(event: Event) {
  yTopDb.value = commitBoundedNumber(event, yTopDb.value, yBottomDb.value + DB_MIN_SPAN, DB_MAX)
}

function onBottomDbChange(event: Event) {
  yBottomDb.value = commitBoundedNumber(event, yBottomDb.value, DB_MIN, yTopDb.value - DB_MIN_SPAN)
}

/**
 * The Nichols gain window's own limits. The minimum span is two decibels rather than the dB window's
 * four, because reading a gain margin is exactly the case where someone wants ±3 dB about 0.
 */
const NICHOLS_MIN_SPAN_DB = 2

function onNicholsTopChange(event: Event) {
  nicholsTopDb.value = commitBoundedNumber(event, nicholsTopDb.value, nicholsBottomDb.value + NICHOLS_MIN_SPAN_DB, DB_MAX)
}

function onNicholsBottomChange(event: Event) {
  nicholsBottomDb.value = commitBoundedNumber(event, nicholsBottomDb.value, DB_MIN, nicholsTopDb.value - NICHOLS_MIN_SPAN_DB)
}

/**
 * The pole-zero window's limits, in hertz. Signed on both axes: a stable pole has a negative σ, and
 * both halves of the jω axis are drawn. The span floor is small rather than tidy — a DUT's lowest
 * root can sit at a fraction of a hertz, and this axis is meant to be able to go there.
 */
const PZ_MAX_HZ = 1e7
const PZ_MIN_SPAN_HZ = 1e-3

/**
 * Named rather than handed the ref, because a template unwraps a ref to its *value*: passing the
 * model in from the markup would hand this function a number and leave nothing to write back to.
 * Each bound is clamped against its own partner, so the window can never close to nothing.
 */
type PoleZeroBound = 'xMin' | 'xMax' | 'yMin' | 'yMax'

function onPoleZeroBoundChange(event: Event, bound: PoleZeroBound) {
  const commit = (current: number, lo: number, hi: number) => commitBoundedNumber(event, current, lo, hi)
  switch (bound) {
    case 'xMin':
      poleZeroXMinHz.value = commit(poleZeroXMinHz.value, -PZ_MAX_HZ, poleZeroXMaxHz.value - PZ_MIN_SPAN_HZ)
      return
    case 'xMax':
      poleZeroXMaxHz.value = commit(poleZeroXMaxHz.value, poleZeroXMinHz.value + PZ_MIN_SPAN_HZ, PZ_MAX_HZ)
      return
    case 'yMin':
      poleZeroYMinHz.value = commit(poleZeroYMinHz.value, -PZ_MAX_HZ, poleZeroYMaxHz.value - PZ_MIN_SPAN_HZ)
      return
    case 'yMax':
      poleZeroYMaxHz.value = commit(poleZeroYMaxHz.value, poleZeroYMinHz.value + PZ_MIN_SPAN_HZ, PZ_MAX_HZ)
      return
  }
}

/**
 * Put the chart's own fitted window into the boxes.
 *
 * Called when Auto is turned off — so the boxes open on the window that was on screen a moment ago
 * rather than on a default belonging to no DUT — and by Autofit, which under these projections means
 * exactly this. The fit comes from the chart (see response/autoWindow.ts); with nothing mounted or
 * nothing measured there is no window to seed from, and the boxes keep what they hold.
 */
function seedNicholsFromFit() {
  const fitted = props.autoWindow
  if (fitted?.kind !== 'Nichols') return
  const seeded = seedNicholsGain(fitted)
  nicholsTopDb.value = seeded.topDb
  nicholsBottomDb.value = seeded.bottomDb
}

function seedPoleZeroFromFit() {
  const fitted = props.autoWindow
  if (fitted?.kind !== 'PoleZero') return
  const seeded = seedPoleZeroWindow(fitted)
  poleZeroXMinHz.value = seeded.xMinHz
  poleZeroXMaxHz.value = seeded.xMaxHz
  poleZeroYMinHz.value = seeded.yMinHz
  poleZeroYMaxHz.value = seeded.yMaxHz
}

function onNicholsAutoChange(event: Event) {
  const auto = (event.target as HTMLInputElement).checked
  if (!auto) seedNicholsFromFit()
  nicholsAutoGain.value = auto
}

function onPoleZeroAutoChange(event: Event) {
  const auto = (event.target as HTMLInputElement).checked
  if (!auto) seedPoleZeroFromFit()
  poleZeroAutoWindow.value = auto
}

// The boxes read and write in the axis' unit; the bounds are converted the other way so the clamp
// happens in the unit the user typed in.
function onTopDelayChange(event: Event) {
  const unit = delayUnit.value
  const next = commitBoundedNumber(
    event,
    usToUnit(delayTopUs.value, unit),
    usToUnit(delayBottomUs.value + delayMinSpanUs.value, unit),
    usToUnit(DELAY_MAX_US, unit),
  )
  delayTopUs.value = unitToUs(next, unit)
}

function onBottomDelayChange(event: Event) {
  const unit = delayUnit.value
  const next = commitBoundedNumber(
    event,
    usToUnit(delayBottomUs.value, unit),
    usToUnit(DELAY_MIN_US, unit),
    usToUnit(delayTopUs.value - delayMinSpanUs.value, unit),
  )
  delayBottomUs.value = unitToUs(next, unit)
}

/** Stored responses actually drawable right now — checked on and carrying traces of this kind. */
function activeOverlayResponseTraces(): OverlayEntry[] {
  return overlays.value.filter((o) => o.visible && o.responseTraces?.length)
}

/** Every currently-checked trace of a given kind, live plus visible overlays. */
function tracesOfKind(kind: ResponseTrace['kind']): ResponseTrace[] {
  const live = traces.value.find((t) => t.kind === kind)
  const overlayTraces = activeOverlayResponseTraces()
    .map((o) => o.responseTraces!.find((t) => t.kind === kind))
    .filter((t): t is ResponseTrace => !!t)
  return live ? [live, ...overlayTraces] : overlayTraces
}

/**
 * The curves autofit sizes against: every checked channel the current view is actually about, live
 * plus visible history overlays — not just Left, and not just the live measurement.
 */
const fitTraces = computed<ResponseTrace[]>(() => {
  if (view.value === 'Gain') return tracesOfKind('Gain')

  const kinds: ResponseTrace['kind'][] = []
  if (showLeft.value) kinds.push('Left')
  if (showRight.value) kinds.push('Right')
  return kinds.flatMap((kind) => tracesOfKind(kind))
})

/**
 * Fits the dB window to the curve, rounded outward to whole steps and never tighter than
 * DB_MIN_SPAN. Sized off the min *and* max columns, not just the plotted line: the band beneath it
 * is part of the curve, and fitting to the line alone clips the ripple out of view.
 */
/**
 * Matches the chart's own `phaseTrace` — what the delay fit has to size against, since the delay
 * curves are drawn from whichever trace carries the phase rather than from the view's own.
 *
 * <p>Under `PerTrace` the chart falls back to the Left curve when no channel is checked, rather
 * than to the right one, which with the right input as the reference is the stimulus leg. This
 * follows it: an empty list here would leave Autofit doing nothing on a chart that is drawing a
 * delay.</p>
 */
const phaseTraces = computed<ResponseTrace[]>(() => {
  if (phaseKind.value !== 'PerTrace') return tracesOfKind('Gain')
  return fitTraces.value.length > 0 ? fitTraces.value : tracesOfKind('Left')
})

function autofitDb() {
  const list = fitTraces.value.filter((t) => t.magnitudeMaxDb.length > 0)
  if (list.length === 0) return

  let lo = Infinity
  let hi = -Infinity
  for (const trace of list) {
    for (let i = 0; i < trace.magnitudeMaxDb.length; i++) {
      const freqHz = trace.freqHz[i]
      if (freqHz < xFromHz.value || freqHz > xToHz.value) continue
      lo = Math.min(lo, trace.magnitudeMinDb[i])
      hi = Math.max(hi, trace.magnitudeMaxDb[i])
    }
  }

  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return

  const top = Math.ceil((hi + 1) / DB_STEP) * DB_STEP
  const bottom = Math.floor((lo - 1) / DB_STEP) * DB_STEP

  yTopDb.value = Math.min(DB_MAX, Math.max(top, bottom + DB_MIN_SPAN))
  yBottomDb.value = Math.max(DB_MIN, Math.min(bottom, yTopDb.value - DB_MIN_SPAN))
}

/**
 * Fits the delay window to the selected delay curve of the traces the chart draws it from, within
 * the plotted frequency window. The grid is chosen from the fitted span so a window of any size
 * shows four to ten ticks; a flat curve gets two steps of height rather than a zero-height window.
 */
function autofitDelay() {
  const values: number[] = []
  for (const trace of phaseTraces.value) {
    const curve = delayValuesOf(trace, delayView.value)
    if (!curve) continue
    for (let i = 0; i < curve.length; i++) {
      const freqHz = trace.freqHz[i]
      if (freqHz < xFromHz.value || freqHz > xToHz.value) continue
      if (Number.isFinite(curve[i])) values.push(curve[i] * 1e6)
    }
  }
  if (values.length === 0) return

  const step = delayFitStepUs(Math.max(...values) - Math.min(...values))
  const fitted = autofitRange(values, { step, minSpan: step * 2, pad: 0.05 })
  if (!fitted) return

  delayTopUs.value = Math.min(DELAY_MAX_US, fitted.top)
  delayBottomUs.value = Math.max(DELAY_MIN_US, fitted.bottom)
}

/** Fits the frequency range to the measured band, snapped to the dropdown's own options. */
function autofitX() {
  const list = fitTraces.value.filter((t) => t.freqHz.length > 0)
  if (list.length === 0) return

  const from = Math.min(...list.map((t) => t.startFreqHz))
  const to = Math.max(...list.map((t) => t.endFreqHz))

  const snapped = snapResponseXWindow(from, to)
  xFromHz.value = snapped.fromHz
  xToHz.value = snapped.toHz
}

// "Autofit when measurement done": running is watched here (rather than in App.vue) because the
// autofit functions and the running/traces state are both already local to this file and to
// useFrequencyResponse() -- this is the same running->not-running transition that flips RUN back
// to a start-labeled button.
/**
 * One Autofit per chart, not one per axis (Joost, 2026-09-30): this one press fits every axis the
 * view is showing. Frequency first, because each of the three value fits is scoped to the frequency
 * window — run the other way round and they size themselves to whatever the previous window held.
 * The phase and delay fits are skipped where their curves are not drawn; each would otherwise write
 * a window from a curve the user cannot see.
 */
function autofitAll() {
  autofitX()
  // Under Nichols and the pole-zero map, Autofit means the chart's own fit written into the boxes —
  // not a checkbox quietly turned back on, which would discard a window the user chose. With Auto
  // already on there is nothing to do, since that fit is what is drawn.
  //
  // After a tick, because `autofitX` has just moved the plotted frequency window: the fit depends on
  // it, and `props.autoWindow` reaches this component through the parent's render, which has not run
  // yet. Seeding synchronously here would write the window from *before* the frequency fit.
  if ((nicholsShowing.value && !nicholsAutoGain.value) || (poleZeroShowing.value && !poleZeroAutoWindow.value)) {
    void nextTick(() => {
      if (nicholsShowing.value && !nicholsAutoGain.value) seedNicholsFromFit()
      if (poleZeroShowing.value && !poleZeroAutoWindow.value) seedPoleZeroFromFit()
    })
  }
  if (!bodeShowing.value) return
  autofitDb()
  // The phase axis is fixed at ±180° and has nothing to fit -- see PHASE_SCALE_RANGE.
  if (delayView.value !== 'None' && phaseAvailable.value) autofitDelay()
}

watch(running, (isRunning, wasRunning) => {
  // Under a polar projection only the frequency window is fitted — autofitAll's own gate. The dB,
  // phase and delay windows belong to the Bode chart, and rewriting them from here would change a
  // window the user cannot see.
  if (wasRunning && !isRunning && autofitOnDone.value) autofitAll()
})
</script>

<template>
  <!-- The chart's one Autofit, at the top of the rail and in no section at all. It used to sit
       inside the first axis section, which read as if it belonged to that one axis (Joost,
       2026-10-18) — it has always fitted every axis the chart is showing, and now it looks like a
       chart action rather than a range control. Above the sections rather than between them so it
       is in the same place on every screen. It also replaced four copies of itself, one
       per projection, each in whichever section happened to come first there. -->
  <button
    type="button"
    class="btn-panel-accent"
    title="Fits every axis the chart is showing to what is drawn: the frequency window to the measured band, and then whichever value axes the projection has — the level or gain window, the plane's window, and the phase and delay windows when those curves are on."
    @click="autofitAll"
  >
    Autofit
  </button>


  <!-- The axis windows, first in the rail and laid out the way the FFT screen's Level range /
       Frequency range are (ChartDisplayPanel.vue): label over a full-width field. Multi-root, so
       they flow into MeasurementLayout's aside grid one by one like SweepDisplayPanel's. -->
  <CollapsibleSection v-if="bodeShowing" :title="view === 'Dbv' ? 'dBV range' : 'Gain range'" size="xs">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Top
        <input
          :value="yTopDb"
          type="number"
          :step="DB_STEP"
          class="tb-input"
          @change="onTopDbChange"
        />
      </label>
      <label class="tb-field">
        Bottom
        <input
          :value="yBottomDb"
          type="number"
          :step="DB_STEP"
          class="tb-input"
          @change="onBottomDbChange"
        />
      </label>
    </div>
  </CollapsibleSection>

  <!-- Nichols' own value axis. Its *phase* axis has no section, and that is deliberate: unwrapped
       degrees make its span a property of the DUT rather than a preference (see
       response/projection.ts), and the frequency window below is what narrows it. -->
  <CollapsibleSection v-if="nicholsShowing" title="Gain range (dB)" size="xs">
    <label
      class="tb-check-row"
      title="Fits the gain axis to the curve on every pass, and always keeps 0 dB and the −180° marker in view. Untick to set the window yourself — the boxes start from the window that was on screen, so unticking changes nothing until you edit them."
    >
      <input type="checkbox" :checked="nicholsAutoGain" @change="onNicholsAutoChange" />
      Auto
    </label>
    <template v-if="!nicholsAutoGain">
      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          Top
          <input
            :value="nicholsTopDb"
            type="number"
            :step="DB_STEP"
            class="tb-input"
            @change="onNicholsTopChange"
          />
        </label>
        <label class="tb-field">
          Bottom
          <input
            :value="nicholsBottomDb"
            type="number"
            :step="DB_STEP"
            class="tb-input"
            @change="onNicholsBottomChange"
          />
        </label>
      </div>
      <p class="text-[11px] text-neutral-500 dark:text-neutral-400">
        A window that leaves 0 dB out is drawn as asked — the gain crossover and the −180° markers
        then sit off the plot.
      </p>
    </template>
  </CollapsibleSection>

  <!-- The map's own window, in hertz on both axes like the chart's labels: σ/2π across, jω/2π up. -->
  <CollapsibleSection v-if="poleZeroShowing" title="Pole/zero range (Hz)" size="xs">
    <label
      class="tb-check-row"
      title="Fits the window to the fitted roots, square, so damping still reads as an angle from the origin. Untick to set the four bounds yourself — the boxes start from the window that was on screen."
    >
      <input type="checkbox" :checked="poleZeroAutoWindow" @change="onPoleZeroAutoChange" />
      Auto
    </label>
    <template v-if="!poleZeroAutoWindow">
      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          σ/2π from
          <input
            :value="poleZeroXMinHz"
            type="number"
            class="tb-input"
            @change="onPoleZeroBoundChange($event, 'xMin')"
          />
        </label>
        <label class="tb-field">
          σ/2π to
          <input
            :value="poleZeroXMaxHz"
            type="number"
            class="tb-input"
            @change="onPoleZeroBoundChange($event, 'xMax')"
          />
        </label>
      </div>
      <div class="grid grid-cols-2 gap-2.5">
        <label class="tb-field">
          jω/2π from
          <input
            :value="poleZeroYMinHz"
            type="number"
            class="tb-input"
            @change="onPoleZeroBoundChange($event, 'yMin')"
          />
        </label>
        <label class="tb-field">
          jω/2π to
          <input
            :value="poleZeroYMaxHz"
            type="number"
            class="tb-input"
            @change="onPoleZeroBoundChange($event, 'yMax')"
          />
        </label>
      </div>
      <p class="text-[11px] text-neutral-500 dark:text-neutral-400">
        A hand-set window is drawn exactly as given, so two axes of different spans tilt the angles
        damping is read from. Keep them equal, or use Auto, when reading ζ off the picture.
      </p>
    </template>
  </CollapsibleSection>

  <CollapsibleSection title="Frequency range (Hz)" size="xs">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        From
        <select
          v-model.number="xFromHz"
          class="tb-input"
        >
          <option v-for="hz in X_FROM_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
        </select>
      </label>
      <label class="tb-field">
        To
        <select
          v-model.number="xToHz"
          class="tb-input"
        >
          <option v-for="hz in X_TO_OPTIONS" :key="hz" :value="hz">{{ hz }}</option>
        </select>
      </label>
    </div>
    <!-- The chart's one Autofit lives in its first axis section; under Bode, Nichols and the
         pole-zero map that is a section above this one, and this is where the projections with no
         value axis of their own (Nyquist, |H(s)|) get it. -->
  </CollapsibleSection>

  <!-- Only meaningful while the Phase trace is on and there is one to draw -- hidden rather than
       shown-but-inert, since the axis it configures is not drawn otherwise. -->
  <!-- No "Phase range" section: the phase is wrapped into ±180° and its axis is the bottom half of
       the plot, fixed (see PHASE_SCALE_RANGE). There is no window to set, so there is no control
       and nothing for Autofit to fit. -->

  <!-- The delay axis still has a window of its own, and this is its pair of boxes. -->
  <CollapsibleSection v-if="bodeShowing && delayView !== 'None' && phaseAvailable" :title="`Delay range (${delayUnit})`" size="xs">
    <div class="grid grid-cols-2 gap-2.5">
      <label class="tb-field">
        Top
        <input
          :value="usToUnit(delayTopUs, delayUnit)"
          type="number"
          :step="usToUnit(delayStepUs, delayUnit)"
          class="tb-input"
          @change="onTopDelayChange"
        />
      </label>
      <label class="tb-field">
        Bottom
        <input
          :value="usToUnit(delayBottomUs, delayUnit)"
          type="number"
          :step="usToUnit(delayStepUs, delayUnit)"
          class="tb-input"
          @change="onBottomDelayChange"
        />
      </label>
    </div>
  </CollapsibleSection>

  <!-- Display *under* the axis windows, which is the FFT rail's order and what this screen was
       asked for (Joost, 2026-10-18); it was above them from 2026-09-27 until then. It sits after
       the delay range as well as the frequency one, so every axis window stays together — with no
       delay curve selected, which is the default, that section is not rendered and Display is
       directly below Frequency range. -->
  <CollapsibleSection title="Display">
    <!-- Every "which one of these is on" control in this rail is a SegmentedToggle, so one look
         answers it on every display rail in the app. -->
    <span class="tb-group">Projection</span>
    <!-- Two rows of a six-column grid, not one flex row: five labels need 278 px and the rail is
         256, so a single row put |H(s)| behind `overflow-hidden` and the view looked as though it
         had never shipped. Three spanning two columns over two spanning three fills both rows
         exactly, with no empty cell to explain. -->
    <SegmentedToggle
      v-model="projection"
      group-label="Projection"
      :columns="6"
      :title="projectionHint"
      :options="projectionOptions"
    />

    <!-- Only meaningful under a polar projection, so it is not shown under Bode at all rather than
         sitting there inert. -->
    <template v-if="!bodeShowing && projection === 'Nyquist'">
      <span class="tb-group">Radius</span>
      <SegmentedToggle
        v-model="nyquistRadius"
        group-label="Radius"
        :options="[
          { value: 'Linear', label: 'Linear', title: 'The textbook plot: distances mean what they look like.' },
          { value: 'Db', label: 'dB', title: 'Keeps a wide range on screen, but distance to −1 is no longer metric.' },
        ]"
      />
      <label v-if="nyquistRadius === 'Db'" class="tb-check-row justify-between">
        Floor (dB)
        <input v-model.number="nyquistRadiusFloorDb" type="number" max="-6" step="10" class="tb-input w-24" />
      </label>
    </template>

    <!-- The contour views only: on both s-plane views the fit *is* the content rather than an overlay
         on a measured curve, so there is nothing for this to turn off. -->
    <label v-if="!bodeShowing && !sPlaneShowing" class="tb-check-row">
      <input v-model="showFittedModel" type="checkbox" class="tb-check" />
      <span title="Draws the Poles and zeros panel's rational model over the measurement, dashed. It is the check on that panel — a root list whose model does not follow the measured contour describes nothing — and it is worth hiding once you have looked, since a poor fit can wander far across the plot.">
        Show fitted model
      </span>
    </label>

    <!-- Both s-plane views: a root outside the measured band is the fit's extrapolation, and since the
         window is fitted to the roots, one of them squeezes every real one into the origin. -->
    <label v-if="!bodeShowing && sPlaneShowing" class="tb-check-row">
      <input v-model="hideOutOfBandRoots" type="checkbox" class="tb-check" />
      <!-- "from the plot" is in the label and not only in the tooltip, and that is the fix for a
           real misreading: "Remove out of band" reads as removing those roots from the *fit*, so the
           Poles and zeros table and H(s) going on listing them looks like the checkbox failing.
           Reported 2026-10-06. The fit cannot drop them — it is one rational function solved as a
           whole, and its residual is quoted against all of it; the lever for "I do not want those
           roots" is the order, which the panel now says outright when most of them are
           extrapolation. -->
      <span title="Leaves the roots the fit placed outside the swept band off the plot, and rescales it to what is left. They are extrapolation — the sweep constrains the response between its endpoints and says nothing beyond them. The fit itself is unchanged: the Poles and zeros panel goes on listing them, flagged, and H(s) keeps them. To get them out of the model, lower the order.">
        Hide out of band from the plot
      </span>
    </label>

    <!-- Both s-plane views share the radius scale, so a root is in the same place on each. -->
    <template v-if="!bodeShowing && sPlaneShowing">
      <span class="tb-group">s-plane radius</span>
      <SegmentedToggle v-model="surfaceScale" group-label="s-plane radius" :options="sPlaneScaleOptions" />
    </template>

    <!-- The |H(s)| surface's own controls. -->
    <div v-if="!bodeShowing && surfaceShowing" class="flex flex-col gap-2 text-xs">
      <span class="tb-group">Surface</span>
      <SegmentedToggle v-model="surfaceMode" group-label="Surface" :options="SURFACE_MODES" />
      <!-- The colour window, the palette and the contours describe a field over the plane. The slice
           is a line plot on its own axes and has none of them, so they are hidden rather than left
           sitting there doing nothing. -->
      <template v-if="surfaceMode !== 'Slice'">
      <label class="flex items-center justify-between gap-2">
        Top (dB)
        <input
          v-model.number="surfaceTopDb"
          type="number"
          step="10"
          title="How far above the passband level the colour ramp reaches. A pole runs to infinity, so this is where it saturates rather than a level anything reaches."
          class="tb-input w-24"
        />
      </label>
      <label class="flex items-center justify-between gap-2">
        Bottom (dB)
        <input
          v-model.number="surfaceBottomDb"
          type="number"
          step="10"
          title="How far below the passband level the colour ramp reaches. Both are offsets from the passband, so the window means the same thing on a 0 dB filter and a +26 dB amplifier."
          class="tb-input w-24"
        />
      </label>
      <label class="flex items-center justify-between gap-2">
        Palette
        <select
          v-model="surfacePalette"
          class="tb-input w-28"
        >
          <option v-for="name in PALETTE_NAMES" :key="name" :value="name">{{ name }}</option>
        </select>
      </label>
      <label class="tb-check-row">
        <input v-model="surfaceContours" type="checkbox" class="tb-check" />
        <span title="Iso-dB lines from the passband level, labelled on the colour key. They are what makes the surface quantitative rather than decorative, and what keeps it readable once a report has desaturated the colours.">
          Contours
        </span>
      </label>
      </template>
    </div>

    <!-- Only the chirp removes a delay, so only the chirp can put one back. The Bode plot has its
         own copy of this control, beside the phase trace it acts on -- see below. -->
    <label v-if="!bodeShowing && latencySamples !== null" class="tb-check-row">
      <input v-model="restoreDelay" type="checkbox" class="tb-check" />
      <span title="The chirp estimator removes a bulk delay per record; this puts it back, so the contour is the physical one. Overlays are always drawn uncorrected.">
        Restore removed delay
      </span>
    </label>

    <template v-if="bodeShowing">
      <span class="tb-group">View</span>
      <SegmentedToggle
        v-model="view"
        group-label="View"
        :options="[
          { value: 'Dbv', label: 'FR (dBV)' },
          { value: 'Gain', label: 'Gain (dB)' },
        ]"
      />
    </template>

    <label class="tb-check-row">
      <input v-model="autofitOnDone" type="checkbox" class="tb-check" /> Autofit when measurement done
    </label>

    <div v-if="bodeShowing && view === 'Dbv'" class="tb-field">
      <span class="tb-group">Channels</span>
      <label class="tb-check-row"><input v-model="showLeft" type="checkbox" class="tb-check" /> Left</label>
      <!-- Not offered when the right input is the reference: what its dBV trace shows then is the
           generator, not a measurement of anything. -->
      <label v-if="!rightChannelIsReference" class="tb-check-row">
        <input v-model="showRight" type="checkbox" class="tb-check" /> Right
      </label>
    </div>

    <div v-if="bodeShowing" class="tb-field">
      <span class="tb-group">Extra traces</span>
      <!-- Hidden outright when the measurement on screen has no phase: with no stimulus on the right
           input there is nothing to compare the left against, and a single channel's own angle is
           meaningless without a capture trigger. See the store's phaseAvailable. -->
      <label
        v-if="phaseAvailable"
        class="tb-check-row"
        :title="
          phaseKind === 'ChannelDifference'
            ? 'Unwrapped phase of the left input relative to the right, on its own axis at the right in degrees. Both inputs are measuring signals of their own here, so this is the difference between two paths rather than the DUT\'s phase response. Range set below, independently of the dB one.'
            : 'Unwrapped phase, with the bulk propagation delay removed, on its own axis at the right in degrees. Range set below, independently of the dB one.'
        "
      >
        <input v-model="showPhase" type="checkbox" class="tb-check" />
        {{ phaseKind === 'ChannelDifference' ? 'Phase difference' : 'Phase' }}
      </label>
      <!-- The same setting the complex views carry, here because the phase is what it acts on: with
           the delay removed a minimum-phase DUT reads a few degrees across its whole passband (8-10°
           on the simulator's Butterworth), which is both uninformative and drawn on top of the level
           trace. Shown under the phase checkbox rather than at the top of the rail so the two read
           as one control and its answer — but **flush with it**, not indented: the indent said
           "sub-option of Phase", and it is not one, since the Delay curves below read it too and
           are drawn whether or not Phase is ticked (Joost, 2026-10-23). -->
      <label v-if="phaseAvailable && latencySamples !== null" class="tb-check-row">
        <input v-model="restoreDelay" type="checkbox" class="tb-check" />
        <span title="The chirp estimator removes a bulk propagation delay per record, and on a minimum-phase DUT that delay is most of its phase — which is why the passband reads a few degrees. This puts it back, so the phase is the physical one (0 → −450° through a 5th-order corner). Overlays are always drawn uncorrected.">
          Restore removed delay
        </span>
      </label>
      <!-- Gated exactly like phase: the delay curves are derived from it and mean nothing without it. -->
      <label v-if="phaseAvailable" class="tb-field" :title="delayHint">
        <span>{{ delayLabel('None') }}</span>
        <select
          v-model="delayView"
          class="tb-input"
        >
          <option v-for="option in DELAY_VIEWS" :key="option.value" :value="option.value">{{ option.label }}</option>
        </select>
      </label>
      <p
        v-if="phaseAvailable && delayView !== 'None' && overlaysWithoutDelay.length > 0"
        class="text-[11px] text-amber-600 dark:text-amber-400"
      >
        Delay was not recorded for: {{ overlaysWithoutDelay.join(', ') }}.
      </p>
      <label
        v-if="props.allowCoherence"
        class="tb-check-row"
        :title="
          averageCount > 1
            ? 'How repeatable the response was, per bin, across the averaged records: 1 means every record agreed, and it falls where noise or too little stimulus energy makes that bin untrustworthy. Own axis at the right, 0 to 1.'
            : 'Needs two or more averages — a single record always reads 1.'
        "
      >
        <input v-model="showCoherence" type="checkbox" class="tb-check" /> Coherence
      </label>
      <p
        v-if="props.allowCoherence && showCoherence && averageCount <= 1"
        class="text-[11px] text-amber-600 dark:text-amber-400"
      >
        Coherence needs 2+ averages; a single record reads 1 everywhere.
      </p>
    </div>

    <!-- Hidden on the pole-zero map, where neither control does anything: that projection draws
         singularities and its own legend, and ResponsePolarChart.renderScene returns before it ever
         reaches the curve the two style props are passed to. A control that cannot change what is on
         screen is worse than a missing one. -->
    <div v-if="bodeShowing || projection !== 'PoleZero'" class="tb-field">
      <span class="tb-group">Style</span>
      <label class="tb-check-row"><input v-model="thickLines" type="checkbox" class="tb-check" /> Thick lines</label>
      <label class="tb-check-row">
        <input v-model="showDataPoints" type="checkbox" class="tb-check" /> Show data points
      </label>
    </div>
  </CollapsibleSection>

  <!-- The notes of whichever projection is on screen. `projection`'s values are the annotation view
       ids, so this follows the Bode/Nyquist/Nichols/pole-zero/s-plane switch with no mapping. -->
  <AnnotationsPanel :view="projection" />

</template>
